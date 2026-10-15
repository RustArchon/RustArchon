// Copyright ©2026 Scott Blomfield

using Microsoft.Extensions.Logging;

namespace RustArchon.Diagnostics.Tests;

public class RingLoggerProviderTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static (ILogger Logger, LogRing Ring, RingLoggerProvider Provider) Create(
        string category = "Test.Category", Func<string, LogLevel, bool>? exclude = null)
    {
        var ring = new LogRing("api", "boot1", 100);
        var provider = new RingLoggerProvider(ring, new FixedTime(Now), exclude);
        provider.SetScopeProvider(new LoggerExternalScopeProvider());
        return (provider.CreateLogger(category), ring, provider);
    }

    private static LogEntry Single(LogRing ring) => Assert.Single(ring.ReadAfter("boot1", 0, 100).Entries);

    [Fact]
    public void Captures_level_category_timestamp_and_formatted_message()
    {
        var (logger, ring, _) = Create("My.Category");

        logger.LogInformation("Hello {Name}", "world");

        var entry = Single(ring);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("My.Category", entry.Category);
        Assert.Equal(Now, entry.TimestampUtc);
        Assert.Equal("Hello world", entry.Message);
        Assert.Equal("api", entry.Source);
    }

    [Fact]
    public void Captures_debug_but_not_trace()
    {
        var (logger, ring, _) = Create();

        logger.LogTrace("t");
        logger.LogDebug("d");

        Assert.Equal("d", Single(ring).Message);
        Assert.False(logger.IsEnabled(LogLevel.Trace));
        Assert.True(logger.IsEnabled(LogLevel.Debug));
        Assert.False(logger.IsEnabled(LogLevel.None));
    }

    [Fact]
    public void Structured_values_become_properties_without_the_original_format()
    {
        var (logger, ring, _) = Create();
        var serverId = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");

        logger.LogInformation("Started {Plugin} on server {ServerId}", "Foo", serverId);

        var props = Single(ring).Properties;
        Assert.Equal("Foo", props["Plugin"]);
        Assert.Equal(serverId.ToString(), props["ServerId"]);
        Assert.DoesNotContain("{OriginalFormat}", props.Keys);
    }

    [Fact]
    public void Scope_values_become_properties_too()
    {
        var (logger, ring, _) = Create();

        using (logger.BeginScope(new Dictionary<string, object> { ["ServerId"] = "abc" }))
        {
            logger.LogInformation("inside");
        }

        Assert.Equal("abc", Single(ring).Properties["ServerId"]);
    }

    [Fact]
    public void Message_exception_and_property_values_are_redacted()
    {
        var (logger, ring, _) = Create();

        logger.LogError(new InvalidOperationException("boom password=hunter2"), "Failed with token=abc123 for {Detail}", "Password=s3cr3t");

        var entry = Single(ring);
        Assert.DoesNotContain("abc123", entry.Message);
        Assert.DoesNotContain("hunter2", entry.Exception);
        Assert.DoesNotContain("s3cr3t", entry.Properties["Detail"]);
        Assert.Contains("InvalidOperationException", entry.Exception);
    }

    [Fact]
    public void Long_messages_and_exceptions_are_truncated()
    {
        var (logger, ring, _) = Create();

        logger.LogError(new Exception(new string('x', 50_000)), "{Big}", new string('y', 50_000));

        var entry = Single(ring);
        Assert.True(entry.Message.Length <= RingLoggerProvider.MaxMessageLength + 20);
        Assert.True(entry.Exception!.Length <= RingLoggerProvider.MaxExceptionLength + 20);
        Assert.EndsWith("[truncated]", entry.Message);
    }

    [Fact]
    public void An_excluded_category_and_level_is_not_captured()
    {
        var (logger, ring, _) = Create("MassTransit.Foo", (category, level) => category.StartsWith("MassTransit", StringComparison.Ordinal) && level < LogLevel.Warning);

        logger.LogInformation("noise");
        logger.LogWarning("kept");

        Assert.Equal("kept", Single(ring).Message);
    }

    [Fact]
    public void A_throwing_formatter_never_throws_out_of_the_logger_and_is_recorded()
    {
        var (logger, ring, _) = Create();

        logger.Log(LogLevel.Information, new EventId(1), "state", null, (_, _) => throw new InvalidOperationException("bad formatter"));

        Assert.Contains("bad formatter", Single(ring).Message);
    }
}
