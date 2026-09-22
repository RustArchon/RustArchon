// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Repositories;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;
using RustArchon.Api.Administration;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// The pieces an announcement rests on besides its own service: the renderer's one way of putting finished markup into an email (and only by name), the
/// publisher recording the send a communication belongs to, and the endpoints' answers - a missing plan is 404, an audience that is not the size the admin
/// confirmed is 409, unacceptable words are 400.
/// </summary>
public class PlanAnnouncementsControllerTests
{
    private readonly Guid _admin = Guid.NewGuid();

    // ---- the renderer -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ATokenNamedAsMarkupIsPutInAsItIsAndEveryOtherTokenIsStillEncoded()
    {
        var tokens = new Dictionary<string, string> { ["Body"] = "<p><strong>Hi</strong></p>", ["Name"] = "<b>Acme & Sons</b>" };

        var (subject, html) = EmailTemplateRenderer.Render("S {{Name}}", "{{Body}} / {{Name}}", tokens, new HashSet<string> { "Body" });

        Assert.Equal("<p><strong>Hi</strong></p> / &lt;b&gt;Acme &amp; Sons&lt;/b&gt;", html);
        Assert.Equal("S <b>Acme & Sons</b>", subject);          // a subject is a plain header: nothing is ever encoded there, raw or not
    }

    [Fact]
    public void WithoutNamingAnyTokenAsMarkupEverythingIsEncodedAsItAlwaysWas()
    {
        var tokens = new Dictionary<string, string> { ["Body"] = "<p>x</p>" };

        var (_, plain) = EmailTemplateRenderer.Render("s", "{{Body}}", tokens);
        var (_, empty) = EmailTemplateRenderer.Render("s", "{{Body}}", tokens, new HashSet<string>());
        var (_, other) = EmailTemplateRenderer.Render("s", "{{Body}}", tokens, new HashSet<string> { "SomethingElse" });

        Assert.All(new[] { plain, empty, other }, h => Assert.Equal("&lt;p&gt;x&lt;/p&gt;", h));
    }

    // ---- the publisher ------------------------------------------------------------------------------------------------------------

    private static (CommunicationPublisher Publisher, List<Communication> Saved) Publisher()
    {
        var template = new EmailTemplate { Code = EmailTemplateRegistry.Codes.PlanAnnouncement, Name = "Announcement" };
        template.Translations.Add(new EmailTemplateTranslation { Culture = "en-US", Subject = "{{AnnouncementSubject}}", HtmlBody = "{{AnnouncementBody}}" });
        var templates = new Mock<IEmailTemplateRepository>();
        templates.Setup(t => t.GetByCodeAsync(EmailTemplateRegistry.Codes.PlanAnnouncement)).ReturnsAsync(template);

        var saved = new List<Communication>();
        var communications = new Mock<ICommunicationRepository>();
        communications.Setup(c => c.AddAsync(It.IsAny<Communication>())).Callback((Communication c) => saved.Add(c)).ReturnsAsync((Communication c) => c);

        var settings = new Mock<IPlatformSettingsCache>();
        settings.Setup(s => s.GetStringAsync(PlatformSettingsRegistry.DefaultCulture)).ReturnsAsync("en-US");
        settings.Setup(s => s.GetStringAsync(PlatformSettingsRegistry.SiteName)).ReturnsAsync("Site");
        settings.Setup(s => s.GetStringAsync(PlatformSettingsRegistry.SiteUrl)).ReturnsAsync("https://site.example");

        var profiles = new Mock<IUserProfileStore>();
        var publisher = new CommunicationPublisher(
            communications.Object, templates.Object, new Mock<IPublishEndpoint>().Object, new ConfigurationBuilder().Build(), settings.Object, profiles.Object);
        return (publisher, saved);
    }

    private static readonly IReadOnlyDictionary<string, string> Tokens = new Dictionary<string, string>
    {
        [EmailTemplateRegistry.Placeholders.AnnouncementSubject] = "Big news",
        [EmailTemplateRegistry.Placeholders.AnnouncementBody] = "<p><strong>Hello</strong> &amp; welcome</p>"
    };

    [Fact]
    public async Task TheAnnouncementBodyReachesTheEmailAsMarkupAndTheSubjectAsTheSubject()
    {
        var (publisher, saved) = Publisher();

        await publisher.QueueTemplatedWithMarkupAsync(
            EmailTemplateRegistry.Codes.PlanAnnouncement, Tokens, new HashSet<string> { EmailTemplateRegistry.Placeholders.AnnouncementBody }, "org@example.com",
            userId: null, tenantId: Guid.NewGuid());

        var email = Assert.Single(saved);
        Assert.Equal("Big news", email.Subject);
        Assert.Contains("<p><strong>Hello</strong> &amp; welcome</p>", email.HtmlBody);
    }

    [Fact]
    public async Task WithoutTheMarkupFlagTheSameTokenIsEncodedSoAnotherCallerCannotSmuggleMarkupIn()
    {
        var (publisher, saved) = Publisher();

        await publisher.QueueTemplatedAsync(EmailTemplateRegistry.Codes.PlanAnnouncement, Tokens, "org@example.com", userId: null, tenantId: null);

        Assert.Contains("&lt;p&gt;&lt;strong&gt;Hello", Assert.Single(saved).HtmlBody);
    }

    [Fact]
    public async Task TheCommunicationRecordsWhichSendItWasOneEmailOf()
    {
        var (publisher, saved) = Publisher();
        var batch = Guid.NewGuid();
        var tenant = Guid.NewGuid();

        await publisher.QueueTemplatedWithMarkupAsync(
            EmailTemplateRegistry.Codes.PlanAnnouncement, Tokens, new HashSet<string> { EmailTemplateRegistry.Placeholders.AnnouncementBody }, "org@example.com",
            userId: null, tenantId: tenant, batchId: batch);

        var email = Assert.Single(saved);
        Assert.Equal(batch, email.BatchId);
        Assert.Equal(tenant, email.TenantId);
    }

    [Fact]
    public async Task AnOrdinaryEmailBelongsToNoBatch()
    {
        var (publisher, saved) = Publisher();

        await publisher.QueueTemplatedAsync(EmailTemplateRegistry.Codes.PlanAnnouncement, Tokens, "a@example.com", null, null);

        Assert.Null(Assert.Single(saved).BatchId);
    }

    // ---- the endpoints ------------------------------------------------------------------------------------------------------------

    private PlanAnnouncementsController Controller(Mock<IPlanAnnouncementService> service)
    {
        var user = new Mock<IUserContext>();
        user.Setup(u => u.GetCurrentUserIdAsync()).ReturnsAsync(_admin);
        return new PlanAnnouncementsController(service.Object, user.Object);
    }

    private static SendPlanAnnouncementDto Request() => new() { Criteria = new AnnouncementCriteriaDto(), Content = new AnnouncementContentDto(), ExpectedRecipients = 3 };

    [Fact]
    public void EveryActionIsBehindThePlanManagementPolicy()
    {
        var policy = typeof(PlanAnnouncementsController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single().Policy;

        Assert.Equal("ManagePlans", policy);
        Assert.Empty(typeof(PlanAnnouncementsController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), inherit: true));
    }

    [Fact]
    public async Task APreviewOfAPlanThatIsNotThereIsNotFoundAndOtherwiseIsTheAnswer()
    {
        var service = new Mock<IPlanAnnouncementService>();
        var id = Guid.NewGuid();
        service.Setup(s => s.PreviewAsync(id, It.IsAny<AnnouncementCriteriaDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AnnouncementPreviewDto { Recipients = 7 });

        var found = await Controller(service).Preview(id, new AnnouncementCriteriaDto(), CancellationToken.None);
        var missing = await Controller(service).Preview(Guid.NewGuid(), new AnnouncementCriteriaDto(), CancellationToken.None);

        Assert.Equal(7, Assert.IsType<AnnouncementPreviewDto>(Assert.IsType<OkObjectResult>(found.Result).Value).Recipients);
        Assert.IsType<NotFoundResult>(missing.Result);
    }

    [Theory]
    [InlineData("audience_changed", typeof(ConflictObjectResult))]
    [InlineData("no_such_plan", typeof(NotFoundObjectResult))]
    [InlineData("unknown_token", typeof(BadRequestObjectResult))]
    [InlineData("default_culture_missing", typeof(BadRequestObjectResult))]
    [InlineData("no_recipients", typeof(BadRequestObjectResult))]
    public async Task EachProblemWithASendHasItsOwnStatus(string code, Type expected)
    {
        var service = new Mock<IPlanAnnouncementService>();
        service.Setup(s => s.SendAsync(It.IsAny<Guid>(), It.IsAny<SendPlanAnnouncementDto>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((AnnouncementResultDto?)null, new AnnouncementProblem(code, "message")));

        var result = await Controller(service).Send(Guid.NewGuid(), Request(), CancellationToken.None);

        Assert.IsType(expected, result.Result);
    }

    [Fact]
    public async Task ASuccessfulSendIsTheAnswerAndCarriesWhoSentIt()
    {
        var service = new Mock<IPlanAnnouncementService>();
        var id = Guid.NewGuid();
        service.Setup(s => s.SendAsync(id, It.IsAny<SendPlanAnnouncementDto>(), _admin, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new AnnouncementResultDto { Queued = 3, BatchId = Guid.NewGuid() }, (AnnouncementProblem?)null));

        var result = await Controller(service).Send(id, Request(), CancellationToken.None);

        Assert.Equal(3, Assert.IsType<AnnouncementResultDto>(Assert.IsType<OkObjectResult>(result.Result).Value).Queued);
        service.Verify(s => s.SendAsync(id, It.IsAny<SendPlanAnnouncementDto>(), _admin, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task ARequestThatFailsValidationIsRefusedBeforeItGoesAnywhere()
    {
        var service = new Mock<IPlanAnnouncementService>();
        var controller = Controller(service);
        controller.ModelState.AddModelError("Content", "required");

        Assert.IsType<BadRequestObjectResult>((await controller.Send(Guid.NewGuid(), Request(), CancellationToken.None)).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.Test(Guid.NewGuid(), new SendAnnouncementTestDto(), CancellationToken.None)).Result);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ATestReportsHowManyEmailsItQueued()
    {
        var service = new Mock<IPlanAnnouncementService>();
        service.Setup(s => s.SendTestAsync(It.IsAny<Guid>(), It.IsAny<SendAnnouncementTestDto>(), It.IsAny<CancellationToken>())).ReturnsAsync((2, (AnnouncementProblem?)null));

        var result = await Controller(service).Test(Guid.NewGuid(), new SendAnnouncementTestDto { ToAddress = "a@example.com" }, CancellationToken.None);

        var body = Assert.IsType<AnnouncementResultDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(2, body.Queued);
        Assert.Null(body.BatchId);
    }

    [Fact]
    public async Task TheHistoryIsPassedThrough()
    {
        var service = new Mock<IPlanAnnouncementService>();
        var id = Guid.NewGuid();
        service.Setup(s => s.HistoryAsync(id, 20, It.IsAny<CancellationToken>())).ReturnsAsync([new AnnouncementBatchDto { Subject = "x" }]);

        var result = await Controller(service).History(id, CancellationToken.None);

        Assert.Equal("x", Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<AnnouncementBatchDto>>(Assert.IsType<OkObjectResult>(result.Result).Value)).Subject);
    }
}
