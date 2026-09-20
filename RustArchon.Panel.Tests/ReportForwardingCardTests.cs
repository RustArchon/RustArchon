// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RustArchon.Panel.Clients;
using RustArchon.Panel.Components.Pages.Servers;
using RustArchon.Shared.DTOs;

namespace RustArchon.Panel.Tests;

/// <summary>
/// The card that shows a server's report address (ADR-0001). The address contains the secret, so it is fetched - which is what mints the
/// secret - only when someone asks, and a caller without the permission simply gets no card. Changing it needs a confirmation, and a
/// "confirmed" badge is only ever shown when the game server really answered with the address.
/// </summary>
public class ReportForwardingCardTests : BunitContext
{
    private readonly Mock<IRustServerApiClient> _client = new();
    private readonly Guid _serverId = Guid.NewGuid();
    private const string Url = "https://panel.example/ingest/reports/11111111-1111-1111-1111-111111111111/SECRET";

    public ReportForwardingCardTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_client.Object);
        Services.AddSingleton(ReportTestSupport.Localizer());
        GivenAddress();
    }

    private ReportForwardingDto Address(string url = Url, bool verified = false, DateTimeOffset? last = null) => new()
    {
        Url = url, Command = $"server.reportsServerEndpoint \"{url}\"", Verified = verified,
        VerifiedAtUtc = verified ? new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero) : null, LastReportReceivedAtUtc = last
    };

    private void GivenAddress(ReportForwardingDto? dto = null) =>
        _client.Setup(c => c.GetReportForwardingAsync(_serverId)).ReturnsAsync(dto ?? Address());

    private void GivenVerdict(ReportForwardingVerdict verdict, string? observed = null) =>
        _client.Setup(c => c.VerifyReportForwardingAsync(_serverId))
            .ReturnsAsync(new VerifyReportForwardingResultDto { Verdict = verdict, ObservedRedacted = observed });

    private IRenderedComponent<ReportForwardingCard> RenderCard(bool expanded = false) =>
        Render<ReportForwardingCard>(p => p.Add(x => x.ServerId, _serverId).Add(x => x.InitiallyExpanded, expanded));

    private static void Open(IRenderedComponent<ReportForwardingCard> cut) => cut.Find("[data-testid=report-forwarding-toggle]").Click();

    // ---- opening it ----

    [Fact]
    public void ItStartsClosedAndFetchesNothingSoMerelyLookingMintsNoSecret()
    {
        var cut = RenderCard();

        Assert.Empty(cut.FindAll("[data-testid=report-forwarding-url]"));
        _client.Verify(c => c.GetReportForwardingAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public void OpeningItShowsTheAddressAndTheReadyToPasteCommand()
    {
        var cut = RenderCard();

        Open(cut);

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(Url, cut.Find("[data-testid=report-forwarding-url]").GetAttribute("value"));
            Assert.Equal($"server.reportsServerEndpoint \"{Url}\"", cut.Find("[data-testid=report-forwarding-command]").GetAttribute("value"));
        });
        _client.Verify(c => c.GetReportForwardingAsync(_serverId), Times.Once);
    }

    [Fact]
    public void ClosingItAndReopeningItDoesNotFetchAgain()
    {
        var cut = RenderCard();

        Open(cut);
        cut.WaitForElement("[data-testid=report-forwarding-url]");
        Open(cut);
        Open(cut);

        _client.Verify(c => c.GetReportForwardingAsync(_serverId), Times.Once);
    }

    [Fact]
    public void TheWizardCanOpenItStraightAway()
    {
        var cut = RenderCard(expanded: true);

        cut.WaitForElement("[data-testid=report-forwarding-url]");
        _client.Verify(c => c.GetReportForwardingAsync(_serverId), Times.Once);
    }

    [Fact]
    public void ACallerWithoutThePermissionSeesNoAddressAndNoButton()
    {
        _client.Setup(c => c.GetReportForwardingAsync(_serverId)).ThrowsAsync(ReportTestSupport.Forbidden);
        var cut = RenderCard();

        Open(cut);

        cut.WaitForElement("[data-testid=report-forwarding-forbidden]");
        Assert.Empty(cut.FindAll("[data-testid=report-forwarding-url]"));
        Assert.Empty(cut.FindAll("[data-testid=report-forwarding-toggle]"));
    }

    [Fact]
    public void AFailureToLoadIsSaidPlainlyAndShowsNoAddress()
    {
        _client.Setup(c => c.GetReportForwardingAsync(_serverId)).ThrowsAsync(new InvalidOperationException("boom"));
        var cut = RenderCard();

        Open(cut);

        Assert.Contains("Failed to load", cut.WaitForElement("[data-testid=report-forwarding-error]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=report-forwarding-url]"));
    }

    [Fact]
    public void ItSaysNativeForwardingMayNotCarryReportsAboutASpecificPlayer()
    {
        var cut = RenderCard(expanded: true);

        var caveat = cut.WaitForElement("[data-testid=report-forwarding-caveat]").TextContent;

        Assert.Contains("general, bug and idea", caveat);
        Assert.Contains("plugin", caveat);
    }

    // ---- copying ----

    [Fact]
    public void CopyingTheAddressUsesTheBrowsersClipboardAndSaysSo()
    {
        var cut = RenderCard(expanded: true);
        cut.WaitForElement("[data-testid=report-forwarding-url]");

        cut.Find("[data-testid=report-forwarding-copy-url]").Click();

        JSInterop.VerifyInvoke("navigator.clipboard.writeText");
        Assert.Contains("Copied", cut.Find("[data-testid=report-forwarding-copy-url]").TextContent);
        Assert.DoesNotContain("Copied", cut.Find("[data-testid=report-forwarding-copy-command]").TextContent);
    }

    // ---- checking ----

    [Fact]
    public void ANewAddressIsNotConfirmedUntilTheGameServerAnswers()
    {
        var cut = RenderCard(expanded: true);

        cut.WaitForElement("[data-testid=report-forwarding-unverified]");
        Assert.Empty(cut.FindAll("[data-testid=report-forwarding-verified]"));
    }

    [Fact]
    public void AMatchIsShownAsConfirmedAfterTheAddressIsReadBackFromTheApi()
    {
        var cut = RenderCard(expanded: true);
        cut.WaitForElement("[data-testid=report-forwarding-verify]");
        GivenVerdict(ReportForwardingVerdict.Matches);
        GivenAddress(Address(verified: true));

        cut.Find("[data-testid=report-forwarding-verify]").Click();

        cut.WaitForElement("[data-testid=report-forwarding-verified]");
        Assert.Contains("alert-success", cut.Find("[data-testid=report-forwarding-verdict]").ClassList);
        Assert.Contains("set to this address", cut.Find("[data-testid=report-forwarding-verdict]").TextContent);
    }

    [Theory]
    [InlineData(ReportForwardingVerdict.NotSet, "no report address set", "alert-danger")]
    [InlineData(ReportForwardingVerdict.Mismatch, "different address", "alert-danger")]
    [InlineData(ReportForwardingVerdict.Unreadable, "not in a form we could read", "alert-warning")]
    [InlineData(ReportForwardingVerdict.Unavailable, "could not reach your game server", "alert-warning")]
    public void ANonMatchIsNeverShownAsConfirmed(ReportForwardingVerdict verdict, string text, string cssClass)
    {
        var cut = RenderCard(expanded: true);
        cut.WaitForElement("[data-testid=report-forwarding-verify]");
        GivenVerdict(verdict);

        cut.Find("[data-testid=report-forwarding-verify]").Click();

        var result = cut.WaitForElement("[data-testid=report-forwarding-verdict]");
        Assert.Contains(text, result.TextContent);
        Assert.Contains(cssClass, result.ClassList);
        Assert.Empty(cut.FindAll("[data-testid=report-forwarding-verified]"));
    }

    [Fact]
    public void AMismatchShowsWhereTheGameServerPointsWithoutItsSecret()
    {
        var cut = RenderCard(expanded: true);
        cut.WaitForElement("[data-testid=report-forwarding-verify]");
        GivenVerdict(ReportForwardingVerdict.Mismatch, "https://elsewhere.example/hook/***");

        cut.Find("[data-testid=report-forwarding-verify]").Click();

        var text = cut.WaitForElement("[data-testid=report-forwarding-verdict]").TextContent;
        Assert.Contains("https://elsewhere.example/hook/***", text);
    }

    [Fact]
    public void ACheckThatFailsSaysSoInsteadOfGuessing()
    {
        var cut = RenderCard(expanded: true);
        cut.WaitForElement("[data-testid=report-forwarding-verify]");
        _client.Setup(c => c.VerifyReportForwardingAsync(_serverId)).ThrowsAsync(new InvalidOperationException("boom"));

        cut.Find("[data-testid=report-forwarding-verify]").Click();

        Assert.Contains("could not be completed", cut.WaitForElement("[data-testid=report-forwarding-error]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=report-forwarding-verified]"));
    }

    [Fact]
    public void WhenTheLastReportArrivedItIsShown()
    {
        GivenAddress(Address(last: new DateTimeOffset(2026, 9, 20, 9, 30, 0, TimeSpan.Zero)));
        var cut = RenderCard(expanded: true);

        Assert.Contains("last report arrived", cut.WaitForElement("[data-testid=report-forwarding-last-received]").TextContent);
    }

    // ---- changing the address ----

    [Fact]
    public void ChangingTheAddressAsksFirstAndCancellingChangesNothing()
    {
        var cut = RenderCard(expanded: true);
        cut.WaitForElement("[data-testid=report-forwarding-rotate]");

        cut.Find("[data-testid=report-forwarding-rotate]").Click();
        Assert.Contains("stops working immediately", cut.Markup);
        cut.Find(".modal-footer .btn-secondary").Click();

        _client.Verify(c => c.RotateReportForwardingAsync(It.IsAny<Guid>()), Times.Never);
        Assert.Equal(Url, cut.Find("[data-testid=report-forwarding-url]").GetAttribute("value"));
    }

    [Fact]
    public void ConfirmingReplacesTheAddressAndForgetsThePreviousCheck()
    {
        const string rotated = "https://panel.example/ingest/reports/11111111-1111-1111-1111-111111111111/NEWSECRET";
        var cut = RenderCard(expanded: true);
        cut.WaitForElement("[data-testid=report-forwarding-verify]");
        GivenVerdict(ReportForwardingVerdict.Mismatch);
        cut.Find("[data-testid=report-forwarding-verify]").Click();
        cut.WaitForElement("[data-testid=report-forwarding-verdict]");
        _client.Setup(c => c.RotateReportForwardingAsync(_serverId)).ReturnsAsync(Address(rotated));

        cut.Find("[data-testid=report-forwarding-rotate]").Click();
        cut.Find(".modal-footer .btn-danger").Click();

        cut.WaitForAssertion(() => Assert.Equal(rotated, cut.Find("[data-testid=report-forwarding-url]").GetAttribute("value")));
        Assert.Empty(cut.FindAll("[data-testid=report-forwarding-verdict]")); // the old answer was about the old address
        _client.Verify(c => c.RotateReportForwardingAsync(_serverId), Times.Once);
    }

    [Fact]
    public void AFailedChangeKeepsTheOldAddressOnScreenAndSaysSo()
    {
        var cut = RenderCard(expanded: true);
        cut.WaitForElement("[data-testid=report-forwarding-rotate]");
        _client.Setup(c => c.RotateReportForwardingAsync(_serverId)).ThrowsAsync(new InvalidOperationException("boom"));

        cut.Find("[data-testid=report-forwarding-rotate]").Click();
        cut.Find(".modal-footer .btn-danger").Click();

        Assert.Contains("Failed to change", cut.WaitForElement("[data-testid=report-forwarding-error]").TextContent);
        Assert.Equal(Url, cut.Find("[data-testid=report-forwarding-url]").GetAttribute("value"));
    }
}
