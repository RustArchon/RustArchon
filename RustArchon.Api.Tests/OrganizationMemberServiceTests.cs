// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Authorization;
using JumpStart.Authorization.Repositories;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Api.Administration;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="OrganizationMemberService"/> - an Organization managing its own people.
/// </summary>
/// <remarks>
/// The two properties worth the most here are the ones that separate this from the site-admin path:
/// a grant is refused unless the caller holds everything the role contains, and the Organization can
/// never be left without an owner.
/// </remarks>
public class OrganizationMemberServiceTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _otherTenantId = Guid.NewGuid();

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    /// <summary>
    /// The service as one particular member sees it: <paramref name="callerId"/> is the grantor whose
    /// own permissions JumpStart's assignment rule measures a grant against.
    /// </summary>
    private OrganizationMemberService CreateService(ApiDbContext context, Guid callerId)
    {
        var resolver = new PermissionResolver(context);
        var registry = new PermissionRegistry(PermissionCatalog.All);
        var userContext = new FixedUserContext(callerId);
        var tenantContext = new FixedTenantContext(_tenantId);

        var validator = new PermissionGrantValidator(
            registry,
            new PermissiveRoleManagementPolicy(registry),
            new DatabasePermissionEvaluator(resolver, userContext, tenantContext));

        return new OrganizationMemberService(
            context,
            new RoleRepository(context, null, validator),
            userContext,
            NullLogger<OrganizationMemberService>.Instance);
    }

    private static async Task<Guid> SeedOwnerRoleAsync(ApiDbContext context)
    {
        var registry = new PermissionRegistry(PermissionCatalog.All);
        var validator = new PermissionGrantValidator(
            registry,
            new PermissiveRoleManagementPolicy(registry),
            new DatabasePermissionEvaluator(new PermissionResolver(context)));

        return await BuiltInRoleSeeder.EnsureAsync(
            context, new RoleRepository(context, null, validator), NullLogger.Instance);
    }

    private async Task<Guid> AddMemberAsync(
        ApiDbContext context, Guid? roleId = null, bool active = true)
    {
        var userId = Guid.NewGuid();

        context.Set<UserTenant>().Add(
            new UserTenant { UserId = userId, TenantId = _tenantId, IsActive = active });

        if (roleId is { } role)
        {
            context.Set<UserRole>().Add(
                new UserRole { UserId = userId, RoleId = role, TenantId = _tenantId });
        }

        await context.SaveChangesAsync();
        return userId;
    }

    private async Task<Guid> AddRoleAsync(
        ApiDbContext context, string name, Guid? tenantId, params string[] permissions)
    {
        var role = new Role { Name = name, TenantId = tenantId };
        context.Set<Role>().Add(role);
        await context.SaveChangesAsync();

        foreach (var permission in permissions)
        {
            context.Set<RolePermission>().Add(
                new RolePermission { RoleId = role.Id, Permission = permission });
        }

        await context.SaveChangesAsync();
        return role.Id;
    }

    [Fact]
    public async Task ListsMembersWithTheRolesTheyHoldHere()
    {
        await using var context = CreateContext();
        var ownerRoleId = await SeedOwnerRoleAsync(context);
        var moderatorId = await AddRoleAsync(
            context, "Moderator", _tenantId, PermissionCatalog.ServerSendCommand);

        var owner = await AddMemberAsync(context, ownerRoleId);
        var moderator = await AddMemberAsync(context, moderatorId);

        var members = await CreateService(context, owner).ListAsync(_tenantId);

        Assert.Equal(2, members.Count);

        var listed = members.Single(m => m.UserId == moderator);
        Assert.Equal(["Moderator"], listed.Roles);
        Assert.Equal([moderatorId], listed.RoleIds);

        // The built-in Owner is a global role whose grant carries the tenant - it has to show up here
        // or the members screen reports the owner as holding nothing.
        Assert.Equal(
            [BuiltInRoleSeeder.OwnerRoleName],
            members.Single(m => m.UserId == owner).Roles);
    }

    /// <summary>
    /// The rule that makes this safe to expose to customers at all: whoever can manage members cannot
    /// use that to award permissions they do not have.
    /// </summary>
    [Fact]
    public async Task RefusesToGrantARoleTheCallerDoesNotFullyHold()
    {
        await using var context = CreateContext();
        var ownerRoleId = await SeedOwnerRoleAsync(context);

        // A member who can manage members and nothing else.
        var limitedRoleId = await AddRoleAsync(
            context, "Coordinator", _tenantId, PermissionCatalog.OrganizationManageMembers);

        var caller = await AddMemberAsync(context, limitedRoleId);
        var target = await AddMemberAsync(context);

        await Assert.ThrowsAsync<PermissionGrantException>(
            () => CreateService(context, caller).AssignRoleAsync(_tenantId, target, ownerRoleId));

        Assert.Empty(await context.Set<UserRole>().AcrossAllTenants()
            .Where(ur => ur.UserId == target).ToListAsync());
    }

    [Fact]
    public async Task GrantsARoleTheCallerDoesHold()
    {
        await using var context = CreateContext();
        var ownerRoleId = await SeedOwnerRoleAsync(context);
        var moderatorId = await AddRoleAsync(
            context, "Moderator", _tenantId, PermissionCatalog.ServerSendCommand);

        var owner = await AddMemberAsync(context, ownerRoleId);
        var target = await AddMemberAsync(context);

        await CreateService(context, owner).AssignRoleAsync(_tenantId, target, moderatorId);

        Assert.Contains(
            PermissionCatalog.ServerSendCommand,
            await new PermissionResolver(context).ResolveAsync(target, _tenantId));
    }

    [Fact]
    public async Task RefusesARoleBelongingToAnotherOrganization()
    {
        await using var context = CreateContext();
        var ownerRoleId = await SeedOwnerRoleAsync(context);
        var theirs = await AddRoleAsync(context, "Moderator", _otherTenantId);

        var owner = await AddMemberAsync(context, ownerRoleId);
        var target = await AddMemberAsync(context);

        var refused = await Assert.ThrowsAsync<MemberManagementException>(
            () => CreateService(context, owner).AssignRoleAsync(_tenantId, target, theirs));

        Assert.Contains("can grant", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Site Admin is global like the built-in Owner, so "is it a global role?" is not the test - the
    /// service names the one global role an Organization may hand out.
    /// </summary>
    [Fact]
    public async Task RefusesTheSiteAdminRole()
    {
        await using var context = CreateContext();
        var ownerRoleId = await SeedOwnerRoleAsync(context);
        var siteAdminId = await AddRoleAsync(
            context, SiteAdminRoleSeeder.RoleName, null, PermissionCatalog.PlatformManageOrganizations);

        var owner = await AddMemberAsync(context, ownerRoleId);
        var target = await AddMemberAsync(context);

        await Assert.ThrowsAsync<MemberManagementException>(
            () => CreateService(context, owner).AssignRoleAsync(_tenantId, target, siteAdminId));
    }

    [Fact]
    public async Task RefusesSomebodyWhoIsNotAMember()
    {
        await using var context = CreateContext();
        var ownerRoleId = await SeedOwnerRoleAsync(context);
        var owner = await AddMemberAsync(context, ownerRoleId);

        await Assert.ThrowsAsync<MemberManagementException>(
            () => CreateService(context, owner)
                .AssignRoleAsync(_tenantId, Guid.NewGuid(), ownerRoleId));
    }

    [Fact]
    public async Task RefusesToRevokeOwnerFromTheLastOwner()
    {
        await using var context = CreateContext();
        var ownerRoleId = await SeedOwnerRoleAsync(context);
        var owner = await AddMemberAsync(context, ownerRoleId);
        await AddMemberAsync(context);

        var refused = await Assert.ThrowsAsync<MemberManagementException>(
            () => CreateService(context, owner).UnassignRoleAsync(_tenantId, owner, ownerRoleId));

        Assert.Contains("own this organization", refused.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            PermissionCatalog.SubscriptionManage,
            await new PermissionResolver(context).ResolveAsync(owner, _tenantId));
    }

    [Fact]
    public async Task AllowsRevokingOwnerWhileAnotherOwnerRemains()
    {
        await using var context = CreateContext();
        var ownerRoleId = await SeedOwnerRoleAsync(context);
        var first = await AddMemberAsync(context, ownerRoleId);
        var second = await AddMemberAsync(context, ownerRoleId);

        await CreateService(context, first).UnassignRoleAsync(_tenantId, second, ownerRoleId);

        Assert.DoesNotContain(
            PermissionCatalog.SubscriptionManage,
            await new PermissionResolver(context).ResolveAsync(second, _tenantId));
    }

    /// <summary>
    /// A suspended owner cannot sign in, so they are not an owner for this purpose - suspending the
    /// last active one has to refuse for the same reason revoking the role does.
    /// </summary>
    [Fact]
    public async Task RefusesToSuspendTheLastActiveOwner()
    {
        await using var context = CreateContext();
        var ownerRoleId = await SeedOwnerRoleAsync(context);
        var owner = await AddMemberAsync(context, ownerRoleId);
        await AddMemberAsync(context, ownerRoleId, active: false);

        await Assert.ThrowsAsync<MemberManagementException>(
            () => CreateService(context, owner).SetActiveAsync(_tenantId, owner, active: false));

        Assert.True(await context.Set<UserTenant>()
            .Where(ut => ut.UserId == owner).Select(ut => ut.IsActive).SingleAsync());
    }

    [Fact]
    public async Task SuspendsANonOwnerFreely()
    {
        await using var context = CreateContext();
        var ownerRoleId = await SeedOwnerRoleAsync(context);
        var owner = await AddMemberAsync(context, ownerRoleId);
        var member = await AddMemberAsync(context);

        await CreateService(context, owner).SetActiveAsync(_tenantId, member, active: false);

        Assert.False(await context.Set<UserTenant>()
            .Where(ut => ut.UserId == member).Select(ut => ut.IsActive).SingleAsync());
    }

    /// <summary>
    /// The rule this covers is unconditional, unlike the last-owner one above: even with a second
    /// active owner in the organization, self-suspension still refuses.
    /// </summary>
    [Fact]
    public async Task RefusesToSuspendYourself_EvenWithAnotherOwnerPresent()
    {
        await using var context = CreateContext();
        var ownerRoleId = await SeedOwnerRoleAsync(context);
        var first = await AddMemberAsync(context, ownerRoleId);
        await AddMemberAsync(context, ownerRoleId);

        var refused = await Assert.ThrowsAsync<MemberManagementException>(
            () => CreateService(context, first).SetActiveAsync(_tenantId, first, active: false));

        Assert.Contains("own access", refused.Message, StringComparison.OrdinalIgnoreCase);

        Assert.True(await context.Set<UserTenant>()
            .Where(ut => ut.UserId == first).Select(ut => ut.IsActive).SingleAsync());
    }

    /// <summary>Restoring your own access is never dangerous, so it is not gated by the self-check.</summary>
    [Fact]
    public async Task RestoringYourselfIsNotRefused()
    {
        await using var context = CreateContext();
        var ownerRoleId = await SeedOwnerRoleAsync(context);
        var owner = await AddMemberAsync(context, ownerRoleId, active: false);

        await CreateService(context, owner).SetActiveAsync(_tenantId, owner, active: true);

        Assert.True(await context.Set<UserTenant>()
            .Where(ut => ut.UserId == owner).Select(ut => ut.IsActive).SingleAsync());
    }

    [Fact]
    public async Task RefusesToRemoveTheLastOwner()
    {
        await using var context = CreateContext();
        var ownerRoleId = await SeedOwnerRoleAsync(context);
        var owner = await AddMemberAsync(context, ownerRoleId);

        await Assert.ThrowsAsync<MemberManagementException>(
            () => CreateService(context, owner).RemoveAsync(_tenantId, owner));

        Assert.True(await context.Set<UserTenant>().AnyAsync(ut => ut.UserId == owner));
    }

    /// <summary>
    /// Unconditional, exactly like <see cref="RefusesToSuspendYourself_EvenWithAnotherOwnerPresent"/>:
    /// a second active owner does not make removing yourself through self-service acceptable.
    /// </summary>
    [Fact]
    public async Task RefusesToRemoveYourself_EvenWithAnotherOwnerPresent()
    {
        await using var context = CreateContext();
        var ownerRoleId = await SeedOwnerRoleAsync(context);
        var first = await AddMemberAsync(context, ownerRoleId);
        await AddMemberAsync(context, ownerRoleId);

        var refused = await Assert.ThrowsAsync<MemberManagementException>(
            () => CreateService(context, first).RemoveAsync(_tenantId, first));

        Assert.Contains("remove yourself", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(await context.Set<UserTenant>().AnyAsync(ut => ut.UserId == first));
    }

    /// <summary>
    /// Removing somebody takes their role grants with them: a person added back later must not
    /// silently regain permissions nobody granted them a second time.
    /// </summary>
    [Fact]
    public async Task RemovingAMemberDropsTheirRoleGrants()
    {
        await using var context = CreateContext();
        var ownerRoleId = await SeedOwnerRoleAsync(context);
        var moderatorId = await AddRoleAsync(
            context, "Moderator", _tenantId, PermissionCatalog.ServerSendCommand);

        var owner = await AddMemberAsync(context, ownerRoleId);
        var member = await AddMemberAsync(context, moderatorId);

        await CreateService(context, owner).RemoveAsync(_tenantId, member);

        Assert.False(await context.Set<UserTenant>().AnyAsync(ut => ut.UserId == member));
        Assert.Empty(await context.Set<UserRole>().AcrossAllTenants()
            .Where(ur => ur.UserId == member).ToListAsync());
    }
}

/// <summary>Stands in for the signed-in caller, whose permissions gate every grant.</summary>
file class FixedUserContext(Guid userId) : IUserContext
{
    public Task<Guid?> GetCurrentUserIdAsync() => Task.FromResult<Guid?>(userId);
}

/// <summary>Stands in for the tenant the caller is acting inside.</summary>
file class FixedTenantContext(Guid tenantId) : ITenantContext
{
    public Task<Guid?> GetCurrentTenantIdAsync() => Task.FromResult<Guid?>(tenantId);
}
