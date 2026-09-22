// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.Tests;

/// <summary>
/// Which of the seeded tiers offer automatic third-party plugin updates: HQM, and only HQM (Scott, 2026-09-21: it is meant to be an HQM
/// differentiator). Existing databases get the same through the <c>GrantThirdPartyPluginUpdatesToHqmAndGold</c> migration, which also covers
/// the Gold (Comped) plan that is not a seeded one.
/// </summary>
public class PlanSeederThirdPartyUpdatesTests
{
    [Fact]
    public async Task OnlyHqmOffersAutomaticThirdPartyPluginUpdatesOnAFreshInstall()
    {
        await using var context = new ApiDbContext(
            new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        await PlanSeeder.EnsureDefaultsAsync(context, NullLogger.Instance);

        var offers = await context.Set<Plan>().ToDictionaryAsync(p => p.Name, p => p.OffersThirdPartyPluginUpdates);
        Assert.Equal(["HQM", "Metal", "Stone", "Wood"], offers.Keys.Order());
        Assert.True(offers["HQM"]);
        Assert.False(offers["Metal"]);
        Assert.False(offers["Stone"]);
        Assert.False(offers["Wood"]);
    }
}
