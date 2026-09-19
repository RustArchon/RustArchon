// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;
using DtoTicketMessageAuthorType = RustArchon.Shared.DTOs.TicketMessageAuthorType;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="GuestTicketController"/> - an anonymous submitter's own access to a single
/// ticket by its guest-access token. Runs against a real (throwaway, Testcontainers-hosted) Postgres -
/// see <see cref="PostgresFixture"/>.
/// </summary>
public class GuestTicketControllerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private async Task<(GuestTicketController Controller, ApiDbContext Context, Queue Queue,
        Mock<IPublishEndpoint> PublishEndpoint)> CreateControllerAsync()
    {
        var context = new ApiDbContext(postgres.Options, tenantContext: null);

        var queue = new Queue
        {
            Id = Guid.NewGuid(), Name = "Support", Slug = $"support-{Guid.NewGuid()}", IsActive = true,
            CreatedById = Guid.Empty, CreatedOn = DateTimeOffset.UtcNow
        };
        context.Set<Queue>().Add(queue);
        await context.SaveChangesAsync();

        await TicketStatusSeeder.EnsureDefaultsAsync(context, NullLogger.Instance);

        var ticketRepository = new TicketRepository(context);
        var ticketStatusRepository = new TicketStatusRepository(context);
        var publishEndpoint = new Mock<IPublishEndpoint>();

        var controller = new GuestTicketController(ticketRepository, ticketStatusRepository, publishEndpoint.Object);

        return (controller, context, queue, publishEndpoint);
    }

    private static async Task<Ticket> SeedGuestTicketAsync(
        ApiDbContext context, Guid queueId, string token, DateTimeOffset? expiresOn = null)
    {
        var submittedStatusId = await context.Set<TicketStatus>()
            .Where(s => s.Slug == TicketStatusSeeder.Slugs.Submitted).Select(s => s.Id).FirstAsync();

        var ticket = new Ticket
        {
            Id = Guid.NewGuid(), TenantId = null, SubmitterEmail = "prospect@example.com",
            SubmitterName = "Prospect", QueueId = queueId, Subject = "Pre-sales question",
            StatusId = submittedStatusId, SubmittedOn = DateTimeOffset.UtcNow,
            GuestAccessToken = token, GuestAccessTokenExpiresOn = expiresOn ?? DateTimeOffset.UtcNow.AddDays(90),
            CreatedOn = DateTimeOffset.UtcNow, CreatedById = Guid.Empty
        };
        context.Set<Ticket>().Add(ticket);
        await context.SaveChangesAsync();
        return ticket;
    }

    [Fact]
    public async Task Get_WithAValidToken_ReturnsTheTicketWithNoNotes()
    {
        var (controller, context, queue, _) = await CreateControllerAsync();
        var token = Guid.NewGuid().ToString("N");
        var ticket = await SeedGuestTicketAsync(context, queue.Id, token);

        var result = await controller.Get(token, CancellationToken.None);

        var detail = Assert.IsType<TicketDetailDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(ticket.Id, detail.Id);
        Assert.Null(detail.Notes);
    }

    [Fact]
    public async Task Get_WithAnUnknownToken_ReturnsNotFound()
    {
        var (controller, _, _, _) = await CreateControllerAsync();

        var result = await controller.Get("not-a-real-token", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Get_WithAnExpiredToken_ReturnsNotFound()
    {
        var (controller, context, queue, _) = await CreateControllerAsync();
        var token = Guid.NewGuid().ToString("N");
        await SeedGuestTicketAsync(context, queue.Id, token, expiresOn: DateTimeOffset.UtcNow.AddDays(-1));

        var result = await controller.Get(token, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task AddMessage_AppendsToTheThreadAndPublishesTicketMessageAdded()
    {
        var (controller, context, queue, publishEndpoint) = await CreateControllerAsync();
        var token = Guid.NewGuid().ToString("N");
        var ticket = await SeedGuestTicketAsync(context, queue.Id, token);

        var result = await controller.AddMessage(
            token, new SaveTicketMessageRequestDto { Body = "Any update?" }, CancellationToken.None);

        var messageDto = Assert.IsType<TicketMessageDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(DtoTicketMessageAuthorType.Customer, messageDto.AuthorType);
        Assert.Null(messageDto.AuthorUserId);

        var messages = await context.Set<TicketMessage>().Where(m => m.TicketId == ticket.Id).ToListAsync();
        Assert.Single(messages);

        publishEndpoint.Verify(
            p => p.Publish(
                It.Is<TicketMessageAdded>(e => e.TicketId == ticket.Id && e.MessageId == messageDto.Id),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AddMessage_WithAnUnknownToken_ReturnsNotFound()
    {
        var (controller, _, _, _) = await CreateControllerAsync();

        var result = await controller.AddMessage(
            "not-a-real-token", new SaveTicketMessageRequestDto { Body = "Hello?" }, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    /// <summary>An anonymous guest is still a "customer" for <c>TicketReplyPolicy</c>'s purposes - see
    /// <see cref="TicketsControllerTests.AddMessage_OnAClosedTicket_ReopensToTheReopenedStatus"/> for the
    /// signed-in-tenant equivalent.</summary>
    [Fact]
    public async Task AddMessage_OnAClosedTicket_ReopensToTheReopenedStatus()
    {
        var (controller, context, queue, _) = await CreateControllerAsync();
        var token = Guid.NewGuid().ToString("N");
        var ticket = await SeedGuestTicketAsync(context, queue.Id, token);

        var closedStatus = await context.Set<TicketStatus>()
            .FirstAsync(s => s.Slug == TicketStatusSeeder.Slugs.Closed);
        var reopenedStatus = await context.Set<TicketStatus>()
            .FirstAsync(s => s.Slug == TicketStatusSeeder.Slugs.Reopened);
        ticket.StatusId = closedStatus.Id;
        ticket.Status = closedStatus;
        await context.SaveChangesAsync();

        var result = await controller.AddMessage(
            token, new SaveTicketMessageRequestDto { Body = "Still an issue." }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var reloaded = await context.Set<Ticket>().AsNoTracking().FirstAsync(t => t.Id == ticket.Id);
        Assert.Equal(reopenedStatus.Id, reloaded.StatusId);
    }

    /// <summary>Same abuse guard as the signed-in-tenant path - see
    /// <see cref="TicketsControllerTests.AddMessage_OnATicketThatPreventsReopening_ReturnsBadRequestAndSavesNoMessage"/>.
    /// <see cref="Ticket.PreventReopening"/> is per-ticket, so this uses the shared, seeded <c>Closed</c>
    /// status freely.</summary>
    [Fact]
    public async Task AddMessage_OnATicketThatPreventsReopening_ReturnsBadRequestAndSavesNoMessage()
    {
        var (controller, context, queue, publishEndpoint) = await CreateControllerAsync();
        var token = Guid.NewGuid().ToString("N");
        var ticket = await SeedGuestTicketAsync(context, queue.Id, token);

        var closedStatus = await context.Set<TicketStatus>()
            .FirstAsync(s => s.Slug == TicketStatusSeeder.Slugs.Closed);
        ticket.StatusId = closedStatus.Id;
        ticket.Status = closedStatus;
        ticket.PreventReopening = true;
        await context.SaveChangesAsync();

        var result = await controller.AddMessage(
            token, new SaveTicketMessageRequestDto { Body = "Let me back in!" }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await context.Set<TicketMessage>().Where(m => m.TicketId == ticket.Id).ToListAsync());
        publishEndpoint.Verify(p => p.Publish(It.IsAny<TicketMessageAdded>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
