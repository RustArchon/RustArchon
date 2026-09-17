// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Billing;
using RustArchon.Api.Data;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests that <see cref="SubscriptionService.GetAsync"/> carries <see cref="Subscription.Status"/>/
/// <see cref="Subscription.StatusReason"/> through onto <see cref="SubscriptionDto"/> - what
/// <c>SubscriptionStatusBanner</c> (RustArchon.Panel) reads to decide whether to show a "you're past
/// due"/"your servers are suspended" notice on every page.
/// </summary>
public class SubscriptionServiceStatusTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    private async Task<Guid> SeedAsync(ApiDbContext context, SubscriptionStatus status, string? reason)
    {
        var tenantId = Guid.NewGuid();
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = "Acme", IsActive = true });

        var plan = new Plan { Name = "Wood", Active = true };
        plan.Prices.Add(new PlanPrice { TermMonths = 1, UnitAmount = 0m, IncludedUnits = 1, Currency = "USD" });
        context.Set<Plan>().Add(plan);

        var subscription = new Subscription
        {
            TenantId = tenantId,
            PlanId = plan.Id,
            Plan = plan,
            StartDate = DateTimeOffset.UtcNow.AddDays(-10),
            Status = status,
            StatusReason = reason
        };
        context.Set<Subscription>().Add(subscription);

        context.Set<SubscriptionPeriod>().Add(new SubscriptionPeriod
        {
            SubscriptionId = subscription.Id,
            Subscription = subscription,
            TermMonths = 1,
            Quantity = 1,
            PeriodStart = DateTimeOffset.UtcNow.AddDays(-10),
            PeriodEnd = DateTimeOffset.UtcNow.AddDays(20),
            StartDate = DateTimeOffset.UtcNow.AddDays(-10),
            EarnedAmount = 0m
        });

        await context.SaveChangesAsync();
        return tenantId;
    }

    private static SubscriptionService CreateService(ApiDbContext context) => new(
        context,
        new SubscriptionRepository(context),
        Moq.Mock.Of<IInvoiceService>(),
        Moq.Mock.Of<Administration.IRoleCompressionService>(),
        TimeProvider.System);

    [Fact]
    public async Task APastDueSubscriptionCarriesItsStatusAndReasonOntoTheDto()
    {
        await using var context = CreateContext();
        var tenantId = await SeedAsync(context, SubscriptionStatus.PastDue, "Invoice overdue.");

        var dto = await CreateService(context).GetAsync(tenantId);

        Assert.NotNull(dto);
        Assert.Equal(SubscriptionStatus.PastDue, dto!.Status);
        Assert.Equal("Invoice overdue.", dto.StatusReason);
    }

    [Fact]
    public async Task AnActiveSubscriptionCarriesActiveWithNoReason()
    {
        await using var context = CreateContext();
        var tenantId = await SeedAsync(context, SubscriptionStatus.Active, reason: null);

        var dto = await CreateService(context).GetAsync(tenantId);

        Assert.NotNull(dto);
        Assert.Equal(SubscriptionStatus.Active, dto!.Status);
        Assert.Null(dto.StatusReason);
    }
}
