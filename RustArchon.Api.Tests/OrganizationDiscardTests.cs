// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Authorization;
using JumpStart.Authorization.Repositories;
using JumpStart.Data;
using JumpStart.MultiTenant.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Api.Administration;
using RustArchon.Api.Billing;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Discarding the empty organization a half-finished registration leaves behind.
/// </summary>
/// <remarks>
/// Registration provisions the organization before redeeming the invitation code, so a provisioning
/// failure costs nobody a code. The price of that order is the reverse case - the organization
/// exists and then redemption loses a race - and this is what clears it up. The tests that matter
/// most are the refusals: the moment anything real is in there, discarding stops being tidying up
/// and becomes destroying evidence.
/// </remarks>
public class OrganizationDiscardTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly Guid _founderId = Guid.NewGuid();

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    private static OrganizationProvisioningService CreateService(ApiDbContext context)
    {
        var registry = new PermissionRegistry(PermissionCatalog.All);
        var validator = new PermissionGrantValidator(
            registry,
            new PermissiveRoleManagementPolicy(registry),
            new DatabasePermissionEvaluator(new PermissionResolver(context)));

        return new OrganizationProvisioningService(
            context,
            new TenantRepository(context, null),
            new UserTenantRepository(context, null),
            new RoleRepository(context, null, validator),
            new PlanRepository(context),
            new SubscriptionRepository(context),
            new UnusedInvoiceService(),
            new StubSettingsCache(),
            TimeProvider.System,
            NullLogger<OrganizationProvisioningService>.Instance);
    }

    /// <summary>Seeds the plan catalogue and the built-in Owner role that provisioning needs.</summary>
    private static async Task SeedPlatformAsync(ApiDbContext context)
    {
        context.Set<Plan>().Add(new Plan
        {
            Name = "Wood",
            Active = true,
            MaximumServers = 1,
            MaximumUsers = 1,
            Prices = [new PlanPrice { TermMonths = 1, BaseAmount = 0m, IncludedUnits = 1 }]
        });

        await context.SaveChangesAsync();

        var registry = new PermissionRegistry(PermissionCatalog.All);
        var validator = new PermissionGrantValidator(
            registry,
            new PermissiveRoleManagementPolicy(registry),
            new DatabasePermissionEvaluator(new PermissionResolver(context)));

        await BuiltInRoleSeeder.EnsureAsync(
            context, new RoleRepository(context, null, validator), NullLogger.Instance);
    }

    private async Task<Guid> ProvisionAsync(ApiDbContext context)
    {
        await SeedPlatformAsync(context);
        var tenant = await CreateService(context).CreateAsync(_founderId, "Newcomer's Organization");
        return tenant.Id;
    }

    private static Task<bool> ExistsAsync(ApiDbContext context, Guid tenantId) =>
        context.Set<Tenant>().AcrossAllTenants().AnyAsync(t => t.Id == tenantId);

    [Fact]
    public async Task AnEmptyOrganizationIsDiscarded()
    {
        await using var context = CreateContext();
        var tenantId = await ProvisionAsync(context);

        Assert.True(await CreateService(context).TryDiscardAsync(tenantId, _founderId));

        // Soft-deleted, so the global filter hides it everywhere from here on.
        Assert.False(await ExistsAsync(context, tenantId));

        // ...and nothing dangles at the account that is being deleted alongside it.
        Assert.Empty(await context.Set<UserTenant>().Where(ut => ut.TenantId == tenantId).ToListAsync());
        Assert.Empty(await context.Set<UserRole>().AcrossAllTenants()
            .Where(ur => ur.TenantId == tenantId).ToListAsync());
    }

    [Fact]
    public async Task AnOrganizationWithAServerIsRefused()
    {
        await using var context = CreateContext();
        var tenantId = await ProvisionAsync(context);

        context.Set<RustServer>().Add(new RustServer
        {
            TenantId = tenantId,
            Name = "Theirs",
            Host = "192.0.2.30",
            Port = 28016,
            RconPassword = "unused"
        });

        await context.SaveChangesAsync();

        Assert.False(await CreateService(context).TryDiscardAsync(tenantId, _founderId));
        Assert.True(await ExistsAsync(context, tenantId));
    }

    [Fact]
    public async Task AnOrganizationWithSomebodyElseInItIsRefused()
    {
        await using var context = CreateContext();
        var tenantId = await ProvisionAsync(context);

        context.Set<UserTenant>().Add(
            new UserTenant { UserId = Guid.NewGuid(), TenantId = tenantId, IsActive = true });

        await context.SaveChangesAsync();

        Assert.False(await CreateService(context).TryDiscardAsync(tenantId, _founderId));
        Assert.True(await ExistsAsync(context, tenantId));
    }

    /// <summary>
    /// A raised invoice is a financial document. Whatever else this is for, it is not for making
    /// those disappear.
    /// </summary>
    [Fact]
    public async Task AnOrganizationThatHasBeenInvoicedIsRefused()
    {
        await using var context = CreateContext();
        var tenantId = await ProvisionAsync(context);

        context.Set<Invoice>().Add(new Invoice
        {
            TenantId = tenantId,
            Number = "INV-0001",
            Status = InvoiceStatus.Open
        });

        await context.SaveChangesAsync();

        Assert.False(await CreateService(context).TryDiscardAsync(tenantId, _founderId));
        Assert.True(await ExistsAsync(context, tenantId));
    }

    [Fact]
    public async Task AnOrganizationThatDoesNotExistIsRefused()
    {
        await using var context = CreateContext();
        await SeedPlatformAsync(context);

        Assert.False(await CreateService(context).TryDiscardAsync(Guid.NewGuid(), _founderId));
    }
}

/// <summary>A free plan raises no invoice, so provisioning never reaches this.</summary>
file class UnusedInvoiceService : IInvoiceService
{
    public Task<Invoice?> IssueForPeriodAsync(
        SubscriptionPeriod period, decimal amount, string description,
        System.Threading.CancellationToken cancellationToken = default) =>
        Task.FromResult<Invoice?>(null);

    public Task<bool> HasBeenBilledAsync(
        Guid periodId, System.Threading.CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}

/// <summary>No default plan configured, so provisioning falls through to the cheapest active one.</summary>
file class StubSettingsCache : IPlatformSettingsCache
{
    public Task<string?> GetStringAsync(string key) => Task.FromResult<string?>(null);

    public Task<bool> GetBooleanAsync(string key, bool defaultValue) => Task.FromResult(defaultValue);

    public Task SetAsync(string key, string value) => Task.CompletedTask;
}
