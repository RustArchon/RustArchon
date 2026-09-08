// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Administration;
using RustArchon.Api.Billing;
using RustArchon.Api.Data;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests the warning a plan-change quote carries when the target plan has no role separation.
/// </summary>
/// <remarks>
/// The decision this exists to support is the customer's, and they only get to make it if the
/// consequence reaches them before they accept: the roles they defined are removed and everyone
/// becomes an owner. So what is asserted here is that the quote reports what compression would
/// actually do - not that some warning flag is set somewhere.
/// </remarks>
public class PlanChangeQuoteWarningTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly Guid _tenantId = Guid.NewGuid();

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    /// <summary>Seeds the plan being moved to, and the tenant's current subscription.</summary>
    private async Task<Guid> SeedAsync(ApiDbContext context, bool targetHasRoles)
    {
        var current = NewPlan("Metal", hasRoles: true);
        var target = NewPlan("Wood", hasRoles: targetHasRoles);

        context.Set<Plan>().AddRange(current, target);
        await context.SaveChangesAsync();

        var subscription = new Subscription
        {
            TenantId = _tenantId,
            PlanId = current.Id,
            StartDate = DateTimeOffset.UtcNow.AddMonths(-3)
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
            EndDate = DateTimeOffset.UtcNow.AddDays(20),
            EarnedAmount = 30m
        };

        context.Set<SubscriptionPeriod>().Add(period);
        await context.SaveChangesAsync();

        return target.Id;
    }

    private static Plan NewPlan(string name, bool hasRoles) => new()
    {
        Name = name,
        Active = true,
        HasRoles = hasRoles,
        MaximumUsers = 10,
        Prices = [new PlanPrice { TermMonths = 1, BaseAmount = 30m, IncludedUnits = 1 }]
    };

    private static SubscriptionService CreateService(
        ApiDbContext context, IRoleCompressionService compression) =>
        new(context, new SubscriptionRepository(context), new UnusedInvoiceService(),
            compression, TimeProvider.System);

    [Fact]
    public async Task WarnsWithTheNumbersCompressionReports_WhenTheTargetPlanHasNoRoles()
    {
        await using var context = CreateContext();
        var targetPlanId = await SeedAsync(context, targetHasRoles: false);

        var compression = new StubCompression(new RoleCompressionResult(MembersPromoted: 4, RolesRemoved: 2));

        var quote = await CreateService(context, compression)
            .QuoteAsync(_tenantId, targetPlanId, targetTermMonths: 1);

        Assert.True(quote.Allowed);
        Assert.True(quote.WillCompressRoles);
        Assert.Equal(4, quote.MembersToPromote);
        Assert.Equal(2, quote.RolesToRemove);
    }

    /// <summary>
    /// An Organization with no roles of its own loses nothing, so there is nothing to warn about -
    /// the warning has to track what would actually happen, not merely which plan was picked.
    /// </summary>
    [Fact]
    public async Task DoesNotWarn_WhenThereIsNothingToCompress()
    {
        await using var context = CreateContext();
        var targetPlanId = await SeedAsync(context, targetHasRoles: false);

        var compression = new StubCompression(new RoleCompressionResult(0, 0));

        var quote = await CreateService(context, compression)
            .QuoteAsync(_tenantId, targetPlanId, targetTermMonths: 1);

        Assert.False(quote.WillCompressRoles);
    }

    [Fact]
    public async Task DoesNotAskAboutCompression_WhenTheTargetPlanKeepsRoles()
    {
        await using var context = CreateContext();
        var targetPlanId = await SeedAsync(context, targetHasRoles: true);

        var compression = new StubCompression(new RoleCompressionResult(4, 2));

        var quote = await CreateService(context, compression)
            .QuoteAsync(_tenantId, targetPlanId, targetTermMonths: 1);

        Assert.False(quote.WillCompressRoles);
        Assert.False(compression.WasAsked);
    }
}

/// <summary>Reports a fixed preview, and records whether it was consulted at all.</summary>
file class StubCompression(RoleCompressionResult result) : IRoleCompressionService
{
    public bool WasAsked { get; private set; }

    public Task<RoleCompressionResult> PreviewAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        WasAsked = true;
        return Task.FromResult(result);
    }

    public Task<RoleCompressionResult> CompressAsync(
        Guid tenantId, string reason, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A quote never applies compression.");
}

/// <summary>Never reached: producing a quote raises no invoice.</summary>
file class UnusedInvoiceService : IInvoiceService
{
    public Task<Invoice?> IssueForPeriodAsync(
        SubscriptionPeriod period, decimal amount, string description,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A quote never issues an invoice.");

    public Task<bool> HasBeenBilledAsync(Guid periodId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A quote never issues an invoice.");
}
