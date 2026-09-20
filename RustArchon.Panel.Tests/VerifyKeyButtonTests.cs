// Copyright ©2026 Scott Blomfield

using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RustArchon.Panel.Clients;
using RustArchon.Panel.Components.Pages.Servers;
using RustArchon.Shared.DTOs;

namespace RustArchon.Panel.Tests;

/// <summary>
/// The Verify button beside a third-party key field. It only ever says "accepted" when the provider positively said so, "could not
/// confirm" is a different message from "valid", and typing a different key clears an answer that was about the old one.
/// </summary>
public class VerifyKeyButtonTests : BunitContext
{
    private readonly Mock<IIntegrationApiClient> _client = new();

    public VerifyKeyButtonTests()
    {
        Services.AddSingleton(_client.Object);
        Services.AddSingleton(ReportTestSupport.Localizer());
    }

    private void GivenVerdict(IntegrationKeyVerdict verdict) =>
        _client.Setup(c => c.VerifyKeyAsync(It.IsAny<VerifyIntegrationKeyRequest>()))
            .ReturnsAsync(new VerifyIntegrationKeyResultDto { Verdict = verdict });

    private IRenderedComponent<VerifyKeyButton> Render(
        string? key = "the-key", IntegrationKeyKind kind = IntegrationKeyKind.SteamWebApi,
        GeolocationProviderKind provider = GeolocationProviderKind.None, List<IntegrationKeyVerdict?>? told = null) =>
        Render<VerifyKeyButton>(p => p
            .Add(x => x.Kind, kind).Add(x => x.Provider, provider).Add(x => x.Key, key)
            .Add(x => x.VerdictChanged, (IntegrationKeyVerdict? v) => told?.Add(v)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithNoKeyTheButtonIsDisabledAndNothingCanBeSent(string? key)
    {
        var cut = Render(key);

        Assert.True(cut.Find("[data-testid=verify-key]").HasAttribute("disabled"));
    }

    [Fact]
    public void ItAsksTheApiAboutTheKindProviderAndKeyThatWereTyped()
    {
        GivenVerdict(IntegrationKeyVerdict.Valid);
        var cut = Render("geo-key", IntegrationKeyKind.Geolocation, GeolocationProviderKind.IpHubInfo);

        cut.Find("[data-testid=verify-key]").Click();

        _client.Verify(c => c.VerifyKeyAsync(It.Is<VerifyIntegrationKeyRequest>(r =>
            r.Kind == IntegrationKeyKind.Geolocation && r.Provider == GeolocationProviderKind.IpHubInfo && r.Key == "geo-key")), Times.Once);
    }

    [Theory]
    [InlineData(IntegrationKeyVerdict.Valid, "The provider accepted this key.", "text-success")]
    [InlineData(IntegrationKeyVerdict.InvalidKey, "The provider rejected this key.", "text-danger")]
    [InlineData(IntegrationKeyVerdict.RateLimited, "The provider is limiting requests right now. Try again in a minute.", "text-warning")]
    [InlineData(IntegrationKeyVerdict.Unreachable, "We could not reach the provider. Try again.", "text-warning")]
    [InlineData(IntegrationKeyVerdict.Unknown, "We could not confirm this key with the provider. You can still save it.", "text-muted")]
    public void EachVerdictHasItsOwnMessageAndOnlyValidIsGreen(IntegrationKeyVerdict verdict, string message, string cssClass)
    {
        GivenVerdict(verdict);
        var cut = Render();

        cut.Find("[data-testid=verify-key]").Click();

        var result = cut.WaitForElement("[data-testid=verify-key-result]");
        Assert.Contains(message, result.TextContent);
        Assert.Contains(cssClass, result.ClassList);
    }

    [Fact]
    public void CouldNotConfirmIsNeverPresentedAsValid()
    {
        GivenVerdict(IntegrationKeyVerdict.Unknown);
        var cut = Render();

        cut.Find("[data-testid=verify-key]").Click();

        var result = cut.WaitForElement("[data-testid=verify-key-result]");
        Assert.DoesNotContain("accepted", result.TextContent);
        Assert.DoesNotContain("text-success", result.ClassList);
    }

    [Fact]
    public void ThePageIsToldEachVerdict()
    {
        var told = new List<IntegrationKeyVerdict?>();
        GivenVerdict(IntegrationKeyVerdict.InvalidKey);
        var cut = Render(told: told);

        cut.Find("[data-testid=verify-key]").Click();

        cut.WaitForAssertion(() => Assert.Equal(IntegrationKeyVerdict.InvalidKey, Assert.Single(told)));
    }

    [Fact]
    public void ABrowserTellingItToSlowDownIsShownAsRateLimited()
    {
        _client.Setup(c => c.VerifyKeyAsync(It.IsAny<VerifyIntegrationKeyRequest>()))
            .ThrowsAsync(ReportTestSupport.Refused(HttpStatusCode.TooManyRequests));
        var cut = Render();

        cut.Find("[data-testid=verify-key]").Click();

        Assert.Contains("limiting requests", cut.WaitForElement("[data-testid=verify-key-result]").TextContent);
    }

    [Fact]
    public void ACallerWhoMayNotCheckKeysIsToldSo()
    {
        _client.Setup(c => c.VerifyKeyAsync(It.IsAny<VerifyIntegrationKeyRequest>())).ThrowsAsync(ReportTestSupport.Forbidden);
        var cut = Render();

        cut.Find("[data-testid=verify-key]").Click();

        Assert.Contains("not allowed", cut.WaitForElement("[data-testid=verify-key-result]").TextContent);
    }

    [Fact]
    public void AFailedCheckSaysSoAndNeverClaimsTheKeyIsGood()
    {
        _client.Setup(c => c.VerifyKeyAsync(It.IsAny<VerifyIntegrationKeyRequest>())).ThrowsAsync(new System.Exception("boom"));
        var cut = Render();

        cut.Find("[data-testid=verify-key]").Click();

        var result = cut.WaitForElement("[data-testid=verify-key-result]");
        Assert.Contains("could not be completed", result.TextContent);
        Assert.DoesNotContain("accepted", result.TextContent);
    }

    [Fact]
    public void TypingADifferentKeyClearsAnAnswerThatWasAboutTheOldOne()
    {
        var told = new List<IntegrationKeyVerdict?>();
        GivenVerdict(IntegrationKeyVerdict.Valid);
        var cut = Render("old-key", told: told);
        cut.Find("[data-testid=verify-key]").Click();
        cut.WaitForElement("[data-testid=verify-key-result]");

        cut.Render(p => p.Add(x => x.Key, "new-key"));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=verify-key-result]")));
        Assert.Null(told[^1]); // and the page is told the verdict no longer holds
    }

    [Fact]
    public void AnAnswerSurvivesARerenderThatDoesNotChangeTheKey()
    {
        GivenVerdict(IntegrationKeyVerdict.Valid);
        var cut = Render("same-key");
        cut.Find("[data-testid=verify-key]").Click();
        cut.WaitForElement("[data-testid=verify-key-result]");

        cut.Render(p => p.Add(x => x.Key, "same-key"));

        Assert.Single(cut.FindAll("[data-testid=verify-key-result]"));
    }

    [Fact]
    public void ChangingTheProviderAlsoClearsTheAnswer()
    {
        GivenVerdict(IntegrationKeyVerdict.Valid);
        var cut = Render("k", IntegrationKeyKind.Geolocation, GeolocationProviderKind.IpHubInfo);
        cut.Find("[data-testid=verify-key]").Click();
        cut.WaitForElement("[data-testid=verify-key-result]");

        cut.Render(p => p.Add(x => x.Provider, GeolocationProviderKind.IpInfoIo));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=verify-key-result]")));
    }
}
