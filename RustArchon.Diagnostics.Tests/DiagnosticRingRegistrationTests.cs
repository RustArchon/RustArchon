// Copyright ©2026 Scott Blomfield

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RustArchon.Diagnostics.Tests;

public class DiagnosticRingRegistrationTests
{
    private static ServiceProvider Build(Action<ILoggingBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging(configure);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_ring_captures_debug_even_when_the_configured_levels_would_hide_it_from_every_other_provider()
    {
        var ring = new LogRing("api", "boot1", 100);
        using var services = Build(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Warning);
            logging.AddFilter("Microsoft", LogLevel.Error);
            logging.AddFilter("RustArchon.My", LogLevel.Critical);
            logging.AddDiagnosticRing(ring);
        });

        var factory = services.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger("RustArchon.My").LogDebug("quiet but wanted");
        factory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command").LogInformation("framework chatter");

        var messages = ring.ReadAfter("boot1", 0, 100).Entries.Select(e => e.Message).ToArray();
        Assert.Contains("quiet but wanted", messages);
        Assert.DoesNotContain("framework chatter", messages);
    }

    [Fact]
    public void The_ring_is_registered_so_it_can_be_resolved()
    {
        var ring = new LogRing("api", "boot1", 100);
        using var services = Build(logging => logging.AddDiagnosticRing(ring));

        Assert.Same(ring, services.GetRequiredService<LogRing>());
    }

    [Fact]
    public void An_exclusion_rule_is_honoured()
    {
        var ring = new LogRing("api", "boot1", 100);
        using var services = Build(logging => logging.AddDiagnosticRing(ring, (category, _) => category == "RustArchon.Noisy"));

        var factory = services.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger("RustArchon.Noisy").LogError("hidden");
        factory.CreateLogger("RustArchon.Quiet").LogError("shown");

        Assert.Equal(new[] { "shown" }, ring.ReadAfter("boot1", 0, 100).Entries.Select(e => e.Message));
    }
}
