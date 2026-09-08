// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Authorization;
using JumpStart.Authorization.Repositories;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="BuiltInRoleSeeder"/>: one shared Owner definition, granted inside each
/// Organization, and the migration of the per-tenant copies that preceded it.
/// </summary>
public class BuiltInRoleSeederTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    /// <summary>
    /// The seeder grants through <see cref="IRoleRepository"/> so JumpStart's rules still apply, and
    /// a validator needs a registry - which is <see cref="PermissionCatalog"/> itself, so a typo
    /// there fails these tests the same way it would fail startup.
    /// </summary>
    private static RoleRepository CreateRepository(ApiDbContext context)
    {
        var registry = new PermissionRegistry(PermissionCatalog.All);
        var evaluator = new DatabasePermissionEvaluator(new PermissionResolver(context));
        var validator = new PermissionGrantValidator(
            registry, new PermissiveRoleManagementPolicy(registry), evaluator);

        return new RoleRepository(context, null, validator);
    }

    private static Task<Guid> SeedAsync(ApiDbContext context) =>
        BuiltInRoleSeeder.EnsureAsync(context, CreateRepository(context), NullLogger.Instance);

    [Fact]
    public async Task CreatesOneGlobalOwnerRoleHoldingEveryOwnerPermission()
    {
        await using var context = CreateContext();

        var ownerRoleId = await SeedAsync(context);

        var role = await context.Set<Role>().AcrossAllTenants().SingleAsync(r => r.Id == ownerRoleId);
        Assert.Null(role.TenantId); // global: the grant carries the tenant, not the definition
        Assert.Equal(BuiltInRoleSeeder.OwnerRoleName, role.Name);

        var granted = await context.Set<RolePermission>()
            .Where(rp => rp.RoleId == ownerRoleId)
            .Select(rp => rp.Permission)
            .ToListAsync();

        Assert.Equal(
            PermissionCatalog.OwnerPermissions.OrderBy(p => p),
            granted.OrderBy(p => p));
    }

    [Fact]
    public async Task IsIdempotent_AndDoesNotDuplicateTheRoleOrItsGrants()
    {
        await using var context = CreateContext();

        var first = await SeedAsync(context);
        var second = await SeedAsync(context);

        Assert.Equal(first, second);

        var roles = await context.Set<Role>().AcrossAllTenants()
            .CountAsync(r => r.Name == BuiltInRoleSeeder.OwnerRoleName);
        Assert.Equal(1, roles);

        var grants = await context.Set<RolePermission>().CountAsync(rp => rp.RoleId == first);
        Assert.Equal(PermissionCatalog.OwnerPermissions.Length, grants);
    }

    /// <summary>
    /// The point of one shared definition: a capability added to the catalog reaches every
    /// Organization's Owner on the next start, rather than needing a row inserted per tenant.
    /// </summary>
    [Fact]
    public async Task TopsUpPermissionsAddedToTheCatalogLater()
    {
        await using var context = CreateContext();

        var ownerRoleId = await SeedAsync(context);

        // Simulate a capability that predates a catalog addition by removing one grant.
        var dropped = await context.Set<RolePermission>()
            .FirstAsync(rp => rp.RoleId == ownerRoleId && rp.Permission == PermissionCatalog.ServerSendCommand);
        context.Set<RolePermission>().Remove(dropped);
        await context.SaveChangesAsync();

        await SeedAsync(context);

        Assert.True(await context.Set<RolePermission>()
            .AnyAsync(rp => rp.RoleId == ownerRoleId && rp.Permission == PermissionCatalog.ServerSendCommand));
    }

    /// <summary>
    /// The migration off the old shape: sign-up used to mint a private Owner role per Organization.
    /// </summary>
    [Fact]
    public async Task MigratesAPerTenantOwnerRoleOntoTheBuiltInOne()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var context = CreateContext();

        var legacy = new Role { Name = BuiltInRoleSeeder.OwnerRoleName, TenantId = tenantId };
        context.Set<Role>().Add(legacy);
        await context.SaveChangesAsync();

        context.Set<RolePermission>().Add(
            new RolePermission { RoleId = legacy.Id, Permission = PermissionCatalog.ServerGet });
        context.Set<UserRole>().Add(
            new UserRole { UserId = userId, RoleId = legacy.Id, TenantId = tenantId });
        await context.SaveChangesAsync();

        var ownerRoleId = await SeedAsync(context);

        // The user now holds the built-in role, inside the same Organization.
        var assignments = await context.Set<UserRole>().AcrossAllTenants()
            .Where(ur => ur.UserId == userId)
            .ToListAsync();

        var assignment = Assert.Single(assignments);
        Assert.Equal(ownerRoleId, assignment.RoleId);
        Assert.Equal(tenantId, assignment.TenantId);

        // The old role is retired rather than erased, so the audit trail survives.
        var retired = await context.Set<Role>().AcrossAllTenants().IgnoringDeletedCheck(legacy.Id);
        Assert.NotNull(retired.DeletedOn);
    }

    /// <summary>
    /// Retiring the old role has to actually revoke it. This only holds because ADR-017 made
    /// permission resolution join through <c>Role</c> - before that, a soft-deleted role vanished
    /// from every screen while its grants kept resolving indefinitely.
    /// </summary>
    [Fact]
    public async Task TheRetiredPerTenantRoleGrantsNothingAfterwards()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var context = CreateContext();

        var legacy = new Role { Name = BuiltInRoleSeeder.OwnerRoleName, TenantId = tenantId };
        context.Set<Role>().Add(legacy);
        await context.SaveChangesAsync();

        // A permission the built-in Owner does NOT hold, so its presence afterwards could only come
        // from the retired role.
        context.Set<RolePermission>().Add(
            new RolePermission { RoleId = legacy.Id, Permission = "RustServer.LegacyOnly" });
        context.Set<UserRole>().Add(
            new UserRole { UserId = userId, RoleId = legacy.Id, TenantId = tenantId });
        await context.SaveChangesAsync();

        await SeedAsync(context);

        var held = await new PermissionResolver(context).ResolveAsync(userId, tenantId);

        Assert.DoesNotContain("RustServer.LegacyOnly", held);
        Assert.Contains(PermissionCatalog.ServerGet, held); // ...but they are a real Owner now
    }

    [Fact]
    public async Task MigrationIsIdempotent_WhenRunTwice()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var context = CreateContext();

        var legacy = new Role { Name = BuiltInRoleSeeder.OwnerRoleName, TenantId = tenantId };
        context.Set<Role>().Add(legacy);
        await context.SaveChangesAsync();
        context.Set<UserRole>().Add(
            new UserRole { UserId = userId, RoleId = legacy.Id, TenantId = tenantId });
        await context.SaveChangesAsync();

        await SeedAsync(context);
        await SeedAsync(context);

        var assignments = await context.Set<UserRole>().AcrossAllTenants()
            .CountAsync(ur => ur.UserId == userId);

        Assert.Equal(1, assignments);
    }

    [Fact]
    public void ReservesTheBuiltInRoleNames()
    {
        Assert.Contains(BuiltInRoleSeeder.OwnerRoleName, BuiltInRoleSeeder.ReservedRoleNames);
        Assert.Contains(SiteAdminRoleSeeder.RoleName, BuiltInRoleSeeder.ReservedRoleNames);
    }
}

file static class TestQueryExtensions
{
    /// <summary>Reads a role including soft-deleted ones, for asserting that it was retired.</summary>
    public static async Task<Role> IgnoringDeletedCheck(this IQueryable<Role> source, Guid id) =>
        await source.IgnoreQueryFilters().SingleAsync(r => r.Id == id);
}
