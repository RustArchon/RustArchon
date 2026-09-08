// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.IntegrationTests;

/// <summary>
/// That holding a permission gets you nothing outside your own Organization.
/// </summary>
/// <remarks>
/// <para>
/// The permission matrix answers "may this caller use this endpoint at all?". This answers the
/// question underneath it, which is the one a multi-tenant system actually lives or dies by: the
/// caller has a real permission and is using it correctly, and must still see only their own rows.
/// A permission check that passes and a tenant filter that does not apply is indistinguishable from
/// working, right up until somebody reads somebody else's servers.
/// </para>
/// <para>
/// Two shapes of failure are covered. A token naming <em>another</em> Organization's row is the
/// obvious one. A token naming <em>no</em> Organization at all is the subtler one: that is a real,
/// signed token - <c>TokenController.Exchange</c> issues it before a tenant is chosen - so the
/// system has to treat "authenticated" and "scoped" as two separate facts (ADR-018).
/// </para>
/// </remarks>
public class TenantScopeTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string ServerPermissions = PermissionCatalog.ServerGet;

    /// <summary>Creates an Organization with one server in it, and returns both ids.</summary>
    private async Task<(Guid TenantId, Guid ServerId)> SeedOrganizationAsync(string name)
    {
        var tenantId = Guid.NewGuid();
        var serverId = Guid.NewGuid();

        await factory.WithDatabaseAsync(async db =>
        {
            db.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = name, IsActive = true });
            await db.SaveChangesAsync();

            db.Set<RustServer>().Add(new RustServer
            {
                Id = serverId,
                TenantId = tenantId,
                Name = $"{name} server",
                Host = "192.0.2.10",
                Port = 28016,
                RconPassword = "unused",
                IsEnabled = false
            });

            await db.SaveChangesAsync();
        });

        return (tenantId, serverId);
    }

    [Fact]
    public async Task AServerBelongingToAnotherOrganizationIsNotReadable()
    {
        var mine = await SeedOrganizationAsync("Mine");
        var theirs = await SeedOrganizationAsync("Theirs");

        using var client = factory.CreateClient();
        client.Authenticate(Guid.NewGuid(), mine.TenantId, ServerPermissions);

        var response = await client.GetAsync(
            new Uri($"/api/RustServers/{theirs.ServerId}", UriKind.Relative));

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MyOwnServerIsReadable()
    {
        var mine = await SeedOrganizationAsync("Readable");

        using var client = factory.CreateClient();
        client.Authenticate(Guid.NewGuid(), mine.TenantId, ServerPermissions);

        var response = await client.GetAsync(
            new Uri($"/api/RustServers/{mine.ServerId}", UriKind.Relative));

        // The control for the test above: same call, same permission, own Organization. Without it,
        // a route that 404'd for everyone would look like perfect isolation.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ListingServersReturnsOnlyMyOwn()
    {
        var mine = await SeedOrganizationAsync("Listing mine");
        var theirs = await SeedOrganizationAsync("Listing theirs");

        using var client = factory.CreateClient();
        client.Authenticate(Guid.NewGuid(), mine.TenantId, PermissionCatalog.ServerList);

        var body = await client.GetStringAsync(new Uri("/api/RustServers", UriKind.Relative));

        // Asserted against the raw response rather than a deserialized shape. The list endpoint
        // returns a paged envelope, and a test that models that envelope would stop checking
        // anything the day the shape changes - whereas "the other Organization's id must not appear
        // anywhere in this response" is the property that actually matters, whatever the shape.
        Assert.Contains(mine.ServerId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(theirs.ServerId.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A token with no <c>tenant_id</c> must not become a key to everything. ADR-018 made the filter
    /// deny rather than admit in that case, and this is that decision stated as a request.
    /// </summary>
    [Fact]
    public async Task ATenantlessTokenSeesNoServersAtAll()
    {
        var seeded = await SeedOrganizationAsync("Invisible to the tenantless");

        using var client = factory.CreateClient();
        client.Authenticate(Guid.NewGuid(), tenantId: null, PermissionCatalog.ServerList);

        var body = await client.GetStringAsync(new Uri("/api/RustServers", UriKind.Relative));

        Assert.DoesNotContain(seeded.ServerId.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ATenantlessTokenCannotReadAnIndividualServer()
    {
        var seeded = await SeedOrganizationAsync("Also invisible");

        using var client = factory.CreateClient();
        client.Authenticate(Guid.NewGuid(), tenantId: null, ServerPermissions);

        var response = await client.GetAsync(
            new Uri($"/api/RustServers/{seeded.ServerId}", UriKind.Relative));

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The Organization-scoped controllers refuse outright rather than answering emptily, because
    /// every one of their operations names a tenant that the token does not supply.
    /// </summary>
    [Theory]
    [InlineData("/api/organization/roles", PermissionCatalog.OrganizationManageMembers)]
    [InlineData("/api/organization/members", PermissionCatalog.OrganizationManageMembers)]
    public async Task OrganizationEndpointsRefuseATenantlessToken(string url, string permission)
    {
        using var client = factory.CreateClient();
        client.Authenticate(Guid.NewGuid(), tenantId: null, permission);

        var response = await client.GetAsync(new Uri(url, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
