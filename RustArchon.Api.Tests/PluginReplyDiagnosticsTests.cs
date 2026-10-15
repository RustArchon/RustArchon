// Copyright ©2026 Scott Blomfield

using System;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.Tests;

/// <summary>
/// What is said when a RustArchon plugin answers a command with something the Api cannot read (change 003): the server, the command and the start of
/// the reply are logged so the cause can be seen, nothing secret is, and the person is never told the plugin is "too old" for it.
/// </summary>
public class PluginReplyDiagnosticsTests
{
    private static readonly Guid ServerId = Guid.Parse("01a09e82-6ba3-746c-a984-c61fba87ec8d");

    [Fact]
    public void AnUnreadableReplyIsLoggedAtWarningWithTheServerTheCommandAndTheReply()
    {
        var logger = new CapturingLogger<PluginReplyDiagnosticsTests>();

        PluginReplyDiagnostics.LogUnreadable(logger, ServerId, "archon.thirdparty.update", "garbled <b>reply</b>");

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(ServerId.ToString(), entry.Message);
        Assert.Contains("archon.thirdparty.update", entry.Message);
        Assert.Contains("garbled <b>reply</b>", entry.Message);
    }

    [Fact]
    public void OnlyTheStartOfALongReplyIsLoggedAndSaysItWasCut()
    {
        var logger = new CapturingLogger<PluginReplyDiagnosticsTests>();

        PluginReplyDiagnostics.LogUnreadable(logger, ServerId, "archon.map.upload", new string('x', 5000));

        var message = Assert.Single(logger.Entries).Message;
        Assert.True(message.Length < PluginReplyDiagnostics.MaxLoggedReplyCharacters + 400);
        Assert.Contains("cut", message);
    }

    [Fact]
    public void ASecretInTheReplyIsRedactedBeforeItIsLogged()
    {
        var logger = new CapturingLogger<PluginReplyDiagnosticsTests>();

        PluginReplyDiagnostics.LogUnreadable(logger, ServerId, "archon.update", "oops password=hunter2 Authorization: Bearer abcdefghijklmnop.qrstuvwxyz");

        var message = Assert.Single(logger.Entries).Message;
        Assert.DoesNotContain("hunter2", message);
        Assert.DoesNotContain("abcdefghijklmnop", message);
    }

    [Fact]
    public void ANullReplyIsLoggedAsEmptyNotAsAnError()
    {
        var logger = new CapturingLogger<PluginReplyDiagnosticsTests>();

        PluginReplyDiagnostics.LogUnreadable(logger, ServerId, "archon.update", null);

        Assert.Contains("empty", Assert.Single(logger.Entries).Message);
    }

    [Theory]
    [InlineData("Unknown command: archon.thirdparty.update", true)]
    [InlineData("  unknown command: archon.update", true)]
    [InlineData("{\"v\":1,\"ok\":true}", false)]
    [InlineData("garbled", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyRustsOwnUnknownCommandTextCountsAsAPluginThatPredatesTheCommand(string? reply, bool expected) =>
        Assert.Equal(expected, PluginReplyDiagnostics.IsUnknownCommand(reply));

    [Fact]
    public void TheUnreadableSentenceDoesNotSayTheyAreTooOldAndPointsAtTheLog()
    {
        Assert.DoesNotContain("too old", PluginReplyDiagnostics.UnreadableMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("diagnostic log", PluginReplyDiagnostics.UnreadableMessage);
        Assert.Equal("unreadable_reply", PluginReplyDiagnostics.UnreadableCode);
    }
}
