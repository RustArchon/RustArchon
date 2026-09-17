// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Administration;
using RustArchon.Api.Data;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="OrganizationSettingsService"/> - an Organization editing its own name and
/// contact address.
/// </summary>
public class OrganizationSettingsServiceTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly Guid _tenantId = Guid.NewGuid();

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    private async Task SeedAsync(ApiDbContext context, string? contactEmail = "owner@acme.example")
    {
        context.Set<Tenant>().Add(new Tenant
        {
            Id = _tenantId, Name = "Acme", IsActive = true, ContactEmail = contactEmail
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task GetReturnsTheCurrentNameAndContactEmail()
    {
        await using var context = CreateContext();
        await SeedAsync(context);

        var result = await new OrganizationSettingsService(context).GetAsync(_tenantId);

        Assert.NotNull(result);
        Assert.Equal("Acme", result.Name);
        Assert.Equal("owner@acme.example", result.ContactEmail);
    }

    [Fact]
    public async Task GetReturnsNullForATenantThatDoesNotExist()
    {
        await using var context = CreateContext();

        var result = await new OrganizationSettingsService(context).GetAsync(_tenantId);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateChangesTheNameAndContactEmail()
    {
        await using var context = CreateContext();
        await SeedAsync(context);

        var updated = await new OrganizationSettingsService(context)
            .UpdateAsync(
                _tenantId, "Acme Holdings", "new-owner@acme.example",
                billingLine1: null, billingLine2: null, billingCity: null, billingState: null,
                billingPostalCode: null, billingCountry: null);

        Assert.True(updated);

        var tenant = await context.Set<Tenant>().SingleAsync(t => t.Id == _tenantId);
        Assert.Equal("Acme Holdings", tenant.Name);
        Assert.Equal("new-owner@acme.example", tenant.ContactEmail);
    }

    [Fact]
    public async Task UpdateWithABlankContactEmailClearsIt()
    {
        await using var context = CreateContext();
        await SeedAsync(context);

        await new OrganizationSettingsService(context).UpdateAsync(
            _tenantId, "Acme", contactEmail: "  ",
            billingLine1: null, billingLine2: null, billingCity: null, billingState: null,
            billingPostalCode: null, billingCountry: null);

        var tenant = await context.Set<Tenant>().SingleAsync(t => t.Id == _tenantId);
        Assert.Null(tenant.ContactEmail);
    }

    [Fact]
    public async Task UpdateReturnsFalseForATenantThatDoesNotExist()
    {
        await using var context = CreateContext();

        var updated = await new OrganizationSettingsService(context)
            .UpdateAsync(
                _tenantId, "Acme", "owner@acme.example",
                billingLine1: null, billingLine2: null, billingCity: null, billingState: null,
                billingPostalCode: null, billingCountry: null);

        Assert.False(updated);
    }
}
