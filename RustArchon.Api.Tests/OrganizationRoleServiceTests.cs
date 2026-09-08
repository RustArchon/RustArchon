// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Authorization;
using JumpStart.Authorization.Repositories;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Api.Administration;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="OrganizationRoleService"/> - a customer defining its own roles, and the
/// things it must not be able to do while defining them.
/// </summary>
public class OrganizationRoleServiceTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _otherTenantId = Guid.NewGuid();
    private readonly Guid _ownerUserId = Guid.NewGuid();

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    /// <summary>A policy that answers yes/no for the plan gate without needing a Plan row.</summary>
    private sealed class FixedPolicy(bool canManage, IPermissionRegistry registry) : IRoleManagementPolicy
    {
        public Task<bool> CanManageRolesAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult(canManage);

        public Task<IReadOnlyCollection<string>> GrantablePermissionsAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<string>>(
                canManage
                    ? registry.All.Where(p => p.DelegableByTenantAdmin).Select(p => p.Name).ToList()
                    : []);
    }

    /// <summary>The caller, standing in for whoever is signed in.</summary>
    private sealed class FixedUser(Guid userId) : JumpStart.Repositories.IUserContext
    {
        public Task<Guid?> GetCurrentUserIdAsync() => Task.FromResult<Guid?>(userId);
    }

    private sealed class FixedTenant(Guid tenantId) : JumpStart.Repositories.ITenantContext
    {
        public Task<Guid?> GetCurrentTenantIdAsync() => Task.FromResult<Guid?>(tenantId);
    }

    private OrganizationRoleService CreateService(
        ApiDbContext context, bool canManageRoles = true, Guid? actingAs = null)
    {
        var registry = new PermissionRegistry(PermissionCatalog.All);
        var policy = new FixedPolicy(canManageRoles, registry);

        var evaluator = new DatabasePermissionEvaluator(
            new PermissionResolver(context),
            new FixedUser(actingAs ?? _ownerUserId),
            new FixedTenant(_tenantId));

        var validator = new PermissionGrantValidator(registry, policy, evaluator);
        var roles = new RoleRepository(context, null, validator);

        return new OrganizationRoleService(
            context, roles, registry, policy, evaluator,
            NullLogger<OrganizationRoleService>.Instance);
    }

    /// <summary>Gives the acting user the built-in Owner role inside the tenant.</summary>
    private async Task<Guid> SeedOwnerAsync(ApiDbContext context)
    {
        var registry = new PermissionRegistry(PermissionCatalog.All);
        var evaluator = new DatabasePermissionEvaluator(new PermissionResolver(context));
        var validator = new PermissionGrantValidator(
            registry, new PermissiveRoleManagementPolicy(registry), evaluator);

        var ownerRoleId = await BuiltInRoleSeeder.EnsureAsync(
            context, new RoleRepository(context, null, validator), NullLogger.Instance);

        context.Set<UserRole>().Add(
            new UserRole { UserId = _ownerUserId, RoleId = ownerRoleId, TenantId = _tenantId });
        await context.SaveChangesAsync();

        return ownerRoleId;
    }

    [Fact]
    public async Task CreatesARoleForTheOrganization()
    {
        await using var context = CreateContext();
        await SeedOwnerAsync(context);
        var service = CreateService(context);

        var role = await service.CreateAsync(_tenantId, "Moderator", "Restarts servers.");

        Assert.NotEqual(Guid.Empty, role.Id);
        Assert.Equal("Moderator", role.Name);
        Assert.False(role.IsBuiltIn);

        var stored = await context.Set<Role>().AcrossAllTenants().SingleAsync(r => r.Id == role.Id);
        Assert.Equal(_tenantId, stored.TenantId);
    }

    /// <summary>
    /// The tier gate. This is what makes <c>Plan.HasRoles</c> mean something after being rendered on
    /// the pricing page for months while no code read it.
    /// </summary>
    [Fact]
    public async Task RefusesToCreateARole_WhenThePlanDoesNotIncludeThem()
    {
        await using var context = CreateContext();
        await SeedOwnerAsync(context);
        var service = CreateService(context, canManageRoles: false);

        var ex = await Assert.ThrowsAsync<RoleManagementException>(
            () => service.CreateAsync(_tenantId, "Moderator", null));

        Assert.Contains("plan does not include", ex.Message);
    }

    [Fact]
    public async Task RefusesAReservedRoleName()
    {
        await using var context = CreateContext();
        await SeedOwnerAsync(context);
        var service = CreateService(context);

        var ex = await Assert.ThrowsAsync<RoleManagementException>(
            () => service.CreateAsync(_tenantId, "Owner", null));

        Assert.Contains("built-in role name", ex.Message);
    }

    [Fact]
    public async Task RefusesADuplicateNameWithinTheSameOrganization()
    {
        await using var context = CreateContext();
        await SeedOwnerAsync(context);
        var service = CreateService(context);

        await service.CreateAsync(_tenantId, "Moderator", null);

        await Assert.ThrowsAsync<RoleManagementException>(
            () => service.CreateAsync(_tenantId, "moderator", null));
    }

    /// <summary>
    /// Two Organizations may both have a "Moderator" - they are different roles. Only the global
    /// namespace is exclusive.
    /// </summary>
    [Fact]
    public async Task AllowsTheSameRoleNameInADifferentOrganization()
    {
        await using var context = CreateContext();
        await SeedOwnerAsync(context);
        var service = CreateService(context);

        await service.CreateAsync(_tenantId, "Moderator", null);
        var other = await service.CreateAsync(_otherTenantId, "Moderator", null);

        Assert.NotEqual(Guid.Empty, other.Id);
    }

    [Fact]
    public async Task RefusesToEditTheBuiltInRole()
    {
        await using var context = CreateContext();
        var ownerRoleId = await SeedOwnerAsync(context);
        var service = CreateService(context);

        var ex = await Assert.ThrowsAsync<RoleManagementException>(
            () => service.UpdateAsync(_tenantId, ownerRoleId, "Overlord", null));

        Assert.Contains("built-in role", ex.Message);
    }

    /// <summary>
    /// Reaching for another Organization's role is refused as such, not reported as "no such role" -
    /// the two are different answers and only one is honest.
    /// </summary>
    [Fact]
    public async Task RefusesToTouchAnotherOrganizationsRole()
    {
        await using var context = CreateContext();
        await SeedOwnerAsync(context);
        var service = CreateService(context);

        var theirs = await service.CreateAsync(_otherTenantId, "Their Role", null);

        var ex = await Assert.ThrowsAsync<RoleManagementException>(
            () => service.UpdateAsync(_tenantId, theirs.Id, "Mine Now", null));

        Assert.Contains("another organization", ex.Message);
    }

    [Fact]
    public async Task SetsThePermissionsARoleGrants()
    {
        await using var context = CreateContext();
        await SeedOwnerAsync(context);
        var service = CreateService(context);

        var role = await service.CreateAsync(_tenantId, "Moderator", null);

        var updated = await service.SetPermissionsAsync(
            _tenantId, role.Id, [PermissionCatalog.ServerList, PermissionCatalog.ServerSendCommand]);

        Assert.Equal(2, updated.Permissions.Count);

        var stored = await context.Set<RolePermission>()
            .Where(rp => rp.RoleId == role.Id)
            .Select(rp => rp.Permission)
            .ToListAsync();

        Assert.Contains(PermissionCatalog.ServerList, stored);
        Assert.Contains(PermissionCatalog.ServerSendCommand, stored);
    }

    /// <summary>
    /// The escalation this whole design exists to prevent: a customer building a role that grants a
    /// platform permission and handing it to themselves.
    /// </summary>
    [Fact]
    public async Task RefusesToPutAPlatformPermissionIntoACustomerRole()
    {
        await using var context = CreateContext();
        await SeedOwnerAsync(context);
        var service = CreateService(context);

        var role = await service.CreateAsync(_tenantId, "Moderator", null);

        var ex = await Assert.ThrowsAsync<RoleManagementException>(
            () => service.SetPermissionsAsync(
                _tenantId, role.Id, [PermissionCatalog.PlatformManageOrganizations]));

        Assert.Contains("cannot be granted", ex.Message);
    }

    /// <summary>
    /// Spending money is not delegable, so no role a customer builds can ever contain it - whatever
    /// their plan.
    /// </summary>
    [Fact]
    public async Task RefusesToPutBillingIntoACustomerRole()
    {
        await using var context = CreateContext();
        await SeedOwnerAsync(context);
        var service = CreateService(context);

        var role = await service.CreateAsync(_tenantId, "Moderator", null);

        await Assert.ThrowsAsync<RoleManagementException>(
            () => service.SetPermissionsAsync(
                _tenantId, role.Id, [PermissionCatalog.SubscriptionManage]));
    }

    [Fact]
    public async Task RefusesAPermissionThatDoesNotExist()
    {
        await using var context = CreateContext();
        await SeedOwnerAsync(context);
        var service = CreateService(context);

        var role = await service.CreateAsync(_tenantId, "Moderator", null);

        await Assert.ThrowsAsync<RoleManagementException>(
            () => service.SetPermissionsAsync(_tenantId, role.Id, ["RustServer.Invented"]));
    }

    /// <summary>
    /// A refused request must leave the role exactly as it was. Applying the removals first and
    /// discovering the bad addition afterwards would strip a role and then fail.
    /// </summary>
    [Fact]
    public async Task LeavesTheRoleUnchanged_WhenAnyPermissionInTheRequestIsRefused()
    {
        await using var context = CreateContext();
        await SeedOwnerAsync(context);
        var service = CreateService(context);

        var role = await service.CreateAsync(_tenantId, "Moderator", null);
        await service.SetPermissionsAsync(_tenantId, role.Id, [PermissionCatalog.ServerList]);

        await Assert.ThrowsAsync<RoleManagementException>(
            () => service.SetPermissionsAsync(
                _tenantId, role.Id,
                [PermissionCatalog.ServerGet, PermissionCatalog.PlatformManageBilling]));

        var stored = await context.Set<RolePermission>()
            .Where(rp => rp.RoleId == role.Id)
            .Select(rp => rp.Permission)
            .ToListAsync();

        var kept = Assert.Single(stored);
        Assert.Equal(PermissionCatalog.ServerList, kept);
    }

    [Fact]
    public async Task DeletingARoleRemovesItsAssignmentsAndStopsItGranting()
    {
        var memberId = Guid.NewGuid();

        await using var context = CreateContext();
        await SeedOwnerAsync(context);
        var service = CreateService(context);

        var role = await service.CreateAsync(_tenantId, "Moderator", null);
        await service.SetPermissionsAsync(_tenantId, role.Id, [PermissionCatalog.ServerSendCommand]);

        context.Set<UserRole>().Add(
            new UserRole { UserId = memberId, RoleId = role.Id, TenantId = _tenantId });
        await context.SaveChangesAsync();

        var resolver = new PermissionResolver(context);
        Assert.Contains(PermissionCatalog.ServerSendCommand, await resolver.ResolveAsync(memberId, _tenantId));

        await service.DeleteAsync(_tenantId, role.Id);

        Assert.Empty(await resolver.ResolveAsync(memberId, _tenantId));
        Assert.Empty(await context.Set<UserRole>().AcrossAllTenants()
            .Where(ur => ur.RoleId == role.Id).ToListAsync());
    }

    [Fact]
    public async Task ListsTheBuiltInRoleAlongsideTheOrganizationsOwn()
    {
        await using var context = CreateContext();
        await SeedOwnerAsync(context);
        var service = CreateService(context);

        await service.CreateAsync(_tenantId, "Moderator", null);

        var result = await service.ListAsync(_tenantId);

        Assert.True(result.PlanIncludesRoles);
        Assert.True(result.CanEditRoles); // an Owner holds Organization.ManageRoles
        Assert.Equal(2, result.Roles.Count);
        Assert.True(result.Roles[0].IsBuiltIn); // built-in first
        Assert.Equal(BuiltInRoleSeeder.OwnerRoleName, result.Roles[0].Name);
        Assert.Equal(1, result.Roles[0].MemberCount);
        Assert.False(result.Roles[1].IsBuiltIn);
    }

    /// <summary>
    /// On a plan without role separation the screen still explains itself - it reports the built-in
    /// role and an empty grantable set rather than looking broken.
    /// </summary>
    [Fact]
    public async Task ListReportsTheTierGate_WithoutHidingEverything()
    {
        await using var context = CreateContext();
        await SeedOwnerAsync(context);
        var service = CreateService(context, canManageRoles: false);

        var result = await service.ListAsync(_tenantId);

        Assert.False(result.PlanIncludesRoles);
        Assert.Empty(result.GrantablePermissions);
        Assert.Single(result.Roles);
        Assert.True(result.Roles[0].IsBuiltIn);

        // The plan gate closes the editor even for an Owner, who holds the permission - the two are
        // separate answers and the screen needs both, but neither one alone opens it.
        Assert.False(result.CanEditRoles);
    }

    /// <summary>
    /// The other gate, on its own: the plan includes roles, this member simply is not the one who
    /// defines them. They can still read the list, because handing out a role requires knowing which
    /// roles exist.
    /// </summary>
    [Fact]
    public async Task ListReportsTheCallerCannotEdit_WhenTheyLackTheRolePermission()
    {
        await using var context = CreateContext();
        await SeedOwnerAsync(context);

        // A member whose only permission is managing members.
        var coordinator = new Role { Name = "Coordinator", TenantId = _tenantId };
        context.Set<Role>().Add(coordinator);
        await context.SaveChangesAsync();

        context.Set<RolePermission>().Add(new RolePermission
        {
            RoleId = coordinator.Id,
            Permission = PermissionCatalog.OrganizationManageMembers
        });

        var memberId = Guid.NewGuid();
        context.Set<UserRole>().Add(
            new UserRole { UserId = memberId, RoleId = coordinator.Id, TenantId = _tenantId });
        await context.SaveChangesAsync();

        var result = await CreateService(context, actingAs: memberId).ListAsync(_tenantId);

        Assert.True(result.PlanIncludesRoles);
        Assert.False(result.CanEditRoles);
        Assert.NotEmpty(result.Roles);
    }

    /// <summary>
    /// The grantable list is what the editor renders, so it must never offer something the rules
    /// would then refuse.
    /// </summary>
    [Fact]
    public async Task GrantableListOffersNothingThatWouldBeRefused()
    {
        await using var context = CreateContext();
        await SeedOwnerAsync(context);
        var service = CreateService(context);

        var result = await service.ListAsync(_tenantId);

        Assert.NotEmpty(result.GrantablePermissions);
        Assert.DoesNotContain(
            result.GrantablePermissions,
            p => p.Name == PermissionCatalog.SubscriptionManage
                || p.Name.StartsWith("Platform.", StringComparison.Ordinal));
    }
}
