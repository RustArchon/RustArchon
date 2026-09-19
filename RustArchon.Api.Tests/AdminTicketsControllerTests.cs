// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Administration;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="AdminTicketsController"/> - the staff console's cross-tenant ticket queues.
/// Runs against a real (throwaway, Testcontainers-hosted) Postgres - see <see cref="PostgresFixture"/>.
/// </summary>
public class AdminTicketsControllerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private async Task<(AdminTicketsController Controller, ApiDbContext Context, Queue QueueA, Queue QueueB,
        Mock<IPublishEndpoint> PublishEndpoint)>
        CreateControllerAsync(Guid staffUserId)
    {
        var context = new ApiDbContext(postgres.Options, tenantContext: null);

        var queueA = new Queue
        {
            Id = Guid.NewGuid(), Name = "Support", Slug = $"support-{Guid.NewGuid()}", IsActive = true,
            CreatedById = Guid.Empty, CreatedOn = DateTimeOffset.UtcNow
        };
        var queueB = new Queue
        {
            Id = Guid.NewGuid(), Name = "Bug Reports", Slug = $"bugs-{Guid.NewGuid()}", IsActive = true,
            CreatedById = Guid.Empty, CreatedOn = DateTimeOffset.UtcNow
        };
        context.Set<Queue>().AddRange(queueA, queueB);
        await context.SaveChangesAsync();

        await TicketStatusSeeder.EnsureDefaultsAsync(context, NullLogger.Instance);

        var ticketRepository = new TicketRepository(context);
        var queueRepository = new QueueRepository(context);
        var ticketStatusRepository = new TicketStatusRepository(context);

        var communicationPublisher = new Mock<ICommunicationPublisher>();
        communicationPublisher
            .Setup(p => p.QueueTemplatedAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        var settingsCache = new Mock<IPlatformSettingsCache>();
        settingsCache.Setup(c => c.GetStringAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        var publishEndpoint = new Mock<IPublishEndpoint>();

        var controller = new AdminTicketsController(
            ticketRepository, queueRepository, ticketStatusRepository, communicationPublisher.Object,
            settingsCache.Object, publishEndpoint.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, staffUserId.ToString())
                    ]))
                }
            }
        };

        return (controller, context, queueA, queueB, publishEndpoint);
    }

    private static async Task<Ticket> SeedTicketAsync(
        ApiDbContext context, Guid queueId, string statusSlug = TicketStatusSeeder.Slugs.Submitted)
    {
        var status = await context.Set<TicketStatus>().FirstAsync(s => s.Slug == statusSlug);

        var ticket = new Ticket
        {
            // No Tenant row backs this - untenanted is fine here since these tests only exercise
            // queue/status filtering, which the staff console applies the same way to a tenant-owned
            // ticket and an anonymous prospect's; TicketsControllerTests covers the tenant-owned path.
            Id = Guid.NewGuid(), TenantId = null, SubmitterEmail = "customer@example.com",
            SubmitterName = "A Customer", QueueId = queueId, Subject = "Help",
            StatusId = status.Id, Status = status, SubmittedOn = DateTimeOffset.UtcNow,
            CreatedOn = DateTimeOffset.UtcNow, CreatedById = Guid.Empty
        };
        context.Set<Ticket>().Add(ticket);
        await context.SaveChangesAsync();
        return ticket;
    }

    [Fact]
    public async Task List_FiltersByQueue()
    {
        var (controller, context, queueA, queueB, _) = await CreateControllerAsync(Guid.NewGuid());
        await SeedTicketAsync(context, queueA.Id);
        await SeedTicketAsync(context, queueB.Id);

        var result = await controller.List(queueA.Id, statusId: null, isClosed: false, CancellationToken.None);

        var list = Assert.IsAssignableFrom<IReadOnlyList<TicketSummaryDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Single(list);
        Assert.Equal(queueA.Id, list[0].QueueId);
    }

    [Fact]
    public async Task List_FiltersByStatus()
    {
        var (controller, context, queueA, _, _) = await CreateControllerAsync(Guid.NewGuid());
        await SeedTicketAsync(context, queueA.Id, TicketStatusSeeder.Slugs.Submitted);
        await SeedTicketAsync(context, queueA.Id, TicketStatusSeeder.Slugs.Closed);
        var closedStatusId = await context.Set<TicketStatus>()
            .Where(s => s.Slug == TicketStatusSeeder.Slugs.Closed).Select(s => s.Id).FirstAsync();

        var result = await controller.List(queueA.Id, closedStatusId, isClosed: false, CancellationToken.None);

        var list = Assert.IsAssignableFrom<IReadOnlyList<TicketSummaryDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Single(list);
        Assert.Equal(TicketStatusSeeder.Slugs.Closed, list[0].Status.Slug);
    }

    /// <summary>The "Open / Closed" filter - keyed off each status's own <c>IsClosed</c> flag rather
    /// than an exact status, so the default working queue doesn't accumulate every ticket ever
    /// resolved, "Closed" shows exactly the closed ones, and "All" (isClosed: null) still sees
    /// everything.</summary>
    [Fact]
    public async Task List_WithNoStatusFilter_FiltersByTheIsClosedBucket()
    {
        var (controller, context, queueA, _, _) = await CreateControllerAsync(Guid.NewGuid());
        await SeedTicketAsync(context, queueA.Id, TicketStatusSeeder.Slugs.Open);
        await SeedTicketAsync(context, queueA.Id, TicketStatusSeeder.Slugs.Closed);

        var openView = await controller.List(queueA.Id, statusId: null, isClosed: false, CancellationToken.None);
        var openList = Assert.IsAssignableFrom<IReadOnlyList<TicketSummaryDto>>(
            Assert.IsType<OkObjectResult>(openView.Result).Value);
        Assert.Single(openList);
        Assert.Equal(TicketStatusSeeder.Slugs.Open, openList[0].Status.Slug);

        var closedView = await controller.List(queueA.Id, statusId: null, isClosed: true, CancellationToken.None);
        var closedList = Assert.IsAssignableFrom<IReadOnlyList<TicketSummaryDto>>(
            Assert.IsType<OkObjectResult>(closedView.Result).Value);
        Assert.Single(closedList);
        Assert.Equal(TicketStatusSeeder.Slugs.Closed, closedList[0].Status.Slug);

        var allView = await controller.List(queueA.Id, statusId: null, isClosed: null, CancellationToken.None);
        var allList = Assert.IsAssignableFrom<IReadOnlyList<TicketSummaryDto>>(
            Assert.IsType<OkObjectResult>(allView.Result).Value);
        Assert.Equal(2, allList.Count);
    }

    [Fact]
    public async Task Update_ToResolved_SetsResolvedOn_AndMovingAwayClearsIt()
    {
        var (controller, context, queueA, _, _) = await CreateControllerAsync(Guid.NewGuid());
        var ticket = await SeedTicketAsync(context, queueA.Id);
        var resolvedStatusId = await context.Set<TicketStatus>()
            .Where(s => s.Slug == TicketStatusSeeder.Slugs.Resolved).Select(s => s.Id).FirstAsync();
        var openStatusId = await context.Set<TicketStatus>()
            .Where(s => s.Slug == TicketStatusSeeder.Slugs.Open).Select(s => s.Id).FirstAsync();

        await controller.Update(
            ticket.Id,
            new UpdateTicketRequestDto { StatusId = resolvedStatusId, QueueId = queueA.Id },
            CancellationToken.None);

        var resolved = await context.Set<Ticket>().AsNoTracking().FirstAsync(t => t.Id == ticket.Id);
        Assert.Equal(resolvedStatusId, resolved.StatusId);
        Assert.NotNull(resolved.ResolvedOn);

        await controller.Update(
            ticket.Id,
            new UpdateTicketRequestDto { StatusId = openStatusId, QueueId = queueA.Id },
            CancellationToken.None);

        var reopened = await context.Set<Ticket>().AsNoTracking().FirstAsync(t => t.Id == ticket.Id);
        Assert.Equal(openStatusId, reopened.StatusId);
        Assert.Null(reopened.ResolvedOn);
    }

    /// <summary>The admin surface for the abuse guard - a staff member sets this on one specific
    /// ticket, not on the status itself. See <c>TicketReplyPolicy</c> for where it's enforced.</summary>
    [Fact]
    public async Task Update_SetsAndClearsPreventReopeningOnTheTicket()
    {
        var (controller, context, queueA, _, _) = await CreateControllerAsync(Guid.NewGuid());
        var ticket = await SeedTicketAsync(context, queueA.Id);

        await controller.Update(
            ticket.Id,
            new UpdateTicketRequestDto { StatusId = ticket.StatusId, QueueId = queueA.Id, PreventReopening = true },
            CancellationToken.None);

        var locked = await context.Set<Ticket>().AsNoTracking().FirstAsync(t => t.Id == ticket.Id);
        Assert.True(locked.PreventReopening);

        await controller.Update(
            ticket.Id,
            new UpdateTicketRequestDto { StatusId = ticket.StatusId, QueueId = queueA.Id, PreventReopening = false },
            CancellationToken.None);

        var unlocked = await context.Set<Ticket>().AsNoTracking().FirstAsync(t => t.Id == ticket.Id);
        Assert.False(unlocked.PreventReopening);
    }

    [Fact]
    public async Task AddMessage_SetsWaitingOnCustomer()
    {
        var staffUserId = Guid.NewGuid();
        var (controller, context, queueA, _, publishEndpoint) = await CreateControllerAsync(staffUserId);
        var ticket = await SeedTicketAsync(context, queueA.Id);

        var result = await controller.AddMessage(
            ticket.Id, new SaveTicketMessageRequestDto { Body = "We're looking into it." },
            CancellationToken.None);

        var messageDto = Assert.IsType<TicketMessageDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

        var reloaded = await context.Set<Ticket>().AsNoTracking()
            .Include(t => t.Status).FirstAsync(t => t.Id == ticket.Id);
        Assert.Equal(TicketStatusSeeder.Slugs.WaitingOnCustomer, reloaded.Status.Slug);

        publishEndpoint.Verify(
            p => p.Publish(
                It.Is<TicketMessageAdded>(e => e.TicketId == ticket.Id && e.MessageId == messageDto.Id),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AddNote_NeverAppearsInTheTenantFacingThread()
    {
        var staffUserId = Guid.NewGuid();
        var (controller, context, queueA, _, _) = await CreateControllerAsync(staffUserId);
        var ticket = await SeedTicketAsync(context, queueA.Id);

        await controller.AddNote(
            ticket.Id, new SaveTicketNoteRequestDto { Content = "Escalated to on-call." },
            CancellationToken.None);

        var detailResult = await controller.Get(ticket.Id, CancellationToken.None);
        var detail = Assert.IsType<TicketDetailDto>(Assert.IsType<OkObjectResult>(detailResult.Result).Value);

        Assert.Single(detail.Notes!);
        Assert.Empty(detail.Messages);
    }
}
