// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Administration;
using RustArchon.Api.Billing;
using RustArchon.Api.Data;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="Data.Plan.OnePerOwner"/> - the rule that stops a free tier from being an
/// unlimited supply of free servers.
/// </summary>
/// <remarks>
/// Two routes lead to an Organization sitting on a plan, and the rule is worthless if it only covers
/// one of them: creating it there, and moving it there afterwards. Both are tested, because "create
/// on a paid tier, then downgrade" is the obvious way round a creation-only check.
/// </remarks>
public class OnePerOwnerTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly Guid _founderId = Guid.NewGuid();

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    private static Plan NewPlan(string name, bool onePerOwner, decimal monthly) => new()
    {
        Name = name,
        Active = true,
        MaximumUsers = 10,
        MaximumServers = 1,
        OnePerOwner = onePerOwner,
        Prices = [new PlanPrice { TermMonths = 1, BaseAmount = monthly, IncludedUnits = 1 }]
    };

    /// <summary>An Organization founded by someone, live, on a plan.</summary>
    private async Task<Guid> AddOrganizationAsync(
        ApiDbContext context, Guid founderId, Guid planId,
        SubscriptionStatus status = SubscriptionStatus.Active)
    {
        var tenant = new Tenant
        {
            Name = $"Org {Guid.NewGuid():N}",
            IsActive = true,
            CreatedById = founderId
        };

        context.Set<Tenant>().Add(tenant);
        await context.SaveChangesAsync();

        var subscription = new Subscription
        {
            TenantId = tenant.Id,
            PlanId = planId,
            StartDate = DateTimeOffset.UtcNow.AddMonths(-1),
            Status = status
        };

        context.Set<Subscription>().Add(subscription);
        await context.SaveChangesAsync();

        var period = new SubscriptionPeriod
        {
            SubscriptionId = subscription.Id,
            TermMonths = 1,
            Quantity = 1,
            PeriodStart = DateTimeOffset.UtcNow.AddDays(-10),
            PeriodEnd = DateTimeOffset.UtcNow.AddDays(20),
            StartDate = DateTimeOffset.UtcNow.AddDays(-10),
            EndDate = DateTimeOffset.UtcNow.AddDays(20)
        };

        context.Set<SubscriptionPeriod>().Add(period);
        await context.SaveChangesAsync();

        return tenant.Id;
    }

    [Fact]
    public async Task APlanWithoutTheFlagIsNeverRestricted()
    {
        await using var context = CreateContext();

        var paid = NewPlan("Stone", onePerOwner: false, 5m);
        context.Set<Plan>().Add(paid);
        await context.SaveChangesAsync();

        await AddOrganizationAsync(context, _founderId, paid.Id);
        await AddOrganizationAsync(context, _founderId, paid.Id);

        // Three would be fine too. A paid plan's second Organization is a second subscription.
        Assert.False(await OnePerOwnerRule.WouldExceedAsync(context, _founderId, paid.Id));
    }

    [Fact]
    public async Task ASecondOrganizationOnARestrictedPlanIsRefused()
    {
        await using var context = CreateContext();

        var free = NewPlan("Wood", onePerOwner: true, 0m);
        context.Set<Plan>().Add(free);
        await context.SaveChangesAsync();

        Assert.False(await OnePerOwnerRule.WouldExceedAsync(context, _founderId, free.Id));

        await AddOrganizationAsync(context, _founderId, free.Id);

        Assert.True(await OnePerOwnerRule.WouldExceedAsync(context, _founderId, free.Id));
    }

    /// <summary>
    /// Counted by who founded the Organization, not who owns it now. Being handed the Owner role in
    /// somebody else's free Organization is ordinary, and must not spend your own allowance.
    /// </summary>
    [Fact]
    public async Task SomebodyElsesOrganizationDoesNotCountAgainstYou()
    {
        await using var context = CreateContext();

        var free = NewPlan("Wood", onePerOwner: true, 0m);
        context.Set<Plan>().Add(free);
        await context.SaveChangesAsync();

        await AddOrganizationAsync(context, Guid.NewGuid(), free.Id);

        Assert.False(await OnePerOwnerRule.WouldExceedAsync(context, _founderId, free.Id));
    }

    [Fact]
    public async Task ACancelledOrganizationReleasesTheAllowance()
    {
        await using var context = CreateContext();

        var free = NewPlan("Wood", onePerOwner: true, 0m);
        context.Set<Plan>().Add(free);
        await context.SaveChangesAsync();

        await AddOrganizationAsync(context, _founderId, free.Id, SubscriptionStatus.Cancelled);

        Assert.False(await OnePerOwnerRule.WouldExceedAsync(context, _founderId, free.Id));
    }

    /// <summary>
    /// The Organization being moved is left out of its own count, so a term-only change - or any
    /// change that keeps it where it is - never refuses itself.
    /// </summary>
    [Fact]
    public async Task TheOrganizationBeingMovedDoesNotBlockItself()
    {
        await using var context = CreateContext();

        var free = NewPlan("Wood", onePerOwner: true, 0m);
        context.Set<Plan>().Add(free);
        await context.SaveChangesAsync();

        var tenantId = await AddOrganizationAsync(context, _founderId, free.Id);

        Assert.True(await OnePerOwnerRule.WouldExceedAsync(context, _founderId, free.Id));
        Assert.False(await OnePerOwnerRule.WouldExceedAsync(context, _founderId, free.Id, tenantId));
    }

    /// <summary>
    /// Organizations predating the founder being recorded carry <see cref="Guid.Empty"/>. Treating
    /// that as a person would make every one of them count against a single phantom owner, and start
    /// refusing plan changes for unrelated customers.
    /// </summary>
    [Fact]
    public async Task AnOrganizationWithNoRecordedFounderIsNotSubjectToTheRule()
    {
        await using var context = CreateContext();

        var free = NewPlan("Wood", onePerOwner: true, 0m);
        context.Set<Plan>().Add(free);
        await context.SaveChangesAsync();

        var orphan = await AddOrganizationAsync(context, Guid.Empty, free.Id);

        Assert.Null(await OnePerOwnerRule.FounderOfAsync(context, orphan));
    }

    [Fact]
    public async Task TheFounderIsReadBackFromTheOrganization()
    {
        await using var context = CreateContext();

        var free = NewPlan("Wood", onePerOwner: true, 0m);
        context.Set<Plan>().Add(free);
        await context.SaveChangesAsync();

        var tenantId = await AddOrganizationAsync(context, _founderId, free.Id);

        Assert.Equal(_founderId, await OnePerOwnerRule.FounderOfAsync(context, tenantId));
    }

    /// <summary>
    /// The route a creation-only check would miss: start on a paid tier, then downgrade to the
    /// restricted one.
    /// </summary>
    [Fact]
    public async Task MovingASecondOrganizationOntoARestrictedPlanIsBlockedOnTheQuote()
    {
        await using var context = CreateContext();

        var free = NewPlan("Wood", onePerOwner: true, 0m);
        var paid = NewPlan("Stone", onePerOwner: false, 5m);
        context.Set<Plan>().AddRange(free, paid);
        await context.SaveChangesAsync();

        // Their first Organization already holds the free tier.
        await AddOrganizationAsync(context, _founderId, free.Id);

        // Their second is on a paid one, and wants to move.
        var second = await AddOrganizationAsync(context, _founderId, paid.Id);

        var quote = await CreateSubscriptionService(context)
            .QuoteAsync(second, free.Id, targetTermMonths: 1);

        Assert.False(quote.Allowed);
        Assert.Contains("Only one organization", quote.BlockedReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MovingOntoAnUnrestrictedPlanIsFineHoweverManyYouHave()
    {
        await using var context = CreateContext();

        var free = NewPlan("Wood", onePerOwner: true, 0m);
        var paid = NewPlan("Stone", onePerOwner: false, 5m);
        context.Set<Plan>().AddRange(free, paid);
        await context.SaveChangesAsync();

        await AddOrganizationAsync(context, _founderId, paid.Id);
        var second = await AddOrganizationAsync(context, _founderId, free.Id);

        var quote = await CreateSubscriptionService(context)
            .QuoteAsync(second, paid.Id, targetTermMonths: 1);

        Assert.True(quote.Allowed);
    }

    private static SubscriptionService CreateSubscriptionService(ApiDbContext context) =>
        new(context,
            new SubscriptionRepository(context),
            new UnreachableInvoiceService(),
            new UnreachableCompression(),
            TimeProvider.System);
}

/// <summary>Never reached: a quote raises no invoice.</summary>
file class UnreachableInvoiceService : IInvoiceService
{
    public Task<Invoice?> IssueForPeriodAsync(
        SubscriptionPeriod period, decimal amount, string description,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A quote never issues an invoice.");

    public Task<bool> HasBeenBilledAsync(Guid periodId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A quote never issues an invoice.");
}

/// <summary>Reports nothing to compress, so it never colours these assertions.</summary>
file class UnreachableCompression : IRoleCompressionService
{
    public Task<RoleCompressionResult> PreviewAsync(
        Guid tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RoleCompressionResult(0, 0));

    public Task<RoleCompressionResult> CompressAsync(
        Guid tenantId, string reason, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A quote never applies compression.");
}
