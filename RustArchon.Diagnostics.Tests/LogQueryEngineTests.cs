// Copyright ©2026 Scott Blomfield

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RustArchon.Diagnostics.Tests;

public class LogQueryEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static LogRing Ring(string source, string instance = "boot1", int capacity = 1000) => new(source, instance, capacity);

    private static void Add(LogRing ring, int secondsAfterT0, LogLevel level, string category, string message,
        string? exception = null, params (string Key, string Value)[] props) =>
        ring.Append(T0.AddSeconds(secondsAfterT0), level, category, message, exception,
            props.ToDictionary(p => p.Key, p => p.Value));

    private static async Task<LogResult> Run(LogQuery query, params LogRing[] rings) =>
        await new LogQueryEngine().ExecuteAsync(query, rings.Select(r => (ILogSource)new RingLogSource(r.Source, r)).ToList(), CancellationToken.None);

    private static async Task<string[]> Lines(LogResult result)
    {
        using var stream = new MemoryStream();
        await LogNdjsonWriter.WriteAsync(result, stream, CancellationToken.None);
        return Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public async Task The_default_minimum_level_is_information()
    {
        var ring = Ring("api");
        Add(ring, 0, LogLevel.Debug, "A", "debug");
        Add(ring, 1, LogLevel.Information, "A", "info");

        var result = await Run(new LogQuery(), ring);

        Assert.Equal(new[] { "info" }, result.Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task A_debug_request_returns_debug_and_a_warning_request_returns_only_warning_and_above()
    {
        var ring = Ring("api");
        Add(ring, 0, LogLevel.Debug, "A", "debug");
        Add(ring, 1, LogLevel.Information, "A", "info");
        Add(ring, 2, LogLevel.Warning, "A", "warn");
        Add(ring, 3, LogLevel.Error, "A", "error");

        var debug = await Run(new LogQuery { MinLevel = LogLevel.Debug }, ring);
        var warning = await Run(new LogQuery { MinLevel = LogLevel.Warning }, ring);

        Assert.Equal(4, debug.Entries.Count);
        Assert.Equal(new[] { "warn", "error" }, warning.Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Category_filters_by_case_insensitive_prefix()
    {
        var ring = Ring("api");
        Add(ring, 0, LogLevel.Information, "RustArchon.Api.Services.Foo", "yes");
        Add(ring, 1, LogLevel.Information, "RustArchon.Api.Controllers.Bar", "no");

        var result = await Run(new LogQuery { Category = "rustarchon.api.services" }, ring);

        Assert.Equal(new[] { "yes" }, result.Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Text_matches_message_exception_or_category_ignoring_case()
    {
        var ring = Ring("api");
        Add(ring, 0, LogLevel.Information, "A", "found NEEDLE here");
        Add(ring, 1, LogLevel.Information, "A", "no", "boom needle");
        Add(ring, 2, LogLevel.Information, "Needle.Cat", "cat");
        Add(ring, 3, LogLevel.Information, "A", "nothing");

        var result = await Run(new LogQuery { Text = "needle" }, ring);

        Assert.Equal(new[] { "found NEEDLE here", "no", "cat" }, result.Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Time_range_is_inclusive()
    {
        var ring = Ring("api");
        for (var i = 0; i < 6; i++)
        {
            Add(ring, i, LogLevel.Information, "A", "m" + i);
        }

        var result = await Run(new LogQuery { FromUtc = T0.AddSeconds(2), ToUtc = T0.AddSeconds(4) }, ring);

        Assert.Equal(new[] { "m2", "m3", "m4" }, result.Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Property_filters_must_all_match_ignoring_case()
    {
        var ring = Ring("api");
        Add(ring, 0, LogLevel.Information, "A", "match", null, ("ServerId", "s1"), ("Plugin", "Foo"));
        Add(ring, 1, LogLevel.Information, "A", "wrong plugin", null, ("ServerId", "s1"), ("Plugin", "Bar"));
        Add(ring, 2, LogLevel.Information, "A", "no props");

        var result = await Run(
            new LogQuery { Properties = new Dictionary<string, string> { ["serverid"] = "S1", ["PLUGIN"] = "foo" } }, ring);

        Assert.Equal(new[] { "match" }, result.Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Count_applies_per_source_and_returns_the_most_recent_without_a_cursor()
    {
        var api = Ring("api");
        var worker = Ring("worker");
        for (var i = 0; i < 10; i++)
        {
            Add(api, i, LogLevel.Information, "A", "api" + i);
            Add(worker, i, LogLevel.Information, "W", "worker" + i);
        }

        var result = await Run(new LogQuery { Count = 3 }, api, worker);

        Assert.Equal(6, result.Entries.Count);
        Assert.Equal(new[] { "api7", "api8", "api9" }, result.Entries.Where(e => e.Source == "api").Select(e => e.Message));
        Assert.Equal(new[] { "worker7", "worker8", "worker9" }, result.Entries.Where(e => e.Source == "worker").Select(e => e.Message));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1_000_000, LogQuery.MaxCount)]
    public void Count_is_clamped(int requested, int expected)
    {
        Assert.Equal(expected, new LogQuery { Count = requested }.EffectiveCount);
    }

    [Fact]
    public async Task Entries_from_several_sources_are_merged_by_time_then_source()
    {
        var api = Ring("api");
        var worker = Ring("worker");
        Add(api, 1, LogLevel.Information, "A", "api1");
        Add(worker, 0, LogLevel.Information, "W", "worker0");
        Add(api, 2, LogLevel.Information, "A", "api2");
        Add(worker, 2, LogLevel.Information, "W", "worker2");

        var result = await Run(new LogQuery(), api, worker);

        Assert.Equal(new[] { "worker0", "api1", "api2", "worker2" }, result.Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Paging_with_the_reported_cursor_returns_every_entry_exactly_once_even_with_a_filter()
    {
        var ring = Ring("api");
        for (var i = 0; i < 40; i++)
        {
            Add(ring, i, i % 3 == 0 ? LogLevel.Error : LogLevel.Debug, "A", "m" + i);
        }

        var seen = new List<string>();
        Dictionary<string, SourceCursor>? after = null;
        for (var guard = 0; guard < 100; guard++)
        {
            var result = await Run(new LogQuery { MinLevel = LogLevel.Error, Count = 4, After = after ?? new Dictionary<string, SourceCursor> { ["api"] = new("boot1", 0) } }, ring);
            if (result.Entries.Count == 0)
            {
                break;
            }

            seen.AddRange(result.Entries.Select(e => e.Message));
            var page = result.Sources.Single().Page!;
            after = new Dictionary<string, SourceCursor> { ["api"] = new(page.InstanceId, page.ReturnedThrough) };
        }

        Assert.Equal(Enumerable.Range(0, 40).Where(i => i % 3 == 0).Select(i => "m" + i), seen);
    }

    [Fact]
    public async Task An_unavailable_source_is_named_with_its_reason_and_the_rest_still_return()
    {
        var api = Ring("api");
        Add(api, 0, LogLevel.Information, "A", "ok");
        var sources = new List<ILogSource> { new RingLogSource("api", api), new FakeUnavailable("worker", "no entries received for 120s") };

        var result = await new LogQueryEngine().ExecuteAsync(new LogQuery(), sources, CancellationToken.None);

        Assert.Single(result.Entries);
        var worker = result.Sources.Single(s => s.Name == "worker");
        Assert.False(worker.Available);
        Assert.Equal("no entries received for 120s", worker.UnavailableReason);
    }

    [Fact]
    public async Task A_source_that_throws_is_reported_unavailable_instead_of_failing_the_query()
    {
        var api = Ring("api");
        Add(api, 0, LogLevel.Information, "A", "ok");
        var sources = new List<ILogSource> { new RingLogSource("api", api), new FakeThrowing("plugin:abc") };

        var result = await new LogQueryEngine().ExecuteAsync(new LogQuery(), sources, CancellationToken.None);

        Assert.Single(result.Entries);
        Assert.Contains("boom", result.Sources.Single(s => s.Name == "plugin:abc").UnavailableReason);
    }

    [Fact]
    public async Task Summary_counts_by_source_level_and_category_without_returning_entries()
    {
        var ring = Ring("api");
        Add(ring, 0, LogLevel.Information, "Cat.A", "1");
        Add(ring, 1, LogLevel.Information, "Cat.A", "2");
        Add(ring, 2, LogLevel.Warning, "Cat.B", "3");
        Add(ring, 3, LogLevel.Debug, "Cat.C", "4");

        var result = await Run(new LogQuery { Summary = true }, ring);

        Assert.True(result.Summary);
        Assert.Empty(result.Entries);
        var summary = Assert.Single(result.Summaries);
        Assert.Equal("api", summary.Source);
        Assert.Equal(3, summary.Total);
        Assert.Equal(2, summary.ByLevel[LogLevel.Information]);
        Assert.Equal(1, summary.ByLevel[LogLevel.Warning]);
        Assert.Equal("Cat.A", summary.TopCategories[0].Key);
        Assert.Equal(2, summary.TopCategories[0].Value);
        Assert.Equal(T0, summary.FirstUtc);
        Assert.Equal(T0.AddSeconds(2), summary.LastUtc);
    }

    [Fact]
    public async Task Ndjson_has_one_valid_json_object_per_entry_with_the_documented_fields_and_a_final_meta_line()
    {
        var ring = Ring("api");
        Add(ring, 0, LogLevel.Warning, "My.Cat", "hello", "System.Exception: x", ("ServerId", "s1"));

        var lines = await Lines(await Run(new LogQuery(), ring));

        Assert.Equal(2, lines.Length);
        using var entry = JsonDocument.Parse(lines[0]);
        var root = entry.RootElement;
        Assert.Equal("api", root.GetProperty("source").GetString());
        Assert.Equal("boot1", root.GetProperty("instance").GetString());
        Assert.Equal(1, root.GetProperty("seq").GetInt64());
        Assert.Equal(T0, root.GetProperty("ts").GetDateTimeOffset());
        Assert.Equal("warning", root.GetProperty("level").GetString());
        Assert.Equal("My.Cat", root.GetProperty("category").GetString());
        Assert.Equal("hello", root.GetProperty("message").GetString());
        Assert.Equal("System.Exception: x", root.GetProperty("exception").GetString());
        Assert.Equal("s1", root.GetProperty("props").GetProperty("ServerId").GetString());

        using var meta = JsonDocument.Parse(lines[1]);
        var m = meta.RootElement.GetProperty("_meta");
        Assert.Equal(1, m.GetProperty("returned").GetInt32());
        var source = m.GetProperty("sources")[0];
        Assert.Equal("api", source.GetProperty("name").GetString());
        Assert.True(source.GetProperty("available").GetBoolean());
        Assert.Equal("boot1", source.GetProperty("instance").GetString());
        Assert.Equal(1, source.GetProperty("oldest").GetInt64());
        Assert.Equal(1, source.GetProperty("head").GetInt64());
        Assert.Equal(1, source.GetProperty("returnedThrough").GetInt64());
        Assert.Equal(0, source.GetProperty("lost").GetInt64());
        Assert.False(source.GetProperty("reset").GetBoolean());
    }

    [Fact]
    public async Task Ndjson_meta_reports_a_gap_and_an_unavailable_reason()
    {
        var ring = Ring("api", capacity: 3);
        for (var i = 0; i < 10; i++)
        {
            Add(ring, i, LogLevel.Information, "A", "m" + i);
        }

        var sources = new List<ILogSource> { new RingLogSource("api", ring), new FakeUnavailable("worker", "rabbit down") };
        var result = await new LogQueryEngine().ExecuteAsync(
            new LogQuery { After = new Dictionary<string, SourceCursor> { ["api"] = new("boot1", 2) } }, sources, CancellationToken.None);

        var lines = await Lines(result);
        using var meta = JsonDocument.Parse(lines[^1]);
        var metaSources = meta.RootElement.GetProperty("_meta").GetProperty("sources");
        Assert.Equal(5, metaSources[0].GetProperty("lost").GetInt64());
        Assert.False(metaSources[1].GetProperty("available").GetBoolean());
        Assert.Equal("rabbit down", metaSources[1].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Ndjson_meta_carries_a_sources_warning_alongside_its_entries()
    {
        var ring = Ring("worker");
        Add(ring, 0, LogLevel.Information, "A", "x");
        var sources = new List<ILogSource> { new WarningSource(new RingLogSource("worker", ring), "last batch received 200s ago") };

        var lines = await Lines(await new LogQueryEngine().ExecuteAsync(new LogQuery(), sources, CancellationToken.None));

        using var meta = JsonDocument.Parse(lines[^1]);
        var source = meta.RootElement.GetProperty("_meta").GetProperty("sources")[0];
        Assert.True(source.GetProperty("available").GetBoolean());
        Assert.Equal("last batch received 200s ago", source.GetProperty("warning").GetString());
    }

    [Fact]
    public async Task Ndjson_redacts_again_so_an_entry_that_bypassed_the_logger_provider_still_leaves_clean()
    {
        var ring = Ring("api");
        Add(ring, 0, LogLevel.Information, "A", "connecting password=hunter2", "token=abc123", ("Detail", "Password=s3cr3t"));

        var text = string.Join('\n', await Lines(await Run(new LogQuery(), ring)));

        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("abc123", text);
        Assert.DoesNotContain("s3cr3t", text);
    }

    [Fact]
    public async Task Ndjson_summary_mode_writes_a_summary_line_and_no_entry_lines()
    {
        var ring = Ring("api");
        Add(ring, 0, LogLevel.Information, "A", "x");

        var lines = await Lines(await Run(new LogQuery { Summary = true }, ring));

        Assert.Equal(2, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        var api = first.RootElement.GetProperty("_summary")[0];
        Assert.Equal("api", api.GetProperty("source").GetString());
        Assert.Equal(1, api.GetProperty("total").GetInt64());
        Assert.Equal(1, api.GetProperty("byLevel").GetProperty("information").GetInt64());
    }

    private sealed class WarningSource(ILogSource inner, string warning) : ILogSource
    {
        public string Name => inner.Name;

        public async ValueTask<LogSourceResult> ReadAsync(SourceCursor? after, int max, Func<LogEntry, bool> filter, CancellationToken cancellationToken) =>
            (await inner.ReadAsync(after, max, filter, cancellationToken)) with { Warning = warning };
    }

    private sealed class FakeUnavailable(string name, string reason) : ILogSource
    {
        public string Name { get; } = name;

        public ValueTask<LogSourceResult> ReadAsync(SourceCursor? after, int max, Func<LogEntry, bool> filter, CancellationToken cancellationToken) =>
            ValueTask.FromResult(LogSourceResult.Unavailable(Name, reason));
    }

    private sealed class FakeThrowing(string name) : ILogSource
    {
        public string Name { get; } = name;

        public ValueTask<LogSourceResult> ReadAsync(SourceCursor? after, int max, Func<LogEntry, bool> filter, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }
}
