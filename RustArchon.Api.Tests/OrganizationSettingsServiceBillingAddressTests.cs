// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Administration;
using RustArchon.Api.Data;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="OrganizationSettingsService"/>'s billing-address half - see
/// <see cref="TenantBillingAddress"/>'s own remarks for why this exists (tax jurisdiction, not shipping)
/// and why <see cref="TenantBillingAddress.Country"/> is the field everything else depends on.
/// </summary>
public class OrganizationSettingsServiceBillingAddressTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly Guid _tenantId = Guid.NewGuid();

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    private async Task SeedTenantAsync(ApiDbContext context)
    {
        context.Set<Tenant>().Add(new Tenant { Id = _tenantId, Name = "Acme", IsActive = true });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task SavingABillingAddressMakesItComeBackOnGet()
    {
        await using var context = CreateContext();
        await SeedTenantAsync(context);

        var service = new OrganizationSettingsService(context);
        await service.UpdateAsync(
            _tenantId, "Acme", "owner@acme.example",
            billingLine1: "123 Main St", billingLine2: "Suite 4", billingCity: "Austin",
            billingState: "TX", billingPostalCode: "78701", billingCountry: "us");

        var settings = await service.GetAsync(_tenantId);

        Assert.NotNull(settings);
        Assert.Equal("123 Main St", settings!.BillingLine1);
        Assert.Equal("Suite 4", settings.BillingLine2);
        Assert.Equal("Austin", settings.BillingCity);
        Assert.Equal("TX", settings.BillingState);
        Assert.Equal("78701", settings.BillingPostalCode);
        // Normalized to uppercase regardless of what was typed - ISO 3166-1 alpha-2 codes are
        // conventionally uppercase, and a mixed-case value would otherwise compare unequal to itself
        // depending on which form last saved it.
        Assert.Equal("US", settings.BillingCountry);
    }

    [Fact]
    public async Task NoBillingAddressEverSetComesBackAsAllNull()
    {
        await using var context = CreateContext();
        await SeedTenantAsync(context);

        var service = new OrganizationSettingsService(context);
        await service.UpdateAsync(
            _tenantId, "Acme", "owner@acme.example",
            billingLine1: null, billingLine2: null, billingCity: null, billingState: null,
            billingPostalCode: null, billingCountry: null);

        var settings = await service.GetAsync(_tenantId);

        Assert.NotNull(settings);
        Assert.Null(settings!.BillingCountry);
        Assert.Null(settings.BillingLine1);

        // No row was created at all for an address that was never really set.
        Assert.False(await context.Set<TenantBillingAddress>().AnyAsync(b => b.TenantId == _tenantId));
    }

    [Fact]
    public async Task SavingWithABlankCountryClearsAPreviouslySetBillingAddress()
    {
        await using var context = CreateContext();
        await SeedTenantAsync(context);

        var service = new OrganizationSettingsService(context);
        await service.UpdateAsync(
            _tenantId, "Acme", "owner@acme.example",
            billingLine1: "123 Main St", billingLine2: null, billingCity: "Austin",
            billingState: "TX", billingPostalCode: "78701", billingCountry: "US");

        // Now clear it - blank country, same as never having set one.
        await service.UpdateAsync(
            _tenantId, "Acme", "owner@acme.example",
            billingLine1: null, billingLine2: null, billingCity: null, billingState: null,
            billingPostalCode: null, billingCountry: "   ");

        var settings = await service.GetAsync(_tenantId);

        Assert.NotNull(settings);
        Assert.Null(settings!.BillingCountry);
        Assert.False(await context.Set<TenantBillingAddress>().AnyAsync(b => b.TenantId == _tenantId));
    }

    [Fact]
    public async Task UpdatingAnExistingBillingAddressOverwritesItInPlaceRatherThanAddingASecondRow()
    {
        await using var context = CreateContext();
        await SeedTenantAsync(context);

        var service = new OrganizationSettingsService(context);
        await service.UpdateAsync(
            _tenantId, "Acme", "owner@acme.example",
            billingLine1: "123 Main St", billingLine2: null, billingCity: "Austin",
            billingState: "TX", billingPostalCode: "78701", billingCountry: "US");

        await service.UpdateAsync(
            _tenantId, "Acme", "owner@acme.example",
            billingLine1: "456 Oak Ave", billingLine2: null, billingCity: "Dallas",
            billingState: "TX", billingPostalCode: "75201", billingCountry: "US");

        var rows = await context.Set<TenantBillingAddress>().Where(b => b.TenantId == _tenantId).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("456 Oak Ave", row.Line1);
        Assert.Equal("Dallas", row.City);
    }
}
