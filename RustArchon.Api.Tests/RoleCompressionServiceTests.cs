// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
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
/// Tests for <see cref="RoleCompressionService"/> - collapsing an Organization onto the built-in
/// Owner role when it downgrades to a plan without role separation.
/// </summary>
public class RoleCompressionServiceTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _otherTenantId = Guid.NewGuid();

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    private static RoleCompressionService CreateService(ApiDbContext context) =>
        new(context, NullLogger<RoleCompressionService>.Instance);

    private static async Task<Guid> SeedOwnerRoleAsync(ApiDbContext context)
    {
        var registry = new PermissionRegistry(PermissionCatalog.All);
        var evaluator = new DatabasePermissionEvaluator(new PermissionResolver(context));
        var validator = new PermissionGrantValidator(
            registry, new PermissiveRoleManagementPolicy(registry), evaluator);

        return await BuiltInRoleSeeder.EnsureAsync(
            context, new RoleRepository(context, null, validator), NullLogger.Instance);
    }

    /// <summary>Adds a member to the Organization, optionally holding a role.</summary>
    private async Task<Guid> AddMemberAsync(ApiDbContext context, Guid? roleId = null)
    {
        var userId = Guid.NewGuid();

        context.Set<UserTenant>().Add(new UserTenant { UserId = userId, TenantId = _tenantId });

        if (roleId is { } role)
        {
            context.Set<UserRole>().Add(
                new UserRole { UserId = userId, RoleId = role, TenantId = _tenantId });
        }

        await context.SaveChangesAsync();
        return userId;
    }

    private async Task<Guid> AddCustomRoleAsync(ApiDbContext context, string name, string permission)
    {
        var role = new Role { Name = name, TenantId = _tenantId };
        context.Set<Role>().Add(role);
        await context.SaveChangesAsync();

        context.Set<RolePermission>().Add(new RolePermission { RoleId = role.Id, Permission = permission });
        await context.SaveChangesAsync();

        return role.Id;
    }

    [Fact]
    public async Task PromotesEveryMemberAndRetiresTheOrganizationsOwnRoles()
    {
        await using var context = CreateContext();
        var ownerRoleId = await SeedOwnerRoleAsync(context);

        var moderatorRoleId = await AddCustomRoleAsync(context, "Moderator", PermissionCatalog.ServerSendCommand);
        var founder = await AddMemberAsync(context, ownerRoleId);
        var moderator = await AddMemberAsync(context, moderatorRoleId);
        var plainMember = await AddMemberAsync(context);

        var result = await CreateService(context).CompressAsync(_tenantId, "downgrade");

        // The founder already had Owner, so only the other two are promoted.
        Assert.Equal(2, result.MembersPromoted);
        Assert.Equal(1, result.RolesRemoved);

        var resolver = new PermissionResolver(context);

        foreach (var userId in new[] { founder, moderator, plainMember })
        {
            var held = await resolver.ResolveAsync(userId, _tenantId);
            Assert.Contains(PermissionCatalog.SubscriptionManage, held);
        }

        // The custom role is retired and grants nothing.
        var retired = await context.Set<Role>().IgnoreQueryFilters()
            .SingleAsync(r => r.Id == moderatorRoleId);
        Assert.NotNull(retired.DeletedOn);

        Assert.Empty(await context.Set<UserRole>().AcrossAllTenants()
            .Where(ur => ur.RoleId == moderatorRoleId).ToListAsync());
    }

    /// <summary>
    /// The built-in role is not the Organization's to remove - one definition serves every
    /// Organization, so retiring it here would retire it everywhere.
    /// </summary>
    [Fact]
    public async Task NeverRetiresTheBuiltInRole()
    {
        await using var context = CreateContext();
        var ownerRoleId = await SeedOwnerRoleAsync(context);
        await AddCustomRoleAsync(context, "Moderator", PermissionCatalog.ServerList);
        await AddMemberAsync(context);

        await CreateService(context).CompressAsync(_tenantId, "downgrade");

        var owner = await context.Set<Role>().AcrossAllTenants().SingleAsync(r => r.Id == ownerRoleId);
        Assert.Null(owner.DeletedOn);
    }

    [Fact]
    public async Task LeavesAnotherOrganizationsRolesAlone()
    {
        await using var context = CreateContext();
        await SeedOwnerRoleAsync(context);
        await AddCustomRoleAsync(context, "Moderator", PermissionCatalog.ServerList);
        await AddMemberAsync(context);

        var theirs = new Role { Name = "Moderator", TenantId = _otherTenantId };
        context.Set<Role>().Add(theirs);
        await context.SaveChangesAsync();

        await CreateService(context).CompressAsync(_tenantId, "downgrade");

        var stillThere = await context.Set<Role>().AcrossAllTenants().SingleAsync(r => r.Id == theirs.Id);
        Assert.Null(stillThere.DeletedOn);
    }

    [Fact]
    public async Task DoesNothing_WhenTheOrganizationHasNoRolesOfItsOwn()
    {
        await using var context = CreateContext();
        var ownerRoleId = await SeedOwnerRoleAsync(context);
        await AddMemberAsync(context, ownerRoleId);
        await AddMemberAsync(context);

        var result = await CreateService(context).CompressAsync(_tenantId, "downgrade");

        Assert.False(result.IsNeeded);
        Assert.Equal(0, result.MembersPromoted);

        // Nobody was promoted, so the plain member is still just a member.
        Assert.Equal(1, await context.Set<UserRole>().AcrossAllTenants()
            .CountAsync(ur => ur.TenantId == _tenantId));
    }

    [Fact]
    public async Task IsIdempotent()
    {
        await using var context = CreateContext();
        var ownerRoleId = await SeedOwnerRoleAsync(context);
        await AddCustomRoleAsync(context, "Moderator", PermissionCatalog.ServerList);
        await AddMemberAsync(context, ownerRoleId);
        await AddMemberAsync(context);

        await CreateService(context).CompressAsync(_tenantId, "downgrade");
        var second = await CreateService(context).CompressAsync(_tenantId, "downgrade again");

        Assert.False(second.IsNeeded);

        // Two members, one Owner assignment each - not three, and not duplicated.
        Assert.Equal(2, await context.Set<UserRole>().AcrossAllTenants()
            .CountAsync(ur => ur.TenantId == _tenantId && ur.RoleId == ownerRoleId));
    }

    /// <summary>
    /// The property that is easiest to get wrong: a downgrade is deferred to the end of the billing
    /// period, so somebody who joins between accepting it and it landing must still be promoted.
    /// A stored list computed at acceptance time would miss them.
    /// </summary>
    [Fact]
    public async Task PromotesMembersWhoJoinedAfterTheDowngradeWasAccepted()
    {
        await using var context = CreateContext();
        var ownerRoleId = await SeedOwnerRoleAsync(context);
        await AddCustomRoleAsync(context, "Moderator", PermissionCatalog.ServerList);
        await AddMemberAsync(context, ownerRoleId);

        var service = CreateService(context);

        // What the customer was shown when they accepted.
        var preview = await service.PreviewAsync(_tenantId);
        Assert.Equal(0, preview.MembersPromoted);

        // ...and then somebody joined, weeks before the change actually lands.
        var latecomer = await AddMemberAsync(context);

        var result = await service.CompressAsync(_tenantId, "downgrade");

        Assert.Equal(1, result.MembersPromoted);
        Assert.Contains(
            PermissionCatalog.SubscriptionManage,
            await new PermissionResolver(context).ResolveAsync(latecomer, _tenantId));
    }

    [Fact]
    public async Task PreviewReportsWhatWouldHappen_WithoutDoingIt()
    {
        await using var context = CreateContext();
        var moderatorRoleId = await AddCustomRoleAsync(context, "Moderator", PermissionCatalog.ServerList);
        await SeedOwnerRoleAsync(context);
        await AddMemberAsync(context, moderatorRoleId);

        var preview = await CreateService(context).PreviewAsync(_tenantId);

        Assert.True(preview.IsNeeded);
        Assert.Equal(1, preview.MembersPromoted);
        Assert.Equal(1, preview.RolesRemoved);

        // Nothing changed.
        var role = await context.Set<Role>().AcrossAllTenants().SingleAsync(r => r.Id == moderatorRoleId);
        Assert.Null(role.DeletedOn);
    }
}
