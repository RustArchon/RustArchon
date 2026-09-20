// Copyright ©2026 Scott Blomfield

using System.Linq;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="PluginSettingsReconciler"/> - the rule that decides which <c>archon.config</c>
/// commands bring a plugin's reported switches in line with a server's saved ones. Pure, so no bus or database.
/// </summary>
public class PluginSettingsReconcilerTests
{
    private static readonly string[] WithConfig = ["config"];

    [Fact]
    public void NothingToSendWhenReportedMatchesDesired()
    {
        Assert.Empty(PluginSettingsReconciler.CommandsFor(true, true, true, true, WithConfig));
        Assert.Empty(PluginSettingsReconciler.CommandsFor(false, false, false, false, WithConfig));
    }

    [Fact]
    public void SendsOnlyTheSettingThatDiffers()
    {
        var commands = PluginSettingsReconciler.CommandsFor(
            desiredRecording: true, desiredCombat: false, reportedRecording: true, reportedCombat: true, WithConfig);

        var single = Assert.Single(commands);
        Assert.Equal("archon.config set combat false", single.Command);
    }

    [Fact]
    public void SendsBothInAStableOrderWhenBothDiffer()
    {
        var commands = PluginSettingsReconciler.CommandsFor(false, true, true, false, WithConfig);

        Assert.Equal(
            ["archon.config set recording false", "archon.config set combat true"],
            commands.Select(c => c.Command));
    }

    [Fact]
    public void ReEnablingASwitchSendsTrue()
    {
        var commands = PluginSettingsReconciler.CommandsFor(true, true, false, true, WithConfig);

        Assert.Equal("archon.config set recording true", Assert.Single(commands).Command);
    }

    [Fact]
    public void SendsNothingWhenThePluginDidNotReportTheConfigCapability()
    {
        // Fail closed: even with a real difference, never send a command the build has not said it understands.
        Assert.Empty(PluginSettingsReconciler.CommandsFor(false, false, true, true, []));
        Assert.Empty(PluginSettingsReconciler.CommandsFor(false, false, true, true, ["recording", "combat"]));
    }

    [Fact]
    public void TheCapabilityNameMustMatchExactly()
    {
        Assert.Empty(PluginSettingsReconciler.CommandsFor(false, false, true, true, ["Config"]));
    }
}
