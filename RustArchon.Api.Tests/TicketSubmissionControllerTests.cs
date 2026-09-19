// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using RustArchon.Api.Administration;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.Captcha;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="TicketSubmissionController"/> - the anonymous contact-form submission path.
/// Runs against a real (throwaway, Testcontainers-hosted) Postgres - see <see cref="PostgresFixture"/>.
/// </summary>
public class TicketSubmissionControllerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private async Task<(TicketSubmissionController Controller, ApiDbContext Context, Queue Queue,
        Mock<ICaptchaVerifierFactory> CaptchaFactory, Mock<IPublishEndpoint> PublishEndpoint)>
        CreateControllerAsync(string captchaProvider = "None")
    {
        var context = new ApiDbContext(postgres.Options, tenantContext: null);

        var queue = new Queue
        {
            Id = Guid.NewGuid(), Name = "Pre-Sales", Slug = $"pre-sales-{Guid.NewGuid()}", IsActive = true,
            CreatedById = Guid.Empty, CreatedOn = DateTimeOffset.UtcNow
        };
        context.Set<Queue>().Add(queue);
        await context.SaveChangesAsync();

        await TicketStatusSeeder.EnsureDefaultsAsync(context, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var ticketRepository = new TicketRepository(context);
        var queueRepository = new QueueRepository(context);
        var ticketStatusRepository = new TicketStatusRepository(context);

        var captchaVerifier = new Mock<ICaptchaVerifier>();
        captchaVerifier
            .Setup(v => v.VerifyAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var captchaFactory = new Mock<ICaptchaVerifierFactory>();
        captchaFactory.Setup(f => f.ResolveAsync()).ReturnsAsync(captchaVerifier.Object);

        var communicationPublisher = new Mock<ICommunicationPublisher>();
        communicationPublisher
            .Setup(p => p.QueueTemplatedAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        var settingsCache = new Mock<IPlatformSettingsCache>();
        settingsCache
            .Setup(c => c.GetStringAsync(PlatformSettingsRegistry.CaptchaProvider))
            .ReturnsAsync(captchaProvider);
        settingsCache
            .Setup(c => c.GetStringAsync(PlatformSettingsRegistry.PanelBaseUrl))
            .ReturnsAsync((string?)null);

        var publishEndpoint = new Mock<IPublishEndpoint>();

        var controller = new TicketSubmissionController(
            ticketRepository, queueRepository, ticketStatusRepository, captchaFactory.Object,
            communicationPublisher.Object, settingsCache.Object, publishEndpoint.Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TicketSubmissionController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        return (controller, context, queue, captchaFactory, publishEndpoint);
    }

    private static SubmitPublicTicketRequestDto NewRequest(Guid queueId) => new()
    {
        Name = "Jamie Prospect",
        Email = "jamie@example.com",
        Subject = "Do you support 200 players?",
        QueueId = queueId,
        Body = "Just checking before I sign up."
    };

    [Fact]
    public async Task Submit_CreatesAnUntenantedTicketWithAGuestAccessToken()
    {
        var (controller, context, queue, _, publishEndpoint) = await CreateControllerAsync();

        var result = await controller.Submit(NewRequest(queue.Id), CancellationToken.None);

        var response = Assert.IsType<SubmitPublicTicketResponseDto>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        var ticket = await context.Set<Ticket>().AsNoTracking().FirstAsync(t => t.Id == response.TicketId);
        Assert.Null(ticket.TenantId);
        Assert.Null(ticket.SubmitterUserId);
        Assert.Equal("jamie@example.com", ticket.SubmitterEmail);
        Assert.False(string.IsNullOrEmpty(ticket.GuestAccessToken));
        Assert.Equal(ticket.GuestAccessToken, response.GuestAccessToken);
        Assert.True(ticket.GuestAccessTokenExpiresOn > DateTimeOffset.UtcNow);

        publishEndpoint.Verify(
            p => p.Publish(It.Is<TicketCreated>(e => e.TicketId == ticket.Id), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Submit_WithTheHoneypotFilledIn_SilentlyDropsTheSubmission()
    {
        var (controller, context, queue, _, publishEndpoint) = await CreateControllerAsync();
        var request = NewRequest(queue.Id);
        request.Website = "https://spam-bot.example";

        var result = await controller.Submit(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.False(await context.Set<Ticket>().AnyAsync(t => t.QueueId == queue.Id));
        publishEndpoint.Verify(
            p => p.Publish(It.IsAny<TicketCreated>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Submit_WithAnInactiveQueue_ReturnsBadRequest()
    {
        var (controller, context, queue, _, _) = await CreateControllerAsync();
        queue.IsActive = false;
        await context.SaveChangesAsync();

        var result = await controller.Submit(NewRequest(queue.Id), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Submit_WhenACaptchaIsConfiguredButNoTokenIsGiven_ReturnsBadRequest()
    {
        var (controller, context, queue, _, _) = await CreateControllerAsync(captchaProvider: "Turnstile");

        var request = NewRequest(queue.Id);
        request.CaptchaToken = null;

        var result = await controller.Submit(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.False(await context.Set<Ticket>().AnyAsync(t => t.QueueId == queue.Id));
    }

    [Fact]
    public async Task Submit_WhenTheCaptchaFailsVerification_ReturnsBadRequestAndCreatesNoTicket()
    {
        var (controller, context, queue, captchaFactory, _) = await CreateControllerAsync(captchaProvider: "Turnstile");

        var failingVerifier = new Mock<ICaptchaVerifier>();
        failingVerifier
            .Setup(v => v.VerifyAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        captchaFactory.Setup(f => f.ResolveAsync()).ReturnsAsync(failingVerifier.Object);

        var request = NewRequest(queue.Id);
        request.CaptchaToken = "a-token";

        var result = await controller.Submit(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.False(await context.Set<Ticket>().AnyAsync(t => t.QueueId == queue.Id));
    }
}
