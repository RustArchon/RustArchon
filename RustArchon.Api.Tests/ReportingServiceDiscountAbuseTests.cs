// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Api.Reporting;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="ReportingService.GetDiscountAbuseSignalsAsync"/> - see
/// <see cref="DiscountAbuseSignalRowDto"/>'s own remarks for why this only ever flags groups where a
/// discount is actually involved.
/// </summary>
public class ReportingServiceDiscountAbuseTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    private async Task<Guid> SeedTenantAsync(ApiDbContext context, string name, string? contactEmail = null)
    {
        var tenantId = Guid.NewGuid();
        context.Set<Tenant>().Add(new Tenant
        {
            Id = tenantId, Name = name, ContactEmail = contactEmail, IsActive = true
        });
        await context.SaveChangesAsync();
        return tenantId;
    }

    private async Task RedeemAsync(ApiDbContext context, Guid tenantId, string code)
    {
        var discount = new Discount { Code = code, AmountType = DiscountAmountType.PercentOff, AmountValue = 10m, CreatedOn = DateTimeOffset.UtcNow };
        context.Set<Discount>().Add(discount);
        context.Set<DiscountRedemption>().Add(new DiscountRedemption
        {
            DiscountId = discount.Id, TenantId = tenantId, Status = DiscountRedemptionStatus.Applied,
            RedeemedOn = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task FlagsTwoOrganizationsSharingAServerWhenOneHasRedeemedADiscount()
    {
        await using var context = CreateContext();
        var tenantA = await SeedTenantAsync(context, "Org A");
        var tenantB = await SeedTenantAsync(context, "Org B");

        context.Set<RustServer>().Add(new RustServer { TenantId = tenantA, Name = "Old", Host = "1.2.3.4", Port = 28016 });
        context.Set<RustServer>().Add(new RustServer { TenantId = tenantB, Name = "New", Host = "1.2.3.4", Port = 28016 });
        await context.SaveChangesAsync();

        await RedeemAsync(context, tenantB, "PROMO");

        var service = new ReportingService(context, TimeProvider.System);
        var result = await service.GetDiscountAbuseSignalsAsync();

        var row = Assert.Single(result.Rows, r => r.SignalType == "Shared server");
        Assert.Equal("1.2.3.4:28016", row.Detail);
        Assert.Equal(2, row.Tenants.Count);
        Assert.Contains(row.Tenants, t => t.TenantId == tenantB && t.RedeemedDiscountCodes.Contains("PROMO"));
    }

    [Fact]
    public async Task DoesNotFlagASharedServerWhenNeitherOrganizationHasRedeemedADiscount()
    {
        await using var context = CreateContext();
        var tenantA = await SeedTenantAsync(context, "Org A");
        var tenantB = await SeedTenantAsync(context, "Org B");

        context.Set<RustServer>().Add(new RustServer { TenantId = tenantA, Name = "Old", Host = "5.6.7.8", Port = 28016 });
        context.Set<RustServer>().Add(new RustServer { TenantId = tenantB, Name = "New", Host = "5.6.7.8", Port = 28016 });
        await context.SaveChangesAsync();

        var service = new ReportingService(context, TimeProvider.System);
        var result = await service.GetDiscountAbuseSignalsAsync();

        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task FlagsTwoOrganizationsSharingAContactEmailWhenOneHasRedeemedADiscount()
    {
        await using var context = CreateContext();
        var tenantA = await SeedTenantAsync(context, "Org A", "same@example.com");
        var tenantB = await SeedTenantAsync(context, "Org B", "Same@Example.com");
        await RedeemAsync(context, tenantA, "PROMO2");

        var service = new ReportingService(context, TimeProvider.System);
        var result = await service.GetDiscountAbuseSignalsAsync();

        var row = Assert.Single(result.Rows, r => r.SignalType == "Shared contact email");
        Assert.Equal(2, row.Tenants.Count);
    }

    [Fact]
    public async Task IncludesAServerThatWasSoftDeletedFromTheOlderOrganization()
    {
        await using var context = CreateContext();
        var tenantA = await SeedTenantAsync(context, "Org A");
        var tenantB = await SeedTenantAsync(context, "Org B");

        var oldServer = new RustServer { TenantId = tenantA, Name = "Old", Host = "9.9.9.9", Port = 28016, DeletedOn = DateTimeOffset.UtcNow };
        context.Set<RustServer>().Add(oldServer);
        context.Set<RustServer>().Add(new RustServer { TenantId = tenantB, Name = "New", Host = "9.9.9.9", Port = 28016 });
        await context.SaveChangesAsync();

        await RedeemAsync(context, tenantB, "PROMO3");

        var service = new ReportingService(context, TimeProvider.System);
        var result = await service.GetDiscountAbuseSignalsAsync();

        Assert.Single(result.Rows, r => r.SignalType == "Shared server" && r.Detail == "9.9.9.9:28016");
    }
}
