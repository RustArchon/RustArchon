// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using JumpStart.Authorization;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.IntegrationTests;

/// <summary>
/// That no permission outside <see cref="PermissionCatalog"/> can be written by any route a customer
/// can reach.
/// </summary>
/// <remarks>
/// <para>
/// The registry is the closed set the whole design rests on: if an arbitrary string could be stored
/// as a permission, then a role could be made to grant something nobody declared, and every
/// reasoning-about-permissions argument elsewhere stops holding. The unit tests prove the repository
/// refuses it; this proves there is no way in through the front door either.
/// </para>
/// <para>
/// Note what the caller here holds. The endpoint's own guard reads a claim from the token, but
/// JumpStart's fourth grant rule - you cannot give away what you do not have - is resolved from the
/// database, deliberately, because a token is a snapshot. So this fixture puts the user in the
/// Organization with the built-in Owner role as well as minting a token: a test that only did the
/// latter would be refused for the wrong reason and prove nothing about the registry.
/// </para>
/// </remarks>
public class PermissionRegistryTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>An Organization on a plan that includes roles, with an Owner in it.</summary>
    private async Task<(Guid TenantId, Guid UserId)> SeedOrganizationWithOwnerAsync()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await factory.WithDatabaseAsync(async db =>
        {
            var plan = await db.Set<Plan>().FirstAsync(p => p.HasRoles);

            db.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = "Registry test", IsActive = true });
            await db.SaveChangesAsync();

            db.Set<Subscription>().Add(new Subscription
            {
                TenantId = tenantId,
                PlanId = plan.Id,
                StartDate = DateTimeOffset.UtcNow.AddMonths(-1)
            });

            db.Set<UserTenant>().Add(new UserTenant { UserId = userId, TenantId = tenantId, IsActive = true });
            await db.SaveChangesAsync();

            var ownerRoleId = await db.Set<Role>()
                .AcrossAllTenants()
                .Where(r => r.TenantId == null && r.Name == BuiltInRoleSeeder.OwnerRoleName)
                .Select(r => r.Id)
                .FirstAsync();

            db.Set<UserRole>().Add(
                new UserRole { UserId = userId, RoleId = ownerRoleId, TenantId = tenantId });

            await db.SaveChangesAsync();
        });

        return (tenantId, userId);
    }

    private HttpClient CreateOwnerClient(Guid tenantId, Guid userId) =>
        factory.CreateClient().Authenticate(
            userId,
            tenantId,
            PermissionCatalog.OrganizationManageRoles,
            PermissionCatalog.OrganizationManageMembers);

    private static async Task<Guid> CreateRoleAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync(
            new Uri("/api/organization/roles", UriKind.Relative),
            new SaveRoleRequestDto { Name = name });

        response.EnsureSuccessStatusCode();

        var created = await response.Content.ReadFromJsonAsync<OrganizationRoleDto>();
        return created!.Id;
    }

    private static Task<HttpResponseMessage> SetPermissionsAsync(
        HttpClient client, Guid roleId, params string[] permissions) =>
        client.PutAsJsonAsync(
            new Uri($"/api/organization/roles/{roleId}/permissions", UriKind.Relative),
            new SetRolePermissionsRequestDto { Permissions = [.. permissions] });

    [Fact]
    public async Task APermissionThatIsNotInTheCatalogIsRefusedAndNotStored()
    {
        var (tenantId, userId) = await SeedOrganizationWithOwnerAsync();
        using var client = CreateOwnerClient(tenantId, userId);

        var roleId = await CreateRoleAsync(client, "Invented permission");

        const string invented = "RustServer.BecomeAdministrator";

        var response = await SetPermissionsAsync(client, roleId, invented);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var stored = await factory.FromDatabaseAsync(db => db.Set<RolePermission>()
            .AnyAsync(rp => rp.Permission == invented));

        Assert.False(stored, "An undeclared permission reached the database.");
    }

    /// <summary>
    /// Platform permissions are declared, so the registry alone would admit them - what refuses this
    /// is the scope and delegability on the descriptor. Worth its own test because it is the grant
    /// that would actually be worth attempting.
    /// </summary>
    [Fact]
    public async Task APlatformPermissionCannotBePutIntoAnOrganizationRole()
    {
        var (tenantId, userId) = await SeedOrganizationWithOwnerAsync();
        using var client = CreateOwnerClient(tenantId, userId);

        var roleId = await CreateRoleAsync(client, "Reaching for the platform");

        var response = await SetPermissionsAsync(
            client, roleId, PermissionCatalog.PlatformManageOrganizations);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var stored = await factory.FromDatabaseAsync(db => db.Set<RolePermission>()
            .AnyAsync(rp => rp.RoleId == roleId
                && rp.Permission == PermissionCatalog.PlatformManageOrganizations));

        Assert.False(stored);
    }

    /// <summary>
    /// A refused request leaves the role exactly as it was, rather than applying the acceptable half.
    /// A role holding a set nobody asked for is worse than one that did not change.
    /// </summary>
    [Fact]
    public async Task AMixedRequestIsRefusedWholesale()
    {
        var (tenantId, userId) = await SeedOrganizationWithOwnerAsync();
        using var client = CreateOwnerClient(tenantId, userId);

        var roleId = await CreateRoleAsync(client, "Half legitimate");

        var response = await SetPermissionsAsync(
            client, roleId, PermissionCatalog.ServerSendCommand, "RustServer.Invented");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var granted = await factory.FromDatabaseAsync(db => db.Set<RolePermission>()
            .CountAsync(rp => rp.RoleId == roleId));

        Assert.Equal(0, granted);
    }

    /// <summary>
    /// The control: a declared, tenant-scoped, delegable permission the caller genuinely holds goes
    /// in. Without this the three refusals above could all be a role-creation failure in disguise.
    /// </summary>
    [Fact]
    public async Task ADeclaredDelegablePermissionIsStored()
    {
        var (tenantId, userId) = await SeedOrganizationWithOwnerAsync();
        using var client = CreateOwnerClient(tenantId, userId);

        var roleId = await CreateRoleAsync(client, "Perfectly ordinary");

        var response = await SetPermissionsAsync(client, roleId, PermissionCatalog.ServerSendCommand);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await factory.FromDatabaseAsync(db => db.Set<RolePermission>()
            .AnyAsync(rp => rp.RoleId == roleId
                && rp.Permission == PermissionCatalog.ServerSendCommand));

        Assert.True(stored);
    }
}
