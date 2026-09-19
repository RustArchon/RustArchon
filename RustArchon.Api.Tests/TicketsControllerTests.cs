// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
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
using DtoTicketMessageAuthorType = RustArchon.Shared.DTOs.TicketMessageAuthorType;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="TicketsController"/> - the tenant-facing "my tickets" surface. Runs against a
/// real (throwaway, Testcontainers-hosted) Postgres - see <see cref="PostgresFixture"/>.
/// </summary>
public class TicketsControllerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Task<Guid?> GetCurrentTenantIdAsync() => Task.FromResult<Guid?>(tenantId);
    }

    /// <summary>
    /// The value <see cref="ClaimTypes.Name"/> carries in the fake principal below - this app's
    /// Identity is username-as-email, so that's the submitter's email address, never
    /// <see cref="ClaimTypes.Email"/> (deliberately absent here - see the comment on it below).
    /// </summary>
    private const string SubmitterEmail = "submitter@example.com";

    private async Task<(TicketsController Controller, ApiDbContext Context, Queue Queue,
        Mock<ICommunicationPublisher> CommunicationPublisher, Mock<IPublishEndpoint> PublishEndpoint)>
        CreateControllerAsync(Guid tenantId, Guid userId)
    {
        var tenantContext = new FixedTenantContext(tenantId);
        var context = new ApiDbContext(postgres.Options, tenantContext);

        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Test tenant {tenantId}", IsActive = true });

        var queue = new Queue
        {
            Id = Guid.NewGuid(), Name = "Support", Slug = $"support-{Guid.NewGuid()}", IsActive = true,
            CreatedById = Guid.Empty, CreatedOn = DateTimeOffset.UtcNow
        };
        context.Set<Queue>().Add(queue);
        await context.SaveChangesAsync();

        // Idempotent - see TicketStatusSeeder's own remarks. Seeding through the real seeder rather
        // than hand-rolling status rows here means these tests exercise the same slugs
        // TicketsController itself looks up by.
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

        var controller = new TicketsController(
            ticketRepository, queueRepository, ticketStatusRepository, tenantContext,
            communicationPublisher.Object, settingsCache.Object, publishEndpoint.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    // No ClaimTypes.Email claim, deliberately - mirrors the real JWT the Panel mints
                    // for its own Api calls, which never carries one. A prior version of this fixture
                    // included a ClaimTypes.Email claim that production never sends, which is exactly
                    // why TicketsController.Create reading ClaimTypes.Email (rather than ClaimTypes.Name,
                    // which does carry the email here) shipped without a test catching the resulting
                    // empty SubmitterEmail - caught live instead, submitting a real ticket through the
                    // Panel. See TicketsController.Create's own remarks.
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                        new Claim(ClaimTypes.Name, SubmitterEmail)
                    ]))
                }
            }
        };

        return (controller, context, queue, communicationPublisher, publishEndpoint);
    }

    private static CreateTicketRequestDto NewTicketDto(Guid queueId, string subject = "Can't connect") => new()
    {
        Subject = subject,
        QueueId = queueId,
        Body = "My server won't start."
    };

    [Fact]
    public async Task Create_OpensATicketAndItsFirstMessage_ForTheCurrentTenant()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var (controller, _, queue, communicationPublisher, publishEndpoint) =
            await CreateControllerAsync(tenantId, userId);

        var result = await controller.Create(NewTicketDto(queue.Id), CancellationToken.None);

        var detail = Assert.IsType<TicketDetailDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(tenantId, detail.TenantId);
        Assert.Equal(SubmitterEmail, detail.SubmitterEmail);
        Assert.Equal("Support", detail.QueueName);
        Assert.Equal(TicketStatusSeeder.Slugs.Submitted, detail.Status.Slug);
        Assert.Single(detail.Messages);
        Assert.Equal(DtoTicketMessageAuthorType.Customer, detail.Messages[0].AuthorType);
        Assert.Null(detail.Notes);

        // The exact property that broke live: QueueTemplatedAsync's toAddress must actually be the
        // submitter's email, not silently empty - the mock above would happily accept either, so this
        // has to check what it was actually called with, not just that it was called.
        communicationPublisher.Verify(p => p.QueueTemplatedAsync(
            EmailTemplateRegistry.Codes.TicketReceived, It.IsAny<IReadOnlyDictionary<string, string>>(),
            SubmitterEmail, userId, tenantId, null, It.IsAny<CancellationToken>()),
            Times.Once);

        publishEndpoint.Verify(
            p => p.Publish(It.Is<TicketCreated>(e => e.TicketId == detail.Id), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Create_WithAnInactiveQueue_ReturnsBadRequest()
    {
        var tenantId = Guid.NewGuid();
        var (controller, context, queue, _, _) = await CreateControllerAsync(tenantId, Guid.NewGuid());
        queue.IsActive = false;
        await context.SaveChangesAsync();

        var result = await controller.Create(NewTicketDto(queue.Id), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    /// <summary>
    /// The property <see cref="ITicketRepository"/>'s own remarks call out explicitly: a ticket with no
    /// tenant (an anonymous prospect's submission) must never appear in a tenant's own "my tickets"
    /// list just because <c>Ticket</c>'s <c>ITenantScopedOptional</c> query filter would otherwise treat
    /// an untenanted row as globally visible.
    /// </summary>
    [Fact]
    public async Task List_NeverIncludesAnUntenantedTicket()
    {
        var tenantId = Guid.NewGuid();
        var (controller, context, queue, _, _) = await CreateControllerAsync(tenantId, Guid.NewGuid());
        var submittedStatusId = await context.Set<TicketStatus>()
            .Where(s => s.Slug == TicketStatusSeeder.Slugs.Submitted).Select(s => s.Id).FirstAsync();

        await context.Set<Ticket>().AddAsync(new Ticket
        {
            Id = Guid.NewGuid(), TenantId = null, SubmitterEmail = "prospect@example.com",
            SubmitterName = "Prospect", QueueId = queue.Id, Subject = "Pre-sales question",
            StatusId = submittedStatusId, SubmittedOn = DateTimeOffset.UtcNow,
            CreatedOn = DateTimeOffset.UtcNow, CreatedById = Guid.Empty
        });
        await context.SaveChangesAsync();

        var result = await controller.List(CancellationToken.None);

        var list = Assert.IsAssignableFrom<IReadOnlyList<TicketSummaryDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Empty(list);
    }

    /// <summary>
    /// <c>List</c>/<c>Get</c> go through <see cref="ITicketRepository"/>'s own read methods, not the
    /// in-memory <c>Queue</c> the Create test above manually attaches - a separate code path that needs
    /// its own coverage for the same "QueueName came back empty" bug (missing <c>.Include(Queue)</c>).
    /// </summary>
    [Fact]
    public async Task List_IncludesTheQueuesName()
    {
        var tenantId = Guid.NewGuid();
        var (controller, _, queue, _, _) = await CreateControllerAsync(tenantId, Guid.NewGuid());
        await controller.Create(NewTicketDto(queue.Id), CancellationToken.None);

        var result = await controller.List(CancellationToken.None);

        var list = Assert.IsAssignableFrom<IReadOnlyList<TicketSummaryDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("Support", Assert.Single(list).QueueName);
    }

    [Fact]
    public async Task Get_ForAnotherTenantsTicket_ReturnsNotFound()
    {
        var ownerTenantId = Guid.NewGuid();
        var (ownerController, context, queue, _, _) = await CreateControllerAsync(ownerTenantId, Guid.NewGuid());
        var created = await ownerController.Create(NewTicketDto(queue.Id), CancellationToken.None);
        var ticketId = ((TicketDetailDto)((OkObjectResult)created.Result!).Value!).Id;

        var (strangerController, _, _, _, _) = await CreateControllerAsync(Guid.NewGuid(), Guid.NewGuid());

        var result = await strangerController.Get(ticketId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task AddMessage_OnAResolvedTicket_ReopensIt()
    {
        var tenantId = Guid.NewGuid();
        var (controller, context, queue, _, publishEndpoint) = await CreateControllerAsync(tenantId, Guid.NewGuid());
        var created = await controller.Create(NewTicketDto(queue.Id), CancellationToken.None);
        var ticketId = ((TicketDetailDto)((OkObjectResult)created.Result!).Value!).Id;

        var resolvedStatus = await context.Set<TicketStatus>()
            .FirstAsync(s => s.Slug == TicketStatusSeeder.Slugs.Resolved);
        var openStatus = await context.Set<TicketStatus>()
            .FirstAsync(s => s.Slug == TicketStatusSeeder.Slugs.Open);

        var ticket = await context.Set<Ticket>().FirstAsync(t => t.Id == ticketId);
        ticket.StatusId = resolvedStatus.Id;
        ticket.Status = resolvedStatus;
        ticket.ResolvedOn = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync();

        var messageResult = await controller.AddMessage(
            ticketId, new SaveTicketMessageRequestDto { Body = "Still broken, please help." },
            CancellationToken.None);

        var reloaded = await context.Set<Ticket>().AsNoTracking().FirstAsync(t => t.Id == ticketId);
        Assert.Equal(openStatus.Id, reloaded.StatusId);
        Assert.Null(reloaded.ResolvedOn);

        var messageId = ((TicketMessageDto)((OkObjectResult)messageResult.Result!).Value!).Id;
        publishEndpoint.Verify(
            p => p.Publish(
                It.Is<TicketMessageAdded>(e => e.TicketId == ticketId && e.MessageId == messageId),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>Distinct from <see cref="AddMessage_OnAResolvedTicket_ReopensIt"/> - a genuinely closed
    /// ticket reopens to <c>Reopened</c>, not <c>Open</c>, so it stays identifiable as "was closed, came
    /// back" - see <c>TicketReplyPolicy</c>'s remarks.</summary>
    [Fact]
    public async Task AddMessage_OnAClosedTicket_ReopensToTheReopenedStatus()
    {
        var tenantId = Guid.NewGuid();
        var (controller, context, queue, _, _) = await CreateControllerAsync(tenantId, Guid.NewGuid());
        var created = await controller.Create(NewTicketDto(queue.Id), CancellationToken.None);
        var ticketId = ((TicketDetailDto)((OkObjectResult)created.Result!).Value!).Id;

        var closedStatus = await context.Set<TicketStatus>()
            .FirstAsync(s => s.Slug == TicketStatusSeeder.Slugs.Closed);
        var reopenedStatus = await context.Set<TicketStatus>()
            .FirstAsync(s => s.Slug == TicketStatusSeeder.Slugs.Reopened);

        var ticket = await context.Set<Ticket>().FirstAsync(t => t.Id == ticketId);
        ticket.StatusId = closedStatus.Id;
        ticket.Status = closedStatus;
        ticket.ClosedOn = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync();

        var messageResult = await controller.AddMessage(
            ticketId, new SaveTicketMessageRequestDto { Body = "Actually, it's still broken." },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(messageResult.Result);
        var reloaded = await context.Set<Ticket>().AsNoTracking().FirstAsync(t => t.Id == ticketId);
        Assert.Equal(reopenedStatus.Id, reloaded.StatusId);
        Assert.Null(reloaded.ClosedOn);
    }

    /// <summary>The abuse guard - a ticket with <see cref="Ticket.PreventReopening"/> refuses the reply
    /// outright rather than reopening it, and the message is never saved. This is a per-ticket flag (not
    /// on <see cref="Data.TicketStatus"/> itself), so this uses the shared, seeded <c>Closed</c> status
    /// freely without the leak-into-another-test risk that mutating a shared status row would have.</summary>
    [Fact]
    public async Task AddMessage_OnATicketThatPreventsReopening_ReturnsBadRequestAndSavesNoMessage()
    {
        var tenantId = Guid.NewGuid();
        var (controller, context, queue, _, publishEndpoint) = await CreateControllerAsync(tenantId, Guid.NewGuid());
        var created = await controller.Create(NewTicketDto(queue.Id), CancellationToken.None);
        var ticketId = ((TicketDetailDto)((OkObjectResult)created.Result!).Value!).Id;

        var closedStatus = await context.Set<TicketStatus>()
            .FirstAsync(s => s.Slug == TicketStatusSeeder.Slugs.Closed);

        var ticket = await context.Set<Ticket>().FirstAsync(t => t.Id == ticketId);
        ticket.StatusId = closedStatus.Id;
        ticket.Status = closedStatus;
        ticket.PreventReopening = true;
        await context.SaveChangesAsync();

        var messageResult = await controller.AddMessage(
            ticketId, new SaveTicketMessageRequestDto { Body = "Let me back in!" }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(messageResult.Result);

        var reloaded = await context.Set<Ticket>().AsNoTracking().FirstAsync(t => t.Id == ticketId);
        Assert.Equal(closedStatus.Id, reloaded.StatusId);
        // Only the ticket's own opening message from Create above - the blocked reply was never saved.
        Assert.Single(await context.Set<TicketMessage>().Where(m => m.TicketId == ticketId).ToListAsync());
        publishEndpoint.Verify(p => p.Publish(It.IsAny<TicketMessageAdded>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
