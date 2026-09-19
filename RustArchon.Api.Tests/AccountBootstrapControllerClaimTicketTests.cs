// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Authorization.Repositories;
using JumpStart.Data;
using JumpStart.MultiTenant.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Administration;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="AccountBootstrapController.ClaimTicket"/> - the sign-up relink flow. Runs
/// against a real (throwaway, Testcontainers-hosted) Postgres for the ticket side - see
/// <see cref="PostgresFixture"/>. Caught live the first time this shipped: the call site
/// (<c>Register.razor</c>) runs before any Blazor circuit exists, so this endpoint has to work off a
/// bare identity-assertion token, resolving the caller's tenant via
/// <see cref="IUserTenantRepository.GetTenantsForUserAsync"/> rather than a tenant_id claim - these
/// tests mock that repository the same way, not <see cref="ITenantContext"/>.
/// </summary>
public class AccountBootstrapControllerClaimTicketTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string SubmitterEmail = "submitter@example.com";

    private async Task<(AccountBootstrapController Controller, ApiDbContext Context, Queue Queue)>
        CreateControllerAsync(Guid userId, Guid? tenantId)
    {
        var context = new ApiDbContext(postgres.Options, tenantContext: null);

        if (tenantId is { } realTenantId)
        {
            context.Set<Tenant>().Add(new Tenant { Id = realTenantId, Name = "Test tenant", IsActive = true });
        }

        var queue = new Queue
        {
            Id = Guid.NewGuid(), Name = "Pre-Sales", Slug = $"pre-sales-{Guid.NewGuid()}", IsActive = true,
            CreatedById = Guid.Empty, CreatedOn = DateTimeOffset.UtcNow
        };
        context.Set<Queue>().Add(queue);
        await context.SaveChangesAsync();

        await TicketStatusSeeder.EnsureDefaultsAsync(context, NullLogger<AccountBootstrapController>.Instance);

        var userTenantRepository = new Mock<IUserTenantRepository>();
        userTenantRepository
            .Setup(r => r.GetTenantsForUserAsync(userId))
            .ReturnsAsync(tenantId is { } id
                ? [new Tenant { Id = id, Name = "Test tenant", IsActive = true }]
                : []);

        var controller = new AccountBootstrapController(
            Mock.Of<IOrganizationProvisioningService>(),
            userTenantRepository.Object,
            Mock.Of<IRoleRepository>(),
            new TicketRepository(context),
            context,
            new ConfigurationBuilder().Build(),
            NullLogger<AccountBootstrapController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    // No ClaimTypes.Email claim, deliberately - same reasoning as
                    // TicketsControllerTests: this app's Identity is username-as-email, and the
                    // short-lived assertion token this endpoint is actually called with never carries
                    // ClaimTypes.Email either.
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                        new Claim(ClaimTypes.Name, SubmitterEmail)
                    ]))
                }
            }
        };

        return (controller, context, queue);
    }

    private static async Task<Ticket> SeedGuestTicketAsync(ApiDbContext context, Guid queueId, string submitterEmail)
    {
        var submittedStatusId = await context.Set<TicketStatus>()
            .Where(s => s.Slug == TicketStatusSeeder.Slugs.Submitted).Select(s => s.Id).FirstAsync();

        var ticket = new Ticket
        {
            Id = Guid.NewGuid(), TenantId = null, SubmitterUserId = null, SubmitterEmail = submitterEmail,
            SubmitterName = submitterEmail, QueueId = queueId, Subject = "Pre-sales question",
            StatusId = submittedStatusId, SubmittedOn = DateTimeOffset.UtcNow,
            GuestAccessToken = Guid.NewGuid().ToString("N"),
            GuestAccessTokenExpiresOn = DateTimeOffset.UtcNow.AddDays(90),
            CreatedOn = DateTimeOffset.UtcNow, CreatedById = Guid.Empty
        };
        context.Set<Ticket>().Add(ticket);
        await context.SaveChangesAsync();
        return ticket;
    }

    /// <summary>The exact scenario Stage 4 exists for: a prospect files a guest ticket, then signs up
    /// using the same email their ticket names, and expects it to show up under their new account.</summary>
    [Fact]
    public async Task ClaimTicket_WithAMatchingVerifiedEmail_RelinksTheTicketToTheCallersAccount()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var (controller, context, queue) = await CreateControllerAsync(userId, tenantId);
        var ticket = await SeedGuestTicketAsync(context, queue.Id, SubmitterEmail);

        var result = await controller.ClaimTicket(
            new ClaimGuestTicketRequestDto { Token = ticket.GuestAccessToken! }, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);

        var reloaded = await context.Set<Ticket>().AsNoTracking().FirstAsync(t => t.Id == ticket.Id);
        Assert.Equal(tenantId, reloaded.TenantId);
        Assert.Equal(userId, reloaded.SubmitterUserId);
    }

    /// <summary>
    /// The spoofing gap flagged during design: token possession alone (a client could submit any
    /// 256-bit string) must never be enough - the caller's own verified email has to match too, or
    /// anyone signed in could claim any ticket whose token they somehow obtained.
    /// </summary>
    [Fact]
    public async Task ClaimTicket_WhenTheCallersEmailDoesNotMatchTheTicket_ReturnsForbidAndLeavesItUnclaimed()
    {
        var (controller, context, queue) = await CreateControllerAsync(Guid.NewGuid(), Guid.NewGuid());
        var ticket = await SeedGuestTicketAsync(context, queue.Id, "someone-else@example.com");

        var result = await controller.ClaimTicket(
            new ClaimGuestTicketRequestDto { Token = ticket.GuestAccessToken! }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);

        var reloaded = await context.Set<Ticket>().AsNoTracking().FirstAsync(t => t.Id == ticket.Id);
        Assert.Null(reloaded.TenantId);
        Assert.Null(reloaded.SubmitterUserId);
    }

    [Fact]
    public async Task ClaimTicket_WithAnUnknownToken_ReturnsNotFound()
    {
        var (controller, _, _) = await CreateControllerAsync(Guid.NewGuid(), Guid.NewGuid());

        var result = await controller.ClaimTicket(
            new ClaimGuestTicketRequestDto { Token = "not-a-real-token" }, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    /// <summary>
    /// Should never happen in practice - <c>EnsureTenant</c> runs first and guarantees exactly one -
    /// but refusing outright beats silently leaving <c>TenantId</c> null if it somehow did.
    /// </summary>
    [Fact]
    public async Task ClaimTicket_WhenTheCallerHasNoTenantYet_ReturnsNotFound()
    {
        var (controller, context, queue) = await CreateControllerAsync(Guid.NewGuid(), tenantId: null);
        var ticket = await SeedGuestTicketAsync(context, queue.Id, SubmitterEmail);

        var result = await controller.ClaimTicket(
            new ClaimGuestTicketRequestDto { Token = ticket.GuestAccessToken! }, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }
}
