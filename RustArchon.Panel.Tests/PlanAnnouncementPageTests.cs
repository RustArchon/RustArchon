// Copyright ©2026 Scott Blomfield

using System.Globalization;
using System.Net;
using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Moq;
using Refit;
using RustArchon.Panel.Clients;
using RustArchon.Panel.Components.Pages.Admin;
using RustArchon.Panel.Localization;
using RustArchon.Shared.DTOs;

namespace RustArchon.Panel.Tests;

/// <summary>
/// The page a site admin writes an announcement to a plan's organizations on: who it reaches (chosen, and counted before anything is sent), the words per language or
/// in one language, a test to themselves, and a send that is only what was confirmed.
/// </summary>
public class PlanAnnouncementPageTests : BunitContext
{
    private readonly Mock<IPlanApiClient> _plans = new();
    private readonly Guid _planId = Guid.NewGuid();

    public PlanAnnouncementPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        var localizer = new Mock<IStringLocalizer<SharedResource>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string key) => new LocalizedString(key, key));
        localizer.Setup(l => l[It.IsAny<string>(), It.IsAny<object[]>()])
            .Returns((string key, object[] args) => new LocalizedString(key, string.Format(key, args)));
        Services.AddSingleton(localizer.Object);
        Services.AddSingleton(_plans.Object);
        Services.AddSingleton<IOptions<RequestLocalizationOptions>>(Options.Create(new RequestLocalizationOptions
        {
            SupportedUICultures = [new CultureInfo("en-US"), new CultureInfo("es-US")]
        }));

        AddAuthorization().SetAuthorized("admin").SetClaims(new Claim(ClaimTypes.Email, "admin@example.com"));

        _plans.Setup(p => p.GetAllPlansAsync()).ReturnsAsync([Plan("Gold", replacedBy: null)]);
        _plans.Setup(p => p.PreviewAnnouncementAsync(_planId, It.IsAny<AnnouncementCriteriaDto>())).ReturnsAsync(Preview(5));
        _plans.Setup(p => p.GetAnnouncementHistoryAsync(_planId)).ReturnsAsync([]);
    }

    private PlanDto Plan(string name, Guid? replacedBy) => new() { Id = _planId, Name = name, Active = true, SupersededByPlanId = replacedBy, SubscriberCount = 4 };

    private static AnnouncementPreviewDto Preview(int recipients, params (string Message, int Count)[] leftOut) => new()
    {
        Recipients = recipients, DefaultCulture = "en-US",
        ByPlanVersion = [new AnnouncementCountDto { Key = "a", Name = "Gold", Count = recipients }],
        ByLanguage = [new AnnouncementCountDto { Key = "es-US", Name = "es-US", Count = 1 }, new AnnouncementCountDto { Key = "", Name = "", Count = recipients - 1 }],
        LeftOut = leftOut.Select(l => new PlanMoveReasonDto { Code = "x", Message = l.Message, Count = l.Count }).ToList()
    };

    private IRenderedComponent<PlanAnnouncement> RenderPage()
    {
        var cut = Render<PlanAnnouncement>(p => p.Add(x => x.Id, _planId));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=ann-recipients]")));
        return cut;
    }

    private static Task Write(IRenderedComponent<PlanAnnouncement> cut, string culture, string subject, string body = "Hello", bool hasText = true)
    {
        cut.Find($"[data-testid=ann-subject-{culture}]").Input(subject);
        var delta = "{\"ops\":[{\"insert\":\"" + body + "\\n\"}]}";
        return cut.InvokeAsync(() => cut.Instance.OnBodyChanged(culture, delta, hasText));
    }

    private async Task<IRenderedComponent<PlanAnnouncement>> ReadyToSendAsync()
    {
        var cut = RenderPage();
        await Write(cut, "en-US", "Big change");
        return cut;
    }

    // ---- the page --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePageNamesThePlanCountsWhoWouldBeEmailedAndStartsAnEditorForEachLanguage()
    {
        var cut = RenderPage();

        Assert.Contains("\"Gold\"", cut.Markup);
        Assert.Contains("5 organization(s) would be emailed.", cut.Find("[data-testid=ann-recipients]").TextContent);
        cut.WaitForAssertion(() => Assert.Equal(2, JSInterop.Invocations["announcementEditor.create"].Count()));
        Assert.Contains(JSInterop.Invocations["announcementEditor.create"], i => (string?)i.Arguments[0] == "ann-editor-en-US" && (string?)i.Arguments[3] == "en-US");
        Assert.Contains(JSInterop.Invocations["announcementEditor.create"], i => (string?)i.Arguments[0] == "ann-editor-es-US");
    }

    [Fact]
    public void APlanThatIsNotThereSaysSoAndOffersNothingToWrite()
    {
        _plans.Setup(p => p.GetAllPlansAsync()).ReturnsAsync([]);

        var cut = Render<PlanAnnouncement>(p => p.Add(x => x.Id, _planId));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=ann-no-plan]")));
        Assert.Empty(cut.FindAll("[data-testid=ann-send]"));
    }

    [Fact]
    public void TheBreakdownByVersionLanguageAndWhoIsLeftOutIsShown()
    {
        _plans.Setup(p => p.PreviewAnnouncementAsync(_planId, It.IsAny<AnnouncementCriteriaDto>())).ReturnsAsync(new AnnouncementPreviewDto
        {
            Recipients = 6, DefaultCulture = "en-US",
            ByPlanVersion = [new() { Key = "1", Name = "Gold", Count = 2 }, new() { Key = "2", Name = "Gold v2", Count = 4 }],
            ByLanguage = [new() { Key = "es-US", Name = "es-US", Count = 2 }, new() { Key = "", Name = "", Count = 4 }],
            LeftOut = [new PlanMoveReasonDto { Code = "x", Message = "the organization has no usable contact email", Count = 3 }]
        });

        var cut = RenderPage();

        Assert.Contains("4 × Gold v2", cut.Find("[data-testid=ann-by-version]").TextContent);
        var languages = cut.Find("[data-testid=ann-by-language]").TextContent;
        Assert.Contains("2 ×", languages);
        Assert.Contains("(the default)", languages);          // "none set" is said as the default language
        Assert.Contains("3 × the organization has no usable contact email", cut.Find("[data-testid=ann-left-out]").TextContent);
    }

    // ---- who it goes to ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void NewerVersionsCanOnlyBeIncludedWhenThePlanHasSome()
    {
        var without = RenderPage();
        Assert.True(without.Find("[data-testid=ann-include-newer]").HasAttribute("disabled"));
        Assert.NotEmpty(without.FindAll("[data-testid=ann-no-newer]"));

        _plans.Setup(p => p.GetAllPlansAsync()).ReturnsAsync([Plan("Gold", replacedBy: Guid.NewGuid())]);
        var with = RenderPage();
        Assert.False(with.Find("[data-testid=ann-include-newer]").HasAttribute("disabled"));
        Assert.Empty(with.FindAll("[data-testid=ann-no-newer]"));
    }

    [Fact]
    public void TheChoicesGoToTheApiAsCriteriaAndTheCountIsAskedAgainEachTime()
    {
        _plans.Setup(p => p.GetAllPlansAsync()).ReturnsAsync([Plan("Gold", replacedBy: Guid.NewGuid())]);
        var cut = RenderPage();

        cut.Find("[data-testid=ann-include-newer]").Change(true);
        cut.Find("[data-testid=ann-include-past-due]").Change(true);

        _plans.Verify(p => p.PreviewAnnouncementAsync(_planId, It.Is<AnnouncementCriteriaDto>(c => !c.IncludeSupersedingVersions && !c.IncludePastDue)), Times.Once());
        _plans.Verify(p => p.PreviewAnnouncementAsync(_planId, It.Is<AnnouncementCriteriaDto>(c => c.IncludeSupersedingVersions && !c.IncludePastDue)), Times.Once());
        _plans.Verify(p => p.PreviewAnnouncementAsync(_planId, It.Is<AnnouncementCriteriaDto>(c => c.IncludeSupersedingVersions && c.IncludePastDue)), Times.Once());
    }

    [Fact]
    public void PastDueIsOffUntilChosen()
    {
        var cut = RenderPage();

        Assert.False(cut.Find("[data-testid=ann-include-past-due]").HasAttribute("checked"));
        _plans.Verify(p => p.PreviewAnnouncementAsync(_planId, It.Is<AnnouncementCriteriaDto>(c => c.IncludePastDue)), Times.Never());
    }

    // ---- the words ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void NothingCanBeSentUntilTheDefaultLanguageHasASubjectAndAMessage()
    {
        var cut = RenderPage();

        Assert.True(cut.Find("[data-testid=ann-send]").HasAttribute("disabled"));
        Assert.Contains("English", cut.Find("[data-testid=ann-problem]").TextContent);
        Assert.NotEmpty(cut.FindAll("[data-testid=ann-default-hint]"));
    }

    [Fact]
    public async Task ASubjectAloneOrAMessageAloneIsNotEnough()
    {
        var cut = RenderPage();

        await Write(cut, "en-US", "A subject", hasText: false);
        Assert.True(cut.Find("[data-testid=ann-send]").HasAttribute("disabled"));

        await Write(cut, "en-US", "", hasText: true);
        Assert.True(cut.Find("[data-testid=ann-send]").HasAttribute("disabled"));
    }

    [Fact]
    public async Task OnceTheDefaultLanguageIsWrittenTheSendIsOffered()
    {
        var cut = await ReadyToSendAsync();

        Assert.False(cut.Find("[data-testid=ann-send]").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll("[data-testid=ann-problem]"));
    }

    [Fact]
    public async Task ANoRecipientsPreviewMeansNothingToSend()
    {
        _plans.Setup(p => p.PreviewAnnouncementAsync(_planId, It.IsAny<AnnouncementCriteriaDto>())).ReturnsAsync(Preview(0));
        var cut = RenderPage();

        await Write(cut, "en-US", "Subject");

        Assert.True(cut.Find("[data-testid=ann-send]").HasAttribute("disabled"));
    }

    [Fact]
    public async Task AnotherLanguageHalfWrittenIsAProblemAndFullyWrittenIsNot()
    {
        var cut = await ReadyToSendAsync();

        await Write(cut, "es-US", "Solo asunto", hasText: false);
        Assert.Contains("Español", string.Join(" ", cut.FindAll("[data-testid=ann-problem]").Select(p => p.TextContent)), StringComparison.OrdinalIgnoreCase);
        Assert.True(cut.Find("[data-testid=ann-send]").HasAttribute("disabled"));

        await Write(cut, "es-US", "Asunto", body: "Cuerpo", hasText: true);
        Assert.Empty(cut.FindAll("[data-testid=ann-problem]"));
        Assert.False(cut.Find("[data-testid=ann-send]").HasAttribute("disabled"));
    }

    [Fact]
    public async Task ALanguageLeftEntirelyEmptyIsLeftOutOfWhatIsSent()
    {
        SendPlanAnnouncementDto? sent = null;
        _plans.Setup(p => p.SendAnnouncementAsync(_planId, It.IsAny<SendPlanAnnouncementDto>()))
            .Callback((Guid _, SendPlanAnnouncementDto dto) => sent = dto).ReturnsAsync(new AnnouncementResultDto { Queued = 5 });
        var cut = await ReadyToSendAsync();

        cut.Find("[data-testid=ann-send]").Click();
        cut.Find(".modal-footer .btn-primary").Click();

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.Equal("en-US", Assert.Single(sent!.Content.Versions).Culture);
    }

    [Fact]
    public void TheTabsAndEditorsAreThereForEveryLanguageAndTheDefaultIsMarkedRequired()
    {
        var cut = RenderPage();

        Assert.Equal(2, cut.FindAll("[data-testid=ann-tabs] .nav-item").Count);
        Assert.Contains("required", cut.Find("[data-testid=ann-tab-en-US]").TextContent);
        Assert.DoesNotContain("required", cut.Find("[data-testid=ann-tab-es-US]").TextContent);
        Assert.NotEmpty(cut.FindAll("[data-testid=ann-editor-es-US]"));
    }

    [Fact]
    public void ChoosingATabShowsThatLanguagesFieldsAndHidesTheOthers()
    {
        var cut = RenderPage();

        cut.Find("[data-testid=ann-tab-es-US]").Click();

        Assert.DoesNotContain("d-none", cut.Find("[data-testid=ann-pane-es-US]").ClassList);
        Assert.Contains("d-none", cut.Find("[data-testid=ann-pane-en-US]").ClassList);
    }

    [Fact]
    public void TheNamesThatCanBeInsertedAreOfferedForTheSubjectAndTheMessage()
    {
        var cut = RenderPage();

        cut.FindAll("[data-testid=ann-subject-token]").First(b => b.TextContent.Contains("OrganizationName")).Click();
        cut.FindAll("[data-testid=ann-body-token]").First(b => b.TextContent.Contains("PlanName")).Click();

        Assert.Contains(JSInterop.Invocations["announcementEditor.insertIntoInput"], i => (string?)i.Arguments[1] == "{{OrganizationName}}");
        Assert.Contains(JSInterop.Invocations["announcementEditor.insertToken"], i => (string?)i.Arguments[1] == "{{PlanName}}");
        Assert.Contains("OrganizationName", cut.Find("[data-testid=ann-token-help]").TextContent);
    }

    // ---- one language for everyone ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASingleLanguageSendNeedsOnlyThatLanguageAndHidesTheOthers()
    {
        SendPlanAnnouncementDto? sent = null;
        _plans.Setup(p => p.SendAnnouncementAsync(_planId, It.IsAny<SendPlanAnnouncementDto>()))
            .Callback((Guid _, SendPlanAnnouncementDto dto) => sent = dto).ReturnsAsync(new AnnouncementResultDto { Queued = 5 });
        var cut = RenderPage();
        await Write(cut, "es-US", "Solo español", body: "Todo");

        cut.Find("[data-testid=ann-mode-single]").Change(true);
        cut.Find("[data-testid=ann-single-culture]").Change("es-US");

        Assert.Empty(cut.FindAll("[data-testid=ann-default-hint]"));
        Assert.Empty(cut.FindAll("[data-testid=ann-problem]"));                                     // no English needed
        Assert.Contains("d-none", cut.Find("[data-testid=ann-tab-en-US]").ParentElement!.ClassList);
        cut.Find("[data-testid=ann-send]").Click();
        cut.Find(".modal-footer .btn-primary").Click();

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.Equal(AnnouncementLanguageMode.SingleLanguage, sent!.Content.Mode);
        Assert.Equal("es-US", sent.Content.SingleCulture);
        Assert.Equal("es-US", Assert.Single(sent.Content.Versions).Culture);
    }

    [Fact]
    public void ASingleLanguageSendWithNothingWrittenInThatLanguageSaysSo()
    {
        var cut = RenderPage();

        cut.Find("[data-testid=ann-mode-single]").Change(true);

        Assert.Contains("English", cut.Find("[data-testid=ann-problem]").TextContent);
        Assert.True(cut.Find("[data-testid=ann-send]").HasAttribute("disabled"));
    }

    // ---- a test ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATestGoesToTheAdminsOwnAddressWithTheWordsAsWritten()
    {
        SendAnnouncementTestDto? sent = null;
        _plans.Setup(p => p.SendAnnouncementTestAsync(_planId, It.IsAny<SendAnnouncementTestDto>()))
            .Callback((Guid _, SendAnnouncementTestDto dto) => sent = dto).ReturnsAsync(new AnnouncementResultDto { Queued = 1 });
        var cut = await ReadyToSendAsync();

        cut.Find("[data-testid=ann-test]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.Equal("admin@example.com", sent!.ToAddress);
        Assert.Equal("Big change", Assert.Single(sent.Content.Versions).Subject);
        cut.WaitForAssertion(() => Assert.Contains("1 test email(s) queued to admin@example.com", cut.Find("[data-testid=ann-success]").TextContent));
        _plans.Verify(p => p.SendAnnouncementAsync(It.IsAny<Guid>(), It.IsAny<SendPlanAnnouncementDto>()), Times.Never());          // a test is not a send
    }

    [Fact]
    public async Task ATestNeedsWordsFirst()
    {
        var cut = RenderPage();
        Assert.True(cut.Find("[data-testid=ann-test]").HasAttribute("disabled"));

        await Write(cut, "en-US", "Now written");

        Assert.False(cut.Find("[data-testid=ann-test]").HasAttribute("disabled"));
    }

    [Fact]
    public void WithoutAnEmailAddressOnTheAccountThereIsNowhereToSendATest()
    {
        var context = new BunitContext();      // a fresh one, whose signed-in person has no email claim
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var localizer = new Mock<IStringLocalizer<SharedResource>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string key) => new LocalizedString(key, key));
        localizer.Setup(l => l[It.IsAny<string>(), It.IsAny<object[]>()]).Returns((string key, object[] args) => new LocalizedString(key, string.Format(key, args)));
        context.Services.AddSingleton(localizer.Object);
        context.Services.AddSingleton(_plans.Object);
        context.Services.AddSingleton<IOptions<RequestLocalizationOptions>>(Options.Create(new RequestLocalizationOptions { SupportedUICultures = [new CultureInfo("en-US")] }));
        context.AddAuthorization().SetAuthorized("no-email");

        var cut = context.Render<PlanAnnouncement>(p => p.Add(x => x.Id, _planId));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=ann-recipients]")));

        Assert.NotEmpty(cut.FindAll("[data-testid=ann-no-test-address]"));
        Assert.True(cut.Find("[data-testid=ann-test]").HasAttribute("disabled"));
    }

    // ---- sending -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SendingAsksForConfirmationFirstAndNothingIsSentUntilItIsGiven()
    {
        var cut = await ReadyToSendAsync();

        cut.Find("[data-testid=ann-send]").Click();

        Assert.Contains("This will email 5 organization(s) now", cut.Find("[data-testid=ann-confirm]").TextContent);
        _plans.Verify(p => p.SendAnnouncementAsync(It.IsAny<Guid>(), It.IsAny<SendPlanAnnouncementDto>()), Times.Never());
    }

    [Fact]
    public async Task DecliningTheConfirmationSendsNothing()
    {
        var cut = await ReadyToSendAsync();
        cut.Find("[data-testid=ann-send]").Click();

        cut.Find(".modal-footer .btn-secondary").Click();

        _plans.Verify(p => p.SendAnnouncementAsync(It.IsAny<Guid>(), It.IsAny<SendPlanAnnouncementDto>()), Times.Never());
        Assert.Empty(cut.FindAll("[data-testid=ann-confirm]"));
    }

    [Fact]
    public async Task ConfirmingSendsTheNumberThatWasShownWithTheCriteriaAndTheWordsAndThenSaysWhatHappened()
    {
        SendPlanAnnouncementDto? sent = null;
        _plans.Setup(p => p.SendAnnouncementAsync(_planId, It.IsAny<SendPlanAnnouncementDto>()))
            .Callback((Guid _, SendPlanAnnouncementDto dto) => sent = dto)
            .ReturnsAsync(new AnnouncementResultDto { Queued = 5, LeftOut = [new PlanMoveReasonDto { Code = "x", Message = "m", Count = 2 }] });
        _plans.Setup(p => p.GetAnnouncementHistoryAsync(_planId)).ReturnsAsync(
            [new AnnouncementBatchDto { Subject = "Big change", Recipients = 5, LeftOut = 2, SentOn = DateTimeOffset.UtcNow }]);
        var cut = await ReadyToSendAsync();
        cut.Find("[data-testid=ann-include-past-due]").Change(true);
        _plans.Setup(p => p.PreviewAnnouncementAsync(_planId, It.Is<AnnouncementCriteriaDto>(c => c.IncludePastDue))).ReturnsAsync(Preview(6));
        cut.Find("[data-testid=ann-include-past-due]").Change(true);

        cut.Find("[data-testid=ann-send]").Click();
        cut.Find(".modal-footer .btn-primary").Click();

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.Equal(6, sent!.ExpectedRecipients);                       // the number on screen when it was confirmed
        Assert.True(sent.Criteria.IncludePastDue);
        var version = Assert.Single(sent.Content.Versions);
        Assert.Equal("Big change", version.Subject);
        Assert.Contains("\"insert\"", version.BodyDelta);                // the editor's structured content, not HTML
        Assert.DoesNotContain("<p>", version.BodyDelta);
        cut.WaitForAssertion(() => Assert.Contains("Queued 5 email(s).", cut.Find("[data-testid=ann-success]").TextContent));
        Assert.Contains("Not sent to 2 organization(s).", cut.Find("[data-testid=ann-success]").TextContent);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=ann-history-row]")));
    }

    [Fact]
    public async Task IfTheAudienceChangedTheApiRefusesAndThePageShowsItsReasonAndCountsAgain()
    {
        var conflict = await ApiException.Create(
            new HttpRequestMessage(HttpMethod.Post, "http://x/"), HttpMethod.Post,
            new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{\"code\":\"audience_changed\",\"message\":\"You confirmed 5 organization(s), but 7 match now.\"}") },
            new RefitSettings());
        _plans.Setup(p => p.SendAnnouncementAsync(_planId, It.IsAny<SendPlanAnnouncementDto>())).ThrowsAsync(conflict);
        var cut = await ReadyToSendAsync();
        _plans.Invocations.Clear();
        _plans.Setup(p => p.PreviewAnnouncementAsync(_planId, It.IsAny<AnnouncementCriteriaDto>())).ReturnsAsync(Preview(7));

        cut.Find("[data-testid=ann-send]").Click();
        cut.Find(".modal-footer .btn-primary").Click();

        cut.WaitForAssertion(() => Assert.Contains("but 7 match now", cut.Find("[data-testid=ann-error]").TextContent));
        cut.WaitForAssertion(() => Assert.Contains("7 organization(s) would be emailed.", cut.Find("[data-testid=ann-recipients]").TextContent));
        Assert.Empty(cut.FindAll("[data-testid=ann-success]"));
    }

    [Fact]
    public async Task AnyOtherFailureIsShownAndNothingIsClaimed()
    {
        _plans.Setup(p => p.SendAnnouncementAsync(_planId, It.IsAny<SendPlanAnnouncementDto>())).ThrowsAsync(new InvalidOperationException("boom"));
        var cut = await ReadyToSendAsync();

        cut.Find("[data-testid=ann-send]").Click();
        cut.Find(".modal-footer .btn-primary").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=ann-error]")));
        Assert.Empty(cut.FindAll("[data-testid=ann-success]"));
    }

    [Fact]
    public void TheHistoryIsListedAndAnEmptyOneSaysSo()
    {
        var empty = RenderPage();
        Assert.Contains("Nothing has been sent from this plan yet.", empty.Find("[data-testid=ann-history]").TextContent);

        _plans.Setup(p => p.GetAnnouncementHistoryAsync(_planId)).ReturnsAsync(
            [new AnnouncementBatchDto { Subject = "Earlier", Recipients = 9, LeftOut = 1, SentOn = DateTimeOffset.UtcNow }]);
        var full = RenderPage();

        var row = Assert.Single(full.FindAll("[data-testid=ann-history-row]"));
        Assert.Contains("Earlier", row.TextContent);
    }

    [Fact]
    public void AFailedHistoryLoadDoesNotBreakThePage()
    {
        _plans.Setup(p => p.GetAnnouncementHistoryAsync(_planId)).ThrowsAsync(new InvalidOperationException("boom"));

        var cut = RenderPage();

        Assert.NotEmpty(cut.FindAll("[data-testid=ann-send]"));
    }

    [Fact]
    public void AFailedCountIsReportedAndSendingStaysOff()
    {
        _plans.Setup(p => p.PreviewAnnouncementAsync(_planId, It.IsAny<AnnouncementCriteriaDto>())).ThrowsAsync(new InvalidOperationException("boom"));

        var cut = Render<PlanAnnouncement>(p => p.Add(x => x.Id, _planId));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=ann-error]")));
        Assert.True(cut.Find("[data-testid=ann-send]").HasAttribute("disabled"));
    }

    // ---- getting to it from the plan list ------------------------------------------------------------------------------------------------------

    [Fact]
    public void OnlyAPlanThatHasHadSubscribersOffersToEmailThem()
    {
        var used = new PlanDto { Id = Guid.NewGuid(), Name = "Used", Active = true, SubscriberCount = 3, Prices = [new PlanPriceDto { TermMonths = 1, BaseAmount = 5m }] };
        var unused = new PlanDto { Id = Guid.NewGuid(), Name = "Unused", Active = true, SubscriberCount = 0, Prices = [new PlanPriceDto { TermMonths = 1, BaseAmount = 5m }] };
        _plans.Setup(p => p.GetAllPlansAsync()).ReturnsAsync([used, unused]);

        var cut = Render<Plans>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));

        var link = Assert.Single(cut.FindAll("[data-testid=plan-announce]"));
        Assert.Equal($"/Admin/Plans/{used.Id}/Announce", link.GetAttribute("href"));
    }
}
