// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using JumpStart.Authorization;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.IntegrationTests;

/// <summary>
/// Registering, from nothing.
/// </summary>
/// <remarks>
/// The path every account takes exactly once, and the one no other test covered: the unit tests all
/// substitute a permissive role-management policy, so the real plan gate never ran in any of them,
/// and the integration tests until now started from a user who already had a tenant.
/// </remarks>
public class AccountBootstrapTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>Calls the endpoint Register.razor calls straight after creating the account.</summary>
    private async Task<HttpResponseMessage> EnsureTenantAsync(Guid userId, string? name = null)
    {
        using var client = factory.CreateClient();
        client.Authenticate(userId);

        var url = name is null
            ? "/api/account-bootstrap/ensure-tenant"
            : $"/api/account-bootstrap/ensure-tenant?tenantName={Uri.EscapeDataString(name)}";

        return await client.PostAsync(new Uri(url, UriKind.Relative), content: null);
    }

    /// <summary>
    /// A brand-new user gets an organization and owns it.
    /// </summary>
    /// <remarks>
    /// The founder's Owner grant runs inside their new tenant, which is on whichever plan sign-up
    /// starts people on - typically the free one, which has no role separation. That combination is
    /// what this asserts still works.
    /// </remarks>
    [Fact]
    public async Task ANewUserGetsAnOrganizationAndOwnsIt()
    {
        var userId = Guid.NewGuid();

        using var response = await EnsureTenantAsync(userId, "Newcomer's Organization");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var tenantId = await factory.FromDatabaseAsync(db => db.Set<UserTenant>()
            .Where(ut => ut.UserId == userId)
            .Select(ut => (Guid?)ut.TenantId)
            .FirstOrDefaultAsync());

        Assert.NotNull(tenantId);

        // Owning it is the point - an organization whose founder can do nothing in it is worse than
        // no organization at all.
        //
        // Asserted against the assignment rather than by resolving permissions: this context comes
        // from DI, so its JwtTenantContext has no request to read a tenant from, and ADR-018's
        // fail-closed filter correctly hides every tenant-scoped row from it. AcrossAllTenants is
        // how a test asks the question the running request would have answered.
        var roleName = await factory.FromDatabaseAsync(db => db.Set<UserRole>()
            .AcrossAllTenants()
            .Where(ur => ur.UserId == userId && ur.TenantId == tenantId)
            .Select(ur => ur.Role.Name)
            .FirstOrDefaultAsync());

        Assert.Equal(BuiltInRoleSeeder.OwnerRoleName, roleName);
    }

    [Fact]
    public async Task ItIsIdempotent()
    {
        var userId = Guid.NewGuid();

        using var first = await EnsureTenantAsync(userId);
        using var second = await EnsureTenantAsync(userId);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        var memberships = await factory.FromDatabaseAsync(db => db.Set<UserTenant>()
            .CountAsync(ut => ut.UserId == userId));

        Assert.Equal(1, memberships);
    }
}
