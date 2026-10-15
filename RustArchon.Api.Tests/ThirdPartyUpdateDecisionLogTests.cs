// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Services;

namespace RustArchon.Api.Tests;

/// <summary>
/// What the plugin-update path says about its decisions (ADR-0006, change 001): every refusal names the server, the plugin, the gate that said no and why,
/// at Information or above; an automatic pass that decides the same thing every few minutes says it once, not every time.
/// </summary>
public class ThirdPartyUpdateDecisionLogTests
{
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> State)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var pairs = state is IEnumerable<KeyValuePair<string, object?>> p
                ? p.ToDictionary(x => x.Key, x => x.Value)
                : new Dictionary<string, object?>();
            Entries.Add((logLevel, formatter(state, exception), pairs));
        }
    }

    private static Guid NewServer() => Guid.NewGuid();

    [Fact]
    public void ARefusalNamesTheServerThePluginTheGateAndTheReasonAtInformation()
    {
        var logger = new CapturingLogger();
        var server = NewServer();

        ThirdPartyUpdateDecisionLog.Refusal(logger, server, "Foo", PluginUpdateTriggers.Manual, "plan_does_not_offer", "This organization's plan does not include plugin updates.");

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(server.ToString(), entry.Message);
        Assert.Contains("Foo", entry.Message);
        Assert.Contains("plan_does_not_offer", entry.Message);
        Assert.Contains("does not include plugin updates", entry.Message);
        Assert.Equal("Foo", entry.State["Plugin"]);
        Assert.Equal(server, entry.State["ServerId"]);
        Assert.Equal("plan_does_not_offer", entry.State["Code"]);
    }

    [Theory]
    [InlineData("file_changed")]
    [InlineData("timeout")]
    public void RefusalsThatMeanTroubleAreWarnings(string code)
    {
        var logger = new CapturingLogger();

        ThirdPartyUpdateDecisionLog.Refusal(logger, NewServer(), "Foo", PluginUpdateTriggers.Manual, code, "x");

        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public void APersonsRefusalIsAlwaysLogged()
    {
        var logger = new CapturingLogger();
        var server = NewServer();

        ThirdPartyUpdateDecisionLog.Refusal(logger, server, "Foo", PluginUpdateTriggers.Manual, "busy", "x");
        ThirdPartyUpdateDecisionLog.Refusal(logger, server, "Foo", PluginUpdateTriggers.Manual, "busy", "x");

        Assert.Equal(2, logger.Entries.Count);
    }

    [Fact]
    public void TheAutomaticPassSaysTheSameRefusalOnceUntilItChanges()
    {
        var logger = new CapturingLogger();
        var server = NewServer();

        for (var i = 0; i < 5; i++)
        {
            ThirdPartyUpdateDecisionLog.Refusal(logger, server, "Foo", PluginUpdateTriggers.Auto, "held_for_wipe", "paused ahead of the wipe");
        }

        Assert.Single(logger.Entries);

        ThirdPartyUpdateDecisionLog.Refusal(logger, server, "Foo", PluginUpdateTriggers.Auto, "no_handshake", "plugin has not reported in");
        ThirdPartyUpdateDecisionLog.Refusal(logger, server, "Foo", PluginUpdateTriggers.Auto, "no_handshake", "plugin has not reported in");

        Assert.Equal(2, logger.Entries.Count);
    }

    [Fact]
    public void ARefusalIsSaidAgainWhenItComesBackAfterSomethingElse()
    {
        var logger = new CapturingLogger();
        var server = NewServer();

        ThirdPartyUpdateDecisionLog.Refusal(logger, server, "Foo", PluginUpdateTriggers.Auto, "a", "x");
        ThirdPartyUpdateDecisionLog.Refusal(logger, server, "Foo", PluginUpdateTriggers.Auto, "b", "x");
        ThirdPartyUpdateDecisionLog.Refusal(logger, server, "Foo", PluginUpdateTriggers.Auto, "a", "x");

        Assert.Equal(3, logger.Entries.Count);
    }

    [Fact]
    public void DifferentPluginsOrServersAreRememberedSeparately()
    {
        var logger = new CapturingLogger();
        var one = NewServer();
        var two = NewServer();

        ThirdPartyUpdateDecisionLog.Refusal(logger, one, "Foo", PluginUpdateTriggers.Auto, "x", "r");
        ThirdPartyUpdateDecisionLog.Refusal(logger, one, "Bar", PluginUpdateTriggers.Auto, "x", "r");
        ThirdPartyUpdateDecisionLog.Refusal(logger, two, "Foo", PluginUpdateTriggers.Auto, "x", "r");

        Assert.Equal(3, logger.Entries.Count);
    }

    [Fact]
    public void AnOfferThatIsNotReadyIsLoggedWithItsStateAndDetailOnceUntilItChanges()
    {
        var logger = new CapturingLogger();
        var server = NewServer();

        ThirdPartyUpdateDecisionLog.OfferWaiting(logger, server, "Foo", ThirdPartyUpdateOfferStates.NotApplicable, "The file is a zip with no instructions.");
        ThirdPartyUpdateDecisionLog.OfferWaiting(logger, server, "Foo", ThirdPartyUpdateOfferStates.NotApplicable, "The file is a zip with no instructions.");
        ThirdPartyUpdateDecisionLog.OfferWaiting(logger, server, "Foo", ThirdPartyUpdateOfferStates.RolledBack, "It did not load.");

        Assert.Equal(2, logger.Entries.Count);
        Assert.Equal(LogLevel.Information, logger.Entries[0].Level);
        Assert.Contains(ThirdPartyUpdateOfferStates.NotApplicable, logger.Entries[0].Message);
        Assert.Contains("zip with no instructions", logger.Entries[0].Message);
        Assert.Contains(server.ToString(), logger.Entries[0].Message);
        Assert.Equal("Foo", logger.Entries[0].State["Plugin"]);
    }

    [Fact]
    public void NoKnownOutdatedPluginsIsSaidOnceAndAgainOnlyAfterSomeAppeared()
    {
        var logger = new CapturingLogger();
        var server = NewServer();

        ThirdPartyUpdateDecisionLog.Offers(logger, server, 0);
        ThirdPartyUpdateDecisionLog.Offers(logger, server, 0);
        Assert.Single(logger.Entries);
        Assert.Contains("no outdated", logger.Entries[0].Message, StringComparison.OrdinalIgnoreCase);

        ThirdPartyUpdateDecisionLog.Offers(logger, server, 2);
        ThirdPartyUpdateDecisionLog.Offers(logger, server, 0);

        Assert.Equal(2, logger.Entries.Count);
    }

    [Fact]
    public void TheSiteWideSwitchBeingOffIsSaidAsAWarningOnceAndWhenItComesBackOn()
    {
        var logger = new CapturingLogger();
        // The switch is global state, so this test works relative to what it is first told rather than assuming a clean slate.
        ThirdPartyUpdateDecisionLog.AutoUpdatesSwitch(logger, true);
        logger.Entries.Clear();

        ThirdPartyUpdateDecisionLog.AutoUpdatesSwitch(logger, false);
        ThirdPartyUpdateDecisionLog.AutoUpdatesSwitch(logger, false);
        ThirdPartyUpdateDecisionLog.AutoUpdatesSwitch(logger, true);

        Assert.Equal(2, logger.Entries.Count);
        Assert.Equal(LogLevel.Warning, logger.Entries[0].Level);
        Assert.Contains("off", logger.Entries[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LogLevel.Information, logger.Entries[1].Level);
    }

    [Fact]
    public void TheRememberedDecisionsDoNotGrowWithoutBound()
    {
        var logger = new CapturingLogger();

        for (var i = 0; i < ThirdPartyUpdateDecisionLog.MaxRemembered + 500; i++)
        {
            ThirdPartyUpdateDecisionLog.Refusal(logger, Guid.NewGuid(), "Foo", PluginUpdateTriggers.Auto, "x", "r");
        }

        Assert.True(ThirdPartyUpdateDecisionLog.Remembered <= ThirdPartyUpdateDecisionLog.MaxRemembered);
    }
}
