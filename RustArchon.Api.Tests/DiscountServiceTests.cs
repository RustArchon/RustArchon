// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Api.Billing;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="DiscountService"/> - the catalog and every redemption rule. Runs against
/// InMemory; nothing here opens a transaction.
/// </summary>
public class DiscountServiceTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    private static DiscountService CreateService(ApiDbContext context, TimeProvider? clock = null) =>
        new(context, clock ?? TimeProvider.System, NullLogger<DiscountService>.Instance);

    private async Task<Guid> SeedTenantAsync(ApiDbContext context)
    {
        var tenantId = Guid.NewGuid();
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Tenant {tenantId}", IsActive = true });
        await context.SaveChangesAsync();
        return tenantId;
    }

    [Fact]
    public async Task CreateNormalisesTheCodeToUpperCase()
    {
        await using var context = CreateContext();

        var discount = await CreateService(context).CreateAsync(
            "  spring24  ", DiscountAmountType.PercentOff, 20m, null, null, false, null, null);

        Assert.Equal("SPRING24", discount.Code);
    }

    [Fact]
    public async Task CreatingTheSameCodeTwiceIsRefused()
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        await service.CreateAsync("DUPLICATE", DiscountAmountType.PercentOff, 10m, null, null, false, null, null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync("duplicate", DiscountAmountType.FlatAmountOff, 5m, null, null, false, null, null));
    }

    [Fact]
    public async Task APercentOffOver100IsRejected()
    {
        await using var context = CreateContext();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => CreateService(context).CreateAsync(
                "TOOBIG", DiscountAmountType.PercentOff, 150m, null, null, false, null, null));
    }

    [Fact]
    public async Task ARedemptionForAValidOpenCodeSucceeds()
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        await service.CreateAsync("OPEN10", DiscountAmountType.PercentOff, 10m, null, null, false, null, null);
        var tenantId = await SeedTenantAsync(context);

        var result = await service.RedeemAsync(tenantId, "open10");

        Assert.True(result.Success);
        Assert.NotNull(result.Redemption);
        Assert.Equal(DiscountRedemptionStatus.Pending, result.Redemption!.Status);
    }

    [Fact]
    public async Task AnExpiredCodeIsRefused()
    {
        var now = DateTimeOffset.UtcNow;
        await using var context = CreateContext();
        var clock = new FixedClock(now);
        var service = CreateService(context, clock);
        await service.CreateAsync(
            "OLD", DiscountAmountType.PercentOff, 10m, null, null, false, null, now.AddDays(-1));
        var tenantId = await SeedTenantAsync(context);

        var result = await service.RedeemAsync(tenantId, "OLD");

        Assert.False(result.Success);
        Assert.Contains("expired", result.ErrorMessage);
    }

    [Fact]
    public async Task AnInactiveCodeIsRefused()
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        var discount = await service.CreateAsync(
            "OFFSWITCH", DiscountAmountType.PercentOff, 10m, null, null, false, null, null);
        await service.SetActiveAsync(discount.Id, false);
        var tenantId = await SeedTenantAsync(context);

        var result = await service.RedeemAsync(tenantId, "OFFSWITCH");

        Assert.False(result.Success);
        Assert.Contains("no longer active", result.ErrorMessage);
    }

    [Fact]
    public async Task ACodeAtItsRedemptionCapIsRefused()
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        await service.CreateAsync("CAPPED", DiscountAmountType.PercentOff, 10m, null, maxRedemptions: 1,
            oncePerOrganization: false, restrictedToTenantId: null, expiresOn: null);

        var firstTenant = await SeedTenantAsync(context);
        var secondTenant = await SeedTenantAsync(context);

        var first = await service.RedeemAsync(firstTenant, "CAPPED");
        var second = await service.RedeemAsync(secondTenant, "CAPPED");

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Contains("fully redeemed", second.ErrorMessage);
    }

    [Fact]
    public async Task ACodeRestrictedToAnotherOrganizationIsRefused()
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        var targetTenant = await SeedTenantAsync(context);
        var otherTenant = await SeedTenantAsync(context);
        await service.CreateAsync("VIP", DiscountAmountType.PercentOff, 15m, null, null, false, targetTenant, null);

        var forTarget = await service.RedeemAsync(targetTenant, "VIP");
        var forOther = await service.RedeemAsync(otherTenant, "VIP");

        Assert.True(forTarget.Success);
        Assert.False(forOther.Success);
        Assert.Contains("isn't valid for this account", forOther.ErrorMessage);
    }

    [Fact]
    public async Task OncePerOrganizationRefusesASecondRedemptionByTheSameTenant()
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        // MaxRedemptions null (open) so the second attempt fails on OncePerOrganization specifically,
        // not on the cap.
        await service.CreateAsync("ONCE", DiscountAmountType.PercentOff, 10m, null, null,
            oncePerOrganization: true, restrictedToTenantId: null, expiresOn: null);
        var tenantId = await SeedTenantAsync(context);

        var first = await service.RedeemAsync(tenantId, "ONCE");
        // Mark the first Applied so the "one pending at a time" rule below doesn't mask this one.
        first.Redemption!.Status = DiscountRedemptionStatus.Applied;
        await context.SaveChangesAsync();

        var second = await service.RedeemAsync(tenantId, "ONCE");

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Contains("already used that code", second.ErrorMessage);
    }

    [Fact]
    public async Task OnlyOneCouponPerOrderRefusesASecondRedemptionWhileOneIsStillPending()
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        await service.CreateAsync("FIRST", DiscountAmountType.PercentOff, 10m, null, null, false, null, null);
        await service.CreateAsync("SECOND", DiscountAmountType.FlatAmountOff, 5m, null, null, false, null, null);
        var tenantId = await SeedTenantAsync(context);

        var first = await service.RedeemAsync(tenantId, "FIRST");
        var second = await service.RedeemAsync(tenantId, "SECOND");

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Contains("already has a discount waiting", second.ErrorMessage);
    }

    [Fact]
    public async Task ACodeThatDoesNotExistIsRefused()
    {
        await using var context = CreateContext();
        var tenantId = await SeedTenantAsync(context);

        var result = await CreateService(context).RedeemAsync(tenantId, "NOPE");

        Assert.False(result.Success);
        Assert.Contains("doesn't exist", result.ErrorMessage);
    }
}
