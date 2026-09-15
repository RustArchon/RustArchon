// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Administration;
using RustArchon.Api.Billing;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="SubscriptionScheduleService"/>'s per-tenant isolation and its handling of
/// <see cref="TaxJurisdictionUnregisteredException"/> - see that class's own remarks for the bug this
/// closes (an unbilled current period was previously never retried until a whole term later) and for
/// why one tenant's failure must never stop the rest of a pass. Runs against InMemory - unlike
/// <see cref="PaymentServiceRefundTests"/>/<see cref="InvoiceServiceStripeTaxTests"/>, nothing exercised
/// here opens a real transaction (that's <see cref="ISubscriptionRepository"/>'s
/// <c>TryApplyChangeAsync</c> path, not touched by these tests - see that method's own remarks).
/// </summary>
public class SubscriptionScheduleServiceTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    private SubscriptionScheduleService CreateService(
        TimeProvider clock, IInvoiceService invoices, out ServiceProvider provider)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new ApiDbContext(
            new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options));
        services.AddScoped<ISubscriptionRepository, SubscriptionRepository>();
        services.AddSingleton(invoices);
        services.AddSingleton(Mock.Of<IRoleCompressionService>());

        provider = services.BuildServiceProvider();
        return new SubscriptionScheduleService(
            provider.GetRequiredService<IServiceScopeFactory>(), clock, NullLogger<SubscriptionScheduleService>.Instance);
    }

    /// <summary>
    /// Seeds a tenant with two periods on one still-open subscription: an older, already-ended one (so
    /// this tenant is picked up by <see cref="SubscriptionScheduleService.RunPassAsync"/>'s own
    /// selection query - see that method's remarks on why it's a permissive superset) and the current
    /// one, whose <see cref="SubscriptionPeriod.PeriodEnd"/> and billed status are given explicitly.
    /// </summary>
    private async Task<(Guid TenantId, SubscriptionPeriod Current)> SeedAsync(
        ApiDbContext context, DateTimeOffset now, DateTimeOffset currentPeriodEnd)
    {
        var tenantId = Guid.NewGuid();
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Test tenant {tenantId}", IsActive = true });

        var plan = new Plan { Name = $"Test plan {Guid.NewGuid()}", Active = true };
        context.Set<Plan>().Add(plan);
        await context.SaveChangesAsync();

        var subscription = new Subscription
        {
            TenantId = tenantId, PlanId = plan.Id, StartDate = now.AddMonths(-2)
        };
        context.Set<Subscription>().Add(subscription);
        await context.SaveChangesAsync();

        // The older, already-ended period - never inspected directly, only there so RunPassAsync's own
        // "any period ended" selection query includes this tenant.
        context.Set<SubscriptionPeriod>().Add(new SubscriptionPeriod
        {
            SubscriptionId = subscription.Id,
            TermMonths = BillingTerms.Monthly,
            Quantity = 1,
            PeriodStart = now.AddMonths(-2),
            PeriodEnd = now.AddMonths(-1),
            StartDate = now.AddMonths(-2),
            EndDate = now.AddMonths(-1),
            EarnedAmount = 15m
        });

        var current = new SubscriptionPeriod
        {
            SubscriptionId = subscription.Id,
            TermMonths = BillingTerms.Monthly,
            Quantity = 1,
            PeriodStart = now.AddMonths(-1),
            PeriodEnd = currentPeriodEnd,
            StartDate = now.AddMonths(-1),
            EndDate = currentPeriodEnd,
            EarnedAmount = 15m
        };
        context.Set<SubscriptionPeriod>().Add(current);
        await context.SaveChangesAsync();

        return (tenantId, current);
    }

    [Fact]
    public async Task AnUnbilledCurrentPeriodIsRetriedEvenThoughItsPeriodEndIsStillInTheFuture()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FixedClock(now);

        await using var context = CreateContext();
        // Not yet due to renew - PeriodEnd is three weeks out - but never successfully billed, the
        // shape a failed RenewAsync call leaves behind (see SubscriptionScheduleService's own remarks).
        var (tenantId, current) = await SeedAsync(context, now, currentPeriodEnd: now.AddDays(21));

        var invoices = new Mock<IInvoiceService>();
        invoices
            .Setup(i => i.IssueForPeriodAsync(
                It.IsAny<SubscriptionPeriod>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Invoice?)null);

        var service = CreateService(clock, invoices.Object, out var provider);
        await using (provider)
        {
            await service.RunPassAsync(CancellationToken.None);
        }

        invoices.Verify(
            i => i.IssueForPeriodAsync(
                It.Is<SubscriptionPeriod>(p => p.Id == current.Id), 15m, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ATaxJurisdictionBlockIsRecordedAndClearedOnceItSucceeds()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FixedClock(now);

        Guid tenantId;
        await using (var context = CreateContext())
        {
            (tenantId, _) = await SeedAsync(context, now, currentPeriodEnd: now.AddDays(-1));
        }

        var invoices = new Mock<IInvoiceService>();
        var callCount = 0;
        invoices
            .Setup(i => i.IssueForPeriodAsync(
                It.IsAny<SubscriptionPeriod>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                // First attempt (the retry-if-unbilled call, since the seeded current period is
                // already unbilled): blocked. Every attempt after that: succeeds.
                return callCount == 1
                    ? throw new TaxJurisdictionUnregisteredException("US", "NY")
                    : Task.FromResult<Invoice?>(null);
            });

        var service = CreateService(clock, invoices.Object, out var provider1);
        await using (provider1)
        {
            await service.RunPassAsync(CancellationToken.None);
        }

        await using (var check = CreateContext())
        {
            var blocked = await check.Set<BlockedInvoiceIssuance>().SingleAsync(b => b.TenantId == tenantId);
            Assert.Equal("US", blocked.Country);
            Assert.Equal("NY", blocked.State);
            Assert.Equal(1, blocked.AttemptCount);
        }

        // Second pass: IssueForPeriodAsync now succeeds (callCount is already past 1), so the block
        // should clear.
        var service2 = CreateService(clock, invoices.Object, out var provider2);
        await using (provider2)
        {
            await service2.RunPassAsync(CancellationToken.None);
        }

        await using (var check = CreateContext())
        {
            Assert.False(await check.Set<BlockedInvoiceIssuance>().AnyAsync(b => b.TenantId == tenantId));
        }
    }

    [Fact]
    public async Task OneTenantsUnhandledFailureDoesNotStopTheRestOfThePass()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FixedClock(now);

        Guid failingTenantId, okTenantId;
        SubscriptionPeriod okCurrent;
        await using (var context = CreateContext())
        {
            (failingTenantId, _) = await SeedAsync(context, now, currentPeriodEnd: now.AddDays(-1));
            (okTenantId, okCurrent) = await SeedAsync(context, now, currentPeriodEnd: now.AddDays(-1));
        }

        var invoices = new Mock<IInvoiceService>();
        invoices
            .Setup(i => i.IssueForPeriodAsync(
                It.Is<SubscriptionPeriod>(p => p.SubscriptionId != okCurrent.SubscriptionId),
                It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Stripe unreachable"));
        invoices
            .Setup(i => i.IssueForPeriodAsync(
                It.Is<SubscriptionPeriod>(p => p.SubscriptionId == okCurrent.SubscriptionId),
                It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Invoice?)null);

        var service = CreateService(clock, invoices.Object, out var provider);
        await using (provider)
        {
            // Would previously throw out of RunPassAsync entirely the moment the failing tenant (sorted
            // first or not) was reached, silently skipping every tenant after it for this whole pass.
            await service.RunPassAsync(CancellationToken.None);
        }

        // The tenant that never failed still got billed despite the other one's unhandled exception -
        // AtLeastOnce rather than Once, since this tenant's period is also due to renew this same pass
        // (a second, separate IssueForPeriodAsync call this test isn't concerned with pinning down; see
        // AnUnbilledCurrentPeriodIsRetriedEvenThoughItsPeriodEndIsStillInTheFuture for that call count).
        invoices.Verify(
            i => i.IssueForPeriodAsync(
                It.Is<SubscriptionPeriod>(p => p.SubscriptionId == okCurrent.SubscriptionId),
                It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        // The failing tenant's unhandled exception isn't a tax-jurisdiction block, so nothing is recorded.
        await using var check = CreateContext();
        Assert.False(await check.Set<BlockedInvoiceIssuance>().AnyAsync(b => b.TenantId == failingTenantId));
    }
}
