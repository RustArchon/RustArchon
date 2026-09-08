// Copyright ©2026 Scott Blomfield

using System;
using System.IdentityModel.Tokens.Jwt;
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

namespace RustArchon.Api.IntegrationTests;

/// <summary>
/// A site admin acting inside a customer's Organization, through the real token pipeline.
/// </summary>
/// <remarks>
/// The unit tests cover the policy's decision and the controller's handling of it separately. This
/// covers the thing that actually matters: an assertion naming somebody else's Organization,
/// exchanged against the running application, produces a usable token for exactly the right people
/// and nothing for anybody else.
/// </remarks>
public class ActingAsTenantTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>Creates a customer Organization with a server in it.</summary>
    private async Task<(Guid TenantId, Guid ServerId)> SeedCustomerAsync()
    {
        var tenantId = Guid.NewGuid();
        var serverId = Guid.NewGuid();

        await factory.WithDatabaseAsync(async db =>
        {
            db.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = "A customer", IsActive = true });
            await db.SaveChangesAsync();

            db.Set<RustServer>().Add(new RustServer
            {
                Id = serverId,
                TenantId = tenantId,
                Name = "Their server",
                Host = "192.0.2.20",
                Port = 28016,
                RconPassword = "unused",
                IsEnabled = false
            });

            await db.SaveChangesAsync();
        });

        return (tenantId, serverId);
    }

    /// <summary>Grants the platform-wide Site Admin role that startup seeded.</summary>
    private async Task MakeSiteAdminAsync(Guid userId) =>
        await factory.WithDatabaseAsync(async db =>
        {
            var roleId = await db.Set<Role>()
                .AcrossAllTenants()
                .Where(r => r.TenantId == null && r.Name == SiteAdminRoleSeeder.RoleName)
                .Select(r => r.Id)
                .FirstAsync();

            // TenantId null: the role is held platform-wide, not inside any one Organization.
            db.Set<UserRole>().Add(new UserRole { UserId = userId, RoleId = roleId, TenantId = null });
            await db.SaveChangesAsync();
        });

    /// <summary>Exchanges an assertion naming <paramref name="tenantId"/> for a real token.</summary>
    private async Task<HttpResponseMessage> ExchangeAsync(Guid userId, Guid tenantId)
    {
        using var client = factory.CreateClient();
        client.Authenticate(userId, tenantId);

        return await client.PostAsync(new Uri("/api/token/exchange", UriKind.Relative), content: null);
    }

    [Fact]
    public async Task ASiteAdminGetsATokenForACustomersOrganization()
    {
        var customer = await SeedCustomerAsync();
        var siteAdmin = Guid.NewGuid();
        await MakeSiteAdminAsync(siteAdmin);

        using var response = await ExchangeAsync(siteAdmin, customer.TenantId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var issued = await ReadTokenAsync(response);

        Assert.Equal(
            customer.TenantId.ToString(),
            issued.Claims.Single(c => c.Type == "tenant_id").Value);

        Assert.Equal("true", issued.Claims.Single(c => c.Type == "acting_as_tenant").Value);
    }

    /// <summary>
    /// The point of the whole design: with that token, the customer's own screens work unchanged.
    /// </summary>
    [Fact]
    public async Task ThatTokenCanReadTheCustomersServer()
    {
        var customer = await SeedCustomerAsync();
        var siteAdmin = Guid.NewGuid();
        await MakeSiteAdminAsync(siteAdmin);

        using var exchange = await ExchangeAsync(siteAdmin, customer.TenantId);
        var token = await ReadRawTokenAsync(exchange);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var response = await client.GetAsync(
            new Uri($"/api/RustServers/{customer.ServerId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// ...and it carries an owner's permissions, not a platform administrator's. Acting as a customer
    /// is not the same as administering the platform, and one token doing both would let a screen
    /// meant for one quietly do the other.
    /// </summary>
    [Fact]
    public async Task ThatTokenDoesNotCarryPlatformPermissions()
    {
        var customer = await SeedCustomerAsync();
        var siteAdmin = Guid.NewGuid();
        await MakeSiteAdminAsync(siteAdmin);

        using var response = await ExchangeAsync(siteAdmin, customer.TenantId);
        var issued = await ReadTokenAsync(response);

        var permissions = issued.Claims
            .Where(c => c.Type == "Permission")
            .Select(c => c.Value)
            .ToList();

        Assert.Contains(PermissionCatalog.ServerGet, permissions);
        Assert.DoesNotContain(PermissionCatalog.PlatformManageOrganizations, permissions);
        Assert.DoesNotContain(PermissionCatalog.PlatformManageBilling, permissions);
    }

    [Fact]
    public async Task AnOrdinaryUserIsRefused()
    {
        var customer = await SeedCustomerAsync();

        using var response = await ExchangeAsync(Guid.NewGuid(), customer.TenantId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The boundary worth stating twice: holding everything inside your own Organization gets you
    /// nowhere near somebody else's.
    /// </summary>
    [Fact]
    public async Task AnOwnerOfADifferentOrganizationIsRefused()
    {
        var customer = await SeedCustomerAsync();
        var theirs = await SeedCustomerAsync();
        var owner = Guid.NewGuid();

        await factory.WithDatabaseAsync(async db =>
        {
            var ownerRoleId = await db.Set<Role>()
                .AcrossAllTenants()
                .Where(r => r.TenantId == null && r.Name == BuiltInRoleSeeder.OwnerRoleName)
                .Select(r => r.Id)
                .FirstAsync();

            db.Set<UserTenant>().Add(
                new UserTenant { UserId = owner, TenantId = theirs.TenantId, IsActive = true });

            db.Set<UserRole>().Add(
                new UserRole { UserId = owner, RoleId = ownerRoleId, TenantId = theirs.TenantId });

            await db.SaveChangesAsync();
        });

        using var response = await ExchangeAsync(owner, customer.TenantId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<JwtSecurityToken> ReadTokenAsync(HttpResponseMessage response) =>
        new JwtSecurityTokenHandler().ReadJwtToken(await ReadRawTokenAsync(response));

    private static async Task<string> ReadRawTokenAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>();
        return body!.Token;
    }

    private sealed class TokenResponse
    {
        public string Token { get; set; } = string.Empty;
    }
}
