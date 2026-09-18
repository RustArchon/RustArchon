// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Administration;
using RustArchon.Api.Billing;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="OrganizationLifecycleService.ForcePlanChangeAsync"/>: an admin moving an
/// already-active Organization onto a different plan immediately, bypassing the upgrade/downgrade
/// timing, proration and invoicing rules the tenant-facing <see cref="ISubscriptionService"/> applies.
/// </summary>
public class OrganizationLifecycleServiceForcePlanChangeTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid _adminId = Guid.NewGuid();

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private Plan MakePlan(string name, bool active, bool hasRoles, int? maximumServers) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ColorCode = "#888888",
        PricingModel = PricingModel.Flat,
        Active = active,
        HasRoles = hasRoles,
        MaximumServers = maximumServers,
        MaximumUsers = 20,
        CreatedById = Guid.Empty,
        Prices =
        [
            new PlanPrice
            {
                TermMonths = BillingTerms.Monthly, BaseAmount = 0m,
                IncludedUnits = maximumServers ?? 0, UnitAmount = 0m, Currency = "USD"
            }
        ]
    };

    private async Task<(Plan oldPlan, Subscription subscription, SubscriptionPeriod period)> SeedAsync(
        ApiDbContext context, int serverCount = 1)
    {
        var oldPlan = MakePlan("HQM", active: true, hasRoles: true, maximumServers: 10);
        context.Set<Tenant>().Add(new Tenant { Id = _tenantId, Name = "Sheltered Gaming", IsActive = true });
        context.Set<Plan>().Add(oldPlan);

        var subscription = new Subscription
        {
            TenantId = _tenantId,
            PlanId = oldPlan.Id,
            StartDate = _now.AddMonths(-1),
            Status = SubscriptionStatus.Active
        };
        context.Set<Subscription>().Add(subscription);

        var period = new SubscriptionPeriod
        {
            Subscription = subscription,
            TermMonths = BillingTerms.Monthly,
            Quantity = 10,
            PeriodStart = _now.AddMonths(-1),
            PeriodEnd = _now.AddMonths(1),
            StartDate = _now.AddMonths(-1),
            EndDate = _now.AddMonths(1),
            EarnedAmount = 29.95m
        };
        context.Set<SubscriptionPeriod>().Add(period);

        for (var i = 0; i < serverCount; i++)
        {
            context.Set<RustServer>().Add(new RustServer
            {
                Id = Guid.NewGuid(), TenantId = _tenantId, Name = $"server-{i}",
                Host = "127.0.0.1", Port = 28015 + i, IsEnabled = true, CreatedById = Guid.Empty
            });
        }

        await context.SaveChangesAsync();
        return (oldPlan, subscription, period);
    }

    private OrganizationLifecycleService CreateService(
        ApiDbContext context,
        Mock<ISubscriptionService>? subscriptionService = null,
        Mock<IRoleCompressionService>? roleCompression = null)
    {
        subscriptionService ??= new Mock<ISubscriptionService>();
        roleCompression ??= new Mock<IRoleCompressionService>();

        var userContext = new Mock<IUserContext>();
        userContext.Setup(u => u.GetCurrentUserIdAsync()).ReturnsAsync(_adminId);

        return new OrganizationLifecycleService(
            context,
            Mock.Of<IPublishEndpoint>(),
            Mock.Of<ICommunicationPublisher>(),
            subscriptionService.Object,
            roleCompression.Object,
            userContext.Object,
            new FixedClock(_now),
            NullLogger<OrganizationLifecycleService>.Instance);
    }

    [Fact]
    public async Task ForcesTheChangeImmediately_ClosingTheOldIntervalAndOpeningANewOneAtListPrice()
    {
        await using var context = CreateContext();
        var (oldPlan, subscription, period) = await SeedAsync(context);
        var newPlan = MakePlan("Gold (Comped)", active: true, hasRoles: true, maximumServers: 10);
        context.Set<Plan>().Add(newPlan);
        await context.SaveChangesAsync();

        var subscriptionService = new Mock<ISubscriptionService>();
        subscriptionService
            .Setup(s => s.CancelScheduledChangeAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreateService(context, subscriptionService)
            .ForcePlanChangeAsync(_tenantId, newPlan.Id, BillingTerms.Monthly, quantity: null, "Comped as a sponsor.");

        Assert.True(result.Success);
        Assert.Null(result.Error);

        await context.Entry(subscription).ReloadAsync();
        await context.Entry(period).ReloadAsync();
        Assert.Equal(_now, subscription.EndDate);
        Assert.Equal(_now, period.EndDate);
        // Not reconciled - whatever was already earned on the old plan's slice stands untouched.
        Assert.Equal(29.95m, period.EarnedAmount);

        var current = await context.Set<Subscription>()
            .Include(s => s.Periods)
            .SingleAsync(s => s.TenantId == _tenantId && s.EndDate == null);
        Assert.Equal(newPlan.Id, current.PlanId);
        Assert.Equal(_now, current.StartDate);
        Assert.Equal("Comped as a sponsor.", current.PlanChangeReason);
        Assert.Equal(_adminId, current.PlanChangedById);

        var newPeriod = current.Periods.Single();
        Assert.Equal(0m, newPeriod.EarnedAmount);
        Assert.Equal(10, newPeriod.Quantity);

        subscriptionService.Verify(
            s => s.CancelScheduledChangeAsync(_tenantId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BlockedWhenTheTargetPlanCannotHoldTheCurrentServerCount()
    {
        await using var context = CreateContext();
        await SeedAsync(context, serverCount: 3);
        var tooSmall = MakePlan("Wood", active: true, hasRoles: false, maximumServers: 1);
        context.Set<Plan>().Add(tooSmall);
        await context.SaveChangesAsync();

        var result = await CreateService(context)
            .ForcePlanChangeAsync(_tenantId, tooSmall.Id, BillingTerms.Monthly, quantity: null, "Testing.");

        Assert.False(result.Success);
        Assert.Contains("allows up to 1 server", result.Error);

        // Nothing should have moved.
        Assert.True(await context.Set<Subscription>().AnyAsync(s => s.TenantId == _tenantId && s.EndDate == null));
    }

    [Fact]
    public async Task BlockedWhenTheTargetPlanIsInactive()
    {
        await using var context = CreateContext();
        await SeedAsync(context);
        var retired = MakePlan("Old Metal", active: false, hasRoles: false, maximumServers: 5);
        context.Set<Plan>().Add(retired);
        await context.SaveChangesAsync();

        var result = await CreateService(context)
            .ForcePlanChangeAsync(_tenantId, retired.Id, BillingTerms.Monthly, quantity: null, "Testing.");

        Assert.False(result.Success);
        Assert.Contains("isn't active", result.Error);
    }

    [Fact]
    public async Task BlockedWhenTheTenantHasNoOpenSubscription()
    {
        await using var context = CreateContext();
        context.Set<Tenant>().Add(new Tenant { Id = _tenantId, Name = "No Subscription", IsActive = true });
        var plan = MakePlan("Gold (Comped)", active: true, hasRoles: true, maximumServers: 10);
        context.Set<Plan>().Add(plan);
        await context.SaveChangesAsync();

        var result = await CreateService(context)
            .ForcePlanChangeAsync(_tenantId, plan.Id, BillingTerms.Monthly, quantity: null, "Testing.");

        Assert.False(result.Success);
        Assert.Contains("no open subscription", result.Error);
    }

    [Fact]
    public async Task MovingToAPlanWithoutRoleSeparation_RunsRoleCompression()
    {
        await using var context = CreateContext();
        await SeedAsync(context);
        var flatPlan = MakePlan("Wood", active: true, hasRoles: false, maximumServers: 10);
        context.Set<Plan>().Add(flatPlan);
        await context.SaveChangesAsync();

        var roleCompression = new Mock<IRoleCompressionService>();
        roleCompression
            .Setup(r => r.CompressAsync(_tenantId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoleCompressionResult(MembersPromoted: 2, RolesRemoved: 1));

        var result = await CreateService(context, roleCompression: roleCompression)
            .ForcePlanChangeAsync(_tenantId, flatPlan.Id, BillingTerms.Monthly, quantity: null, "Testing.");

        Assert.True(result.Success);
        roleCompression.Verify(
            r => r.CompressAsync(_tenantId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
