// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Authorization;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Api.Administration;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="SiteAdminCrossTenantPolicy"/> - the one deliberate exception to "membership
/// is what puts you in a tenant".
/// </summary>
/// <remarks>
/// This is the sharpest thing in the codebase: it hands somebody a token for an Organization they do
/// not belong to. So what is tested is not only that a site admin gets in, but the three boundaries
/// around it - an ordinary member of another Organization does not, a signed-in nobody does not, and
/// whoever does get in arrives holding an owner's permissions rather than a platform administrator's.
/// </remarks>
public class SiteAdminCrossTenantPolicyTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly Guid _customerTenantId = Guid.NewGuid();

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    private static SiteAdminCrossTenantPolicy CreatePolicy(ApiDbContext context) =>
        new(context,
            new PermissionResolver(context),
            NullLogger<SiteAdminCrossTenantPolicy>.Instance);

    private async Task SeedCustomerAsync(ApiDbContext context)
    {
        context.Set<Tenant>().Add(
            new Tenant { Id = _customerTenantId, Name = "A customer", IsActive = true });

        await context.SaveChangesAsync();
    }

    /// <summary>Grants a role holding <paramref name="permissions"/>, in <paramref name="tenantId"/>.</summary>
    private static async Task<Guid> GrantAsync(
        ApiDbContext context, Guid userId, Guid? tenantId, string name, params string[] permissions)
    {
        var role = new Role { Name = name, TenantId = tenantId };
        context.Set<Role>().Add(role);
        await context.SaveChangesAsync();

        foreach (var permission in permissions)
        {
            context.Set<RolePermission>().Add(
                new RolePermission { RoleId = role.Id, Permission = permission });
        }

        context.Set<UserRole>().Add(
            new UserRole { UserId = userId, RoleId = role.Id, TenantId = tenantId });

        await context.SaveChangesAsync();
        return role.Id;
    }

    [Fact]
    public async Task ASiteAdminMayActAsACustomer()
    {
        await using var context = CreateContext();
        await SeedCustomerAsync(context);

        var siteAdmin = Guid.NewGuid();

        // Platform-wide: the grant carries no tenant, which is what makes it a platform role.
        await GrantAsync(
            context, siteAdmin, null, SiteAdminRoleSeeder.RoleName,
            PermissionCatalog.PlatformManageOrganizations);

        var access = await CreatePolicy(context).EvaluateAsync(siteAdmin, _customerTenantId);

        Assert.NotNull(access);
    }

    /// <summary>
    /// The property that makes this support rather than escalation: they arrive able to do what the
    /// customer's own owner could do, and nothing more.
    /// </summary>
    [Fact]
    public async Task TheyArriveHoldingAnOwnersPermissionsAndNotTheirPlatformOnes()
    {
        await using var context = CreateContext();
        await SeedCustomerAsync(context);

        var siteAdmin = Guid.NewGuid();

        await GrantAsync(
            context, siteAdmin, null, SiteAdminRoleSeeder.RoleName,
            PermissionCatalog.PlatformManageOrganizations,
            PermissionCatalog.PlatformManageBilling);

        var access = await CreatePolicy(context).EvaluateAsync(siteAdmin, _customerTenantId);

        Assert.NotNull(access);
        Assert.Equal(
            PermissionCatalog.OwnerPermissions.OrderBy(p => p, StringComparer.Ordinal),
            access.Permissions.OrderBy(p => p, StringComparer.Ordinal));

        // While acting as a customer they are not administering the platform. A token that was both
        // would let one screen quietly do the other.
        Assert.DoesNotContain(PermissionCatalog.PlatformManageBilling, access.Permissions);
        Assert.DoesNotContain(PermissionCatalog.PlatformManageOrganizations, access.Permissions);
    }

    [Fact]
    public async Task AnOrdinaryUserMayNot()
    {
        await using var context = CreateContext();
        await SeedCustomerAsync(context);

        var stranger = Guid.NewGuid();

        Assert.Null(await CreatePolicy(context).EvaluateAsync(stranger, _customerTenantId));
    }

    /// <summary>
    /// An owner of their own Organization holds plenty - just nothing that reaches outside it.
    /// </summary>
    [Fact]
    public async Task SomebodyElsesOwnerMayNot()
    {
        await using var context = CreateContext();
        await SeedCustomerAsync(context);

        var theirTenantId = Guid.NewGuid();
        var owner = Guid.NewGuid();

        await GrantAsync(
            context, owner, theirTenantId, "Owner of somewhere else",
            [.. PermissionCatalog.OwnerPermissions]);

        Assert.Null(await CreatePolicy(context).EvaluateAsync(owner, _customerTenantId));
    }

    /// <summary>
    /// The permission that gates this is the one that already allows suspending an Organization,
    /// cancelling it and sending RCON commands to its servers - not any platform permission.
    /// </summary>
    [Fact]
    public async Task AnotherPlatformPermissionIsNotEnough()
    {
        await using var context = CreateContext();
        await SeedCustomerAsync(context);

        var reporter = Guid.NewGuid();

        await GrantAsync(
            context, reporter, null, "Reports only", PermissionCatalog.PlatformViewReports);

        Assert.Null(await CreatePolicy(context).EvaluateAsync(reporter, _customerTenantId));
    }

    [Fact]
    public async Task AnOrganizationThatDoesNotExistIsRefused()
    {
        await using var context = CreateContext();
        await SeedCustomerAsync(context);

        var siteAdmin = Guid.NewGuid();

        await GrantAsync(
            context, siteAdmin, null, SiteAdminRoleSeeder.RoleName,
            PermissionCatalog.PlatformManageOrganizations);

        Assert.Null(await CreatePolicy(context).EvaluateAsync(siteAdmin, Guid.NewGuid()));
    }

    /// <summary>
    /// A revoked site admin loses this with everything else - the policy resolves from the database
    /// on every exchange rather than trusting a claim that was true when the token was minted.
    /// </summary>
    [Fact]
    public async Task RevokingTheRoleRevokesTheAccess()
    {
        await using var context = CreateContext();
        await SeedCustomerAsync(context);

        var siteAdmin = Guid.NewGuid();

        var roleId = await GrantAsync(
            context, siteAdmin, null, SiteAdminRoleSeeder.RoleName,
            PermissionCatalog.PlatformManageOrganizations);

        Assert.NotNull(await CreatePolicy(context).EvaluateAsync(siteAdmin, _customerTenantId));

        var assignment = await context.Set<UserRole>()
            .AcrossAllTenants()
            .FirstAsync(ur => ur.UserId == siteAdmin && ur.RoleId == roleId);

        context.Set<UserRole>().Remove(assignment);
        await context.SaveChangesAsync();

        Assert.Null(await CreatePolicy(context).EvaluateAsync(siteAdmin, _customerTenantId));
    }
}
