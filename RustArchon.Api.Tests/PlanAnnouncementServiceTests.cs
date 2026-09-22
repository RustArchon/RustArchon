// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Administration;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Services;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// A message from the site admin to the organizations on a plan, against a real Postgres: who it reaches (the plan, or the plan and everything that
/// superseded it; past-due only if asked; only organizations that can be reached), which language each organization gets, that the admin's words are checked
/// before anything is queued, and that what is sent is what the admin confirmed.
/// </summary>
public class PlanAnnouncementServiceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record Queued(
        string TemplateCode, IReadOnlyDictionary<string, string> Tokens, IReadOnlySet<string> Raw, string To, Guid? UserId, Guid? TenantId, string? Culture, Guid? BatchId);

    private (PlanAnnouncementService Service, List<Queued> Sent, HashSet<string> FailFor, UserProfileStore Profiles) Build(string defaultCulture = "en-US")
    {
        var sent = new List<Queued>();
        var failFor = new HashSet<string>();
        var publisher = new Mock<ICommunicationPublisher>();
        publisher.Setup(p => p.QueueTemplatedWithMarkupAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<IReadOnlySet<string>>(), It.IsAny<string>(), It.IsAny<Guid?>(),
                It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns((string code, IReadOnlyDictionary<string, string> tokens, IReadOnlySet<string> raw, string to, Guid? user, Guid? tenant, string? culture, Guid? batch, CancellationToken _) =>
            {
                if (failFor.Contains(to))
                {
                    throw new InvalidOperationException("broker down");
                }

                sent.Add(new Queued(code, tokens, raw, to, user, tenant, culture, batch));
                return Task.FromResult(Guid.NewGuid());
            });
        var settings = new Mock<IPlatformSettingsCache>();
        settings.Setup(s => s.GetStringAsync(PlatformSettingsRegistry.DefaultCulture)).ReturnsAsync(defaultCulture);
        settings.Setup(s => s.GetStringAsync(PlatformSettingsRegistry.SiteName)).ReturnsAsync("Our Site");
        var profiles = new UserProfileStore(new ApiDbContext(postgres.Options), new FixedClock(Now));
        var service = new PlanAnnouncementService(
            new ApiDbContext(postgres.Options), publisher.Object, profiles, settings.Object, new FixedClock(Now), NullLogger<PlanAnnouncementService>.Instance);
        return (service, sent, failFor, profiles);
    }

    // ---- plans and organizations -----------------------------------------------------------------------------------------------

    private static string UniqueName() => "Announce " + Guid.NewGuid().ToString("N")[..10];

    private async Task<Guid> PlanAsync(string name, Guid? supersededBy = null, bool active = true)
    {
        await using var context = new ApiDbContext(postgres.Options);
        var plan = new Plan { Name = name, Active = active, SupersededByPlanId = supersededBy };
        plan.Prices.Add(new PlanPrice { TermMonths = 1, UnitAmount = 0m, IncludedUnits = 1, Currency = "USD" });
        context.Set<Plan>().Add(plan);
        await context.SaveChangesAsync();
        return plan.Id;
    }

    /// <summary>A plan and the versions that replaced it in turn: [0] is the oldest, the last is the newest.</summary>
    private async Task<Guid[]> VersionsAsync(int count)
    {
        var name = UniqueName();
        var ids = new Guid[count];
        for (var i = count - 1; i >= 0; i--)
        {
            ids[i] = await PlanAsync(name, i == count - 1 ? null : ids[i + 1], active: i == count - 1);
        }

        return ids;
    }

    private async Task<Guid> OrganizationAsync(
        Guid planId, string? name = null, string? email = "owner@example.com", SubscriptionStatus status = SubscriptionStatus.Active, bool active = true,
        Guid? createdBy = null, bool open = true)
    {
        var tenantId = Guid.NewGuid();
        await using var context = new ApiDbContext(postgres.Options);
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = name ?? "Org " + tenantId.ToString("N")[..8], IsActive = active, ContactEmail = email });
        await context.SaveChangesAsync();
        if (createdBy is { } creator)
        {
            await context.Set<Tenant>().Where(t => t.Id == tenantId).ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedById, creator));
        }

        context.Set<Subscription>().Add(new Subscription
        {
            TenantId = tenantId, PlanId = planId, StartDate = Now.AddMonths(-2), Status = status, EndDate = open ? null : Now.AddDays(-1)
        });
        await context.SaveChangesAsync();
        return tenantId;
    }

    private static AnnouncementCriteriaDto Criteria(bool superseding = false, bool pastDue = false) =>
        new() { IncludeSupersedingVersions = superseding, IncludePastDue = pastDue };

    private static string Body(string text) => JsonSerializer.Serialize(new { ops = new[] { new { insert = text + "\n" } } });

    private static AnnouncementVersionDto Version(string culture, string subject, string body) =>
        new() { Culture = culture, Subject = subject, BodyDelta = Body(body) };

    private static AnnouncementContentDto Content(params AnnouncementVersionDto[] versions) => new() { Mode = AnnouncementLanguageMode.PerLanguage, Versions = [.. versions] };

    // ---- who it reaches -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePlanOnlyReachesThatPlansOwnOrganizations()
    {
        var v = await VersionsAsync(2);
        var oldOrg = await OrganizationAsync(v[0]);
        await OrganizationAsync(v[1]);
        var (service, _, _, _) = Build();

        var audience = await service.ResolveAudienceAsync(v[0], Criteria());

        Assert.Equal([oldOrg], audience!.Recipients.Select(r => r.TenantId));
    }

    [Fact]
    public async Task IncludingNewerVersionsAddsEveryPlanThatSupersededItButNeverOlderOnes()
    {
        var v = await VersionsAsync(3);
        var on1 = await OrganizationAsync(v[0]);
        var on2 = await OrganizationAsync(v[1]);
        var on3 = await OrganizationAsync(v[2]);
        var (service, _, _, _) = Build();

        var fromOldest = await service.ResolveAudienceAsync(v[0], Criteria(superseding: true));
        var fromMiddle = await service.ResolveAudienceAsync(v[1], Criteria(superseding: true));
        var fromNewest = await service.ResolveAudienceAsync(v[2], Criteria(superseding: true));

        Assert.Equal(new[] { on1, on2, on3 }.Order(), fromOldest!.Recipients.Select(r => r.TenantId).Order());
        Assert.Equal(new[] { on2, on3 }.Order(), fromMiddle!.Recipients.Select(r => r.TenantId).Order());          // not the version before it
        Assert.Equal([on3], fromNewest!.Recipients.Select(r => r.TenantId));
    }

    [Fact]
    public async Task EachRecipientNamesTheVersionItIsOn()
    {
        var v = await VersionsAsync(2);
        await OrganizationAsync(v[0]);
        var (service, _, _, _) = Build();

        var audience = await service.ResolveAudienceAsync(v[0], Criteria(superseding: true));

        Assert.Equal(v[0], Assert.Single(audience!.Recipients).PlanId);
    }

    [Fact]
    public async Task OrganizationsThatLeftThePlanAreNotReached()
    {
        var v = await VersionsAsync(1);
        var current = await OrganizationAsync(v[0]);
        await OrganizationAsync(v[0], open: false);
        var (service, _, _, _) = Build();

        var audience = await service.ResolveAudienceAsync(v[0], Criteria());

        Assert.Equal([current], audience!.Recipients.Select(r => r.TenantId));
    }

    [Fact]
    public async Task PastDueOrganizationsAreLeftOutUnlessAskedForAndCountedEitherWay()
    {
        var v = await VersionsAsync(1);
        await OrganizationAsync(v[0]);
        var late = await OrganizationAsync(v[0], status: SubscriptionStatus.PastDue);
        var (service, _, _, _) = Build();

        var without = await service.ResolveAudienceAsync(v[0], Criteria(pastDue: false));
        var with = await service.ResolveAudienceAsync(v[0], Criteria(pastDue: true));

        Assert.Equal(1, without!.Recipients.Count);
        Assert.Equal(1, without.LeftOut[PlanAnnouncementService.PastDueLeftOut]);
        Assert.Equal(2, with!.Recipients.Count);
        Assert.Contains(late, with.Recipients.Select(r => r.TenantId));
        Assert.False(with.LeftOut.ContainsKey(PlanAnnouncementService.PastDueLeftOut));
    }

    [Theory]
    [InlineData(SubscriptionStatus.Suspended, true)]
    [InlineData(SubscriptionStatus.Cancelled, true)]
    [InlineData(SubscriptionStatus.Active, false)]
    public async Task SuspendedCancelledAndInactiveOrganizationsAreNeverReachedNoMatterWhatIsAskedFor(SubscriptionStatus status, bool tenantActive)
    {
        var v = await VersionsAsync(1);
        await OrganizationAsync(v[0], status: status, active: tenantActive);
        var (service, _, _, _) = Build();

        var audience = await service.ResolveAudienceAsync(v[0], Criteria(superseding: true, pastDue: true));

        Assert.Empty(audience!.Recipients);
        Assert.Equal(1, audience.LeftOut[PlanAnnouncementService.NotInGoodStanding]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not an email")]
    [InlineData("Owner <owner@example.com>")]
    [InlineData("a@b")]
    public async Task AnOrganizationWithNoUsableContactEmailIsReportedNotSilentlyDropped(string? email)
    {
        var v = await VersionsAsync(1);
        await OrganizationAsync(v[0], email: email);
        await OrganizationAsync(v[0]);
        var (service, _, _, _) = Build();

        var audience = await service.ResolveAudienceAsync(v[0], Criteria());

        Assert.Equal(1, audience!.Recipients.Count);
        Assert.Equal(1, audience.LeftOut[PlanAnnouncementService.NoContactEmail]);
    }

    [Fact]
    public async Task TheAddressIsTrimmedAndRecipientsAreInOrganizationNameOrder()
    {
        var v = await VersionsAsync(1);
        await OrganizationAsync(v[0], name: "Zulu", email: "  z@example.com ");
        await OrganizationAsync(v[0], name: "alpha", email: "a@example.com");
        var (service, _, _, _) = Build();

        var audience = await service.ResolveAudienceAsync(v[0], Criteria());

        Assert.Equal(["alpha", "Zulu"], audience!.Recipients.Select(r => r.OrganizationName));
        Assert.Equal("z@example.com", audience.Recipients[1].Email);
    }

    [Fact]
    public async Task AnOrganizationsLanguageIsTheLanguageOfThePersonWhoCreatedIt()
    {
        var v = await VersionsAsync(1);
        var spanish = Guid.NewGuid();
        var unset = Guid.NewGuid();
        var (service, _, _, profiles) = Build();
        await profiles.SetPreferredCultureAsync(spanish, "es-US");
        var esOrg = await OrganizationAsync(v[0], createdBy: spanish);
        var noneOrg = await OrganizationAsync(v[0], createdBy: unset);

        var audience = await service.ResolveAudienceAsync(v[0], Criteria());

        Assert.Equal("es-US", audience!.Recipients.Single(r => r.TenantId == esOrg).Culture);
        Assert.Null(audience.Recipients.Single(r => r.TenantId == noneOrg).Culture);
    }

    [Fact]
    public async Task APlanThatDoesNotExistHasNoAudience()
    {
        var (service, _, _, _) = Build();

        Assert.Null(await service.ResolveAudienceAsync(Guid.NewGuid(), Criteria()));
        Assert.Null(await service.PreviewAsync(Guid.NewGuid(), Criteria()));
    }

    [Fact]
    public async Task ALoopInTheVersionLinksDoesNotHangTheLookup()
    {
        var a = await PlanAsync(UniqueName(), active: false);
        var b = await PlanAsync(UniqueName(), supersededBy: a, active: false);
        await using (var context = new ApiDbContext(postgres.Options))
        {
            await context.Set<Plan>().Where(p => p.Id == a).ExecuteUpdateAsync(s => s.SetProperty(p => p.SupersededByPlanId, b));
        }

        await OrganizationAsync(a);
        var (service, _, _, _) = Build();

        var audience = await service.ResolveAudienceAsync(a, Criteria(superseding: true));

        Assert.Single(audience!.Recipients);
    }

    // ---- the preview ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePreviewSaysHowManyByVersionAndByLanguageAndWhoIsLeftOut()
    {
        var v = await VersionsAsync(2);
        var (service, _, _, profiles) = Build(defaultCulture: "en-US");
        var spanish = Guid.NewGuid();
        await profiles.SetPreferredCultureAsync(spanish, "es-US");
        await OrganizationAsync(v[0], createdBy: spanish);
        await OrganizationAsync(v[1]);
        await OrganizationAsync(v[1]);
        await OrganizationAsync(v[1], email: null);
        await OrganizationAsync(v[1], status: SubscriptionStatus.Suspended);

        var preview = await service.PreviewAsync(v[0], Criteria(superseding: true));

        Assert.Equal(3, preview!.Recipients);
        Assert.Equal(2, preview.ByPlanVersion.Single(c => c.Key == v[1].ToString()).Count);
        Assert.Equal(1, preview.ByPlanVersion.Single(c => c.Key == v[0].ToString()).Count);
        Assert.Equal(1, preview.ByLanguage.Single(c => c.Key == "es-US").Count);
        Assert.Equal(2, preview.ByLanguage.Single(c => c.Key == string.Empty).Count);          // none set: they get the default language
        Assert.Equal("en-US", preview.DefaultCulture);
        Assert.Contains(preview.LeftOut, r => r.Code == PlanAnnouncementService.NoContactEmail && r.Count == 1);
        Assert.Contains(preview.LeftOut, r => r.Code == PlanAnnouncementService.NotInGoodStanding && r.Count == 1);
        Assert.All(preview.LeftOut, r => Assert.NotEqual(r.Code, r.Message));
    }

    // ---- the words ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AVersionPerLanguageIsPreparedAndTheDefaultLanguageMustBeThere()
    {
        var (service, _, _, _) = Build(defaultCulture: "en-US");

        var ok = await service.PrepareAsync(Content(Version("en-US", "Hello", "Body"), Version("es-US", "Hola", "Cuerpo")));
        var missing = await service.PrepareAsync(Content(Version("es-US", "Hola", "Cuerpo")));

        Assert.NotNull(ok.Prepared);
        Assert.Equal(2, ok.Prepared!.Versions.Count);
        Assert.Null(missing.Prepared);
        Assert.Equal("default_culture_missing", missing.Problem!.Code);
        Assert.Contains("en-US", missing.Problem.Message);
    }

    [Fact]
    public async Task ARegionlessVersionSatisfiesTheDefaultLanguage()
    {
        var (service, _, _, _) = Build(defaultCulture: "en-US");

        var result = await service.PrepareAsync(Content(Version("en", "Hello", "Body")));

        Assert.NotNull(result.Prepared);
    }

    [Fact]
    public async Task ASingleLanguageSendNeedsOnlyThatLanguageEvenIfItIsNotTheDefault()
    {
        var (service, _, _, _) = Build(defaultCulture: "en-US");
        var content = Content(Version("es-US", "Hola", "Cuerpo"));
        content.Mode = AnnouncementLanguageMode.SingleLanguage;
        content.SingleCulture = "es-US";

        var result = await service.PrepareAsync(content);

        Assert.NotNull(result.Prepared);
        Assert.Equal("es-US", result.Prepared!.SingleCulture);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fr-FR")]
    public async Task ASingleLanguageSendMustNameOneOfTheVersionsItHas(string? single)
    {
        var (service, _, _, _) = Build();
        var content = Content(Version("en-US", "Hello", "Body"));
        content.Mode = AnnouncementLanguageMode.SingleLanguage;
        content.SingleCulture = single;

        var result = await service.PrepareAsync(content);

        Assert.Equal("single_culture_missing", result.Problem!.Code);
    }

    [Fact]
    public async Task ATokenThatHasNoValueIsRefusedInTheSubjectAndInTheBody()
    {
        var (service, _, _, _) = Build();

        var inSubject = await service.PrepareAsync(Content(Version("en-US", "Hi {{Password}}", "Body")));
        var inBody = await service.PrepareAsync(Content(Version("en-US", "Hi", "Dear {{Nope}}")));
        var fine = await service.PrepareAsync(Content(Version("en-US", "Hi {{OrganizationName}}", "On {{PlanName}} at {{SiteName}}")));

        Assert.Equal("unknown_token", inSubject.Problem!.Code);
        Assert.Contains("{{Password}}", inSubject.Problem.Message);
        Assert.Equal("unknown_token", inBody.Problem!.Code);
        Assert.NotNull(fine.Prepared);
    }

    [Theory]
    [InlineData("", "Body", "empty_subject")]
    [InlineData("   ", "Body", "empty_subject")]
    [InlineData("Subject", "", "empty_body")]
    [InlineData("Subject", "   ", "empty_body")]
    public async Task ASubjectAndAMessageAreBothRequired(string subject, string body, string code)
    {
        var (service, _, _, _) = Build();

        var result = await service.PrepareAsync(Content(Version("en-US", subject, body)));

        Assert.Equal(code, result.Problem!.Code);
    }

    [Fact]
    public async Task AMessageOfOnlyEmptyParagraphsIsNotAMessage()
    {
        var (service, _, _, _) = Build();
        var version = new AnnouncementVersionDto { Culture = "en-US", Subject = "Hi", BodyDelta = "{\"ops\":[{\"insert\":\"\\n\\n\\n\"}]}" };

        Assert.Equal("empty_body", (await service.PrepareAsync(Content(version))).Problem!.Code);
    }

    [Theory]
    [InlineData("english")]
    [InlineData("en_US")]
    [InlineData("<script>")]
    public async Task ALanguageThatIsNotALanguageNameIsRefused(string culture)
    {
        var (service, _, _, _) = Build();

        Assert.Equal("bad_culture", (await service.PrepareAsync(Content(Version(culture, "Hi", "Body")))).Problem!.Code);
    }

    [Fact]
    public async Task TwoVersionsForOneLanguageAreRefusedWhateverTheCase()
    {
        var (service, _, _, _) = Build();

        var result = await service.PrepareAsync(Content(Version("en-US", "A", "a"), Version("EN-us", "B", "b")));

        Assert.Equal("duplicate_culture", result.Problem!.Code);
    }

    [Fact]
    public async Task ASubjectIsMadeOneLineAndAnOverlongOneIsRefused()
    {
        var (service, _, _, _) = Build();

        var lines = await service.PrepareAsync(Content(Version("en-US", "Two\r\nBcc: x@y.z", "Body")));
        var tooLong = await service.PrepareAsync(Content(Version("en-US", new string('s', 201), "Body")));

        Assert.Equal("Two Bcc: x@y.z", lines.Prepared!.Versions["en-US"].Subject);
        Assert.Equal("subject_too_long", tooLong.Problem!.Code);
    }

    [Fact]
    public async Task AMessageThatIsNotWhatTheEditorSendsIsRefused()
    {
        var (service, _, _, _) = Build();
        var version = new AnnouncementVersionDto { Culture = "en-US", Subject = "Hi", BodyDelta = "<p>raw html is not a message</p>" };

        Assert.Equal("bad_body", (await service.PrepareAsync(Content(version))).Problem!.Code);
    }

    [Fact]
    public async Task NoVersionsAtAllIsNothingToSend()
    {
        var (service, _, _, _) = Build();

        Assert.Equal("no_versions", (await service.PrepareAsync(new AnnouncementContentDto())).Problem!.Code);
    }

    [Theory]
    [InlineData("es-US", "es-US", "en-US", "es-US")]         // exactly
    [InlineData("es-MX", "es", "en-US", "es")]               // its language without the region
    [InlineData("fr-FR", "es-US", "en-US", "en-US")]         // no version for it: the default language's
    [InlineData(null, "es-US", "en-US", "en-US")]            // none set: the default language's
    [InlineData("de-DE", "es-US", "en-GB", "en")]            // the default language without its region
    public void EachOrganizationGetsTheBestVersionForItsLanguage(string? organizationLanguage, string versionA, string defaultCulture, string expected)
    {
        var versions = new[] { versionA, "en-US", "en" }.Distinct().ToDictionary(c => c, c => new PreparedVersion(c, c, c), StringComparer.OrdinalIgnoreCase);
        if (defaultCulture == "en-GB")
        {
            versions.Remove("en-US");
        }

        var picked = PlanAnnouncementService.Pick(versions, organizationLanguage, defaultCulture);

        Assert.Equal(expected, picked!.Culture);
    }

    [Fact]
    public void WhenNothingFitsThereIsNoVersionToPick()
    {
        var versions = new Dictionary<string, PreparedVersion> { ["fr-FR"] = new("fr-FR", "s", "b") };

        Assert.Null(PlanAnnouncementService.Pick(versions, "de-DE", "en-US"));
    }

    // ---- sending -------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EachOrganizationIsEmailedOnceInItsOwnLanguageWithItsOwnNameFilledIn()
    {
        var v = await VersionsAsync(1);
        var (service, sent, _, profiles) = Build();
        var spanish = Guid.NewGuid();
        await profiles.SetPreferredCultureAsync(spanish, "es-US");
        var english = await OrganizationAsync(v[0], name: "Alpha Co", email: "alpha@example.com");
        var espanol = await OrganizationAsync(v[0], name: "Beta SA", email: "beta@example.com", createdBy: spanish);
        var content = Content(Version("en-US", "Change for {{OrganizationName}}", "Dear {{OrganizationName}}, your {{PlanName}} plan changes."),
            Version("es-US", "Cambio para {{OrganizationName}}", "Estimado {{OrganizationName}}, su plan {{PlanName}} cambia."));

        var (result, problem) = await service.SendAsync(v[0], new SendPlanAnnouncementDto { Criteria = Criteria(), Content = content, ExpectedRecipients = 2 }, sentBy: Guid.NewGuid());

        Assert.Null(problem);
        Assert.Equal(2, result!.Queued);
        Assert.Equal(2, sent.Count);
        var en = sent.Single(s => s.To == "alpha@example.com");
        var es = sent.Single(s => s.To == "beta@example.com");
        Assert.Equal("Change for Alpha Co", en.Tokens[EmailTemplateRegistry.Placeholders.AnnouncementSubject]);
        Assert.Contains("Dear Alpha Co", en.Tokens[EmailTemplateRegistry.Placeholders.AnnouncementBody]);
        Assert.Equal("Cambio para Beta SA", es.Tokens[EmailTemplateRegistry.Placeholders.AnnouncementSubject]);
        Assert.Contains("Estimado Beta SA", es.Tokens[EmailTemplateRegistry.Placeholders.AnnouncementBody]);
        Assert.Equal("es-US", es.Culture);
        Assert.Equal(english, en.TenantId);
        Assert.Equal(espanol, es.TenantId);
    }

    [Fact]
    public async Task EmailsGoThroughTheAnnouncementTemplateWithTheBodyAsTrustedMarkupOnly()
    {
        var v = await VersionsAsync(1);
        await OrganizationAsync(v[0]);
        var (service, sent, _, _) = Build();

        await service.SendAsync(v[0], new SendPlanAnnouncementDto { Criteria = Criteria(), Content = Content(Version("en-US", "S", "B")), ExpectedRecipients = 1 }, null);

        var email = Assert.Single(sent);
        Assert.Equal(EmailTemplateRegistry.Codes.PlanAnnouncement, email.TemplateCode);
        Assert.Equal([EmailTemplateRegistry.Placeholders.AnnouncementBody], email.Raw);          // only the body is markup; the subject never is
        Assert.Null(email.UserId);                                                                // an organization, not a person
    }

    [Fact]
    public async Task AnOrganizationsNameIsEncodedInTheBodyAndCannotBecomeMarkup()
    {
        var v = await VersionsAsync(1);
        await OrganizationAsync(v[0], name: "<script>alert(1)</script> & Sons");
        var (service, sent, _, _) = Build();

        await service.SendAsync(v[0], new SendPlanAnnouncementDto
        {
            Criteria = Criteria(), Content = Content(Version("en-US", "For {{OrganizationName}}", "Dear {{OrganizationName}}")), ExpectedRecipients = 1
        }, null);

        var body = sent.Single().Tokens[EmailTemplateRegistry.Placeholders.AnnouncementBody];
        Assert.DoesNotContain("<script", body);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; &amp; Sons", body);
    }

    [Fact]
    public async Task ALineBreakInAnOrganizationsNameCannotForgeAHeaderInTheSubject()
    {
        var v = await VersionsAsync(1);
        await OrganizationAsync(v[0], name: "Evil\r\nBcc: attacker@example.com");
        var (service, sent, _, _) = Build();

        await service.SendAsync(v[0], new SendPlanAnnouncementDto
        {
            Criteria = Criteria(), Content = Content(Version("en-US", "For {{OrganizationName}}", "Body")), ExpectedRecipients = 1
        }, null);

        var subject = sent.Single().Tokens[EmailTemplateRegistry.Placeholders.AnnouncementSubject];
        Assert.DoesNotContain('\n', subject);
        Assert.DoesNotContain('\r', subject);
    }

    [Fact]
    public async Task ASingleLanguageSendGivesEveryoneThatVersionWhateverTheirLanguage()
    {
        var v = await VersionsAsync(1);
        var (service, sent, _, profiles) = Build();
        var spanish = Guid.NewGuid();
        await profiles.SetPreferredCultureAsync(spanish, "es-US");
        await OrganizationAsync(v[0], email: "a@example.com", createdBy: spanish);
        await OrganizationAsync(v[0], email: "b@example.com");
        var content = Content(Version("en-US", "English", "English body"), Version("es-US", "Spanish", "Spanish body"));
        content.Mode = AnnouncementLanguageMode.SingleLanguage;
        content.SingleCulture = "en-US";

        await service.SendAsync(v[0], new SendPlanAnnouncementDto { Criteria = Criteria(), Content = content, ExpectedRecipients = 2 }, null);

        Assert.All(sent, s => Assert.Equal("English", s.Tokens[EmailTemplateRegistry.Placeholders.AnnouncementSubject]));
        Assert.All(sent, s => Assert.Equal("en-US", s.Culture));
    }

    [Fact]
    public async Task TheSendIsRecordedAsABatchThatEveryEmailBelongsTo()
    {
        var v = await VersionsAsync(1);
        await OrganizationAsync(v[0]);
        await OrganizationAsync(v[0], email: null);
        var (service, sent, _, _) = Build();
        var admin = Guid.NewGuid();

        var (result, _) = await service.SendAsync(
            v[0], new SendPlanAnnouncementDto { Criteria = Criteria(superseding: true, pastDue: true), Content = Content(Version("en-US", "The subject", "Body")), ExpectedRecipients = 1 }, admin);

        Assert.All(sent, s => Assert.Equal(result!.BatchId, s.BatchId));
        await using var context = new ApiDbContext(postgres.Options);
        var batch = await context.Set<CommunicationBatch>().AsNoTracking().SingleAsync(b => b.Id == result!.BatchId);
        Assert.Equal(v[0], batch.PlanId);
        Assert.Equal(1, batch.RecipientCount);
        Assert.Equal(1, batch.SkippedCount);
        Assert.Equal("The subject", batch.Subject);
        Assert.True(batch.IncludeSupersedingVersions);
        Assert.True(batch.IncludePastDue);
        Assert.Equal("PerLanguage", batch.LanguageMode);
        Assert.Equal(admin, batch.SentById);
        Assert.Equal(Now, batch.SentOn);
        Assert.Contains(result!.LeftOut, r => r.Code == PlanAnnouncementService.NoContactEmail);
    }

    [Fact]
    public async Task WhatWasConfirmedIsWhatIsSentOrNothingIs()
    {
        var v = await VersionsAsync(1);
        await OrganizationAsync(v[0]);
        await OrganizationAsync(v[0]);
        var (service, sent, _, _) = Build();

        var (result, problem) = await service.SendAsync(
            v[0], new SendPlanAnnouncementDto { Criteria = Criteria(), Content = Content(Version("en-US", "S", "B")), ExpectedRecipients = 1 }, null);

        Assert.Null(result);
        Assert.Equal("audience_changed", problem!.Code);
        Assert.Empty(sent);
        await using var context = new ApiDbContext(postgres.Options);
        Assert.Equal(0, await context.Set<CommunicationBatch>().CountAsync(b => b.PlanId == v[0]));          // and no batch was left behind
    }

    [Fact]
    public async Task NothingIsSentToNobodyOrForAPlanThatIsNotThere()
    {
        var v = await VersionsAsync(1);
        var (service, sent, _, _) = Build();
        var body = new SendPlanAnnouncementDto { Criteria = Criteria(), Content = Content(Version("en-US", "S", "B")), ExpectedRecipients = 0 };

        Assert.Equal("no_recipients", (await service.SendAsync(v[0], body, null)).Problem!.Code);
        Assert.Equal("no_such_plan", (await service.SendAsync(Guid.NewGuid(), body, null)).Problem!.Code);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task TheWordsAreCheckedBeforeAnythingIsQueued()
    {
        var v = await VersionsAsync(1);
        await OrganizationAsync(v[0]);
        var (service, sent, _, _) = Build();

        var (result, problem) = await service.SendAsync(
            v[0], new SendPlanAnnouncementDto { Criteria = Criteria(), Content = Content(Version("en-US", "Hi", "Dear {{Nope}}")), ExpectedRecipients = 1 }, null);

        Assert.Null(result);
        Assert.Equal("unknown_token", problem!.Code);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task OneEmailFailingToQueueDoesNotStopTheRestAndIsReported()
    {
        var v = await VersionsAsync(1);
        await OrganizationAsync(v[0], email: "ok1@example.com");
        await OrganizationAsync(v[0], email: "bad@example.com");
        await OrganizationAsync(v[0], email: "ok2@example.com");
        var (service, sent, failFor, _) = Build();
        failFor.Add("bad@example.com");

        var (result, problem) = await service.SendAsync(
            v[0], new SendPlanAnnouncementDto { Criteria = Criteria(), Content = Content(Version("en-US", "S", "B")), ExpectedRecipients = 3 }, null);

        Assert.Null(problem);
        Assert.Equal(2, result!.Queued);
        Assert.Equal(["ok1@example.com", "ok2@example.com"], sent.Select(s => s.To).Order());
        Assert.Contains(result.LeftOut, r => r.Code == "could_not_queue" && r.Count == 1);
        await using var context = new ApiDbContext(postgres.Options);
        Assert.Equal(2, (await context.Set<CommunicationBatch>().AsNoTracking().SingleAsync(b => b.Id == result.BatchId)).RecipientCount);
    }

    // ---- a test send ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATestGoesToOneAddressOnceForEachLanguageMarkedAsATestAndTiedToNoOrganization()
    {
        var v = await VersionsAsync(1);
        var (service, sent, _, _) = Build();

        var (queued, problem) = await service.SendTestAsync(v[0], new SendAnnouncementTestDto
        {
            Content = Content(Version("en-US", "Hello {{OrganizationName}}", "Body {{PlanName}}"), Version("es-US", "Hola", "Cuerpo")), ToAddress = "admin@example.com"
        });

        Assert.Null(problem);
        Assert.Equal(2, queued);
        Assert.All(sent, s => Assert.Equal("admin@example.com", s.To));
        Assert.All(sent, s => Assert.Null(s.TenantId));
        Assert.All(sent, s => Assert.Null(s.BatchId));
        Assert.All(sent, s => Assert.StartsWith("[Test] ", s.Tokens[EmailTemplateRegistry.Placeholders.AnnouncementSubject]));
        Assert.Equal("[Test] Hello Example Organization", sent.Single(s => s.Culture == "en-US").Tokens[EmailTemplateRegistry.Placeholders.AnnouncementSubject]);
        await using var context = new ApiDbContext(postgres.Options);
        Assert.Equal(0, await context.Set<CommunicationBatch>().CountAsync(b => b.PlanId == v[0]));
    }

    [Fact]
    public async Task ATestOfASingleLanguageSendIsOneEmail()
    {
        var v = await VersionsAsync(1);
        var (service, sent, _, _) = Build();
        var content = Content(Version("en-US", "E", "e"), Version("es-US", "S", "s"));
        content.Mode = AnnouncementLanguageMode.SingleLanguage;
        content.SingleCulture = "es-US";

        var (queued, _) = await service.SendTestAsync(v[0], new SendAnnouncementTestDto { Content = content, ToAddress = "admin@example.com" });

        Assert.Equal(1, queued);
        Assert.Equal("es-US", Assert.Single(sent).Culture);
    }

    [Fact]
    public async Task ATestWithBadWordsOrForAMissingPlanSendsNothing()
    {
        var v = await VersionsAsync(1);
        var (service, sent, _, _) = Build();

        var badWords = await service.SendTestAsync(v[0], new SendAnnouncementTestDto { Content = Content(Version("en-US", "", "B")), ToAddress = "a@example.com" });
        var noPlan = await service.SendTestAsync(Guid.NewGuid(), new SendAnnouncementTestDto { Content = Content(Version("en-US", "S", "B")), ToAddress = "a@example.com" });

        Assert.Equal("empty_subject", badWords.Problem!.Code);
        Assert.Equal("no_such_plan", noPlan.Problem!.Code);
        Assert.Empty(sent);
    }

    // ---- history -------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheHistoryIsThisPlansSendsNewestFirst()
    {
        var v = await VersionsAsync(1);
        var other = await VersionsAsync(1);
        await OrganizationAsync(v[0]);
        await OrganizationAsync(other[0]);
        var (service, _, _, _) = Build();
        var body = (string subject) => new SendPlanAnnouncementDto { Criteria = Criteria(), Content = Content(Version("en-US", subject, "B")), ExpectedRecipients = 1 };
        await service.SendAsync(v[0], body("first"), null);
        await service.SendAsync(other[0], body("elsewhere"), null);

        // A later send from this plan: its time is later than the first, so it comes first.
        var later = new PlanAnnouncementService(
            new ApiDbContext(postgres.Options), new Mock<ICommunicationPublisher>().Object, new UserProfileStore(new ApiDbContext(postgres.Options), new FixedClock(Now)),
            Mock.Of<IPlatformSettingsCache>(s => s.GetStringAsync(PlatformSettingsRegistry.DefaultCulture) == Task.FromResult<string?>("en-US")),
            new FixedClock(Now.AddHours(1)), NullLogger<PlanAnnouncementService>.Instance);
        await later.SendAsync(v[0], body("second"), null);

        var history = await service.HistoryAsync(v[0], 10);

        Assert.Equal(["second", "first"], history.Select(h => h.Subject));
        Assert.All(history, h => Assert.Equal(1, h.Recipients));
    }
}
