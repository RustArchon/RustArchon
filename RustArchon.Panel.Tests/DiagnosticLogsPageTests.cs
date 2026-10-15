// Copyright ©2026 Scott Blomfield

using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using Refit;
using RustArchon.Panel.Clients;
using RustArchon.Panel.Components.Pages.Admin;
using RustArchon.Panel.Localization;
using RustArchon.Shared.DTOs;

namespace RustArchon.Panel.Tests;

/// <summary>
/// The platform-admin Diagnostic logs page: creating, replacing and revoking the one log token. The secret appears once, in the response to creating, and
/// nowhere else; replacing and revoking each need a confirmation; a refusal is shown as its own sentence.
/// </summary>
public class DiagnosticLogsPageTests : BunitContext
{
    private const string Secret = "abcd1234.SECRETSECRETSECRETSECRETSECRETSECRETSECRETSEC";

    private readonly Mock<IDiagnosticLogAdminApiClient> _client = new();
    private DiagnosticLogTokenStatusDto _status = new() { Exists = false };

    public DiagnosticLogsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _client.Setup(c => c.GetAsync()).ReturnsAsync(() => _status);
        _client.Setup(c => c.CreateAsync()).ReturnsAsync(() =>
        {
            _status = new DiagnosticLogTokenStatusDto
            {
                Exists = true, TokenId = "abcd1234", CreatedAtUtc = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero), CreatedBy = "admin@example.com",
            };
            return new DiagnosticLogTokenCreatedDto { Token = Secret, TokenId = "abcd1234", CreatedAtUtc = _status.CreatedAtUtc!.Value };
        });
        _client.Setup(c => c.RevokeAsync()).Returns(() =>
        {
            _status.RevokedAtUtc = new DateTimeOffset(2026, 10, 14, 13, 0, 0, TimeSpan.Zero);
            return Task.CompletedTask;
        });

        var localizer = new Mock<IStringLocalizer<SharedResource>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string key) => new LocalizedString(key, key));
        Services.AddSingleton(_client.Object);
        Services.AddSingleton(localizer.Object);
        AddAuthorization().SetAuthorized("test-admin");
    }

    private void GivenExistingToken(DateTimeOffset? revoked = null) =>
        _status = new DiagnosticLogTokenStatusDto
        {
            Exists = true, TokenId = "abcd1234", CreatedAtUtc = new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.Zero), CreatedBy = "admin@example.com", RevokedAtUtc = revoked,
        };

    private static ApiException Forbidden() =>
        ApiException.Create(new HttpRequestMessage(), HttpMethod.Get, new HttpResponseMessage(HttpStatusCode.Forbidden), new RefitSettings()).Result;

    [Fact]
    public void WithNoTokenItSaysTheAddressIsOffAndOffersToCreateOne()
    {
        var cut = Render<DiagnosticLogs>();

        Assert.Contains("does not exist", cut.Find("[data-testid=token-none]").TextContent);
        Assert.NotEmpty(cut.FindAll("[data-testid=create-token]"));
        Assert.Empty(cut.FindAll("[data-testid=revoke-token]"));
    }

    [Fact]
    public void CreatingShowsTheSecretOnceAndTheStatusAfterwards()
    {
        var cut = Render<DiagnosticLogs>();

        cut.Find("[data-testid=create-token]").Click();

        Assert.Equal(Secret, cut.Find("[data-testid=new-token]").GetAttribute("value"));
        Assert.Equal("abcd1234", cut.Find("[data-testid=token-id]").TextContent);
        Assert.Equal("Active", cut.Find("[data-testid=token-state]").TextContent);
        _client.Verify(c => c.CreateAsync(), Times.Once);
    }

    [Fact]
    public void DismissingTheSecretRemovesItFromThePageForGood()
    {
        var cut = Render<DiagnosticLogs>();
        cut.Find("[data-testid=create-token]").Click();

        cut.Find("[data-testid=dismiss-token]").Click();

        Assert.DoesNotContain("SECRETSECRET", cut.Markup);
        Assert.Empty(cut.FindAll("[data-testid=new-token]"));
    }

    [Fact]
    public void AnExistingTokenShowsItsIdAndCreatorButNeverItsSecret()
    {
        GivenExistingToken();

        var cut = Render<DiagnosticLogs>();

        Assert.Equal("abcd1234", cut.Find("[data-testid=token-id]").TextContent);
        Assert.Contains("admin@example.com", cut.Find("[data-testid=token-status]").TextContent);
        Assert.DoesNotContain("SECRETSECRET", cut.Markup);
        Assert.Empty(cut.FindAll("[data-testid=new-token]"));
    }

    [Fact]
    public void ReplacingNeedsAConfirmationAndCancellingDoesNothing()
    {
        GivenExistingToken();
        var cut = Render<DiagnosticLogs>();

        cut.Find("[data-testid=rotate-token]").Click();
        Assert.Contains("stops the current one working", cut.Find("[data-testid=confirm-panel]").TextContent);
        cut.Find("[data-testid=confirm-no]").Click();

        _client.Verify(c => c.CreateAsync(), Times.Never);
        Assert.Empty(cut.FindAll("[data-testid=confirm-panel]"));
    }

    [Fact]
    public void ConfirmingAReplacementCreatesANewTokenAndShowsIt()
    {
        GivenExistingToken();
        var cut = Render<DiagnosticLogs>();

        cut.Find("[data-testid=rotate-token]").Click();
        cut.Find("[data-testid=confirm-yes]").Click();

        _client.Verify(c => c.CreateAsync(), Times.Once);
        Assert.Equal(Secret, cut.Find("[data-testid=new-token]").GetAttribute("value"));
    }

    [Fact]
    public void RevokingNeedsAConfirmationThenRevokesAndShowsRevoked()
    {
        GivenExistingToken();
        var cut = Render<DiagnosticLogs>();

        cut.Find("[data-testid=revoke-token]").Click();
        _client.Verify(c => c.RevokeAsync(), Times.Never);
        cut.Find("[data-testid=confirm-yes]").Click();

        _client.Verify(c => c.RevokeAsync(), Times.Once);
        Assert.Equal("Revoked", cut.Find("[data-testid=token-state]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=revoke-token]"));
    }

    [Fact]
    public void ARevokedTokenOffersANewOneInsteadOfAnotherRevoke()
    {
        GivenExistingToken(revoked: new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero));

        var cut = Render<DiagnosticLogs>();

        Assert.Contains("Create new token", cut.Find("[data-testid=rotate-token]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=revoke-token]"));
    }

    [Fact]
    public void AFailedCreateIsShownAsItsOwnSentenceAndNoSecretAppears()
    {
        _client.Setup(c => c.CreateAsync()).ThrowsAsync(new InvalidOperationException("storage unavailable"));
        var cut = Render<DiagnosticLogs>();

        cut.Find("[data-testid=create-token]").Click();

        Assert.NotEmpty(cut.Find("[data-testid=diagnostics-error]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=new-token]"));
    }

    [Fact]
    public void WithoutPermissionThePageSaysSoAndOffersNothing()
    {
        _client.Setup(c => c.GetAsync()).ThrowsAsync(Forbidden());

        var cut = Render<DiagnosticLogs>();

        Assert.NotEmpty(cut.FindAll("[data-testid=diagnostics-denied]"));
        Assert.Empty(cut.FindAll("[data-testid=create-token]"));
    }

    [Fact]
    public void TheUsageExampleNamesTheHeaderAndNeverContainsATokenValue()
    {
        var cut = Render<DiagnosticLogs>();

        var example = cut.Find("[data-testid=usage-example]").TextContent;

        Assert.Contains("X-RustArchon-Log-Token", example);
        Assert.Contains("/diagnostics/logs/help", example);
    }
}
