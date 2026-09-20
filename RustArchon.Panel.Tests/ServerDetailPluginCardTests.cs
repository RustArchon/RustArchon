// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Bunit;
using JumpStart.Repositories;
using JumpStart.Services.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using Refit;
using RustArchon.Messaging.Contracts;
using RustArchon.Panel.Clients;
using RustArchon.Panel.Components.Pages.Servers;
using RustArchon.Panel.Infrastructure;
using RustArchon.Panel.Localization;
using RustArchon.Panel.Services;
using RustArchon.Shared.DTOs;

namespace RustArchon.Panel.Tests;

/// <summary>
/// The RustArchon-plugin card at the top of <c>ServerDetail</c>'s Plugins tab. The rule under test is fail
/// closed: the switches are offered only when the plugin is positively in the server's plugin list <em>and</em> has
/// answered its handshake <em>and</em> reported the <c>config</c> capability, and a feature claims to work only
/// when its own capability was reported.
/// </summary>
public class ServerDetailPluginCardTests : BunitContext
{
    private readonly Mock<IRustServerApiClient> _client = new();
    private readonly Guid _serverId = Guid.NewGuid();
    private RustServerDto _server;

    public ServerDetailPluginCardTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        _server = new RustServerDto
        {
            Id = _serverId,
            Name = "Test Server",
            Host = "127.0.0.1",
            Port = 28015,
            ConnectionStatus = RconConnectionStatus.Connected,
            PluginRecordingEnabled = true,
            PluginCombatLogEnabled = true
        };

        _client.Setup(c => c.GetByIdAsync(_serverId)).ReturnsAsync(() => _server);
        _client
            .Setup(c => c.GetEventsAsync(
                _serverId, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool?>(),
                It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<bool>()))
            .ReturnsAsync(new PagedResult<RconEventDto> { Items = [] });
        _client.Setup(c => c.GetCurrentPlayersAsync(_serverId)).ReturnsAsync([]);
        _client
            .Setup(c => c.GetKillsAsync(
                _serverId, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>()))
            .ReturnsAsync(new PagedResult<PlayerKillEventDto> { Items = [] });
        _client
            .Setup(c => c.GetInactivePlayersAsync(_serverId, It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new PagedResult<InactivePlayerDto> { Items = [] });
        _client
            .Setup(c => c.GetServerInfoHistoryAsync(_serverId, It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>()))
            .ReturnsAsync([]);
        _client
            .Setup(c => c.GetConnectionLogAsync(_serverId, It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>()))
            .ReturnsAsync([]);
        _client
            .Setup(c => c.SendCommandAsync(_serverId, It.IsAny<SendCommandRequest>()))
            .ReturnsAsync(new RconCommandResult(false, null, null, null, null));
        _client.Setup(c => c.GetPluginsAsync(_serverId)).ReturnsAsync([]);
        GivenNoHandshake();

        var hubClient = new Mock<IRconHubClient>();
        hubClient.Setup(h => h.ConnectAsync(_serverId)).Returns(Task.CompletedTask);

        var tokenStore = new Mock<ITokenStore>();
        tokenStore.Setup(t => t.GetToken()).Returns((string?)null);

        var valkeyCache = new Mock<IValkeyCache>();
        valkeyCache.Setup(c => c.GetStringAsync(It.IsAny<string>())).ReturnsAsync("RustArchon");

        var localizer = new Mock<IStringLocalizer<SharedResource>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string key) => new LocalizedString(key, key));

        Services.AddSingleton(_client.Object);
        Services.AddSingleton(hubClient.Object);
        Services.AddSingleton(tokenStore.Object);
        Services.AddSingleton(new SiteBrandingService(valkeyCache.Object, new Mock<ISiteBrandingApiClient>().Object));
        Services.AddSingleton(localizer.Object);

        AddAuthorization().SetAuthorized("test-admin");
    }

    private IRenderedComponent<ServerDetail> RenderPluginsTab()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/servers/{_serverId}?tab=plugins");
        return Render<ServerDetail>(parameters => parameters.Add(p => p.Id, _serverId));
    }

    private void GivenPluginListed(string name = "RustArchon") =>
        _client.Setup(c => c.GetPluginsAsync(_serverId)).ReturnsAsync(
            [new ServerPluginDto { Name = name, Author = "RustArchon", Version = "0.1.0", Framework = ServerModFramework.Carbon, CapturedAtUtc = DateTimeOffset.UtcNow }]);

    /// <summary>The Api's 204: the plugin has never answered.</summary>
    private void GivenNoHandshake() =>
        _client.Setup(c => c.GetPluginStatusAsync(_serverId)).ReturnsAsync(
            Mock.Of<IApiResponse<ServerPluginStatusDto>>(r => r.IsSuccessStatusCode == true && r.Content == null));

    // What the next GivenHandshake reports about the installed file's signature. Defaults to "the plugin did not say",
    // which is what the pre-signing tests above assume.
    private (string State, string Fingerprint, bool? MatchesThisPanel) _signing = ("unknown", "", null);

    private void GivenSigning(string state, string fingerprint = "0123456789abcdef", bool? matchesThisPanel = null) =>
        _signing = (state, fingerprint, matchesThisPanel);

    // What the next GivenHandshake says about updating: is a newer version on offer, is the Updater plugin installed.
    private (bool Available, bool UpdaterInstalled, string Latest) _updates = (false, false, "0.1.0");

    // Where the installed plugin's key stands in the Panel's key history ("active", "retired", "revoked", or empty).
    private string _keyState = "";

    // The installed Updater's version and the one the Panel serves (empty when the test does not care).
    private (string? Installed, string? Latest, bool Newer) _updaterVersions = (null, null, false);

    private void GivenUpdaterVersions(string? installed, string? latest, bool newer) => _updaterVersions = (installed, latest, newer);

    private void GivenKeyState(string state) => _keyState = state;

    private void GivenUpdates(bool available, bool updaterInstalled, string latest = "0.2.1") =>
        _updates = (available, updaterInstalled, latest);

    private void GivenHandshake(
        bool recording = true, bool combat = true, bool persisted = true, params string[] capabilities)
    {
        var dto = new ServerPluginStatusDto
        {
            ProtocolVersion = 1,
            PluginVersion = "0.1.0",
            Capabilities = capabilities.Length == 0 ? [RustArchonPlugin.ConfigCapability] : capabilities,
            RecordingEnabled = recording,
            CombatLogEnabled = combat,
            SettingsPersisted = persisted,
            SigningState = _signing.State,
            SigningKeyFingerprint = _signing.Fingerprint,
            SigningKeyMatchesThisPanel = _signing.MatchesThisPanel,
            SigningKeyState = _keyState,
            UpdaterVersion = _updaterVersions.Installed,
            LatestUpdaterVersion = _updaterVersions.Latest,
            UpdaterUpdateAvailable = _updaterVersions.Newer,
            UpdateAvailable = _updates.Available,
            UpdaterInstalled = _updates.UpdaterInstalled,
            LatestPluginVersion = _updates.Latest,
            CapturedAtUtc = DateTimeOffset.UtcNow
        };
        var response = new Mock<IApiResponse<ServerPluginStatusDto>>();
        response.SetupGet(r => r.IsSuccessStatusCode).Returns(true);
        response.SetupGet(r => r.Content).Returns(dto);
        _client.Setup(c => c.GetPluginStatusAsync(_serverId)).ReturnsAsync(response.Object);
    }

    private static string CardText(IRenderedComponent<ServerDetail> cut) => cut.Find(".rustarchon-plugin-card").TextContent;

    // ---- fail closed ---------------------------------------------------------------------------------------

    [Fact]
    public void PluginNotInTheList_SaysNotDetectedAndOffersNoSwitches()
    {
        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-not-detected]")));
        Assert.Empty(cut.FindAll("#plugin-recording"));
        Assert.Empty(cut.FindAll("#plugin-combat"));
    }

    [Fact]
    public void AStaleHandshakeIsIgnoredWhenThePluginIsNoLongerListed()
    {
        // The plugin was uninstalled; the stored handshake lingers. It must not be trusted.
        GivenHandshake();

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-not-detected]")));
        Assert.Empty(cut.FindAll("#plugin-recording"));
        Assert.Empty(cut.FindAll("[data-testid=plugin-version]"));
    }

    [Fact]
    public void ALookalikePluginNameDoesNotCount()
    {
        GivenPluginListed("RustArchonHelper");
        GivenHandshake();

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-not-detected]")));
        Assert.Empty(cut.FindAll("#plugin-recording"));
    }

    [Fact]
    public void ListedButNeverAnswered_SaysSoAndOffersNoSwitches()
    {
        GivenPluginListed();
        GivenNoHandshake();

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-no-handshake]")));
        Assert.Empty(cut.FindAll("#plugin-recording"));
        Assert.Empty(cut.FindAll("#plugin-combat"));
    }

    [Fact]
    public void AFailedStatusFetchLeavesEveryPluginFeatureOff()
    {
        GivenPluginListed();
        _client.Setup(c => c.GetPluginStatusAsync(_serverId)).ThrowsAsync(new InvalidOperationException("boom"));

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-no-handshake]")));
        Assert.Empty(cut.FindAll("#plugin-recording"));
    }

    [Fact]
    public void APluginThatDidNotReportConfig_CannotBeConfigured()
    {
        GivenPluginListed();
        GivenHandshake(capabilities: ["recording", "combat"]); // no "config"

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-not-configurable]")));
        Assert.Empty(cut.FindAll("#plugin-recording"));
        Assert.Empty(cut.FindAll("#plugin-combat"));
    }

    // ---- what it shows -------------------------------------------------------------------------------------

    [Fact]
    public void AnAnsweringPlugin_ShowsItsVersionAndBothSwitchesAtTheirSavedValues()
    {
        _server.PluginRecordingEnabled = true;
        _server.PluginCombatLogEnabled = false;
        GivenPluginListed();
        GivenHandshake(recording: true, combat: false);

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.Contains("0.1.0", cut.Find("[data-testid=plugin-version]").TextContent));
        Assert.True(cut.Find("#plugin-recording").HasAttribute("checked"));
        Assert.False(cut.Find("#plugin-combat").HasAttribute("checked"));
    }

    [Fact]
    public void AFeatureIsMarkedUnavailableUntilThePluginReportsItsCapability()
    {
        GivenPluginListed();
        GivenHandshake(capabilities: ["config"]); // configurable, but no recording/combat code yet

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll("[data-testid=plugin-recording-unavailable]"));
            Assert.NotEmpty(cut.FindAll("[data-testid=plugin-combat-unavailable]"));
        });
    }

    [Fact]
    public void AFeatureIsNotMarkedUnavailableOnceThePluginReportsItsCapability()
    {
        GivenPluginListed();
        GivenHandshake(capabilities: ["config", "recording", "combat"]);

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#plugin-recording")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-recording-unavailable]"));
        Assert.Empty(cut.FindAll("[data-testid=plugin-combat-unavailable]"));
    }

    [Fact]
    public void ShowsApplyingWhileThePluginStillReportsTheOldValue()
    {
        _server.PluginRecordingEnabled = false; // saved: off
        GivenPluginListed();
        GivenHandshake(recording: true); // plugin still says on

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-recording-pending]")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-combat-pending]"));
    }

    [Fact]
    public void NoApplyingNoteWhenThePluginMatchesTheSavedValues()
    {
        GivenPluginListed();
        GivenHandshake(recording: true, combat: true);

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#plugin-recording")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-recording-pending]"));
        Assert.Empty(cut.FindAll("[data-testid=plugin-combat-pending]"));
    }

    [Fact]
    public void WarnsWhenThePluginCouldNotPersistItsSettings()
    {
        GivenPluginListed();
        GivenHandshake(persisted: false);

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-not-persisted]")));
    }

    // ---- saving --------------------------------------------------------------------------------------------

    [Fact]
    public void TurningRecordingOffSavesBothValuesAndKeepsTheOtherSwitchAsItWas()
    {
        GivenPluginListed();
        GivenHandshake();
        UpdateSettingsTypedArgs? captured = null;
        _client.Setup(c => c.UpdatePluginSettingsAsync(_serverId, It.IsAny<UpdateServerPluginSettingsDto>()))
            .Callback<Guid, UpdateServerPluginSettingsDto>((_, s) => captured = new(s.RecordingEnabled, s.CombatLogEnabled))
            .ReturnsAsync((Guid _, UpdateServerPluginSettingsDto s) =>
            {
                _server = new RustServerDto
                {
                    Id = _serverId, Name = "Test Server", Host = "127.0.0.1", Port = 28015,
                    ConnectionStatus = RconConnectionStatus.Connected,
                    PluginRecordingEnabled = s.RecordingEnabled!.Value,
                    PluginCombatLogEnabled = s.CombatLogEnabled!.Value
                };
                return _server;
            });
        var cut = RenderPluginsTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#plugin-recording")));

        cut.Find("#plugin-recording").Change(false);

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(captured);
            Assert.False(captured!.Recording);
            Assert.True(captured.Combat); // untouched, but still sent: the endpoint requires both
        });
    }

    [Fact]
    public void TurningCombatLogOffSavesItAndKeepsRecording()
    {
        GivenPluginListed();
        GivenHandshake();
        UpdateSettingsTypedArgs? captured = null;
        _client.Setup(c => c.UpdatePluginSettingsAsync(_serverId, It.IsAny<UpdateServerPluginSettingsDto>()))
            .Callback<Guid, UpdateServerPluginSettingsDto>((_, s) => captured = new(s.RecordingEnabled, s.CombatLogEnabled))
            .ReturnsAsync(_server);
        var cut = RenderPluginsTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#plugin-combat")));

        cut.Find("#plugin-combat").Change(false);

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(captured);
            Assert.True(captured!.Recording);
            Assert.False(captured.Combat);
        });
    }

    [Fact]
    public void AFailedSaveShowsAnErrorInsteadOfPretendingItWorked()
    {
        GivenPluginListed();
        GivenHandshake();
        _client.Setup(c => c.UpdatePluginSettingsAsync(_serverId, It.IsAny<UpdateServerPluginSettingsDto>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var cut = RenderPluginsTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#plugin-recording")));

        cut.Find("#plugin-recording").Change(false);

        cut.WaitForAssertion(() =>
            Assert.Contains("Failed to save the plugin settings.", cut.Find("[data-testid=plugin-settings-error]").TextContent));
    }

    // ---- signing ---------------------------------------------------------------------------------------------

    [Fact]
    public void SignedByThisPanel_SaysSoWithTheKeyAndOffersNoDownload()
    {
        GivenPluginListed();
        GivenSigning("valid", "0123456789abcdef", matchesThisPanel: true);
        GivenHandshake();

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() =>
        {
            var line = cut.Find("[data-testid=plugin-signing-ok]").TextContent;
            Assert.Contains("Signed by this Panel", line);
            Assert.Contains("0123456789abcdef", line);
        });
        Assert.Empty(cut.FindAll("[data-testid=plugin-download]"));
    }

    [Fact]
    public void SignedByADifferentPanel_WarnsThatItsUpdatesWillBeRefusedAndOffersTheDownload()
    {
        GivenPluginListed();
        GivenSigning("valid", "fedcba9876543210", matchesThisPanel: false);
        GivenHandshake();

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("different Panel", cut.Find("[data-testid=plugin-signing-other-panel]").TextContent);
            Assert.NotEmpty(cut.FindAll("[data-testid=plugin-download]"));
        });
        Assert.Empty(cut.FindAll("[data-testid=plugin-signing-ok]"));
    }

    [Fact]
    public void ValidButNotComparableToThisPanel_IsShownPlainlyWithoutClaimingItIsThisPanels()
    {
        GivenPluginListed();
        GivenSigning("valid", "0123456789abcdef", matchesThisPanel: null);
        GivenHandshake();

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-signing-valid-unmatched]")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-signing-ok]"));
        Assert.Empty(cut.FindAll("[data-testid=plugin-download]"));
    }

    [Fact]
    public void AnInvalidSignature_SaysTheFileWasAlteredAndOffersTheDownload()
    {
        GivenPluginListed();
        GivenSigning("invalid", "0123456789abcdef");
        GivenHandshake();

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("does not match its signature", cut.Find("[data-testid=plugin-signing-invalid]").TextContent);
            Assert.NotEmpty(cut.FindAll("[data-testid=plugin-download]"));
        });
    }

    [Fact]
    public void AnUnsignedCopy_SaysSoAndOffersTheDownload()
    {
        GivenPluginListed();
        GivenSigning("unsigned", "");
        GivenHandshake();

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Unsigned copy", cut.Find("[data-testid=plugin-signing-unsigned]").TextContent);
            Assert.NotEmpty(cut.FindAll("[data-testid=plugin-download]"));
        });
    }

    [Fact]
    public void ABuildThatPredatesSigning_IsToldToDownloadTheCurrentOne()
    {
        GivenPluginListed();
        GivenSigning("unknown", "");
        GivenHandshake();

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("does not report its signature", cut.Find("[data-testid=plugin-signing-predates]").TextContent);
            Assert.NotEmpty(cut.FindAll("[data-testid=plugin-download]"));
        });
        Assert.Empty(cut.FindAll("[data-testid=plugin-signing-unchecked]"));
    }

    [Theory]
    [InlineData("unlocated")]
    [InlineData("error")]
    public void WhenTheSignatureCouldNotBeChecked_SaysNotCheckedAndDoesNotPushADownload(string state)
    {
        GivenPluginListed();
        GivenSigning(state, "");
        GivenHandshake();

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-signing-unchecked]")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-signing-ok]"));
        Assert.Empty(cut.FindAll("[data-testid=plugin-download]"));
    }

    [Fact]
    public void ThePluginNotBeingInstalledOffersTheDownload()
    {
        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-not-detected]")));
        Assert.NotEmpty(cut.FindAll("[data-testid=plugin-download]"));
    }

    [Fact]
    public void AStaleSigningStateIsIgnoredWhenThePluginIsNoLongerListed()
    {
        // Uninstalled: the stored handshake lingers with "valid / this Panel". It must not be shown.
        GivenSigning("valid", "0123456789abcdef", matchesThisPanel: true);
        GivenHandshake();

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-not-detected]")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-signing-ok]"));
    }

    // ---- downloading -----------------------------------------------------------------------------------------

    private static HttpResponseMessage Download(byte[] bytes, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new ByteArrayContent(bytes) };

    [Fact]
    public void ClickingDownload_HandsTheExactBytesToTheBrowserUntouched()
    {
        // CRLF, a byte-order mark, a lone LF and non-ASCII: anything a "helpful" layer might normalize. The
        // signature covers these exact bytes, so every one must survive.
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, 0x0D, 0x0A, 0x0A, 0xC3, 0xA9, 0x00, 0xFF, 0x2F, 0x2F };
        _client.Setup(c => c.DownloadPluginAsync()).ReturnsAsync(() => Download(bytes));
        var module = JSInterop.SetupModule("./js/fileDownload.js");
        module.SetupVoid("downloadBytes", _ => true).SetVoidResult();
        var cut = RenderPluginsTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-download]")));

        cut.Find("[data-testid=plugin-download]").Click();

        cut.WaitForAssertion(() =>
        {
            var call = Assert.Single(module.Invocations["downloadBytes"]);
            Assert.Equal("RustArchon.cs", call.Arguments[0]);
            Assert.Equal(Convert.ToBase64String(bytes), call.Arguments[1]);
            Assert.Equal("text/plain", call.Arguments[2]);
        });
        Assert.Empty(cut.FindAll("[data-testid=plugin-download-error]"));
    }

    [Fact]
    public void AnErrorStatusFromTheApiShowsAnErrorAndDownloadsNothing()
    {
        _client.Setup(c => c.DownloadPluginAsync()).ReturnsAsync(() => Download([1, 2, 3], HttpStatusCode.ServiceUnavailable));
        var module = JSInterop.SetupModule("./js/fileDownload.js");
        module.SetupVoid("downloadBytes", _ => true).SetVoidResult();
        GivenPluginListed();
        GivenSigning("unsigned", "");
        GivenHandshake();
        var cut = RenderPluginsTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-download]")));

        cut.Find("[data-testid=plugin-download]").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("Failed to download the plugin.", cut.Find("[data-testid=plugin-download-error]").TextContent));
        module.VerifyNotInvoke("downloadBytes");
    }

    [Fact]
    public void AThrownErrorShowsAnErrorInsteadOfCrashingThePage()
    {
        _client.Setup(c => c.DownloadPluginAsync()).ThrowsAsync(new HttpRequestException("boom"));
        GivenPluginListed();
        GivenSigning("unsigned", "");
        GivenHandshake();
        var cut = RenderPluginsTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-download]")));

        cut.Find("[data-testid=plugin-download]").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("Failed to download the plugin.", cut.Find("[data-testid=plugin-download-error]").TextContent));
    }

    // ---- updating ------------------------------------------------------------------------------------------

    private void GivenSignedByThisPanel() => GivenSigning("valid", "0123456789abcdef", matchesThisPanel: true);

    [Fact]
    public void NoUpdateControlsAreOfferedForAPluginThisPanelDidNotSign()
    {
        GivenPluginListed();
        GivenSigning("valid", "fedcba9876543210", matchesThisPanel: false);
        GivenUpdates(available: true, updaterInstalled: true);
        _server.PluginUpdatesEnabled = true;
        GivenHandshake();

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-signing-other-panel]")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-updates]"));
        Assert.Empty(cut.FindAll("[data-testid=plugin-update]"));
    }

    [Theory]
    [InlineData("unsigned")]
    [InlineData("invalid")]
    [InlineData("unknown")]
    public void NoUpdateControlsAreOfferedForAPluginWhoseSignatureIsNotValid(string state)
    {
        GivenPluginListed();
        GivenSigning(state, "");
        GivenUpdates(available: true, updaterInstalled: true);
        _server.PluginUpdatesEnabled = true;
        GivenHandshake();

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-version]")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-updates]"));
    }

    [Fact]
    public void ThePluginIsNotOfferedAnUpdateWhenTheHandshakeIsMissing()
    {
        GivenPluginListed();
        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-no-handshake]")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-updates]"));
    }

    [Fact]
    public void WithoutTheUpdater_SaysSoAndOffersItsDownloadButNoUpdateButton()
    {
        GivenPluginListed();
        GivenSignedByThisPanel();
        GivenUpdates(available: true, updaterInstalled: false);
        _server.PluginUpdatesEnabled = true;
        GivenHandshake();

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll("[data-testid=plugin-updater-missing]"));
            Assert.NotEmpty(cut.FindAll("[data-testid=plugin-download-updater]"));
        });
        Assert.Empty(cut.FindAll("[data-testid=plugin-update]"));
    }

    [Fact]
    public void WithUpdatesTurnedOff_ShowsTheOfferButNoUpdateButton()
    {
        GivenPluginListed();
        GivenSignedByThisPanel();
        GivenUpdates(available: true, updaterInstalled: true);
        _server.PluginUpdatesEnabled = false;
        GivenHandshake();

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.Contains("0.1.0", cut.Find("[data-testid=plugin-update-available]").TextContent));
        Assert.Contains("0.2.1", cut.Find("[data-testid=plugin-update-available]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=plugin-update]"));
        Assert.False(cut.Find("#plugin-updates").HasAttribute("checked"));
    }

    [Fact]
    public void WhenEverythingIsInPlace_TheUpdateButtonAppears()
    {
        GivenPluginListed();
        GivenSignedByThisPanel();
        GivenUpdates(available: true, updaterInstalled: true);
        _server.PluginUpdatesEnabled = true;
        GivenHandshake();

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-update]")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-updater-missing]"));
    }

    [Fact]
    public void WhenNothingIsNewer_SaysUpToDateAndOffersNoButton()
    {
        GivenPluginListed();
        GivenSignedByThisPanel();
        GivenUpdates(available: false, updaterInstalled: true);
        _server.PluginUpdatesEnabled = true;
        GivenHandshake();

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-up-to-date]")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-update]"));
        Assert.Empty(cut.FindAll("[data-testid=plugin-update-available]"));
    }

    [Fact]
    public void ClickingUpdate_AsksTheApiAndSaysItStarted()
    {
        GivenPluginListed();
        GivenSignedByThisPanel();
        GivenUpdates(available: true, updaterInstalled: true);
        _server.PluginUpdatesEnabled = true;
        GivenHandshake();
        _client.Setup(c => c.StartPluginUpdateAsync(_serverId))
            .ReturnsAsync(new PluginUpdateResultDto { Started = true, Code = "started" });
        var cut = RenderPluginsTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-update]")));

        cut.Find("[data-testid=plugin-update]").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("Update started", cut.Find("[data-testid=plugin-update-result]").TextContent));
        _client.Verify(c => c.StartPluginUpdateAsync(_serverId), Times.Once);
        Assert.Contains("text-success", cut.Find("[data-testid=plugin-update-result]").ClassName);
    }

    [Theory]
    [InlineData("updates_disabled", "turned off")]
    [InlineData("not_signed_by_this_panel", "not signed by this Panel")]
    [InlineData("updater_missing", "Updater is not installed")]
    [InlineData("up_to_date", "Nothing newer")]
    [InlineData("panel_url_invalid", "public address")]
    [InlineData("not_connected", "not connected")]
    [InlineData("timeout", "did not answer")]
    [InlineData("busy", "already in progress")]
    [InlineData("something_unexpected", "could not be started")]
    public void ARefusalIsShownAsAPlainSentenceNotACode(string code, string expected)
    {
        GivenPluginListed();
        GivenSignedByThisPanel();
        GivenUpdates(available: true, updaterInstalled: true);
        _server.PluginUpdatesEnabled = true;
        GivenHandshake();
        _client.Setup(c => c.StartPluginUpdateAsync(_serverId))
            .ReturnsAsync(new PluginUpdateResultDto { Started = false, Code = code, Message = "raw api text" });
        var cut = RenderPluginsTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-update]")));

        cut.Find("[data-testid=plugin-update]").Click();

        cut.WaitForAssertion(() =>
        {
            var text = cut.Find("[data-testid=plugin-update-result]").TextContent;
            Assert.Contains(expected, text);
            Assert.DoesNotContain("raw api text", text);
        });
        Assert.Contains("text-danger", cut.Find("[data-testid=plugin-update-result]").ClassName);
    }

    [Fact]
    public void AThrownErrorOnUpdateIsShownInsteadOfCrashingThePage()
    {
        GivenPluginListed();
        GivenSignedByThisPanel();
        GivenUpdates(available: true, updaterInstalled: true);
        _server.PluginUpdatesEnabled = true;
        GivenHandshake();
        _client.Setup(c => c.StartPluginUpdateAsync(_serverId)).ThrowsAsync(new HttpRequestException("boom"));
        var cut = RenderPluginsTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-update]")));

        cut.Find("[data-testid=plugin-update]").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("could not be started", cut.Find("[data-testid=plugin-update-result]").TextContent));
    }

    [Fact]
    public void TogglingUpdatesSavesItAndKeepsTheOtherTwoSwitchesAtTheirSavedValues()
    {
        GivenPluginListed();
        GivenSignedByThisPanel();
        GivenUpdates(available: false, updaterInstalled: true);
        _server.PluginRecordingEnabled = true;
        _server.PluginCombatLogEnabled = false;
        _server.PluginUpdatesEnabled = false;
        GivenHandshake();
        UpdateServerPluginSettingsDto? sent = null;
        _client.Setup(c => c.UpdatePluginSettingsAsync(_serverId, It.IsAny<UpdateServerPluginSettingsDto>()))
            .Callback<Guid, UpdateServerPluginSettingsDto>((_, s) => sent = s)
            .ReturnsAsync((Guid _, UpdateServerPluginSettingsDto s) =>
            {
                _server = new RustServerDto
                {
                    Id = _serverId, Name = "Test Server", Host = "127.0.0.1", Port = 28015,
                    ConnectionStatus = RconConnectionStatus.Connected,
                    PluginRecordingEnabled = s.RecordingEnabled!.Value,
                    PluginCombatLogEnabled = s.CombatLogEnabled!.Value,
                    PluginUpdatesEnabled = s.UpdatesEnabled!.Value
                };
                return _server;
            });
        var cut = RenderPluginsTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#plugin-updates")));

        cut.Find("#plugin-updates").Change(true);

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.True(sent!.UpdatesEnabled);
        Assert.True(sent.RecordingEnabled);
        Assert.False(sent.CombatLogEnabled);
    }

    [Fact]
    public void TogglingRecordingKeepsUpdatesAtItsSavedValue()
    {
        GivenPluginListed();
        GivenSignedByThisPanel();
        _server.PluginUpdatesEnabled = true;
        GivenHandshake();
        UpdateServerPluginSettingsDto? sent = null;
        _client.Setup(c => c.UpdatePluginSettingsAsync(_serverId, It.IsAny<UpdateServerPluginSettingsDto>()))
            .Callback<Guid, UpdateServerPluginSettingsDto>((_, s) => sent = s)
            .ReturnsAsync(_server);
        var cut = RenderPluginsTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#plugin-recording")));

        cut.Find("#plugin-recording").Change(false);

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.False(sent!.RecordingEnabled);
        Assert.True(sent.UpdatesEnabled);
    }

    [Fact]
    public void ClickingDownloadUpdater_HandsTheExactBytesToTheBrowserUnderTheUpdatersName()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, 0x0D, 0x0A, 0x0A, 0xC3, 0xA9, 0x00, 0xFF };
        _client.Setup(c => c.DownloadPluginUpdaterAsync()).ReturnsAsync(() => Download(bytes));
        var module = JSInterop.SetupModule("./js/fileDownload.js");
        module.SetupVoid("downloadBytes", _ => true).SetVoidResult();
        GivenPluginListed();
        GivenSignedByThisPanel();
        GivenUpdates(available: false, updaterInstalled: false);
        GivenHandshake();
        var cut = RenderPluginsTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-download-updater]")));

        cut.Find("[data-testid=plugin-download-updater]").Click();

        cut.WaitForAssertion(() =>
        {
            var call = Assert.Single(module.Invocations["downloadBytes"]);
            Assert.Equal("RustArchonUpdater.cs", call.Arguments[0]);
            Assert.Equal(Convert.ToBase64String(bytes), call.Arguments[1]);
        });
        _client.Verify(c => c.DownloadPluginAsync(), Times.Never);
    }

    // ---- retired and revoked keys --------------------------------------------------------------------------

    [Fact]
    public void ARetiredKeyIsStillUpdatableAndSaysTheUpdateMovesItToTheCurrentKey()
    {
        GivenPluginListed();
        GivenSigning("valid", "aaaaaaaaaaaaaaaa", matchesThisPanel: false); // not the ACTIVE key...
        GivenKeyState("retired");                                            // ...but an earlier key of this Panel
        GivenUpdates(available: true, updaterInstalled: true);
        _server.PluginUpdatesEnabled = true;
        GivenHandshake();

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("earlier key", cut.Find("[data-testid=plugin-signing-retired]").TextContent);
            Assert.NotEmpty(cut.FindAll("[data-testid=plugin-update]"));
        });
        Assert.Empty(cut.FindAll("[data-testid=plugin-signing-other-panel]")); // NOT the "different Panel" warning
        Assert.Empty(cut.FindAll("[data-testid=plugin-download]"));
    }

    [Fact]
    public void ARevokedKeyOffersNoUpdateAndTheManualDownload()
    {
        GivenPluginListed();
        GivenSigning("valid", "aaaaaaaaaaaaaaaa", matchesThisPanel: false);
        GivenKeyState("revoked");
        GivenUpdates(available: true, updaterInstalled: true);
        _server.PluginUpdatesEnabled = true;
        GivenHandshake();

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("revoked", cut.Find("[data-testid=plugin-signing-revoked]").TextContent);
            Assert.NotEmpty(cut.FindAll("[data-testid=plugin-download]"));
        });
        Assert.Empty(cut.FindAll("[data-testid=plugin-updates]"));
        Assert.Empty(cut.FindAll("[data-testid=plugin-update]"));
    }

    [Fact]
    public void AKeyThisPanelNeverHadStillGetsTheDifferentPanelWarningAndNoUpdate()
    {
        GivenPluginListed();
        GivenSigning("valid", "ffffffffffffffff", matchesThisPanel: false);
        GivenKeyState(""); // not one of this Panel's keys
        GivenUpdates(available: true, updaterInstalled: true);
        _server.PluginUpdatesEnabled = true;
        GivenHandshake();

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-signing-other-panel]")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-updates]"));
    }

    [Theory]
    [InlineData("updater_too_old", "newer Updater")]
    [InlineData("key_revoked", "has been revoked")]
    public void TheNewRefusalsAreShownAsPlainSentences(string code, string expected)
    {
        GivenPluginListed();
        GivenSignedByThisPanel();
        GivenUpdates(available: true, updaterInstalled: true);
        _server.PluginUpdatesEnabled = true;
        GivenHandshake();
        _client.Setup(c => c.StartPluginUpdateAsync(_serverId))
            .ReturnsAsync(new PluginUpdateResultDto { Started = false, Code = code, Message = "raw api text" });
        var cut = RenderPluginsTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-update]")));

        cut.Find("[data-testid=plugin-update]").Click();

        cut.WaitForAssertion(() => Assert.Contains(expected, cut.Find("[data-testid=plugin-update-result]").TextContent));
    }

    // ---- an outdated Updater -------------------------------------------------------------------------------

    [Fact]
    public void ANewerUpdaterOnOfferIsSaidToBeInstalledByHandWithADownload()
    {
        GivenPluginListed();
        GivenSignedByThisPanel();
        GivenUpdates(available: false, updaterInstalled: true);
        GivenUpdaterVersions("0.1.0", "0.2.0", newer: true);
        GivenHandshake();

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() =>
        {
            var text = cut.Find("[data-testid=plugin-updater-outdated]").TextContent;
            Assert.Contains("0.1.0", text);
            Assert.Contains("0.2.0", text);
            Assert.Contains("by hand", text);
            Assert.NotEmpty(cut.FindAll("[data-testid=plugin-download-updater]"));
        });
    }

    [Fact]
    public void AnUpToDateUpdaterShowsNoNoticeAndNoDownload()
    {
        GivenPluginListed();
        GivenSignedByThisPanel();
        GivenUpdates(available: false, updaterInstalled: true);
        GivenUpdaterVersions("0.2.0", "0.2.0", newer: false);
        GivenHandshake();

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-up-to-date]")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-updater-outdated]"));
        Assert.Empty(cut.FindAll("[data-testid=plugin-download-updater]"));
    }

    // ---- the Combat tab -----------------------------------------------------------------------------------

    private IRenderedComponent<ServerDetail> RenderCombatTab()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/servers/{_serverId}?tab=combat");
        return Render<ServerDetail>(parameters => parameters.Add(p => p.Id, _serverId));
    }

    private void GivenCombatCapable() => GivenHandshake(capabilities: [RustArchonPlugin.ConfigCapability, RustArchonPlugin.CombatCapability]);

    private static CombatEventDto Hit(long seq, string attacker = "76561198000000001", string attackerName = "Alice", string victim = "76561198000000002",
        string victimName = "Bob", string kind = "hit", bool headshot = false, double? distance = 42.5, bool attackerIsPlayer = true, bool victimIsPlayer = true) => new()
    {
        Sequence = seq, OccurredAtUtc = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero).AddSeconds(seq), Kind = kind,
        AttackerId = attacker, AttackerName = attackerName, AttackerIsPlayer = attackerIsPlayer,
        VictimId = victim, VictimName = victimName, VictimIsPlayer = victimIsPlayer,
        Weapon = "rifle.ak", Damage = 40.5, DamageType = "Bullet", Headshot = headshot, Distance = distance
    };

    private void GivenCombatLog(params CombatEventDto[] events) =>
        _client.Setup(c => c.GetCombatLogAsync(_serverId, It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<int>()))
            .ReturnsAsync(new CombatLogDto { Events = [.. events] });

    [Fact]
    public void TheCombatTabIsAlwaysOfferedSoAServerWithoutThePluginIsToldWhatItNeeds()
    {
        var cut = RenderPluginsTab();

        Assert.NotEmpty(cut.FindAll("[data-testid=tab-combat]"));
    }

    [Fact]
    public void WithoutThePluginTheCombatTabSaysSoAndAsksForNothing()
    {
        var cut = RenderCombatTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=combat-needs-plugin]")));
        Assert.Empty(cut.FindAll("[data-testid=combat-table]"));
        _client.Verify(c => c.GetCombatLogAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void AListedPluginThatHasNotAnsweredGetsTheWaitMessageAndNoQuery()
    {
        GivenPluginListed();

        var cut = RenderCombatTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=combat-no-handshake]")));
        _client.Verify(c => c.GetCombatLogAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void APluginThatDoesNotReportTheCombatCapabilityIsToldToUpdateAndNothingIsQueried()
    {
        GivenPluginListed();
        GivenHandshake(); // config only

        var cut = RenderCombatTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=combat-not-supported]")));
        Assert.Empty(cut.FindAll("[data-testid=combat-filters]"));
        _client.Verify(c => c.GetCombatLogAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void WithTheCapabilityTheLogLoadsForTheLast24HoursAndShowsEveryColumn()
    {
        GivenPluginListed();
        GivenCombatCapable();
        GivenCombatLog(
            Hit(1, headshot: true),
            Hit(2, kind: "death"),
            Hit(3, attacker: "bear", attackerName: "", attackerIsPlayer: false, distance: null));
        var before = DateTimeOffset.UtcNow;

        var cut = RenderCombatTab();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid=combat-row]").Count));
        var rows = cut.FindAll("[data-testid=combat-row]");
        Assert.Contains("Alice", rows[0].TextContent);
        Assert.Contains("76561198000000001", rows[0].TextContent);
        Assert.Contains("rifle.ak", rows[0].TextContent);
        Assert.Contains("40.5", rows[0].TextContent); // one decimal, invariant
        Assert.Contains("42.5 m", rows[0].TextContent);
        Assert.NotEmpty(rows[0].QuerySelectorAll("[data-testid=combat-headshot]"));
        Assert.NotEmpty(rows[1].QuerySelectorAll("[data-testid=combat-death]"));
        Assert.Contains("bear", rows[2].TextContent);                 // an entity is shown by name, not as a player
        Assert.DoesNotContain(" m", rows[2].TextContent.Replace("rifle.ak", "")); // no distance, no "0 m"
        _client.Verify(c => c.GetCombatLogAsync(
            _serverId,
            It.Is<DateTimeOffset?>(s => s != null && s.Value > before.AddDays(-1).AddMinutes(-5) && s.Value < before.AddDays(-1).AddMinutes(5)),
            null, null, 100), Times.Once);
    }

    [Fact]
    public void ThePlayerCellShowsTheNameWithTheSteamIdAndFallsBackToTheIdWhenThereIsNoName()
    {
        GivenPluginListed();
        GivenCombatCapable();
        GivenCombatLog(Hit(1, attackerName: "", victimName: "Bob"));

        var cut = RenderCombatTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=combat-row]")));
        var row = cut.Find("[data-testid=combat-row]").TextContent;
        Assert.Contains("76561198000000001", row);
        Assert.Contains("Bob", row);
    }

    [Fact]
    public void AnEmptyWindowSaysNothingWasRecorded()
    {
        GivenPluginListed();
        GivenCombatCapable();
        GivenCombatLog();

        var cut = RenderCombatTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=combat-empty]")));
        Assert.Empty(cut.FindAll("[data-testid=combat-table]"));
    }

    [Fact]
    public void WithTheSwitchOffItWarnsButStillShowsWhatWasRecordedBefore()
    {
        _server.PluginCombatLogEnabled = false;
        GivenPluginListed();
        GivenCombatCapable();
        GivenCombatLog(Hit(1));

        var cut = RenderCombatTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=combat-row]")));
        Assert.NotEmpty(cut.FindAll("[data-testid=combat-switched-off]"));
    }

    [Fact]
    public void WithTheSwitchOnThereIsNoWarning()
    {
        GivenPluginListed();
        GivenCombatCapable();
        GivenCombatLog(Hit(1));

        var cut = RenderCombatTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=combat-row]")));
        Assert.Empty(cut.FindAll("[data-testid=combat-switched-off]"));
    }

    [Fact]
    public void PickingAWindowReloadsWithThatWindow()
    {
        GivenPluginListed();
        GivenCombatCapable();
        GivenCombatLog(Hit(1));
        var cut = RenderCombatTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=combat-row]")));

        cut.Find("#combat-window").Change("all");

        cut.WaitForAssertion(() => _client.Verify(c => c.GetCombatLogAsync(_serverId, null, null, null, 100), Times.Once));
    }

    [Fact]
    public void AnHourWindowAsksForTheLastHour()
    {
        GivenPluginListed();
        GivenCombatCapable();
        GivenCombatLog(Hit(1));
        var cut = RenderCombatTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=combat-row]")));
        var before = DateTimeOffset.UtcNow;

        cut.Find("#combat-window").Change("1h");

        cut.WaitForAssertion(() => _client.Verify(c => c.GetCombatLogAsync(
            _serverId, It.Is<DateTimeOffset?>(s => s != null && s.Value > before.AddHours(-1).AddMinutes(-5) && s.Value < before.AddHours(-1).AddMinutes(5)),
            null, null, 100), Times.Once));
    }

    [Fact]
    public void AValidPlayerIdIsSentAsTheFilter()
    {
        GivenPluginListed();
        GivenCombatCapable();
        GivenCombatLog(Hit(1));
        var cut = RenderCombatTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=combat-row]")));

        cut.Find("#combat-player").Input(" 76561198000000002 ");
        cut.Find("[data-testid=combat-apply]").Click();

        cut.WaitForAssertion(() => _client.Verify(c => c.GetCombatLogAsync(_serverId, It.IsAny<DateTimeOffset?>(), null, "76561198000000002", 100), Times.Once));
    }

    [Theory]
    [InlineData("Alice")]
    [InlineData("7656119800000000x")]
    [InlineData("123456789012345678901")]
    public void APlayerIdThatIsNotASteamIdIsExplainedAndNotSent(string bad)
    {
        GivenPluginListed();
        GivenCombatCapable();
        GivenCombatLog(Hit(1));
        var cut = RenderCombatTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=combat-row]")));
        _client.Invocations.Clear();

        cut.Find("#combat-player").Input(bad);
        cut.Find("[data-testid=combat-apply]").Click();

        cut.WaitForAssertion(() => Assert.Contains("SteamID64", cut.Find("[data-testid=combat-error]").TextContent));
        _client.Verify(c => c.GetCombatLogAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void ShowOlderAsksForAHundredMoreAndStopsOfferingItAtFiveHundred()
    {
        GivenPluginListed();
        GivenCombatCapable();
        _client.Setup(c => c.GetCombatLogAsync(_serverId, It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<int>()))
            .ReturnsAsync(new CombatLogDto { Events = [Hit(1)], HasMore = true });
        var cut = RenderCombatTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=combat-more]")));

        for (var limit = 200; limit <= 500; limit += 100)
        {
            var expected = limit;
            cut.Find("[data-testid=combat-more]").Click();
            cut.WaitForAssertion(() => _client.Verify(c => c.GetCombatLogAsync(_serverId, It.IsAny<DateTimeOffset?>(), null, null, expected), Times.Once));
            if (limit < 500) { cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=combat-more]"))); }
        }

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-testid=combat-more]"));
            Assert.NotEmpty(cut.FindAll("[data-testid=combat-capped]"));
        });
    }

    [Fact]
    public void ChangingTheFilterStartsAgainFromTheFirstPageSize()
    {
        GivenPluginListed();
        GivenCombatCapable();
        _client.Setup(c => c.GetCombatLogAsync(_serverId, It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<int>()))
            .ReturnsAsync(new CombatLogDto { Events = [Hit(1)], HasMore = true });
        var cut = RenderCombatTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=combat-more]")));
        cut.Find("[data-testid=combat-more]").Click();
        cut.WaitForAssertion(() => _client.Verify(c => c.GetCombatLogAsync(_serverId, It.IsAny<DateTimeOffset?>(), null, null, 200), Times.Once));

        cut.Find("#combat-window").Change("7d");

        cut.WaitForAssertion(() => _client.Verify(c => c.GetCombatLogAsync(_serverId, It.Is<DateTimeOffset?>(s => s != null && s.Value < DateTimeOffset.UtcNow.AddDays(-6)), null, null, 100), Times.Once));
    }

    [Fact]
    public void AFailedLoadShowsAnErrorInsteadOfCrashingThePage()
    {
        GivenPluginListed();
        GivenCombatCapable();
        _client.Setup(c => c.GetCombatLogAsync(_serverId, It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<int>()))
            .ThrowsAsync(new HttpRequestException("boom"));

        var cut = RenderCombatTab();

        cut.WaitForAssertion(() => Assert.Contains("Failed to load the combat log.", cut.Find("[data-testid=combat-error]").TextContent));
    }

    [Fact]
    public void PlayerNamesAreShownAsTextNeverAsMarkup()
    {
        GivenPluginListed();
        GivenCombatCapable();
        GivenCombatLog(Hit(1, attackerName: "<img src=x onerror=alert(1)>", victimName: "<script>alert(2)</script>"));

        var cut = RenderCombatTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=combat-row]")));
        Assert.Empty(cut.FindAll("[data-testid=combat-row] img"));
        Assert.Empty(cut.FindAll("[data-testid=combat-row] script"));
        Assert.Contains("<img src=x onerror=alert(1)>", cut.Find("[data-testid=combat-row]").TextContent);
    }

    // ---- the Bases tab ------------------------------------------------------------------------------------

    private IRenderedComponent<ServerDetail> RenderBasesTab()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/servers/{_serverId}?tab=bases");
        return Render<ServerDetail>(parameters => parameters.Add(p => p.Id, _serverId));
    }

    private void GivenBasesCapable() => GivenHandshake(capabilities: [RustArchonPlugin.ConfigCapability, RustArchonPlugin.TcsCapability]);

    private static BaseTcDto Base(int id, string owner = "76561198000000001", double x = 100.4, double y = 25.6, double z = -300.7, params (string Id, string Name)[] authorized) => new()
    {
        Id = id, X = x, Y = y, Z = z, OwnerId = owner,
        Authorized = authorized.Select(a => new BaseAuthorizedPlayerDto { PlayerId = a.Id, Name = a.Name }).ToList()
    };

    private void GivenBases(bool ready, params BaseTcDto[] tcs) =>
        _client.Setup(c => c.GetBasesAsync(_serverId)).ReturnsAsync(new BasesDto
        {
            Ready = ready, CapturedAtUtc = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero), Tcs = [.. tcs]
        });

    private void VerifyBasesNeverAsked() => _client.Verify(c => c.GetBasesAsync(It.IsAny<Guid>()), Times.Never);

    [Fact]
    public void TheBasesTabIsAlwaysOfferedSoAServerWithoutThePluginIsToldWhatItNeeds()
    {
        Assert.NotEmpty(RenderPluginsTab().FindAll("[data-testid=tab-bases]"));
    }

    [Fact]
    public void WithoutThePluginTheBasesTabSaysSoAndAsksForNothing()
    {
        var cut = RenderBasesTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=bases-needs-plugin]")));
        VerifyBasesNeverAsked();
    }

    [Fact]
    public void AListedPluginThatHasNotAnsweredGetsTheWaitMessageAndNoQueryForBases()
    {
        GivenPluginListed();

        var cut = RenderBasesTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=bases-no-handshake]")));
        VerifyBasesNeverAsked();
    }

    [Fact]
    public void APluginThatDoesNotReportTheTcsCapabilityIsToldToUpdateAndNothingIsQueried()
    {
        GivenPluginListed();
        GivenHandshake(capabilities: [RustArchonPlugin.ConfigCapability, RustArchonPlugin.CombatCapability]); // combat, but no tcs

        var cut = RenderBasesTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=bases-not-supported]")));
        VerifyBasesNeverAsked();
    }

    [Fact]
    public void WithTheCapabilityEveryBaseIsListedWithItsOwnerLocationAndWhoIsAuthorized()
    {
        GivenPluginListed();
        GivenBasesCapable();
        GivenBases(true,
            Base(2, "76561198000000002", 5, 6, 7),
            Base(1, "76561198000000001", 100.4, 25.6, -300.7, ("76561198000000001", "Alice"), ("76561198000000003", "Carol")));

        var cut = RenderBasesTab();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=base-row]").Count));
        var rows = cut.FindAll("[data-testid=base-row]");
        Assert.Contains("Alice", rows[0].TextContent);                 // the owner's name, from the authorized list
        Assert.Contains("76561198000000001", rows[0].TextContent);
        Assert.Contains("x 100", rows[0].TextContent);
        Assert.Contains("z -301", rows[0].TextContent);
        Assert.Contains("Alice, Carol", cut.FindAll("[data-testid=base-authorized]")[0].TextContent);
        Assert.Contains("76561198000000002", rows[1].TextContent);     // no name known: the id stands in
        Assert.Contains("Nobody", cut.FindAll("[data-testid=base-authorized]")[1].TextContent);
    }

    [Fact]
    public void AnAuthorizedPlayerWithNoNameIsShownByTheirId()
    {
        GivenPluginListed();
        GivenBasesCapable();
        GivenBases(true, Base(1, "76561198000000001", 0, 0, 0, ("76561198000000009", "")));

        var cut = RenderBasesTab();

        cut.WaitForAssertion(() => Assert.Contains("76561198000000009", cut.Find("[data-testid=base-authorized]").TextContent));
    }

    [Fact]
    public void WithoutThePermissionTheTabSaysYouAreNotAllowedNotThatSomethingFailed()
    {
        GivenPluginListed();
        GivenBasesCapable();
        _client.Setup(c => c.GetBasesAsync(_serverId)).ThrowsAsync(
            Refit.ApiException.Create(new HttpRequestMessage(), HttpMethod.Get, new HttpResponseMessage(HttpStatusCode.Forbidden), new Refit.RefitSettings()).GetAwaiter().GetResult());

        var cut = RenderBasesTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=bases-forbidden]")));
        Assert.Empty(cut.FindAll("[data-testid=bases-error]"));
        Assert.Empty(cut.FindAll("[data-testid=bases-table]"));
        Assert.Empty(cut.FindAll("[data-testid=bases-refresh]"));
    }

    [Fact]
    public void AFailedBasesLoadShowsAnErrorInsteadOfCrashingThePage()
    {
        GivenPluginListed();
        GivenBasesCapable();
        _client.Setup(c => c.GetBasesAsync(_serverId)).ThrowsAsync(new HttpRequestException("boom"));

        var cut = RenderBasesTab();

        cut.WaitForAssertion(() => Assert.Contains("Failed to load the bases.", cut.Find("[data-testid=bases-error]").TextContent));
        Assert.Empty(cut.FindAll("[data-testid=bases-forbidden]"));
    }

    [Fact]
    public void AListFromAnUnfinishedScanCarriesAWarningAndACompleteOneDoesNot()
    {
        GivenPluginListed();
        GivenBasesCapable();
        GivenBases(false, Base(1));

        var partial = RenderBasesTab();
        partial.WaitForAssertion(() => Assert.NotEmpty(partial.FindAll("[data-testid=base-row]")));
        Assert.NotEmpty(partial.FindAll("[data-testid=bases-not-ready]"));

        GivenBases(true, Base(1));
        var complete = RenderBasesTab();
        complete.WaitForAssertion(() => Assert.NotEmpty(complete.FindAll("[data-testid=base-row]")));
        Assert.Empty(complete.FindAll("[data-testid=bases-not-ready]"));
    }

    [Fact]
    public void ANeverReadServerSaysNoBasesYetWithoutTheScanWarning()
    {
        GivenPluginListed();
        GivenBasesCapable();
        _client.Setup(c => c.GetBasesAsync(_serverId)).ReturnsAsync(new BasesDto { Ready = false, CapturedAtUtc = null, Tcs = [] });

        var cut = RenderBasesTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=bases-empty]")));
        Assert.Empty(cut.FindAll("[data-testid=bases-not-ready]"));   // nothing was read, so there is no partial list to warn about
        Assert.Empty(cut.FindAll("[data-testid=bases-captured]"));
    }

    [Fact]
    public void WithRecordingOffItWarnsThatTheListIsNotBeingKeptUpToDate()
    {
        _server.PluginRecordingEnabled = false;
        GivenPluginListed();
        GivenBasesCapable();
        GivenBases(true, Base(1));

        var cut = RenderBasesTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=base-row]")));
        Assert.NotEmpty(cut.FindAll("[data-testid=bases-recording-off]"));
    }

    [Fact]
    public void RefreshAsksAgainAndShowsWhenTheListWasTaken()
    {
        GivenPluginListed();
        GivenBasesCapable();
        GivenBases(true, Base(1));
        var cut = RenderBasesTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=base-row]")));
        Assert.Contains("2026-09-20 12:00:00 UTC", cut.Find("[data-testid=bases-captured]").TextContent);

        cut.Find("[data-testid=bases-refresh]").Click();

        cut.WaitForAssertion(() => _client.Verify(c => c.GetBasesAsync(_serverId), Times.Exactly(2)));
    }

    [Fact]
    public void PlayerNamesAreShownAsTextNeverAsMarkupOnTheBasesTab()
    {
        GivenPluginListed();
        GivenBasesCapable();
        GivenBases(true, Base(1, "76561198000000001", 0, 0, 0, ("76561198000000001", "<img src=x onerror=alert(1)>")));

        var cut = RenderBasesTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=base-row]")));
        Assert.Empty(cut.FindAll("[data-testid=base-row] img"));
        Assert.Contains("<img src=x onerror=alert(1)>", cut.Find("[data-testid=base-row]").TextContent);
    }

    [Fact]
    public void AnOwnerTheApiHasANameForIsShownByThatNameEvenWhenNotOnTheirOwnCupboard()
    {
        GivenPluginListed();
        GivenBasesCapable();
        var tc = Base(1, "76561198000000001", 0, 0, 0);
        tc.OwnerName = "Alice From History";
        GivenBases(true, tc);

        var cut = RenderBasesTab();

        cut.WaitForAssertion(() => Assert.Contains("Alice From History", cut.Find("[data-testid=base-row]").TextContent));
        Assert.Contains("76561198000000001", cut.Find("[data-testid=base-row]").TextContent);       // the id stays beneath the name
    }

    [Fact]
    public void AnOwnerNameIsShownAsTextNeverAsMarkup()
    {
        GivenPluginListed();
        GivenBasesCapable();
        var tc = Base(1, "76561198000000001", 0, 0, 0);
        tc.OwnerName = "<b onmouseover=alert(1)>owner</b>";
        GivenBases(true, tc);

        var cut = RenderBasesTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=base-row]")));
        Assert.Empty(cut.FindAll("[data-testid=base-row] b"));
        Assert.Contains("<b onmouseover=alert(1)>owner</b>", cut.Find("[data-testid=base-row]").TextContent);
    }

    [Fact]
    public void AnApiResolvedAuthorizedNameIsListedLikeAPluginOne()
    {
        GivenPluginListed();
        GivenBasesCapable();
        GivenBases(true, Base(1, "76561198000000001", 0, 0, 0, ("76561198000000009", "Resolved By Api")));

        var cut = RenderBasesTab();

        cut.WaitForAssertion(() => Assert.Contains("Resolved By Api", cut.Find("[data-testid=base-authorized]").TextContent));
    }

    // ---- the Positions tab --------------------------------------------------------------------------------

    private IRenderedComponent<ServerDetail> RenderPositionsTab()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/servers/{_serverId}?tab=positions");
        return Render<ServerDetail>(parameters => parameters.Add(p => p.Id, _serverId));
    }

    private void GivenPositionsCapable() => GivenHandshake(capabilities: [RustArchonPlugin.ConfigCapability, RustArchonPlugin.PositionsCapability]);

    private static PositionSampleDto Pos(long seq, string player, int minutesAgo = 1, double x = 100.4, double y = 25.6, double z = -300.7, int yaw = 90, string name = "", string marker = "") => new()
    {
        Sequence = seq, PlayerId = player, PlayerName = name, X = x, Y = y, Z = z, Yaw = yaw, Marker = marker,
        OccurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo)
    };

    private void GivenPositions(params PositionSampleDto[] samples) =>
        _client.Setup(c => c.GetPositionsAsync(_serverId, It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), null, It.IsAny<int>()))
            .ReturnsAsync(new PositionsDto { Samples = [.. samples.OrderByDescending(s => s.OccurredAtUtc).ThenByDescending(s => s.Sequence)] });

    private void GivenTrack(string player, bool hasMore, params PositionSampleDto[] samples) =>
        _client.Setup(c => c.GetPositionsAsync(_serverId, It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), player, It.IsAny<int>()))
            .ReturnsAsync(new PositionsDto { Samples = [.. samples.OrderByDescending(s => s.OccurredAtUtc).ThenByDescending(s => s.Sequence)], HasMore = hasMore });

    private void VerifyPositionsNeverAsked() =>
        _client.Verify(c => c.GetPositionsAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<int>()), Times.Never);

    private static readonly Refit.ApiException ForbiddenResponse =
        Refit.ApiException.Create(new HttpRequestMessage(), HttpMethod.Get, new HttpResponseMessage(HttpStatusCode.Forbidden), new Refit.RefitSettings()).GetAwaiter().GetResult();

    [Fact]
    public void ThePositionsTabIsAlwaysOfferedSoAServerWithoutThePluginIsToldWhatItNeeds()
    {
        Assert.NotEmpty(RenderPluginsTab().FindAll("[data-testid=tab-positions]"));
    }

    [Fact]
    public void WithoutThePluginThePositionsTabSaysSoAndAsksForNothing()
    {
        var cut = RenderPositionsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=positions-needs-plugin]")));
        VerifyPositionsNeverAsked();
    }

    [Fact]
    public void AListedPluginThatHasNotAnsweredGetsTheWaitMessageAndNoQueryForPositions()
    {
        GivenPluginListed();

        var cut = RenderPositionsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=positions-no-handshake]")));
        VerifyPositionsNeverAsked();
    }

    [Fact]
    public void APluginThatDoesNotReportThePositionsCapabilityIsToldToUpdateAndNothingIsQueried()
    {
        GivenPluginListed();
        GivenHandshake(capabilities: [RustArchonPlugin.ConfigCapability, RustArchonPlugin.TcsCapability]); // tcs, but no positions

        var cut = RenderPositionsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=positions-not-supported]")));
        VerifyPositionsNeverAsked();
    }

    [Fact]
    public void EachPlayerSeenIsOneRowAtTheirNewestPositionWithTheNameTheyWereGiven()
    {
        GivenPluginListed();
        GivenPositionsCapable();
        GivenPositions(
            Pos(1, "76561198000000001", minutesAgo: 5, x: 1, z: 1, name: "Alice", marker: "on"),
            Pos(2, "76561198000000001", minutesAgo: 1, x: 100.4, z: -300.7, yaw: 90),     // newest: no name on this one
            Pos(3, "76561198000000002", minutesAgo: 2, x: 5, z: 6, yaw: 270));            // never named

        var cut = RenderPositionsTab();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=position-row]").Count));
        // Rows are ordered by name, and a bare id sorts before "Alice".
        var rows = cut.FindAll("[data-testid=position-row]");
        var alice = rows.Single(r => r.TextContent.Contains("Alice"));
        var unnamed = rows.Single(r => !r.TextContent.Contains("Alice"));
        Assert.Contains("Alice", alice.TextContent);                    // the name comes from the sample that had it
        Assert.Contains("x 100", alice.TextContent);                    // the location from the newest sample
        Assert.Contains("z -301", alice.TextContent);
        Assert.Contains("E (90°)", alice.TextContent);
        Assert.Contains("76561198000000002", unnamed.TextContent);        // no name known: the id stands in
        Assert.Contains("W (270°)", unnamed.TextContent);
    }

    [Fact]
    public void APlayerWhoseNewestSampleIsTheLeavingOneShowsAsLeftAndAfterThoseStillHere()
    {
        GivenPluginListed();
        GivenPositionsCapable();
        GivenPositions(
            Pos(1, "76561198000000001", minutesAgo: 4, name: "Alice", marker: "on"),
            Pos(2, "76561198000000001", minutesAgo: 3, name: "Alice", marker: "off"),
            Pos(3, "76561198000000002", minutesAgo: 1, name: "Zed"));

        var cut = RenderPositionsTab();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=position-row]").Count));
        var statuses = cut.FindAll("[data-testid=position-status]").Select(s => s.TextContent.Trim()).ToArray();
        Assert.Equal(["Online", "Left"], statuses);                        // Zed is here, so first; Alice left
        Assert.Contains("Zed", cut.FindAll("[data-testid=position-row]")[0].TextContent);
    }

    [Theory]
    [InlineData(0, "N")]
    [InlineData(44, "NE")]
    [InlineData(90, "E")]
    [InlineData(180, "S")]
    [InlineData(270, "W")]
    [InlineData(359, "N")]
    public void TheFacingIsShownAsACompassPointAndDegrees(int yaw, string point)
    {
        GivenPluginListed();
        GivenPositionsCapable();
        GivenPositions(Pos(1, "76561198000000001", yaw: yaw, name: "A"));

        var cut = RenderPositionsTab();

        cut.WaitForAssertion(() => Assert.Contains(point + " (" + yaw + "°)", cut.Find("[data-testid=position-facing]").TextContent));
    }

    [Fact]
    public void WithoutThePermissionThePositionsTabSaysYouAreNotAllowedNotThatSomethingFailed()
    {
        GivenPluginListed();
        GivenPositionsCapable();
        _client.Setup(c => c.GetPositionsAsync(_serverId, It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<int>())).ThrowsAsync(ForbiddenResponse);

        var cut = RenderPositionsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=positions-forbidden]")));
        Assert.Empty(cut.FindAll("[data-testid=positions-error]"));
        Assert.Empty(cut.FindAll("[data-testid=positions-table]"));
        Assert.Empty(cut.FindAll("[data-testid=positions-refresh]"));
    }

    [Fact]
    public void AFailedPositionsLoadShowsAnErrorInsteadOfCrashingThePage()
    {
        GivenPluginListed();
        GivenPositionsCapable();
        _client.Setup(c => c.GetPositionsAsync(_serverId, It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<int>())).ThrowsAsync(new HttpRequestException("boom"));

        var cut = RenderPositionsTab();

        cut.WaitForAssertion(() => Assert.Contains("Failed to load positions.", cut.Find("[data-testid=positions-error]").TextContent));
        Assert.Empty(cut.FindAll("[data-testid=positions-forbidden]"));
    }

    [Fact]
    public void NobodySeenRecentlySaysSo()
    {
        GivenPluginListed();
        GivenPositionsCapable();
        GivenPositions();

        var cut = RenderPositionsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=positions-empty]")));
        Assert.Empty(cut.FindAll("[data-testid=positions-table]"));
    }

    [Fact]
    public void WithRecordingOffItWarnsThatPositionsAreNotBeingRecorded()
    {
        _server.PluginRecordingEnabled = false;
        GivenPluginListed();
        GivenPositionsCapable();
        GivenPositions();

        var cut = RenderPositionsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=positions-recording-off]")));
    }

    [Fact]
    public void TheListAsksForARecentWindowOnlyAndRefreshAsksAgain()
    {
        GivenPluginListed();
        GivenPositionsCapable();
        GivenPositions(Pos(1, "76561198000000001", name: "A"));
        var cut = RenderPositionsTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=position-row]")));
        Assert.NotEmpty(cut.FindAll("[data-testid=positions-captured]"));

        cut.Find("[data-testid=positions-refresh]").Click();

        cut.WaitForAssertion(() => _client.Verify(
            c => c.GetPositionsAsync(_serverId, It.Is<DateTimeOffset?>(s => s != null && s > DateTimeOffset.UtcNow.AddMinutes(-11) && s < DateTimeOffset.UtcNow.AddMinutes(-9)),
                null, null, It.IsAny<int>()),
            Times.Exactly(2)));
    }

    [Fact]
    public void ShowTrackLoadsThatPlayersLastHourAndTotalsTheDistanceOnTheFlat()
    {
        GivenPluginListed();
        GivenPositionsCapable();
        GivenPositions(Pos(3, "76561198000000001", minutesAgo: 1, x: 30, y: 999, z: 40, name: "Alice"));
        GivenTrack("76561198000000001", hasMore: false,
            Pos(1, "76561198000000001", minutesAgo: 30, x: 0, y: 0, z: 0, marker: "on"),
            Pos(2, "76561198000000001", minutesAgo: 20, x: 30, y: 500, z: 0),          // 30 m, the height is ignored
            Pos(3, "76561198000000001", minutesAgo: 10, x: 30, y: 0, z: 40));          // 40 m more
        var cut = RenderPositionsTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=position-track]")));

        cut.Find("[data-testid=position-track]").Click();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid=track-row]").Count));
        Assert.Contains("70 m", cut.Find("[data-testid=track-total]").TextContent);
        Assert.Contains("Joined", cut.FindAll("[data-testid=track-row]")[2].TextContent);        // newest first, so the join is last
        Assert.Contains("Alice", cut.Find("[data-testid=track-pane]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=track-truncated]"));
    }

    [Fact]
    public void ATrackThatWasCutShortSaysSo()
    {
        GivenPluginListed();
        GivenPositionsCapable();
        GivenPositions(Pos(1, "76561198000000001", name: "A"));
        GivenTrack("76561198000000001", hasMore: true, Pos(1, "76561198000000001", minutesAgo: 5));
        var cut = RenderPositionsTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=position-track]")));

        cut.Find("[data-testid=position-track]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=track-truncated]")));
    }

    [Fact]
    public void AnEmptyTrackSaysNothingWasRecordedAndCloseHidesIt()
    {
        GivenPluginListed();
        GivenPositionsCapable();
        GivenPositions(Pos(1, "76561198000000001", name: "A"));
        GivenTrack("76561198000000001", hasMore: false);
        var cut = RenderPositionsTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=position-track]")));

        cut.Find("[data-testid=position-track]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=track-empty]")));
        cut.Find("[data-testid=track-close]").Click();

        Assert.Empty(cut.FindAll("[data-testid=track-pane]"));
    }

    [Fact]
    public void AFailedTrackLoadShowsAnErrorAndTheListStays()
    {
        GivenPluginListed();
        GivenPositionsCapable();
        GivenPositions(Pos(1, "76561198000000001", name: "A"));
        _client.Setup(c => c.GetPositionsAsync(_serverId, It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), "76561198000000001", It.IsAny<int>())).ThrowsAsync(new HttpRequestException("boom"));
        var cut = RenderPositionsTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=position-track]")));

        cut.Find("[data-testid=position-track]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=track-error]")));
        Assert.NotEmpty(cut.FindAll("[data-testid=position-row]"));
    }

    [Fact]
    public void PlayerNamesAreShownAsTextNeverAsMarkupOnThePositionsTab()
    {
        GivenPluginListed();
        GivenPositionsCapable();
        GivenPositions(Pos(1, "76561198000000001", name: "<img src=x onerror=alert(1)>"));

        var cut = RenderPositionsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=position-row]")));
        Assert.Empty(cut.FindAll("[data-testid=position-row] img"));
        Assert.Contains("<img src=x onerror=alert(1)>", cut.Find("[data-testid=position-row]").TextContent);
    }

    private sealed record UpdateSettingsTypedArgs(bool? Recording, bool? Combat);
}
