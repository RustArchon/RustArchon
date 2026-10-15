// Copyright ©2026 Scott Blomfield

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RustArchon.Diagnostics.Tests;

public class LogNdjsonMergerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static LogRing Ring(string source) => new(source, source + "-boot", 100);

    private static void Add(LogRing ring, int seconds, string message, LogLevel level = LogLevel.Information) =>
        ring.Append(T0.AddSeconds(seconds), level, "Cat", message, null, null);

    private static async Task<LogResult> Run(LogRing ring, bool summary = false) =>
        await new LogQueryEngine().ExecuteAsync(
            new LogQuery { Summary = summary }, [new RingLogSource(ring.Source, ring)], CancellationToken.None);

    private static async Task<string[]> Lines(LogResult result)
    {
        using var stream = new MemoryStream();
        await LogNdjsonWriter.WriteAsync(result, stream, CancellationToken.None);
        return Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static async Task<string[]> Merge(string[] upstream, LogResult local)
    {
        using var stream = new MemoryStream();
        await LogNdjsonMerger.MergeAsync(upstream, local, stream, CancellationToken.None);
        return Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public async Task Entries_from_upstream_and_local_are_interleaved_by_time_then_source()
    {
        var api = Ring("api");
        var panel = Ring("panel");
        Add(api, 0, "api0");
        Add(api, 2, "api2");
        Add(panel, 1, "panel1");
        Add(panel, 2, "panel2");

        var lines = await Merge(await Lines(await Run(api)), await Run(panel));

        var messages = lines.SkipLast(1).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("message").GetString()).ToArray();
        Assert.Equal(new[] { "api0", "panel1", "api2", "panel2" }, messages);
    }

    [Fact]
    public async Task The_meta_line_lists_every_source_from_both_and_sums_returned()
    {
        var api = Ring("api");
        var panel = Ring("panel");
        Add(api, 0, "a");
        Add(panel, 0, "p1");
        Add(panel, 1, "p2");

        var lines = await Merge(await Lines(await Run(api)), await Run(panel));

        using var meta = JsonDocument.Parse(lines[^1]);
        var m = meta.RootElement.GetProperty("_meta");
        Assert.Equal(3, m.GetProperty("returned").GetInt32());
        Assert.Equal(new[] { "api", "panel" }, m.GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("name").GetString()));
        Assert.Equal(2, m.GetProperty("sources")[1].GetProperty("returned").GetInt32());
        Assert.Equal("panel-boot", m.GetProperty("sources")[1].GetProperty("instance").GetString());
    }

    [Fact]
    public async Task An_upstream_unavailable_source_and_its_reason_survive_the_merge()
    {
        var panel = Ring("panel");
        Add(panel, 0, "p");
        var upstreamResult = await new LogQueryEngine().ExecuteAsync(
            new LogQuery(), [new Unavailable("worker", "rabbit down")], CancellationToken.None);

        var lines = await Merge(await Lines(upstreamResult), await Run(panel));

        using var meta = JsonDocument.Parse(lines[^1]);
        var worker = meta.RootElement.GetProperty("_meta").GetProperty("sources")[0];
        Assert.False(worker.GetProperty("available").GetBoolean());
        Assert.Equal("rabbit down", worker.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Upstream_entry_lines_pass_through_unchanged()
    {
        var api = Ring("api");
        Add(api, 0, "keep me exactly");
        var upstream = await Lines(await Run(api));

        var lines = await Merge(upstream, await Run(Ring("panel")));

        Assert.Equal(upstream[0], lines[0]);
    }

    [Fact]
    public async Task Summary_mode_merges_the_summary_arrays_and_writes_no_entries()
    {
        var api = Ring("api");
        var panel = Ring("panel");
        Add(api, 0, "a");
        Add(panel, 0, "p");

        var lines = await Merge(await Lines(await Run(api, summary: true)), await Run(panel, summary: true));

        Assert.Equal(2, lines.Length);
        using var summary = JsonDocument.Parse(lines[0]);
        Assert.Equal(new[] { "api", "panel" }, summary.RootElement.GetProperty("_summary").EnumerateArray().Select(s => s.GetProperty("source").GetString()));
        using var meta = JsonDocument.Parse(lines[1]);
        Assert.True(meta.RootElement.GetProperty("_meta").GetProperty("summary").GetBoolean());
    }

    [Fact]
    public async Task Upstream_without_a_meta_line_is_refused_rather_than_guessed_at()
    {
        var panel = Ring("panel");

        await Assert.ThrowsAsync<InvalidDataException>(() => Merge(["{\"source\":\"api\"}"], Run(panel).Result));
    }

    [Fact]
    public async Task Upstream_that_is_not_json_is_refused()
    {
        var panel = Ring("panel");

        await Assert.ThrowsAsync<InvalidDataException>(() => Merge(["this is not json"], Run(panel).Result));
    }

    private sealed class Unavailable(string name, string reason) : ILogSource
    {
        public string Name { get; } = name;

        public ValueTask<LogSourceResult> ReadAsync(SourceCursor? after, int max, Func<LogEntry, bool> filter, CancellationToken cancellationToken) =>
            ValueTask.FromResult(LogSourceResult.Unavailable(Name, reason));
    }
}
