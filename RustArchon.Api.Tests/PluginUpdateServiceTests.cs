// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Tests;

/// <summary>
/// <see cref="PluginUpdateService"/>: every precondition, in order, before anything happens - and the token is minted
/// last and thrown away again if the Updater refuses. An update replaces code running with full server privileges, so
/// each refusal here is a fail-closed decision worth its own test.
/// </summary>
public class PluginUpdateServiceTests
{
    private const string PanelKey = "0123456789abcdef";
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ServerId = Guid.NewGuid();
    private const string OkReply = "{\"v\":1,\"ok\":true,\"data\":{\"phase\":\"downloading\"}}";

    private readonly Mock<IServerPluginStatusRepository> _statuses = new();
    private readonly Mock<IServerPluginRepository> _plugins = new();
    private readonly Mock<IPluginScriptService> _script = new();
    private readonly Mock<IPluginUpdateTokenRepository> _tokens = new();
    private readonly Mock<IPlatformSettingsCache> _settings = new();
    private readonly Mock<IRequestClient<SendRconCommand>> _client = new();
    private readonly List<SendRconCommand> _sent = [];

    public PluginUpdateServiceTests()
    {
        // Everything is in order by default; each test breaks one thing.
        GivenStatus();
        GivenPlugins(RustArchonPlugin.Name, RustArchonPlugin.UpdaterName);
        _script.Setup(s => s.GetLatestVersionAsync()).ReturnsAsync("0.2.1");
        _script.Setup(s => s.GetKeyStateAsync(It.Is<string>(f => string.Equals(f, PanelKey, StringComparison.OrdinalIgnoreCase))))
            .ReturnsAsync(PluginKeyState.Active); // anything else is unknown, which is refused
        _settings.Setup(s => s.GetStringAsync(PlatformSettingsRegistry.PanelBaseUrl)).ReturnsAsync("http://192.168.0.46:5200");
        _tokens.Setup(t => t.MintAsync(TenantId, ServerId, It.IsAny<string>(), It.IsAny<TimeSpan>())).ReturnsAsync("TOKEN123");
        GivenUpdaterReplies(OkReply);
    }

    private PluginUpdateService Create() =>
        new(_statuses.Object, _plugins.Object, _script.Object, _tokens.Object, _settings.Object, _client.Object,
            NullLogger<PluginUpdateService>.Instance);

    private static RustServer Server(bool enabled = true, bool updates = true) =>
        new() { Id = ServerId, TenantId = TenantId, IsEnabled = enabled, PluginUpdatesEnabled = updates };

    private void GivenStatus(string state = "valid", string fingerprint = PanelKey, string version = "0.2.0") =>
        _statuses.Setup(r => r.GetForServerAcrossTenantsAsync(TenantId, ServerId)).ReturnsAsync(new ServerPluginStatus
        {
            TenantId = TenantId,
            RustServerId = ServerId,
            PluginVersion = version,
            SigningState = state,
            SigningKeyFingerprint = fingerprint
        });

    private void GivenPlugins(params string[] names) =>
        _plugins.Setup(r => r.GetForServerAsync(ServerId)).ReturnsAsync(
            names.Select(n => new ServerPlugin { Name = n, RustServerId = ServerId, TenantId = TenantId }).ToList());

    private void GivenUpdaterReplies(string message, bool success = true) =>
        _client
            .Setup(c => c.GetResponse<RconCommandResult>(It.IsAny<SendRconCommand>(), It.IsAny<CancellationToken>(), It.IsAny<RequestTimeout>()))
            .Callback<SendRconCommand, CancellationToken, RequestTimeout>((request, _, _) => _sent.Add(request))
            .ReturnsAsync(Mock.Of<Response<RconCommandResult>>(r =>
                r.Message == new RconCommandResult(success, message, null, null, success ? null : "NotConnected")));

    private void AssertNothingHappened()
    {
        _tokens.Verify(t => t.MintAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        Assert.Empty(_sent);
    }

    // ---- refusals, before anything is minted or sent -------------------------------------------------------

    [Fact]
    public async Task ADisabledServerIsRefused()
    {
        var result = await Create().StartAsync(Server(enabled: false));

        Assert.False(result.Started);
        Assert.Equal("server_disabled", result.Code);
        AssertNothingHappened();
    }

    [Fact]
    public async Task UpdatesOffForTheServerIsRefused()
    {
        var result = await Create().StartAsync(Server(updates: false));

        Assert.Equal("updates_disabled", result.Code);
        AssertNothingHappened();
    }

    [Fact]
    public async Task APluginThatHasNeverReportedInIsRefused()
    {
        _statuses.Setup(r => r.GetForServerAcrossTenantsAsync(TenantId, ServerId)).ReturnsAsync((ServerPluginStatus?)null);

        var result = await Create().StartAsync(Server());

        Assert.Equal("no_handshake", result.Code);
        AssertNothingHappened();
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("unsigned")]
    [InlineData("unknown")]
    [InlineData("unlocated")]
    [InlineData("error")]
    public async Task APluginWhoseSignatureIsNotValidIsRefused(string state)
    {
        GivenStatus(state: state);

        var result = await Create().StartAsync(Server());

        Assert.Equal("not_signed_by_this_panel", result.Code);
        AssertNothingHappened();
    }

    [Fact]
    public async Task APluginSignedByADifferentPanelIsRefused()
    {
        GivenStatus(state: "valid", fingerprint: "fedcba9876543210");

        var result = await Create().StartAsync(Server());

        Assert.Equal("not_signed_by_this_panel", result.Code);
        Assert.Equal("0.2.0", result.FromVersion);
        AssertNothingHappened();
    }

    [Fact]
    public async Task IfThisPanelHasNoKeyYetNothingCanBeUpdated()
    {
        _script.Setup(s => s.GetKeyStateAsync(It.IsAny<string>())).ReturnsAsync((PluginKeyState?)null);

        var result = await Create().StartAsync(Server());

        Assert.Equal("not_signed_by_this_panel", result.Code);
        AssertNothingHappened();
    }

    [Fact]
    public async Task AKeyFingerprintIsComparedIgnoringCase()
    {
        GivenStatus(fingerprint: PanelKey.ToUpperInvariant());

        var result = await Create().StartAsync(Server());

        Assert.True(result.Started);
    }

    [Fact]
    public async Task AServerWithoutTheUpdaterIsRefused()
    {
        GivenPlugins(RustArchonPlugin.Name, "Kits");

        var result = await Create().StartAsync(Server());

        Assert.Equal("updater_missing", result.Code);
        AssertNothingHappened();
    }

    [Fact]
    public async Task ALookalikeUpdaterNameDoesNotCount()
    {
        GivenPlugins(RustArchonPlugin.Name, "RustArchonUpdaterExtra", "ArchonSpikeIO");

        var result = await Create().StartAsync(Server());

        Assert.Equal("updater_missing", result.Code);
    }

    [Fact]
    public async Task ANullPluginListIsTreatedAsNoUpdater()
    {
        _plugins.Setup(r => r.GetForServerAsync(ServerId)).ReturnsAsync((List<ServerPlugin>)null!);

        var result = await Create().StartAsync(Server());

        Assert.Equal("updater_missing", result.Code);
    }

    [Theory]
    [InlineData("0.2.0")] // already current
    [InlineData("0.1.9")] // older than installed
    [InlineData(null)]
    [InlineData("not-a-version")]
    public async Task NothingNewerToInstallIsRefused(string? latest)
    {
        _script.Setup(s => s.GetLatestVersionAsync()).ReturnsAsync(latest);

        var result = await Create().StartAsync(Server());

        Assert.Equal("up_to_date", result.Code);
        AssertNothingHappened();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://192.168.0.46/")]
    [InlineData("/relative")]
    public async Task APanelUrlTheServerCouldNotReachIsRefused(string? baseUrl)
    {
        _settings.Setup(s => s.GetStringAsync(PlatformSettingsRegistry.PanelBaseUrl)).ReturnsAsync(baseUrl);

        var result = await Create().StartAsync(Server());

        Assert.Equal("panel_url_invalid", result.Code);
        AssertNothingHappened();
    }

    // ---- the accepted path ---------------------------------------------------------------------------------

    [Fact]
    public async Task ASuccessfulStartMintsATokenForThisServerAndSendsTheCommand()
    {
        var result = await Create().StartAsync(Server());

        Assert.True(result.Started);
        Assert.Equal("started", result.Code);
        Assert.Equal("0.2.0", result.FromVersion);
        Assert.Equal("0.2.1", result.ToVersion);
        _tokens.Verify(t => t.MintAsync(TenantId, ServerId, PanelKey, PluginUpdateService.TokenLifetime), Times.Once);
        var command = Assert.Single(_sent);
        Assert.Equal(ServerId, command.ServerId);
        Assert.Equal($"archon.update 0.2.1 http://192.168.0.46:5200/ingest/plugin/{ServerId}/TOKEN123", command.Command);
    }

    [Fact]
    public async Task TheCommandIsNonInteractiveSoItNeverAppearsAsSomethingAPersonTyped()
    {
        await Create().StartAsync(Server());

        Assert.False(Assert.Single(_sent).Interactive);
    }

    [Fact]
    public async Task TheTokenLivesTenMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), PluginUpdateService.TokenLifetime);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("https://panel.example.com", "https://panel.example.com")]
    [InlineData("https://panel.example.com/", "https://panel.example.com")]
    [InlineData("https://panel.example.com/rustarchon/", "https://panel.example.com/rustarchon")]
    [InlineData("http://192.168.0.46:5200/?x=1#frag", "http://192.168.0.46:5200")]
    public async Task TheUrlIsBuiltFromThePanelBaseWithNoDoubleSlashAndNoQueryOrFragment(string baseUrl, string expectedPrefix)
    {
        _settings.Setup(s => s.GetStringAsync(PlatformSettingsRegistry.PanelBaseUrl)).ReturnsAsync(baseUrl);

        await Create().StartAsync(Server());

        Assert.Equal($"archon.update 0.2.1 {expectedPrefix}/ingest/plugin/{ServerId}/TOKEN123", Assert.Single(_sent).Command);
    }

    [Fact]
    public async Task ASuccessfulStartKeepsTheToken()
    {
        await Create().StartAsync(Server());

        _tokens.Verify(t => t.RevokeAsync(It.IsAny<string>()), Times.Never);
    }

    // ---- the Updater refuses or is unreachable: the token is thrown away -----------------------------------

    [Theory]
    [InlineData("{\"v\":1,\"ok\":false,\"err\":\"not_newer\",\"message\":\"installed 0.2.1, offered 0.2.1\"}", "not_newer")]
    [InlineData("{\"v\":1,\"ok\":false,\"err\":\"busy\",\"message\":\"already\"}", "busy")]
    [InlineData("{\"v\":1,\"ok\":false,\"err\":\"main_missing\",\"message\":\"x\"}", "main_missing")]
    public async Task WhenTheUpdaterRefusesItsReasonIsPassedOnAndTheTokenIsRevoked(string reply, string expectedCode)
    {
        GivenUpdaterReplies(reply);

        var result = await Create().StartAsync(Server());

        Assert.False(result.Started);
        Assert.Equal(expectedCode, result.Code);
        _tokens.Verify(t => t.RevokeAsync("TOKEN123"), Times.Once);
    }

    [Theory]
    [InlineData("{\"v\":1,\"ok\":false,\"err\":\"<script>alert(1)</script>\",\"message\":\"x\"}")]
    [InlineData("{\"v\":1,\"ok\":false,\"err\":\"this_code_is_much_longer_than_forty_characters_allowed\"}")]
    [InlineData("{\"v\":1,\"ok\":false}")]
    [InlineData("{\"v\":1,\"ok\":false,\"err\":\"\"}")]
    public async Task ACodeFromTheServerThatIsNotSimpleTextIsDroppedNotShown(string reply)
    {
        GivenUpdaterReplies(reply);

        var result = await Create().StartAsync(Server());

        Assert.Equal("updater_refused", result.Code);
    }

    [Theory]
    [InlineData("Unknown command: archon.update")]
    [InlineData("")]
    [InlineData("<html>nope</html>")]
    public async Task AReplyThatIsNotAnEnvelopeMeansTheUpdaterIsNotLoaded(string reply)
    {
        GivenUpdaterReplies(reply);

        var result = await Create().StartAsync(Server());

        Assert.False(result.Started);
        Assert.Equal("updater_missing", result.Code);
        _tokens.Verify(t => t.RevokeAsync("TOKEN123"), Times.Once);
    }

    [Theory]
    [InlineData("{\"v\":1,\"ok\":\"true\"}")] // the string "true" is not the boolean
    [InlineData("{\"v\":1,\"ok\":1}")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task OnlyAnActualBooleanTrueCountsAsAccepted(string reply)
    {
        GivenUpdaterReplies(reply);

        var result = await Create().StartAsync(Server());

        Assert.False(result.Started);
        _tokens.Verify(t => t.RevokeAsync("TOKEN123"), Times.Once);
    }

    [Fact]
    public async Task AServerThatIsNotConnectedIsReportedAndTheTokenRevoked()
    {
        GivenUpdaterReplies("", success: false);

        var result = await Create().StartAsync(Server());

        Assert.Equal("not_connected", result.Code);
        _tokens.Verify(t => t.RevokeAsync("TOKEN123"), Times.Once);
    }

    [Fact]
    public async Task ATimeoutIsReportedAndTheTokenRevoked()
    {
        _client
            .Setup(c => c.GetResponse<RconCommandResult>(It.IsAny<SendRconCommand>(), It.IsAny<CancellationToken>(), It.IsAny<RequestTimeout>()))
            .ThrowsAsync(new RequestTimeoutException());

        var result = await Create().StartAsync(Server());

        Assert.Equal("timeout", result.Code);
        Assert.False(result.Started);
        _tokens.Verify(t => t.RevokeAsync("TOKEN123"), Times.Once);
    }

    [Fact]
    public async Task TheTokenIsNeverInTheMessageShownToTheAdmin()
    {
        GivenUpdaterReplies("{\"v\":1,\"ok\":false,\"err\":\"busy\",\"message\":\"already in progress\"}");

        var result = await Create().StartAsync(Server());

        Assert.DoesNotContain("TOKEN123", result.Message);
        Assert.DoesNotContain("TOKEN123", result.Code);
    }
    // ---- key rotation: retired and revoked keys -----------------------------------------------------------

    private const string OldKey = "aaaaaaaaaaaaaaaa";

    private void GivenServerOnKey(string fingerprint, PluginKeyState? state, string updaterVersion = "v0.2.0")
    {
        GivenStatus(fingerprint: fingerprint);
        _script.Setup(s => s.GetKeyStateAsync(It.Is<string>(f => string.Equals(f, fingerprint, StringComparison.OrdinalIgnoreCase))))
            .ReturnsAsync(state);
        _plugins.Setup(r => r.GetForServerAsync(ServerId)).ReturnsAsync(
        [
            new ServerPlugin { Name = RustArchonPlugin.Name, RustServerId = ServerId, TenantId = TenantId },
            new ServerPlugin { Name = RustArchonPlugin.UpdaterName, Version = updaterVersion, RustServerId = ServerId, TenantId = TenantId }
        ]);
    }

    [Fact]
    public async Task AServerOnARetiredKeyIsUpdatedAndItsTokenRemembersThatKey()
    {
        GivenServerOnKey(OldKey, PluginKeyState.Retired);

        var result = await Create().StartAsync(Server());

        Assert.True(result.Started);
        _tokens.Verify(t => t.MintAsync(TenantId, ServerId, OldKey, PluginUpdateService.TokenLifetime), Times.Once);
    }

    [Fact]
    public async Task TheFingerprintStoredOnTheTokenIsLowerCase()
    {
        GivenServerOnKey(OldKey.ToUpperInvariant(), PluginKeyState.Retired);

        await Create().StartAsync(Server());

        _tokens.Verify(t => t.MintAsync(TenantId, ServerId, OldKey, PluginUpdateService.TokenLifetime), Times.Once);
    }

    [Theory]
    [InlineData("v0.1.0")]
    [InlineData("v0.1.9")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("junk")]
    public async Task AKeyChangeNeedsAnUpdaterThatCanBridgeAndAnOlderOneIsToldToUpdateByHand(string? updaterVersion)
    {
        GivenServerOnKey(OldKey, PluginKeyState.Retired, updaterVersion!);

        var result = await Create().StartAsync(Server());

        Assert.Equal("updater_too_old", result.Code);
        Assert.False(result.Started);
        AssertNothingHappened();
    }

    [Theory]
    [InlineData("v0.2.0")]
    [InlineData("0.2.0")]
    [InlineData("v0.3.1")]
    [InlineData("v1.0.0")]
    public async Task AnUpdaterFromZeroPointTwoOnCanBridge(string updaterVersion)
    {
        GivenServerOnKey(OldKey, PluginKeyState.Retired, updaterVersion);

        Assert.True((await Create().StartAsync(Server())).Started);
    }

    [Fact]
    public async Task AnOldUpdaterIsFineWhenNoKeyChangeIsNeeded()
    {
        GivenServerOnKey(PanelKey, PluginKeyState.Active, "v0.1.0");

        Assert.True((await Create().StartAsync(Server())).Started);
    }

    [Fact]
    public async Task AServerOnARevokedKeyCannotBeUpdatedFromHere()
    {
        GivenServerOnKey(OldKey, PluginKeyState.Revoked);

        var result = await Create().StartAsync(Server());

        Assert.Equal("key_revoked", result.Code);
        Assert.Contains("by hand", result.Message);
        AssertNothingHappened();
    }

    [Fact]
    public async Task AServerOnAKeyThisPanelNeverHadCannotBeUpdated()
    {
        GivenServerOnKey("bbbbbbbbbbbbbbbb", null);

        Assert.Equal("not_signed_by_this_panel", (await Create().StartAsync(Server())).Code);
        AssertNothingHappened();
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("unsigned")]
    public async Task ARetiredKeyDoesNotHelpAPluginWhoseSignatureIsNotValid(string state)
    {
        GivenServerOnKey(OldKey, PluginKeyState.Retired);
        GivenStatus(state: state, fingerprint: OldKey);

        Assert.Equal("not_signed_by_this_panel", (await Create().StartAsync(Server())).Code);
        AssertNothingHappened();
    }
}
