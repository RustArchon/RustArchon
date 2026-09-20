// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
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
/// The platform-admin Plugin page: the key list with rotate and revoke, the release list with upload, publish and
/// withdraw, and the audit log. Every destructive step needs a confirmation, revoking and withdrawing need a reason,
/// a refusal from the Api is shown as its own sentence, and nothing secret ever reaches the page.
/// </summary>
public class PluginAdminPageTests : BunitContext
{
    private readonly Mock<IPluginAdminApiClient> _client = new();
    private readonly List<PluginKeyDto> _keys;
    private PluginReleasesDto _releases;

    public PluginAdminPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        _keys =
        [
            new PluginKeyDto { Fingerprint = "1111111111111111", State = "active", ServersReporting = 2, LastReportedUtc = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero) },
            new PluginKeyDto { Fingerprint = "2222222222222222", State = "retired", RetiredAtUtc = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), ServersReporting = 1 },
            new PluginKeyDto { Fingerprint = "3333333333333333", State = "revoked", RevokedAtUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), RevokedReason = "leaked in a ticket" }
        ];
        _releases = new PluginReleasesDto
        {
            Main = new PluginServedDto { Kind = "main", ServedVersion = "0.2.2", EmbeddedVersion = "0.2.2", FromRelease = false },
            Updater = new PluginServedDto { Kind = "updater", ServedVersion = "0.2.0", EmbeddedVersion = "0.2.0", FromRelease = false },
            Releases = []
        };

        _client.Setup(c => c.GetKeysAsync()).ReturnsAsync(() => _keys);
        _client.Setup(c => c.GetReleasesAsync()).ReturnsAsync(() => _releases);
        _client.Setup(c => c.GetEventsAsync(It.IsAny<int>())).ReturnsAsync([]);

        var localizer = new Mock<IStringLocalizer<SharedResource>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string key) => new LocalizedString(key, key));
        // The page formats some messages with string.Format, which reads the [] (key) indexer only.
        Services.AddSingleton(_client.Object);
        Services.AddSingleton(localizer.Object);

        AddAuthorization().SetAuthorized("test-admin");
    }

    private IRenderedComponent<Plugin> RenderPage()
    {
        var cut = Render<Plugin>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-keys]")));
        return cut;
    }

    private static async Task<ApiException> Refused(HttpStatusCode status, string body) =>
        await ApiException.Create(
            new HttpRequestMessage(HttpMethod.Post, "http://x/"), HttpMethod.Post,
            new HttpResponseMessage(status) { Content = new StringContent(body) }, new RefitSettings());

    private static PluginReleaseDto Release(string state, string version = "9.1.0", string kind = "main", bool served = false) => new()
    {
        Id = Guid.NewGuid(), Kind = kind, Version = version, State = state, IsServed = served,
        Sha256 = new string('a', 64), UploadedAtUtc = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero), UploadedBy = "admin@example.com"
    };

    private static PluginReleaseDto Copy(PluginReleaseDto r, string state, bool served) => new()
    {
        Id = r.Id, Kind = r.Kind, Version = r.Version, State = state, IsServed = served,
        Sha256 = r.Sha256, UploadedAtUtc = r.UploadedAtUtc, UploadedBy = r.UploadedBy
    };

    // ---- access --------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutThePermissionOnlyTheAccessMessageIsShown()
    {
        _client.Setup(c => c.GetKeysAsync()).ThrowsAsync(await Refused(HttpStatusCode.Forbidden, ""));

        var cut = Render<Plugin>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-admin-denied]")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-keys]"));
        Assert.Empty(cut.FindAll("[data-testid=rotate-key]"));
    }

    // ---- keys ----------------------------------------------------------------------------------------------

    [Fact]
    public void EveryKeyIsListedWithItsStateAndHowManyServersLastReportedIt()
    {
        var cut = RenderPage();

        var rows = cut.FindAll("[data-testid=key-row]");
        Assert.Equal(["1111111111111111", "2222222222222222", "3333333333333333"], rows.Select(r => r.GetAttribute("data-fingerprint")).ToArray());
        Assert.Contains("Active", rows[0].TextContent);
        Assert.Contains("Retired", rows[1].TextContent);
        Assert.Contains("Revoked", rows[2].TextContent);
        Assert.Contains("leaked in a ticket", rows[2].TextContent);
        Assert.Contains("2", rows[0].TextContent);
    }

    [Fact]
    public void OnlyARetiredKeyOffersRevokeSoTheActiveOneCanNeverBeRevokedAlone()
    {
        var cut = RenderPage();

        var withRevoke = cut.FindAll("[data-testid=key-row]").Where(r => r.QuerySelector("[data-testid=revoke-key]") is not null)
            .Select(r => r.GetAttribute("data-fingerprint")).ToArray();

        Assert.Equal(["2222222222222222"], withRevoke);
    }

    [Fact]
    public void TheKeyListNeverShowsAnythingSecret()
    {
        var cut = RenderPage();

        Assert.DoesNotContain("PRIVATE", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RotatingAsksForConfirmationFirstAndCancelChangesNothing()
    {
        var cut = RenderPage();

        cut.Find("[data-testid=rotate-key]").Click();
        Assert.NotEmpty(cut.FindAll("[data-testid=confirm-rotate]"));
        cut.Find("[data-testid=confirm-rotate] button.btn-outline-secondary").Click();

        Assert.Empty(cut.FindAll("[data-testid=confirm-rotate]"));
        _client.Verify(c => c.RotateKeyAsync(It.IsAny<RotatePluginKeyRequestDto>()), Times.Never);
    }

    [Fact]
    public void ConfirmingARotationSendsTheNoteAndReportsTheNewKey()
    {
        RotatePluginKeyRequestDto? sent = null;
        _client.Setup(c => c.RotateKeyAsync(It.IsAny<RotatePluginKeyRequestDto>()))
            .Callback<RotatePluginKeyRequestDto>(r => sent = r)
            .ReturnsAsync(new PluginKeyDto { Fingerprint = "4444444444444444", State = "active" });
        var cut = RenderPage();

        cut.Find("[data-testid=rotate-key]").Click();
        cut.Find("#rotate-note").Input("yearly rotation");
        cut.Find("[data-testid=confirm-rotate-go]").Click();

        cut.WaitForAssertion(() => Assert.Contains("4444444444444444", cut.Find("[data-testid=plugin-admin-success]").TextContent));
        Assert.Equal("yearly rotation", sent!.Note);
        Assert.False(sent.RevokeCurrent);
        Assert.Empty(cut.FindAll("[data-testid=confirm-rotate]")); // closed again
        _client.Verify(c => c.GetKeysAsync(), Times.AtLeast(2));    // and the list was reloaded
    }

    [Fact]
    public void RevokingTheCurrentKeyWhileRotatingWarnsAndNeedsAReasonBeforeItCanBeConfirmed()
    {
        var cut = RenderPage();
        cut.Find("[data-testid=rotate-key]").Click();

        cut.Find("#rotate-revoke").Change(true);

        Assert.NotEmpty(cut.FindAll("[data-testid=revoke-warning]"));
        Assert.True(cut.Find("[data-testid=confirm-rotate-go]").HasAttribute("disabled"));
        cut.Find("#rotate-reason").Input("suspected compromise");
        Assert.False(cut.Find("[data-testid=confirm-rotate-go]").HasAttribute("disabled"));
    }

    [Fact]
    public void RotatingWithARevokeSendsTheFlagAndTheReason()
    {
        RotatePluginKeyRequestDto? sent = null;
        _client.Setup(c => c.RotateKeyAsync(It.IsAny<RotatePluginKeyRequestDto>()))
            .Callback<RotatePluginKeyRequestDto>(r => sent = r)
            .ReturnsAsync(new PluginKeyDto { Fingerprint = "4444444444444444", State = "active" });
        var cut = RenderPage();

        cut.Find("[data-testid=rotate-key]").Click();
        cut.Find("#rotate-revoke").Change(true);
        cut.Find("#rotate-reason").Input("suspected compromise");
        cut.Find("[data-testid=confirm-rotate-go]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.True(sent!.RevokeCurrent);
        Assert.Equal("suspected compromise", sent.RevokeReason);
    }

    [Fact]
    public void RevokingARetiredKeyNeedsAReasonAndSendsIt()
    {
        string? key = null;
        RevokePluginKeyRequestDto? sent = null;
        _client.Setup(c => c.RevokeKeyAsync(It.IsAny<string>(), It.IsAny<RevokePluginKeyRequestDto>()))
            .Callback<string, RevokePluginKeyRequestDto>((k, r) => { key = k; sent = r; })
            .ReturnsAsync(new PluginKeyDto { Fingerprint = "2222222222222222", State = "revoked" });
        var cut = RenderPage();

        cut.Find("[data-testid=revoke-key]").Click();
        Assert.True(cut.Find("[data-testid=confirm-revoke-go]").HasAttribute("disabled")); // no reason yet
        cut.Find("#revoke-reason").Input("found on pastebin");
        cut.Find("[data-testid=confirm-revoke-go]").Click();

        cut.WaitForAssertion(() => Assert.Contains("2222222222222222", cut.Find("[data-testid=plugin-admin-success]").TextContent));
        Assert.Equal("2222222222222222", key);
        Assert.Equal("found on pastebin", sent!.Reason);
    }

    [Fact]
    public async Task ARefusalFromTheApiIsShownAsItsOwnSentenceAndNothingElseChanges()
    {
        _client.Setup(c => c.RotateKeyAsync(It.IsAny<RotatePluginKeyRequestDto>()))
            .ThrowsAsync(await Refused(HttpStatusCode.BadRequest, "The signing key changed while rotating; nothing was changed. Try again."));
        var cut = RenderPage();

        cut.Find("[data-testid=rotate-key]").Click();
        cut.Find("[data-testid=confirm-rotate-go]").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("nothing was changed", cut.Find("[data-testid=plugin-admin-error]").TextContent));
        Assert.Empty(cut.FindAll("[data-testid=plugin-admin-success]"));
        Assert.False(cut.Find("[data-testid=rotate-key]").HasAttribute("disabled")); // not left stuck busy
    }

    // ---- releases ------------------------------------------------------------------------------------------

    [Fact]
    public void TheServedVersionsAndWhereTheyComeFromAreShown()
    {
        _releases.Main = new PluginServedDto { Kind = "main", ServedVersion = "0.3.0", EmbeddedVersion = "0.2.2", FromRelease = true };

        var text = RenderPage().Find("[data-testid=served]").TextContent;

        Assert.Contains("0.3.0", text);
        Assert.Contains("uploaded release", text);
        Assert.Contains("0.2.2", text);
        Assert.Contains("built-in", text);
    }

    [Fact]
    public void WithNoReleasesItSaysSo()
    {
        Assert.NotEmpty(RenderPage().FindAll("[data-testid=no-releases]"));
    }

    [Fact]
    public void OnlyADraftOffersPublishAndAWithdrawnReleaseOffersNothing()
    {
        _releases.Releases = [Release("draft", "9.1.0"), Release("published", "9.0.0"), Release("withdrawn", "8.0.0")];

        var rows = RenderPage().FindAll("[data-testid=release-row]");

        Assert.NotNull(rows[0].QuerySelector("[data-testid=publish-release]"));
        Assert.NotNull(rows[0].QuerySelector("[data-testid=withdraw-release]"));
        Assert.Null(rows[1].QuerySelector("[data-testid=publish-release]"));
        Assert.NotNull(rows[1].QuerySelector("[data-testid=withdraw-release]"));
        Assert.Null(rows[2].QuerySelector("[data-testid=publish-release]"));
        Assert.Null(rows[2].QuerySelector("[data-testid=withdraw-release]"));
    }

    [Fact]
    public void TheReleaseBeingServedIsMarked()
    {
        _releases.Releases = [Release("published", "9.1.0", served: true), Release("published", "9.0.0")];

        var rows = RenderPage().FindAll("[data-testid=release-row]");

        Assert.NotNull(rows[0].QuerySelector("[data-testid=release-served]"));
        Assert.Null(rows[1].QuerySelector("[data-testid=release-served]"));
    }

    [Fact]
    public void PublishingNeedsConfirmationAndThenPublishesThatRelease()
    {
        var draft = Release("draft");
        _releases.Releases = [draft];
        _client.Setup(c => c.PublishReleaseAsync(draft.Id)).ReturnsAsync(Copy(draft, "published", true));
        var cut = RenderPage();

        cut.Find("[data-testid=publish-release]").Click();
        Assert.Contains("9.1.0", cut.Find("[data-testid=confirm-publish]").TextContent);
        _client.Verify(c => c.PublishReleaseAsync(It.IsAny<Guid>()), Times.Never); // not yet
        cut.Find("[data-testid=confirm-publish-go]").Click();

        cut.WaitForAssertion(() => Assert.Contains("now what this Panel delivers", cut.Find("[data-testid=plugin-admin-success]").TextContent));
        _client.Verify(c => c.PublishReleaseAsync(draft.Id), Times.Once);
    }

    [Fact]
    public void PublishingAReleaseThatIsNotNewerSaysNothingChangedYet()
    {
        var draft = Release("draft", "0.0.1");
        _releases.Releases = [draft];
        _client.Setup(c => c.PublishReleaseAsync(draft.Id)).ReturnsAsync(Copy(draft, "published", false));
        var cut = RenderPage();

        cut.Find("[data-testid=publish-release]").Click();
        cut.Find("[data-testid=confirm-publish-go]").Click();

        cut.WaitForAssertion(() => Assert.Contains("nothing changes yet", cut.Find("[data-testid=plugin-admin-success]").TextContent));
    }

    [Fact]
    public void WithdrawingNeedsAReasonAndSendsIt()
    {
        var release = Release("published", served: true);
        _releases.Releases = [release];
        WithdrawPluginReleaseRequestDto? sent = null;
        _client.Setup(c => c.WithdrawReleaseAsync(release.Id, It.IsAny<WithdrawPluginReleaseRequestDto>()))
            .Callback<Guid, WithdrawPluginReleaseRequestDto>((_, r) => sent = r)
            .ReturnsAsync(Copy(release, "withdrawn", release.IsServed));
        var cut = RenderPage();

        cut.Find("[data-testid=withdraw-release]").Click();
        Assert.True(cut.Find("[data-testid=confirm-withdraw-go]").HasAttribute("disabled"));
        cut.Find("#withdraw-reason").Input("crashes on load");
        cut.Find("[data-testid=confirm-withdraw-go]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-admin-success]")));
        Assert.Equal("crashes on load", sent!.Reason);
    }

    [Fact]
    public void UploadIsDisabledUntilAFileIsChosen()
    {
        Assert.True(RenderPage().Find("[data-testid=upload-go]").HasAttribute("disabled"));
    }

    [Fact]
    public void ChoosingAFileAndUploadingSendsTheKindTheNotesAndTheFile()
    {
        var uploaded = Release("draft", "9.1.0");
        string? kind = null;
        string? notes = null;
        _client.Setup(c => c.UploadReleaseAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<StreamPart>()))
            .Callback<string, string?, StreamPart>((k, n, _) => { kind = k; notes = n; })
            .ReturnsAsync(uploaded);
        var cut = RenderPage();

        cut.Find("#upload-kind").Change("updater");
        cut.Find("#upload-notes").Input("adds bridging");
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("class X {}", "RustArchonUpdater.cs"));
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=upload-go]").HasAttribute("disabled")));
        cut.Find("[data-testid=upload-go]").Click();

        cut.WaitForAssertion(() => Assert.Contains("as a draft", cut.Find("[data-testid=plugin-admin-success]").TextContent));
        Assert.Equal("updater", kind);
        Assert.Equal("adds bridging", notes);
    }

    [Fact]
    public async Task AnUploadTheApiRefusesShowsItsReason()
    {
        _client.Setup(c => c.UploadReleaseAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<StreamPart>()))
            .ThrowsAsync(await Refused(HttpStatusCode.BadRequest, "The file already carries a signature line. Upload the raw source; the Panel signs it itself."));
        var cut = RenderPage();

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("class X {}", "RustArchon.cs"));
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=upload-go]").HasAttribute("disabled")));
        cut.Find("[data-testid=upload-go]").Click();

        cut.WaitForAssertion(() => Assert.Contains("already carries a signature line", cut.Find("[data-testid=plugin-admin-error]").TextContent));
    }

    // ---- audit log -----------------------------------------------------------------------------------------

    [Fact]
    public void TheAuditLogListsWhatWasDoneByWhomAndWhy()
    {
        _client.Setup(c => c.GetEventsAsync(It.IsAny<int>())).ReturnsAsync(
        [
            new PluginAdminEventDto { AtUtc = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero), Kind = "KeyRevoked", Subject = "2222222222222222", Actor = "admin@example.com", Detail = "leaked" }
        ]);

        var row = Assert.Single(RenderPage().FindAll("[data-testid=event-row]"));

        Assert.Contains("Key revoked", row.TextContent);
        Assert.Contains("2222222222222222", row.TextContent);
        Assert.Contains("admin@example.com", row.TextContent);
        Assert.Contains("leaked", row.TextContent);
    }
}
