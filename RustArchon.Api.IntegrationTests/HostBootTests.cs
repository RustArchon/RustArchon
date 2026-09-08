// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using JumpStart.Authorization;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.IntegrationTests;

/// <summary>
/// That the test host is the real application, and starts the way the real one does.
/// </summary>
/// <remarks>
/// Every other test in this project rests on this: if the host quietly failed to run its seeders, or
/// authenticated everybody, the refusals asserted elsewhere would pass for the wrong reason.
/// </remarks>
public class HostBootTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task StartupSeedsTheBuiltInOwnerRoleAndThePlanCatalog()
    {
        var owner = await factory.FromDatabaseAsync(db => db.Set<Role>()
            .AcrossAllTenants()
            .FirstOrDefaultAsync(r => r.TenantId == null && r.Name == BuiltInRoleSeeder.OwnerRoleName));

        Assert.NotNull(owner);

        var plans = await factory.FromDatabaseAsync(db => db.Set<Plan>().CountAsync());
        Assert.True(plans > 0, "PlanSeeder should have seeded the pricing tiers.");
    }

    [Fact]
    public async Task AnUnauthenticatedRequestIsChallenged()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/organization/roles", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A valid token is not on its own an authorization. This is the distinction the whole project
    /// tests, stated once at its simplest.
    /// </summary>
    [Fact]
    public async Task AnAuthenticatedRequestWithNoPermissionsIsForbidden()
    {
        using var client = factory.CreateClient()
            .Authenticate(Guid.NewGuid(), Guid.NewGuid());

        var response = await client.GetAsync(new Uri("/api/organization/roles", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
