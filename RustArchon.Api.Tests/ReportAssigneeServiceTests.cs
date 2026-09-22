// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using JumpStart.Authorization;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Services;

namespace RustArchon.Api.Tests;

/// <summary>
/// Who a report can be assigned to: the organization's active members who hold <c>RustServer.ManageReports</c>, and nobody from
/// outside it. Assigning to someone who could not change the report's status would strand it, so the list is the permission holders,
/// not simply the members.
/// </summary>
public class ReportAssigneeServiceTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _otherTenantId = Guid.NewGuid();

    private sealed class FixedTenantContext(Guid? tenantId) : ITenantContext
    {
        public Task<Guid?> GetCurrentTenantIdAsync() => Task.FromResult(tenantId);
    }

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    private static ReportAssigneeService CreateService(ApiDbContext context, Guid? tenantId) =>
        new(context, new PermissionResolver(context), new FixedTenantContext(tenantId));

    private async Task<Guid> AddRoleAsync(ApiDbContext context, Guid tenantId, params string[] permissions)
    {
        var role = new Role { Name = $"Role {Guid.NewGuid():N}", TenantId = tenantId };
        context.Set<Role>().Add(role);
        await context.SaveChangesAsync();

        foreach (var permission in permissions)
        {
            context.Set<RolePermission>().Add(new RolePermission { RoleId = role.Id, Permission = permission });
        }

        await context.SaveChangesAsync();
        return role.Id;
    }

    private async Task<Guid> AddMemberAsync(ApiDbContext context, Guid tenantId, Guid? roleId = null, bool active = true)
    {
        var userId = Guid.NewGuid();
        context.Set<UserTenant>().Add(new UserTenant { UserId = userId, TenantId = tenantId, IsActive = active });
        if (roleId is { } role)
        {
            context.Set<UserRole>().Add(new UserRole { UserId = userId, RoleId = role, TenantId = tenantId });
        }

        await context.SaveChangesAsync();
        return userId;
    }

    [Fact]
    public async Task OnlyMembersWhoCanManageReportsAreOffered()
    {
        await using var context = CreateContext();
        var moderatorRole = await AddRoleAsync(context, _tenantId, PermissionCatalog.ServerViewReports, PermissionCatalog.ServerManageReports);
        var viewerRole = await AddRoleAsync(context, _tenantId, PermissionCatalog.ServerViewReports);
        var moderator = await AddMemberAsync(context, _tenantId, moderatorRole);
        await AddMemberAsync(context, _tenantId, viewerRole);
        await AddMemberAsync(context, _tenantId);

        var offered = await CreateService(context, _tenantId).ListAsync();

        Assert.Equal([moderator], offered);
    }

    [Fact]
    public async Task ASuspendedMemberIsNotOffered()
    {
        await using var context = CreateContext();
        var role = await AddRoleAsync(context, _tenantId, PermissionCatalog.ServerManageReports);
        await AddMemberAsync(context, _tenantId, role, active: false);
        var active = await AddMemberAsync(context, _tenantId, role);

        Assert.Equal([active], await CreateService(context, _tenantId).ListAsync());
    }

    [Fact]
    public async Task APermissionHeldInAnotherOrganizationDoesNotCount()
    {
        await using var context = CreateContext();
        var theirRole = await AddRoleAsync(context, _otherTenantId, PermissionCatalog.ServerManageReports);

        // A member of this organization whose only moderator role belongs to the other one, and a member of the other one.
        var here = Guid.NewGuid();
        context.Set<UserTenant>().Add(new UserTenant { UserId = here, TenantId = _tenantId, IsActive = true });
        context.Set<UserRole>().Add(new UserRole { UserId = here, RoleId = theirRole, TenantId = _otherTenantId });
        await context.SaveChangesAsync();
        await AddMemberAsync(context, _otherTenantId, theirRole);

        Assert.Empty(await CreateService(context, _tenantId).ListAsync());
    }

    [Fact]
    public async Task WithNoCurrentOrganizationNobodyIsOfferedAndNobodyCanBeAssigned()
    {
        await using var context = CreateContext();
        var role = await AddRoleAsync(context, _tenantId, PermissionCatalog.ServerManageReports);
        var member = await AddMemberAsync(context, _tenantId, role);
        var service = CreateService(context, tenantId: null);

        Assert.Empty(await service.ListAsync());
        Assert.False(await service.CanBeAssignedAsync(member));
    }

    [Fact]
    public async Task AMemberWhoCanManageReportsCanBeAssigned()
    {
        await using var context = CreateContext();
        var role = await AddRoleAsync(context, _tenantId, PermissionCatalog.ServerManageReports);
        var member = await AddMemberAsync(context, _tenantId, role);

        Assert.True(await CreateService(context, _tenantId).CanBeAssignedAsync(member));
    }

    [Fact]
    public async Task ThePeopleWhoCannotBeAssignedAreRefused()
    {
        await using var context = CreateContext();
        var manage = await AddRoleAsync(context, _tenantId, PermissionCatalog.ServerManageReports);
        var viewOnly = await AddRoleAsync(context, _tenantId, PermissionCatalog.ServerViewReports);
        var service = CreateService(context, _tenantId);

        var viewer = await AddMemberAsync(context, _tenantId, viewOnly);
        var suspended = await AddMemberAsync(context, _tenantId, manage, active: false);
        var outsider = await AddMemberAsync(context, _otherTenantId, await AddRoleAsync(context, _otherTenantId, PermissionCatalog.ServerManageReports));

        Assert.False(await service.CanBeAssignedAsync(viewer));
        Assert.False(await service.CanBeAssignedAsync(suspended));
        Assert.False(await service.CanBeAssignedAsync(outsider));
        Assert.False(await service.CanBeAssignedAsync(Guid.NewGuid()));
    }
}
