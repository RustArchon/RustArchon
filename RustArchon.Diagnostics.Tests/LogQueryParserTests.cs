// Copyright ©2026 Scott Blomfield

using Microsoft.Extensions.Logging;

namespace RustArchon.Diagnostics.Tests;

public class LogQueryParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static LogQueryParseResult Parse(params (string Key, string Value)[] pairs)
    {
        var dictionary = pairs
            .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(p => p.Value).ToList(), StringComparer.OrdinalIgnoreCase);
        return LogQueryParser.Parse(dictionary, Now);
    }

    private static ParsedLogRequest Ok(params (string Key, string Value)[] pairs)
    {
        var result = Parse(pairs);
        Assert.True(result.Ok, result.Error);
        return result.Request!;
    }

    [Fact]
    public void NoParametersMeansTheDefaults()
    {
        var request = Ok();

        Assert.Equal(new[] { "api", "worker", "panel" }, request.Sources);
        Assert.Equal(LogLevel.Information, request.Query.MinLevel);
        Assert.Equal(LogQuery.DefaultCount, request.Query.Count);
        Assert.False(request.Query.Summary);
        Assert.Null(request.Query.After);
    }

    [Fact]
    public void ASingleWorkerInstanceCanBeNamedByItsSourceName()
    {
        var request = Ok(("sources", "api,worker:abcd1234,WORKER:00ff"));

        Assert.Equal(new[] { "api", "worker:abcd1234", "worker:00ff" }, request.Sources);
    }

    [Theory]
    [InlineData("worker:")]
    [InlineData("worker:xyz")]
    [InlineData("worker:abcd1234abcd1234abcd1234abcd1234ab")]
    public void ABadWorkerInstanceNameIsRefused(string source)
    {
        var result = Parse(("sources", source));

        Assert.False(result.Ok);
        Assert.Contains("sources", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SourcesAreACommaListAndPluginSourcesNameAServer()
    {
        var request = Ok(("sources", "api, plugin:3f2504e0-4f89-11d3-9a0c-0305e82c3301 ,worker"));

        Assert.Equal(new[] { "api", "plugin:3f2504e0-4f89-11d3-9a0c-0305e82c3301", "worker" }, request.Sources);
    }

    [Fact]
    public void SourcesMayBeRepeatedAsWell()
    {
        var request = Ok(("sources", "api"), ("sources", "worker"));

        Assert.Equal(new[] { "api", "worker" }, request.Sources);
    }

    [Theory]
    [InlineData("trace", LogLevel.Trace)]
    [InlineData("DEBUG", LogLevel.Debug)]
    [InlineData("info", LogLevel.Information)]
    [InlineData("information", LogLevel.Information)]
    [InlineData("warn", LogLevel.Warning)]
    [InlineData("warning", LogLevel.Warning)]
    [InlineData("err", LogLevel.Error)]
    [InlineData("error", LogLevel.Error)]
    [InlineData("critical", LogLevel.Critical)]
    [InlineData("1", LogLevel.Debug)]
    public void LevelAcceptsNamesAndNumbers(string text, LogLevel expected)
    {
        Assert.Equal(expected, Ok(("level", text)).Query.MinLevel);
    }

    [Fact]
    public void FromAndToAcceptIsoTimestamps()
    {
        var request = Ok(("from", "2026-10-14T10:00:00Z"), ("to", "2026-10-14T11:30:00+00:00"));

        Assert.Equal(new DateTimeOffset(2026, 10, 14, 10, 0, 0, TimeSpan.Zero), request.Query.FromUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 11, 30, 0, TimeSpan.Zero), request.Query.ToUtc);
    }

    [Theory]
    [InlineData("90s", 90)]
    [InlineData("15m", 900)]
    [InlineData("2h", 7200)]
    [InlineData("1d", 86400)]
    public void SinceIsARelativeWindowEndingNow(string text, int seconds)
    {
        Assert.Equal(Now.AddSeconds(-seconds), Ok(("since", text)).Query.FromUtc);
    }

    [Fact]
    public void CategoryAndTextAreTaken()
    {
        var request = Ok(("category", "RustArchon.Api.Services"), ("text", "held_for_wipe"));

        Assert.Equal("RustArchon.Api.Services", request.Query.Category);
        Assert.Equal("held_for_wipe", request.Query.Text);
    }

    [Fact]
    public void PropertyFiltersAreKeyColonValueAndMayRepeat()
    {
        var request = Ok(("prop", "ServerId:abc"), ("prop", "Plugin:Foo:Bar"));

        Assert.Equal("abc", request.Query.Properties!["ServerId"]);
        Assert.Equal("Foo:Bar", request.Query.Properties["Plugin"]);
    }

    [Fact]
    public void CountIsTakenAndClampedByTheQueryItself()
    {
        Assert.Equal(25, Ok(("count", "25")).Query.Count);
    }

    [Fact]
    public void AfterIsSourceInstanceSequenceAndTheSourceNameMayItselfContainColons()
    {
        var request = Ok(
            ("after", "api:boot1:120"),
            ("after", "worker:abcd1234:abcd1234ffffffffffffffffffffffff:7"),
            ("after", "plugin:3f2504e0-4f89-11d3-9a0c-0305e82c3301:12345:9"));

        var after = request.Query.After!;
        Assert.Equal(new SourceCursor("boot1", 120), after["api"]);
        Assert.Equal(new SourceCursor("abcd1234ffffffffffffffffffffffff", 7), after["worker:abcd1234"]);
        Assert.Equal(new SourceCursor("12345", 9), after["plugin:3f2504e0-4f89-11d3-9a0c-0305e82c3301"]);
    }

    [Fact]
    public void ModeSummarySetsSummary()
    {
        Assert.True(Ok(("mode", "summary")).Query.Summary);
        Assert.False(Ok(("mode", "entries")).Query.Summary);
    }

    [Theory]
    [InlineData("level", "loud")]
    [InlineData("level", "9")]
    [InlineData("from", "yesterday-ish")]
    [InlineData("to", "nope")]
    [InlineData("since", "soon")]
    [InlineData("since", "-5m")]
    [InlineData("count", "many")]
    [InlineData("count", "-1")]
    [InlineData("mode", "everything")]
    [InlineData("prop", "novalue")]
    [InlineData("prop", ":x")]
    [InlineData("after", "api:boot1")]
    [InlineData("after", "api:boot1:notanumber")]
    [InlineData("after", "api:boot1:-3")]
    [InlineData("sources", "api,,worker")]
    [InlineData("sources", "")]
    [InlineData("sources", "database")]
    [InlineData("sources", "plugin:not-a-guid")]
    [InlineData("sources", "plugin:")]
    public void BadInputIsRefusedWithAnExplanationNamingTheParameter(string key, string value)
    {
        var result = Parse((key, value));

        Assert.False(result.Ok);
        Assert.Contains(key, result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromAfterToIsRefused()
    {
        var result = Parse(("from", "2026-10-14T12:00:00Z"), ("to", "2026-10-14T11:00:00Z"));

        Assert.False(result.Ok);
    }

    [Fact]
    public void UnknownParametersAreRefusedSoATypoDoesNotSilentlyReturnTooMuch()
    {
        var result = Parse(("levl", "debug"));

        Assert.False(result.Ok);
        Assert.Contains("levl", result.Error!);
    }
}
