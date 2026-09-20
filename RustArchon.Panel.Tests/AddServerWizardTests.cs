// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Refit;
using RustArchon.Messaging.Contracts;
using RustArchon.Panel.Clients;
using RustArchon.Panel.Components.Pages.Servers;
using RustArchon.Shared.DTOs;

namespace RustArchon.Panel.Tests;

/// <summary>
/// The add-server wizard (ADR-0002): the connection test is the server's real, live connection status, a half-set-up server can be
/// resumed or discarded, every optional step can be skipped, and nothing is shown as working unless something positively said it was.
/// </summary>
public class AddServerWizardTests : BunitContext
{
    private readonly Mock<IRustServerApiClient> _client = new();
    private readonly Mock<IIntegrationApiClient> _integration = new();
    private readonly Guid _id = Guid.NewGuid();
    private RconConnectionStatus _status = RconConnectionStatus.Connecting;
    private string? _detail;
    private ServerModFramework _framework = ServerModFramework.None;

    public AddServerWizardTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_client.Object);
        Services.AddSingleton(_integration.Object);
        Services.AddSingleton(ReportTestSupport.Localizer());

        _client.Setup(c => c.GetPlanLimitAsync()).ReturnsAsync(new ServerPlanLimitDto { Quantity = 5, CurrentServerCount = 1 });
        _client.Setup(c => c.GetByIdAsync(_id)).ReturnsAsync(() => Server());
        _client.Setup(c => c.CreateAsync(It.IsAny<CreateRustServerDto>())).ReturnsAsync(() => Server());
        _client.Setup(c => c.UpdateAsync(_id, It.IsAny<UpdateRustServerDto>())).ReturnsAsync(() => Server());
        _client.Setup(c => c.DeleteAsync(_id)).ReturnsAsync(true);
        _client.Setup(c => c.GetPluginsAsync(_id)).ReturnsAsync(() => PluginList());
        _client.Setup(c => c.GetPluginStatusAsync(_id)).ReturnsAsync(NoStatus());
        _client.Setup(c => c.GetReportForwardingAsync(_id)).ReturnsAsync(new ReportForwardingDto { Url = "https://panel.example/ingest/reports/x/SECRET", Command = "cmd" });
    }

    private RustServerDto Server(bool enabled = true, GeolocationProviderKind provider = GeolocationProviderKind.None, bool steamKey = false) => new()
    {
        Id = _id, Name = "My server", Description = "A description", Host = "192.0.2.10", Port = 28016, IsEnabled = enabled,
        ConnectionStatus = _status, ConnectionStatusDetail = _detail, GeolocationProvider = provider, HasSteamApiKey = steamKey
    };

    private List<ServerPluginDto> PluginList(params string[] names) =>
        _framework == ServerModFramework.None && names.Length == 0
            ? []
            : (names.Length == 0 ? ["Some plugin"] : names.ToList()).Select(n => new ServerPluginDto { Name = n, Framework = _framework }).ToList();

    private static IApiResponse<ServerPluginStatusDto> NoStatus() =>
        Mock.Of<IApiResponse<ServerPluginStatusDto>>(r => r.IsSuccessStatusCode == true && r.Content == null);

    private static IApiResponse<ServerPluginStatusDto> Status(ServerPluginStatusDto dto)
    {
        var response = new Mock<IApiResponse<ServerPluginStatusDto>>();
        response.SetupGet(r => r.IsSuccessStatusCode).Returns(true);
        response.SetupGet(r => r.Content).Returns(dto);
        return response.Object;
    }

    private IRenderedComponent<AddServerWizard> RenderNew() => Render<AddServerWizard>(p => p
        .Add(x => x.PollInterval, TimeSpan.FromMilliseconds(15)).Add(x => x.TroubleshootAfter, TimeSpan.FromMilliseconds(120)));

    private IRenderedComponent<AddServerWizard> RenderResume() => Render<AddServerWizard>(p => p
        .Add(x => x.ServerId, (Guid?)_id)
        .Add(x => x.PollInterval, TimeSpan.FromMilliseconds(15)).Add(x => x.TroubleshootAfter, TimeSpan.FromMilliseconds(120)));

    private static AngleSharp.Dom.IElement Step(IRenderedComponent<AddServerWizard> cut, string name) => cut.WaitForElement($"[data-testid=step-{name}]");

    private static void Type(IRenderedComponent<AddServerWizard> cut, string testId, string value) =>
        cut.Find($"[data-testid={testId}]").Input(value);

    private static void Click(IRenderedComponent<AddServerWizard> cut, string testId) => cut.Find($"[data-testid={testId}]").Click();

    private IRenderedComponent<AddServerWizard> ResumeConnected()
    {
        _status = RconConnectionStatus.Connected;
        var cut = RenderResume();
        Step(cut, "connect");
        cut.WaitForElement("[data-testid=wizard-connected]");
        return cut;
    }

    private IRenderedComponent<AddServerWizard> ToIntegrations()
    {
        var cut = ResumeConnected();
        Click(cut, "wizard-connect-next");
        Step(cut, "integrations");
        return cut;
    }

    private IRenderedComponent<AddServerWizard> ToReports()
    {
        var cut = ToIntegrations();
        Click(cut, "wizard-integrations-save");
        Step(cut, "reports");
        return cut;
    }

    private IRenderedComponent<AddServerWizard> ToPlugin()
    {
        var cut = ToReports();
        Click(cut, "wizard-reports-next");
        Step(cut, "plugin");
        return cut;
    }

    [Fact]
    public void TheDisclosureOfWhatRecordingDoesIsShownOpenBeforeTheOwnerChoosesThePlugin()
    {
        var cut = ToPlugin();

        Assert.True(cut.Find("[data-testid=recording-disclosure]").HasAttribute("open"));
        Assert.Contains("live map", cut.Find("[data-testid=recording-disclosure-benefit]").TextContent);
    }

    private IRenderedComponent<AddServerWizard> ToPluginChecklist()
    {
        var cut = ToPlugin();
        Click(cut, "wizard-plugin-yes");
        cut.WaitForElement("[data-testid=wizard-plugin-checklist]");
        return cut;
    }

    private static string Text(IRenderedComponent<AddServerWizard> cut, string testId) => cut.Find($"[data-testid={testId}]").TextContent;

    // ---- basics ----

    [Fact]
    public void ItStartsOnTheBasicsStepAndChecksThePlanLimit()
    {
        var cut = RenderNew();

        Step(cut, "basics");
        _client.Verify(c => c.GetPlanLimitAsync(), Times.Once);
        Assert.Empty(cut.FindAll("[data-testid=wizard-limit]"));
        Assert.Equal("/servers", cut.Find("a.btn-secondary").GetAttribute("href")?.TrimStart('/').Insert(0, "/"));
    }

    [Theory]
    [InlineData(true, "Buy more capacity")]
    [InlineData(false, "Upgrade your plan")]
    public void AnAccountAtItsLimitIsToldAtOnceAndCannotGoOn(bool canBuy, string expected)
    {
        _client.Setup(c => c.GetPlanLimitAsync()).ReturnsAsync(new ServerPlanLimitDto { Quantity = 2, CurrentServerCount = 2, CanBuyCapacity = canBuy, PlanName = "Starter" });

        var cut = RenderNew();

        Assert.Contains(expected, cut.WaitForElement("[data-testid=wizard-limit]").TextContent);
        Assert.True(cut.Find("[data-testid=wizard-basics-next]").HasAttribute("disabled"));
    }

    [Fact]
    public void AFailedLimitCheckDoesNotBlockTheWizard()
    {
        _client.Setup(c => c.GetPlanLimitAsync()).ThrowsAsync(new InvalidOperationException("boom"));

        var cut = RenderNew();

        Step(cut, "basics");
        Assert.Empty(cut.FindAll("[data-testid=wizard-limit]"));
        Assert.False(cut.Find("[data-testid=wizard-basics-next]").HasAttribute("disabled"));
    }

    [Fact]
    public void AServerNeedsANameToGoOn()
    {
        var cut = RenderNew();
        Step(cut, "basics");

        Click(cut, "wizard-basics-next");

        Assert.Contains("Give the server a name", cut.Find("[data-testid=wizard-error]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=step-connect]"));
    }

    [Fact]
    public void ANameThatIsTooLongIsRefused()
    {
        var cut = RenderNew();
        Step(cut, "basics");
        Type(cut, "wizard-name", new string('n', 101));

        Click(cut, "wizard-basics-next");

        cut.Find("[data-testid=wizard-error]");
        Assert.Empty(cut.FindAll("[data-testid=step-connect]"));
    }

    [Fact]
    public void ANamedServerMovesOnToConnect()
    {
        var cut = RenderNew();
        Step(cut, "basics");
        Type(cut, "wizard-name", "  My server  ");

        Click(cut, "wizard-basics-next");

        Step(cut, "connect");
        Assert.Contains("web RCON", cut.Find("[data-testid=wizard-rcon-help]").TextContent);
    }

    // ---- connecting ----

    private IRenderedComponent<AddServerWizard> ToConnectForm()
    {
        var cut = RenderNew();
        Step(cut, "basics");
        Type(cut, "wizard-name", "My server");
        Type(cut, "wizard-description", "A description");
        Click(cut, "wizard-basics-next");
        Step(cut, "connect");
        return cut;
    }

    private static void FillConnection(IRenderedComponent<AddServerWizard> cut, string host = "192.0.2.10", string port = "28016", string password = "hunter2")
    {
        Type(cut, "wizard-host", host);
        Type(cut, "wizard-port", port);
        Type(cut, "wizard-password", password);
    }

    [Fact]
    public void ConnectingCreatesTheServerWithWhatWasEnteredAndMovesTheUrlToTheResumeAddress()
    {
        var cut = ToConnectForm();
        FillConnection(cut);

        Click(cut, "wizard-connect");

        cut.WaitForAssertion(() => _client.Verify(c => c.CreateAsync(It.Is<CreateRustServerDto>(d =>
            d.Name == "My server" && d.Description == "A description" && d.Host == "192.0.2.10" && d.Port == 28016 && d.RconPassword == "hunter2")), Times.Once));
        cut.WaitForElement("[data-testid=wizard-connection]");
        Assert.EndsWith($"/servers/{_id}/setup", Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public void TheHostPortAndPasswordAreAllRequiredBeforeAnythingIsCreated()
    {
        var cut = ToConnectForm();
        FillConnection(cut, password: "");

        Click(cut, "wizard-connect");

        Assert.Contains("RCON password", cut.Find("[data-testid=wizard-error]").TextContent);
        _client.Verify(c => c.CreateAsync(It.IsAny<CreateRustServerDto>()), Times.Never);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    public void APortOutsideTheValidRangeIsRefused(string port)
    {
        var cut = ToConnectForm();
        FillConnection(cut, port: port);

        Click(cut, "wizard-connect");

        cut.Find("[data-testid=wizard-error]");
        _client.Verify(c => c.CreateAsync(It.IsAny<CreateRustServerDto>()), Times.Never);
    }

    [Fact]
    public void ADuplicateNameSendsTheUserBackToTheNameWithTheReason()
    {
        _client.Setup(c => c.CreateAsync(It.IsAny<CreateRustServerDto>()))
            .ThrowsAsync(ReportTestSupport.Refused(HttpStatusCode.Conflict, "A server named 'My server' already exists."));
        var cut = ToConnectForm();
        FillConnection(cut);

        Click(cut, "wizard-connect");

        Step(cut, "basics");
        Assert.Contains("already exists", cut.Find("[data-testid=wizard-error]").TextContent);
    }

    [Fact]
    public void ARefusalToCreateIsShownAndTheUserStaysToTryAgain()
    {
        _client.Setup(c => c.CreateAsync(It.IsAny<CreateRustServerDto>()))
            .ThrowsAsync(ReportTestSupport.Refused(HttpStatusCode.BadRequest, "You're using all 1 of your server slot(s)."));
        var cut = ToConnectForm();
        FillConnection(cut);

        Click(cut, "wizard-connect");

        Assert.Contains("all 1 of your server slot", cut.WaitForElement("[data-testid=wizard-error]").TextContent);
        Step(cut, "connect");
        Assert.False(cut.Find("[data-testid=wizard-connect]").HasAttribute("disabled"));
    }

    [Fact]
    public void TheWizardWatchesTheLiveStatusAndOffersToContinueOnlyOnceItIsConnected()
    {
        var cut = ToConnectForm();
        FillConnection(cut);
        Click(cut, "wizard-connect");
        cut.WaitForElement("[data-testid=wizard-connection]");
        Assert.Contains("Connecting", Text(cut, "wizard-status"));
        Assert.Empty(cut.FindAll("[data-testid=wizard-connect-next]"));

        _status = RconConnectionStatus.Connected;

        cut.WaitForElement("[data-testid=wizard-connected]");
        cut.Find("[data-testid=wizard-connect-next]");
        Assert.Contains("Connected", Text(cut, "wizard-status"));
    }

    [Fact]
    public void AConnectionThatFailsShowsTheReasonAndAfterAWhileTheThingsToCheck()
    {
        _status = RconConnectionStatus.Error;
        _detail = "Authentication failed";
        var cut = ToConnectForm();
        FillConnection(cut);
        Click(cut, "wizard-connect");

        cut.WaitForElement("[data-testid=wizard-status-detail]");
        Assert.Contains("Authentication failed", Text(cut, "wizard-status-detail"));

        var hints = cut.WaitForElement("[data-testid=wizard-troubleshooting]", TimeSpan.FromSeconds(5));
        Assert.Contains("+rcon.password", hints.TextContent);
        Assert.Empty(cut.FindAll("[data-testid=wizard-connect-next]"));
    }

    [Fact]
    public void ConnectedSaysWhichModFrameworkTheServerRuns()
    {
        _framework = ServerModFramework.Carbon;
        var cut = ResumeConnected();

        cut.WaitForAssertion(() => Assert.Contains("Carbon", Text(cut, "wizard-framework")));
    }

    [Fact]
    public void ConnectedWithNoModFrameworkSaysThePluginNeedsOne()
    {
        var cut = ResumeConnected();

        Assert.Contains("No Oxide or Carbon", Text(cut, "wizard-framework"));
    }

    // ---- fixing a connection that failed ----

    [Fact]
    public void UpdateAndRetrySendsAFullRecordSoNothingElseIsReset()
    {
        _status = RconConnectionStatus.Error;
        _client.Setup(c => c.GetByIdAsync(_id)).ReturnsAsync(() => Server(provider: GeolocationProviderKind.IpHubInfo));
        var cut = RenderResume();
        Step(cut, "connect");
        Type(cut, "wizard-host", "203.0.113.5");
        Type(cut, "wizard-port", "28017");

        Click(cut, "wizard-retry");

        cut.WaitForAssertion(() => _client.Verify(c => c.UpdateAsync(_id, It.Is<UpdateRustServerDto>(d =>
            d.Id == _id && d.Name == "My server" && d.Description == "A description" && d.Host == "203.0.113.5" && d.Port == 28017
            && d.RconPassword == null // left blank: keep the one that is saved
            && d.GeolocationProvider == GeolocationProviderKind.IpHubInfo && d.SteamApiKey == null && d.GeolocationApiKey == null)), Times.Once));
    }

    [Fact]
    public void ANewPasswordIsSentWhenOneIsTyped()
    {
        _status = RconConnectionStatus.Error;
        var cut = RenderResume();
        Step(cut, "connect");
        Type(cut, "wizard-password", "new-password");

        Click(cut, "wizard-retry");

        cut.WaitForAssertion(() => _client.Verify(c => c.UpdateAsync(_id, It.Is<UpdateRustServerDto>(d => d.RconPassword == "new-password")), Times.Once));
    }

    [Fact]
    public void AFailedUpdateIsShown()
    {
        _status = RconConnectionStatus.Error;
        _client.Setup(c => c.UpdateAsync(_id, It.IsAny<UpdateRustServerDto>())).ThrowsAsync(ReportTestSupport.Refused(HttpStatusCode.BadRequest, "Nope."));
        var cut = RenderResume();
        Step(cut, "connect");

        Click(cut, "wizard-retry");

        Assert.Contains("Nope.", cut.WaitForElement("[data-testid=wizard-error]").TextContent);
    }

    // ---- discarding ----

    [Fact]
    public void DiscardingAsksFirstAndKeepingItChangesNothing()
    {
        _status = RconConnectionStatus.Error;
        var cut = RenderResume();
        Step(cut, "connect");

        Click(cut, "wizard-discard");
        Assert.Contains("slot is freed", cut.Markup);
        cut.Find(".modal-footer .btn-secondary").Click();

        _client.Verify(c => c.DeleteAsync(It.IsAny<Guid>()), Times.Never);
        Step(cut, "connect");
    }

    [Fact]
    public void ConfirmingRemovesTheHalfMadeServerAndReturnsToTheList()
    {
        _status = RconConnectionStatus.Error;
        var cut = RenderResume();
        Step(cut, "connect");

        Click(cut, "wizard-discard");
        cut.Find(".modal-footer .btn-danger").Click();

        cut.WaitForAssertion(() => _client.Verify(c => c.DeleteAsync(_id), Times.Once));
        Assert.EndsWith("/servers", Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public void ADiscardThatFailsIsShownAndTheWizardStays()
    {
        _status = RconConnectionStatus.Error;
        _client.Setup(c => c.DeleteAsync(_id)).ThrowsAsync(new InvalidOperationException("boom"));
        var cut = RenderResume();
        Step(cut, "connect");

        Click(cut, "wizard-discard");
        cut.Find(".modal-footer .btn-danger").Click();

        cut.WaitForElement("[data-testid=wizard-error]");
        Step(cut, "connect");
    }

    [Fact]
    public void AConnectedServerHasNothingToDiscardAtThisStep()
    {
        var cut = ResumeConnected();

        Assert.Empty(cut.FindAll("[data-testid=wizard-discard]"));
    }

    // ---- resuming ----

    [Fact]
    public void TheResumeAddressPicksUpAtTheConnectStepForAnExistingServer()
    {
        _status = RconConnectionStatus.Reconnecting;

        var cut = RenderResume();

        Step(cut, "connect");
        Assert.Contains("Reconnecting", Text(cut, "wizard-status"));
        Assert.Equal("192.0.2.10", cut.Find("[data-testid=wizard-host]").GetAttribute("value"));
        _client.Verify(c => c.CreateAsync(It.IsAny<CreateRustServerDto>()), Times.Never);
    }

    [Fact]
    public void ResumingAServerThatIsAlreadyConnectedLetsTheUserSimplyContinue()
    {
        var cut = ResumeConnected();

        cut.Find("[data-testid=wizard-connect-next]");
    }

    [Fact]
    public void ResumingAServerThatIsGoneSaysSoAndStartsAgain()
    {
        _client.Setup(c => c.GetByIdAsync(_id)).ThrowsAsync(ReportTestSupport.Refused(HttpStatusCode.NotFound));

        var cut = RenderResume();

        Step(cut, "basics");
        Assert.Contains("could not be found", cut.Find("[data-testid=wizard-error]").TextContent);
    }

    [Fact]
    public void ADisabledServerIsNeverCalledConnected()
    {
        _status = RconConnectionStatus.Connected;
        _client.Setup(c => c.GetByIdAsync(_id)).ReturnsAsync(() => Server(enabled: false));

        var cut = RenderResume();

        Step(cut, "connect");
        Assert.Empty(cut.FindAll("[data-testid=wizard-connected]"));
    }

    // ---- integrations ----

    [Fact]
    public void SkippingWithNothingEnteredMovesOnAndSavesNothing()
    {
        var cut = ToIntegrations();

        Assert.Equal("Skip", Text(cut, "wizard-integrations-save").Trim());
        Click(cut, "wizard-integrations-save");

        Step(cut, "reports");
        _client.Verify(c => c.UpdateAsync(It.IsAny<Guid>(), It.IsAny<UpdateRustServerDto>()), Times.Never);
    }

    [Fact]
    public void ASteamKeyIsSavedOnAFullRecordThatKeepsEverythingElse()
    {
        var cut = ToIntegrations();
        Type(cut, "wizard-steam-key", "STEAMKEY");
        Assert.Equal("Save and continue", Text(cut, "wizard-integrations-save").Trim());

        Click(cut, "wizard-integrations-save");

        Step(cut, "reports");
        _client.Verify(c => c.UpdateAsync(_id, It.Is<UpdateRustServerDto>(d =>
            d.SteamApiKey == "STEAMKEY" && d.GeolocationApiKey == null && d.Name == "My server" && d.Host == "192.0.2.10"
            && d.Port == 28016 && d.RconPassword == null)), Times.Once);
    }

    [Fact]
    public void ChoosingAGeolocationProviderWithAKeySavesBoth()
    {
        var cut = ToIntegrations();
        cut.Find("[data-testid=wizard-geo-provider]").Change("IpHubInfo");
        Type(cut, "wizard-geo-key", "GEOKEY");

        Click(cut, "wizard-integrations-save");

        Step(cut, "reports");
        _client.Verify(c => c.UpdateAsync(_id, It.Is<UpdateRustServerDto>(d =>
            d.GeolocationProvider == GeolocationProviderKind.IpHubInfo && d.GeolocationApiKey == "GEOKEY")), Times.Once);
    }

    [Fact]
    public void AFailedSaveKeepsTheUserOnTheStepWithTheReason()
    {
        _client.Setup(c => c.UpdateAsync(_id, It.IsAny<UpdateRustServerDto>())).ThrowsAsync(ReportTestSupport.Refused(HttpStatusCode.BadRequest, "Bad key format."));
        var cut = ToIntegrations();
        Type(cut, "wizard-steam-key", "STEAMKEY");

        Click(cut, "wizard-integrations-save");

        Assert.Contains("Bad key format.", cut.WaitForElement("[data-testid=wizard-error]").TextContent);
        Step(cut, "integrations");
    }

    [Fact]
    public void TheSteamVerifyButtonAsksAboutTheKeyThatWasTypedAndSavesNothing()
    {
        _integration.Setup(c => c.VerifyKeyAsync(It.IsAny<VerifyIntegrationKeyRequest>()))
            .ReturnsAsync(new VerifyIntegrationKeyResultDto { Verdict = IntegrationKeyVerdict.Valid });
        var cut = ToIntegrations();
        Assert.True(cut.Find("[data-testid=verify-steam]").HasAttribute("disabled")); // nothing typed yet
        Type(cut, "wizard-steam-key", "STEAMKEY");

        Click(cut, "verify-steam");

        Assert.Contains("accepted", cut.WaitForElement("[data-testid=verify-steam-result]").TextContent);
        _integration.Verify(c => c.VerifyKeyAsync(It.Is<VerifyIntegrationKeyRequest>(r => r.Kind == IntegrationKeyKind.SteamWebApi && r.Key == "STEAMKEY")), Times.Once);
        _client.Verify(c => c.UpdateAsync(It.IsAny<Guid>(), It.IsAny<UpdateRustServerDto>()), Times.Never);
    }

    [Fact]
    public void TheGeolocationVerifyButtonOnlyExistsOnceAProviderIsChosen()
    {
        var cut = ToIntegrations();
        Assert.Empty(cut.FindAll("[data-testid=verify-geo]"));

        cut.Find("[data-testid=wizard-geo-provider]").Change("IpInfoIo");

        cut.WaitForElement("[data-testid=verify-geo]");
    }

    [Fact]
    public void ARejectedKeyIsShownAsRejectedButCanStillBeSaved()
    {
        _integration.Setup(c => c.VerifyKeyAsync(It.IsAny<VerifyIntegrationKeyRequest>()))
            .ReturnsAsync(new VerifyIntegrationKeyResultDto { Verdict = IntegrationKeyVerdict.InvalidKey });
        var cut = ToIntegrations();
        Type(cut, "wizard-steam-key", "BADKEY");

        Click(cut, "verify-steam");

        Assert.Contains("rejected", cut.WaitForElement("[data-testid=verify-steam-result]").TextContent);
        Assert.False(cut.Find("[data-testid=wizard-integrations-save]").HasAttribute("disabled"));
    }

    [Fact]
    public void AlreadyConfiguredIsShownWhenTheServerHasASteamKey()
    {
        _status = RconConnectionStatus.Connected;
        _client.Setup(c => c.GetByIdAsync(_id)).ReturnsAsync(() => Server(steamKey: true));
        var cut = RenderResume();
        cut.WaitForElement("[data-testid=wizard-connect-next]");
        Click(cut, "wizard-connect-next");

        Assert.Contains("Already configured", cut.Find("[data-testid=wizard-steam-key]").GetAttribute("placeholder"));
    }

    // ---- reports ----

    [Fact]
    public void TheReportsStepShowsTheAddressStraightAwayAndCanBeSkipped()
    {
        var cut = ToReports();

        cut.WaitForElement("[data-testid=report-forwarding-url]");
        _client.Verify(c => c.GetReportForwardingAsync(_id), Times.Once);

        Click(cut, "wizard-reports-next");
        Step(cut, "plugin");
    }

    [Fact]
    public void ACallerWhoCannotSeeTheAddressCanStillFinish()
    {
        _client.Setup(c => c.GetReportForwardingAsync(_id)).ThrowsAsync(ReportTestSupport.Forbidden);
        var cut = ToReports();

        cut.WaitForElement("[data-testid=report-forwarding-forbidden]");
        Click(cut, "wizard-reports-next");
        Step(cut, "plugin");
    }

    // ---- plugin ----

    [Fact]
    public void ReachingTheEndSavesThatTheWizardWasFinishedOnceForThisServer()
    {
        _client.Setup(c => c.CompleteSetupAsync(_id)).ReturnsAsync(Server());
        var cut = ToPlugin();

        Click(cut, "wizard-plugin-skip");

        Step(cut, "done");
        _client.Verify(c => c.CompleteSetupAsync(_id), Times.Once);
    }

    [Fact]
    public void LeavingBeforeTheEndSavesNothingSoTheServerKeepsItsFinishSetupLink()
    {
        var cut = ToPlugin();

        Assert.NotNull(cut);
        _client.Verify(c => c.CompleteSetupAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public void FailingToSaveTheCompletionShowsNoErrorAndTheEndStillShows()
    {
        _client.Setup(c => c.CompleteSetupAsync(_id)).ThrowsAsync(new HttpRequestException("boom"));
        var cut = ToPlugin();

        Click(cut, "wizard-plugin-skip");

        Step(cut, "done");
        Assert.Empty(cut.FindAll("[data-testid=wizard-error]"));
    }

    [Fact]
    public void DecliningThePluginGoesStraightToTheEnd()
    {
        var cut = ToPlugin();

        Click(cut, "wizard-plugin-skip");

        Step(cut, "done");
        Assert.Empty(cut.FindAll("[data-testid=done-plugin-pending]"));
    }

    [Fact]
    public void AcceptingThePluginShowsAChecklistWhereNothingIsTickedYet()
    {
        var cut = ToPluginChecklist();

        foreach (var check in new[] { "check-installed", "check-responding", "check-signature", "check-current" })
        {
            Assert.Empty(cut.FindAll($"[data-testid={check}] .bi-check-circle-fill"));
        }

        Assert.Empty(cut.FindAll("[data-testid=wizard-plugin-ready]"));
        Assert.Equal("Next anyway", Text(cut, "wizard-plugin-next").Trim());
    }

    [Theory]
    [InlineData(ServerModFramework.Carbon, "carbon/plugins folder")]
    [InlineData(ServerModFramework.Oxide, "oxide/plugins folder")]
    [InlineData(ServerModFramework.None, "carbon/plugins (Carbon) or oxide/plugins (Oxide)")]
    public void TheInstallInstructionNamesTheFolderForTheFrameworkTheServerRuns(ServerModFramework framework, string expected)
    {
        _framework = framework;
        var cut = ToPluginChecklist();

        cut.WaitForAssertion(() => Assert.Contains(expected, cut.Find("[data-testid=step-plugin] ol").TextContent));
    }

    private static ServerPluginStatusDto GoodStatus() => new()
    {
        PluginVersion = "0.8.0", ProtocolVersion = 1, SigningState = PluginSigningStates.Valid, SigningKeyMatchesThisPanel = true,
        Capabilities = ["config"], UpdateAvailable = false
    };

    [Fact]
    public void ARespondingCorrectlySignedPluginTicksEverythingAndOffersFinish()
    {
        _framework = ServerModFramework.Carbon;
        _client.Setup(c => c.GetPluginsAsync(_id)).ReturnsAsync(() => PluginList(RustArchonPlugin.Name));
        _client.Setup(c => c.GetPluginStatusAsync(_id)).ReturnsAsync(() => Status(GoodStatus()));
        var cut = ToPluginChecklist();

        cut.WaitForElement("[data-testid=wizard-plugin-ready]", TimeSpan.FromSeconds(5));

        foreach (var check in new[] { "check-installed", "check-responding", "check-signature", "check-current" })
        {
            cut.Find($"[data-testid={check}] .bi-check-circle-fill");
        }

        Assert.Contains("0.8.0", Text(cut, "wizard-plugin-ready"));
        Assert.Equal("Next", Text(cut, "wizard-plugin-next").Trim());
    }

    [Fact]
    public void APluginInTheListThatHasNotAnsweredIsInstalledButNotWorking()
    {
        _client.Setup(c => c.GetPluginsAsync(_id)).ReturnsAsync(() => PluginList(RustArchonPlugin.Name));
        var cut = ToPluginChecklist();

        cut.WaitForElement("[data-testid=check-installed] .bi-check-circle-fill", TimeSpan.FromSeconds(5));
        Assert.Empty(cut.FindAll("[data-testid=check-responding] .bi-check-circle-fill"));
        Assert.Empty(cut.FindAll("[data-testid=wizard-plugin-ready]"));
    }

    [Theory]
    [InlineData(PluginSigningStates.Invalid, "changed, or signed by a different Panel")]
    [InlineData(PluginSigningStates.Unsigned, "not signed by a Panel")]
    [InlineData(PluginSigningStates.Unknown, "has not said whether")]
    public void APluginWhoseFileIsNotOneThisPanelSignedIsNeverReadyAndSaysWhy(string signing, string expected)
    {
        var dto = GoodStatus();
        dto.SigningState = signing;
        dto.SigningKeyMatchesThisPanel = false;
        _client.Setup(c => c.GetPluginsAsync(_id)).ReturnsAsync(() => PluginList(RustArchonPlugin.Name));
        _client.Setup(c => c.GetPluginStatusAsync(_id)).ReturnsAsync(() => Status(dto));
        var cut = ToPluginChecklist();

        var detail = cut.WaitForElement("[data-testid=check-signature-detail]", TimeSpan.FromSeconds(5));

        Assert.Contains(expected, detail.TextContent);
        Assert.Empty(cut.FindAll("[data-testid=wizard-plugin-ready]"));
        Assert.Equal("Next anyway", Text(cut, "wizard-plugin-next").Trim());
    }

    [Fact]
    public void ValidlySignedByADifferentPanelIsNotReadyEither()
    {
        var dto = GoodStatus();
        dto.SigningKeyMatchesThisPanel = false;
        _client.Setup(c => c.GetPluginsAsync(_id)).ReturnsAsync(() => PluginList(RustArchonPlugin.Name));
        _client.Setup(c => c.GetPluginStatusAsync(_id)).ReturnsAsync(() => Status(dto));
        var cut = ToPluginChecklist();

        cut.WaitForElement("[data-testid=check-signature-detail]", TimeSpan.FromSeconds(5));
        Assert.Empty(cut.FindAll("[data-testid=wizard-plugin-ready]"));
    }

    [Fact]
    public void AnUpdateBeingAvailableIsNotAFailureButIsNotTickedEither()
    {
        var dto = GoodStatus();
        dto.UpdateAvailable = true;
        _client.Setup(c => c.GetPluginsAsync(_id)).ReturnsAsync(() => PluginList(RustArchonPlugin.Name));
        _client.Setup(c => c.GetPluginStatusAsync(_id)).ReturnsAsync(() => Status(dto));
        var cut = ToPluginChecklist();

        cut.WaitForElement("[data-testid=wizard-plugin-ready]", TimeSpan.FromSeconds(5));
        Assert.Empty(cut.FindAll("[data-testid=check-current] .bi-check-circle-fill"));
        cut.Find("[data-testid=check-current] .bi-x-circle-fill");
    }

    [Fact]
    public void AStatusThatIsStaleAfterAnUninstallIsNotTrusted()
    {
        // The status endpoint can keep answering after the plugin is removed; only a plugin that is also in the list counts.
        _client.Setup(c => c.GetPluginsAsync(_id)).ReturnsAsync(() => PluginList("Some other plugin"));
        _client.Setup(c => c.GetPluginStatusAsync(_id)).ReturnsAsync(() => Status(GoodStatus()));
        var cut = ToPluginChecklist();

        cut.WaitForAssertion(() => _client.Verify(c => c.GetPluginStatusAsync(_id), Times.AtLeastOnce), TimeSpan.FromSeconds(5));
        Assert.Empty(cut.FindAll("[data-testid=check-installed] .bi-check-circle-fill"));
        Assert.Empty(cut.FindAll("[data-testid=check-responding] .bi-check-circle-fill"));
        Assert.Empty(cut.FindAll("[data-testid=wizard-plugin-ready]"));
    }

    [Fact]
    public void DownloadingThePluginFetchesTheSignedFileAndHandsItToTheBrowser()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        _client.Setup(c => c.DownloadPluginAsync()).ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        var module = JSInterop.SetupModule("./js/fileDownload.js");
        module.SetupVoid("downloadBytes", _ => true).SetVoidResult();
        var cut = ToPluginChecklist();

        Click(cut, "wizard-plugin-download");

        cut.WaitForAssertion(() => module.VerifyInvoke("downloadBytes"));
        Assert.Equal("RustArchon.cs", module.Invocations["downloadBytes"].Single().Arguments[0]);
        Assert.Equal(Convert.ToBase64String(bytes), module.Invocations["downloadBytes"].Single().Arguments[1]);
    }

    [Fact]
    public void AFailedDownloadIsShown()
    {
        _client.Setup(c => c.DownloadPluginAsync()).ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var cut = ToPluginChecklist();

        Click(cut, "wizard-plugin-download");

        Assert.Contains("Failed to download", cut.WaitForElement("[data-testid=wizard-plugin-download-error]").TextContent);
    }

    [Fact]
    public void GoingBackFromTheChecklistReturnsToTheQuestion()
    {
        var cut = ToPluginChecklist();

        cut.Find("[data-testid=step-plugin] [data-testid=wizard-back]").Click();

        cut.WaitForElement("[data-testid=wizard-plugin-yes]");
    }

    // ---- finishing ----

    [Fact]
    public void TheEndSummarisesWhatHappenedAndLinksToTheServer()
    {
        var cut = ToPlugin();
        Click(cut, "wizard-plugin-skip");

        var done = Step(cut, "done");

        Assert.Contains("'My server' is set up.", done.TextContent);
        Assert.Contains("connected", done.TextContent);
        Assert.Equal($"servers/{_id}", cut.Find("[data-testid=wizard-open-server]").GetAttribute("href"));
        cut.Find("[data-testid=wizard-add-another]");
    }

    [Fact]
    public void SavedIntegrationsAreMentionedAtTheEnd()
    {
        var cut = ToIntegrations();
        Type(cut, "wizard-steam-key", "STEAMKEY");
        Click(cut, "wizard-integrations-save");
        Step(cut, "reports");
        Click(cut, "wizard-reports-next");
        Step(cut, "plugin");
        Click(cut, "wizard-plugin-skip");

        Step(cut, "done");

        cut.Find("[data-testid=done-integrations]");
    }

    [Fact]
    public void FinishingWithAPluginThatIsNotWorkingYetSaysWhereToSeeItLater()
    {
        var cut = ToPluginChecklist();

        Click(cut, "wizard-plugin-next");
        Step(cut, "updates");
        Click(cut, "wizard-updates-skip");

        Step(cut, "done");
        Assert.Contains("Plugins tab", Text(cut, "done-plugin-pending"));
    }

    [Fact]
    public void TheProgressListMarksWhereTheUserIs()
    {
        var cut = ToIntegrations();

        Assert.Contains("text-bg-primary", cut.Find("[data-testid=progress-integrations]").ClassList);
        Assert.Contains("text-bg-success", cut.Find("[data-testid=progress-connect]").ClassList);
        Assert.Contains("text-bg-secondary", cut.Find("[data-testid=progress-plugin]").ClassList);
    }

    [Fact]
    public async Task LeavingThePageStopsThePollingSoNothingKeepsCallingTheApi()
    {
        _status = RconConnectionStatus.Connecting;
        var cut = RenderResume();
        Step(cut, "connect");
        await Task.Delay(80);

        await DisposeComponentsAsync();
        await Task.Delay(60); // an iteration that was already running when the page was left may still finish
        _client.Invocations.Clear();
        await Task.Delay(120);

        _client.Verify(c => c.GetByIdAsync(It.IsAny<Guid>()), Times.Never);
    }

    // ---- updates ----

    private IRenderedComponent<AddServerWizard> ToUpdates()
    {
        var cut = ToPluginChecklist();
        Click(cut, "wizard-plugin-next");
        Step(cut, "updates");
        return cut;
    }

    private UpdateServerPluginSettingsDto? _savedSettings;

    private void GivenSettingsSaveWorks(bool recording = true, bool combat = true)
    {
        _client.Setup(c => c.UpdatePluginSettingsAsync(_id, It.IsAny<UpdateServerPluginSettingsDto>()))
            .Callback<Guid, UpdateServerPluginSettingsDto>((_, dto) => _savedSettings = dto)
            .ReturnsAsync((Guid _, UpdateServerPluginSettingsDto dto) =>
            {
                var saved = Server();
                saved.PluginRecordingEnabled = dto.RecordingEnabled!.Value;
                saved.PluginCombatLogEnabled = dto.CombatLogEnabled!.Value;
                saved.PluginUpdatesEnabled = dto.UpdatesEnabled!.Value;
                saved.PluginAutoUpdateEnabled = dto.AutoUpdateEnabled ?? false;
                return saved;
            });
    }

    [Fact]
    public void AfterThePluginStepTheUpdatesStepGathersEveryUpdateSettingOnOnePage()
    {
        var cut = ToUpdates();

        Assert.NotNull(cut.Find("[data-testid=wizard-allow-updates]"));
        Assert.NotNull(cut.Find("[data-testid=wizard-auto-update]"));
        Assert.NotNull(cut.Find("[data-testid=wizard-updater-note]"));
        Assert.False(cut.Find("[data-testid=wizard-allow-updates]").HasAttribute("checked"));
        Assert.False(cut.Find("[data-testid=wizard-auto-update]").HasAttribute("checked"));
    }

    [Fact]
    public void EachSettingSaysWhyYouMightWantIt()
    {
        var cut = ToUpdates();

        var allow = Text(cut, "wizard-allow-updates-why");
        Assert.Contains("one click", allow);
        Assert.Contains("signed", allow);
        Assert.Contains("previous version back", allow);
        var auto = Text(cut, "wizard-auto-update-why");
        Assert.Contains("by itself", auto);
        Assert.Contains("without having to remember", auto);
        Assert.Contains("players do not need to leave", auto);
        Assert.Contains("choose when your server changes", auto);
        Assert.Contains("later on the server's Plugins tab", cut.Find("[data-testid=step-updates] p").TextContent);
    }

    [Fact]
    public void TheAutomaticSwitchWaitsForTheAllowSwitch()
    {
        var cut = ToUpdates();
        Assert.True(cut.Find("[data-testid=wizard-auto-update]").HasAttribute("disabled"));
        Assert.Contains("Turn on Allow updates first", Text(cut, "wizard-auto-update-needs-allow"));

        cut.Find("[data-testid=wizard-allow-updates]").Change(true);

        Assert.False(cut.Find("[data-testid=wizard-auto-update]").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll("[data-testid=wizard-auto-update-needs-allow]"));
    }

    [Fact]
    public void TurningAllowOffAgainTurnsAutomaticOffToo()
    {
        GivenSettingsSaveWorks();
        var cut = ToUpdates();
        cut.Find("[data-testid=wizard-allow-updates]").Change(true);
        cut.Find("[data-testid=wizard-auto-update]").Change(true);

        cut.Find("[data-testid=wizard-allow-updates]").Change(false);
        cut.Find("[data-testid=wizard-allow-updates]").Change(true);

        Assert.False(cut.Find("[data-testid=wizard-auto-update]").HasAttribute("checked"));
    }

    [Fact]
    public void SavingBothSwitchesOnSendsThemAndTheOthersAtTheirSavedValuesThenFinishes()
    {
        GivenSettingsSaveWorks();
        var cut = ToUpdates();
        cut.Find("[data-testid=wizard-allow-updates]").Change(true);
        cut.Find("[data-testid=wizard-auto-update]").Change(true);

        Click(cut, "wizard-updates-save");

        Step(cut, "done");
        Assert.NotNull(_savedSettings);
        Assert.True(_savedSettings!.UpdatesEnabled);
        Assert.True(_savedSettings.AutoUpdateEnabled);
        Assert.Equal(false, _savedSettings.RecordingEnabled);          // the test server's saved values, sent back unchanged
        Assert.Equal(false, _savedSettings.CombatLogEnabled);
        Assert.Contains("installed automatically", Text(cut, "done-updates"));
    }

    [Fact]
    public void AllowingUpdatesWithoutAutomaticSendsAutomaticOffAndSaysOneClick()
    {
        GivenSettingsSaveWorks();
        var cut = ToUpdates();
        cut.Find("[data-testid=wizard-allow-updates]").Change(true);

        Click(cut, "wizard-updates-save");

        Step(cut, "done");
        Assert.True(_savedSettings!.UpdatesEnabled);
        Assert.False(_savedSettings.AutoUpdateEnabled);
        Assert.Contains("one click", Text(cut, "done-updates"));
    }

    [Fact]
    public void SkippingChangesNothingAndSaysNothingAboutUpdates()
    {
        GivenSettingsSaveWorks();
        var cut = ToUpdates();

        Click(cut, "wizard-updates-skip");

        Step(cut, "done");
        Assert.Null(_savedSettings);
        Assert.Empty(cut.FindAll("[data-testid=done-updates]"));
    }

    [Fact]
    public void ANewServerThatComesBackWithBothSwitchesOnShowsThemOnAndKeepingThemChangesNothing()
    {
        // The Api's default for a new server is both on (Scott, 2026-09-20): the page presents that as the starting point, says so, and
        // "Keep as they are" is a real choice that saves nothing and still tells the person what is on.
        GivenSettingsSaveWorks();
        _client.Setup(c => c.GetByIdAsync(_id)).ReturnsAsync(() =>
        {
            var server = Server();
            server.PluginUpdatesEnabled = true;
            server.PluginAutoUpdateEnabled = true;
            return server;
        });
        var cut = ToUpdates();
        Assert.True(cut.Find("[data-testid=wizard-allow-updates]").HasAttribute("checked"));
        Assert.True(cut.Find("[data-testid=wizard-auto-update]").HasAttribute("checked"));
        Assert.Contains("start on for a new server", cut.Find("[data-testid=step-updates] p").TextContent);
        Assert.Equal("Keep as they are", Text(cut, "wizard-updates-skip").Trim());

        Click(cut, "wizard-updates-skip");

        Step(cut, "done");
        Assert.Null(_savedSettings);
        Assert.Contains("installed automatically", Text(cut, "done-updates"));
    }

    [Fact]
    public void TurningBothOffOnAServerThatHasThemOnSavesThatAndSaysNothingAboutUpdates()
    {
        GivenSettingsSaveWorks();
        _client.Setup(c => c.GetByIdAsync(_id)).ReturnsAsync(() =>
        {
            var server = Server();
            server.PluginUpdatesEnabled = true;
            server.PluginAutoUpdateEnabled = true;
            return server;
        });
        var cut = ToUpdates();

        cut.Find("[data-testid=wizard-allow-updates]").Change(false);
        Click(cut, "wizard-updates-save");

        Step(cut, "done");
        Assert.False(_savedSettings!.UpdatesEnabled);
        Assert.False(_savedSettings.AutoUpdateEnabled);
        Assert.Empty(cut.FindAll("[data-testid=done-updates]"));
    }

    [Fact]
    public void SavingWithBothLeftOffMakesNoCall()
    {
        GivenSettingsSaveWorks();
        var cut = ToUpdates();

        Click(cut, "wizard-updates-save");

        Step(cut, "done");
        Assert.Null(_savedSettings);
        _client.Verify(c => c.UpdatePluginSettingsAsync(It.IsAny<Guid>(), It.IsAny<UpdateServerPluginSettingsDto>()), Times.Never);
    }

    [Fact]
    public void AFailedSaveStaysOnTheStepAndSaysSoWithoutLosingTheChoices()
    {
        _client.Setup(c => c.UpdatePluginSettingsAsync(_id, It.IsAny<UpdateServerPluginSettingsDto>())).ThrowsAsync(new HttpRequestException("boom"));
        var cut = ToUpdates();
        cut.Find("[data-testid=wizard-allow-updates]").Change(true);
        cut.Find("[data-testid=wizard-auto-update]").Change(true);

        Click(cut, "wizard-updates-save");

        cut.WaitForElement("[data-testid=wizard-error]");
        Assert.NotEmpty(cut.FindAll("[data-testid=step-updates]"));
        Assert.True(cut.Find("[data-testid=wizard-allow-updates]").HasAttribute("checked"));
        Assert.True(cut.Find("[data-testid=wizard-auto-update]").HasAttribute("checked"));
    }

    [Fact]
    public void BackReturnsToThePluginChecklist()
    {
        var cut = ToUpdates();

        Click(cut, "wizard-back");

        cut.WaitForElement("[data-testid=wizard-plugin-checklist]");
    }

    [Fact]
    public void TheUpdaterNoteSaysWhetherTheHelperIsAlreadyThereOrWillBeInstalledForYou()
    {
        var missing = ToUpdates();
        Assert.NotEmpty(missing.FindAll("[data-testid=wizard-updater-missing]"));
        Assert.Contains("can install it for you", Text(missing, "wizard-updater-missing"));

        _framework = ServerModFramework.Carbon;
        _client.Setup(c => c.GetPluginsAsync(_id)).ReturnsAsync(() => PluginList(RustArchonPlugin.Name));
        var dto = GoodStatus();
        dto.UpdaterInstalled = true;
        _client.Setup(c => c.GetPluginStatusAsync(_id)).ReturnsAsync(() => Status(dto));
        var installed = ToPluginChecklist();
        installed.WaitForElement("[data-testid=wizard-plugin-ready]", TimeSpan.FromSeconds(5));     // the wizard has read the plugin's status by now
        Click(installed, "wizard-plugin-next");
        Step(installed, "updates");
        Assert.NotEmpty(installed.FindAll("[data-testid=wizard-updater-installed]"));
        Assert.Empty(installed.FindAll("[data-testid=wizard-updater-missing]"));
    }

    [Fact]
    public void AServerThatAlreadyHasTheSettingsOnShowsThemOn()
    {
        _client.Setup(c => c.GetByIdAsync(_id)).ReturnsAsync(() =>
        {
            var server = Server();
            server.PluginUpdatesEnabled = true;
            server.PluginAutoUpdateEnabled = true;
            return server;
        });
        var cut = ToUpdates();

        Assert.True(cut.Find("[data-testid=wizard-allow-updates]").HasAttribute("checked"));
        Assert.True(cut.Find("[data-testid=wizard-auto-update]").HasAttribute("checked"));
    }

    [Fact]
    public void SkippingThePluginSkipsTheUpdatesStepAndItIsNotShownInTheProgressList()
    {
        var cut = ToPlugin();
        Assert.Empty(cut.FindAll("[data-testid=progress-updates]"));

        Click(cut, "wizard-plugin-skip");

        Step(cut, "done");
        Assert.Empty(cut.FindAll("[data-testid=step-updates]"));
    }

    [Fact]
    public void ChoosingThePluginAddsTheUpdatesStepToTheProgressListAfterIt()
    {
        var cut = ToPluginChecklist();

        var progress = cut.FindAll("[data-testid=wizard-progress] li").Select(li => li.TextContent.Trim()).ToList();

        Assert.Equal("5. Plugin", progress[4]);
        Assert.Equal("6. Updates", progress[5]);
        Assert.Equal("7. Done", progress[6]);
    }
}
