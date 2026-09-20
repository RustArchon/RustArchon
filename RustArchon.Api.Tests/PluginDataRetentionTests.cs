// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Api.Data;
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;
using static RustArchon.Api.Tests.CombatChunkCodecTests;

namespace RustArchon.Api.Tests;

/// <summary>
/// Enforcing plan retention on plugin-recorded data against a real Postgres: each organization keeps what its plan says,
/// an organization with no usable plan is held to the shortest catalog value (never "forever", never "delete all"), a
/// chunk is kept while its newest event is inside the window, and one organization's cutoff never touches another's.
/// </summary>
public class PluginDataRetentionTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Task<Guid?> GetCurrentTenantIdAsync() => Task.FromResult<Guid?>(tenantId);
    }

    private ApiDbContext PlatformContext() => new(postgres.Options);

    /// <summary>A new organization, optionally on a plan that keeps <paramref name="retentionDays"/> days of history.</summary>
    private async Task<Guid> TenantAsync(int? retentionDays, bool endedSubscription = false)
    {
        await using var context = PlatformContext();
        var tenantId = Guid.NewGuid();
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Retention tenant {tenantId}", IsActive = true });

        if (retentionDays is not null)
        {
            var plan = new Plan { Name = $"Retention plan {Guid.NewGuid()}", Active = true, RetentionHistory = retentionDays.Value };
            plan.Prices.Add(new PlanPrice { TermMonths = 1, UnitAmount = 0m, IncludedUnits = 1, Currency = "USD" });
            context.Set<Plan>().Add(plan);
            context.Set<Subscription>().Add(new Subscription
            {
                TenantId = tenantId, PlanId = plan.Id, Plan = plan, StartDate = Now.AddYears(-1),
                EndDate = endedSubscription ? Now.AddDays(-1) : null
            });
        }

        await context.SaveChangesAsync();
        return tenantId;
    }

    /// <summary>Stores a one-event chunk whose newest event is <paramref name="daysOld"/> days before "now".</summary>
    private async Task ChunkAsync(Guid tenantId, double daysOld, long bootId = 1)
    {
        var context = new ApiDbContext(postgres.Options, new FixedTenantContext(tenantId));
        var when = Now.AddDays(-daysOld).ToUnixTimeMilliseconds();
        await new PluginCombatChunkRepository(context).AppendAsync(tenantId, Guid.NewGuid(), bootId, false, Batch(Ev(1, when)), Now);
    }

    private async Task<int> CountAsync(Guid tenantId)
    {
        await using var context = PlatformContext();
        return await context.PluginCombatChunks.AcrossAllTenants().CountAsync(c => c.TenantId == tenantId);
    }

    private async Task<int> PruneAsync()
    {
        await using var context = PlatformContext();
        return await new PluginDataRetention(context, NullLogger<PluginDataRetention>.Instance).PruneAsync(Now);
    }

    [Fact]
    public async Task ChunksOlderThanThePlansRetentionAreDeletedAndNewerOnesKept()
    {
        var tenant = await TenantAsync(retentionDays: 60);
        await ChunkAsync(tenant, daysOld: 61);
        await ChunkAsync(tenant, daysOld: 59, bootId: 2);
        await ChunkAsync(tenant, daysOld: 1, bootId: 3);

        await PruneAsync();

        Assert.Equal(2, await CountAsync(tenant));
    }

    [Fact]
    public async Task EachOrganizationKeepsItsOwnPlansWindow()
    {
        var short30 = await TenantAsync(30);
        var long265 = await TenantAsync(265);
        await ChunkAsync(short30, daysOld: 100);
        await ChunkAsync(long265, daysOld: 100);

        await PruneAsync();

        Assert.Equal(0, await CountAsync(short30));
        Assert.Equal(1, await CountAsync(long265)); // the longer plan's data is untouched by the shorter one's cutoff
    }

    [Fact]
    public async Task OrganizationWithNoSubscriptionIsHeldToTheDefaultNotKeptForeverNorEmptied()
    {
        var tenant = await TenantAsync(retentionDays: null);
        await ChunkAsync(tenant, daysOld: PluginDataRetention.DefaultRetentionDays + 5);
        await ChunkAsync(tenant, daysOld: PluginDataRetention.DefaultRetentionDays - 5, bootId: 2);

        await PruneAsync();

        Assert.Equal(1, await CountAsync(tenant));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task APlanWithANonPositiveRetentionFallsBackToTheDefault(int retention)
    {
        var tenant = await TenantAsync(retention);
        await ChunkAsync(tenant, daysOld: 1);
        await ChunkAsync(tenant, daysOld: PluginDataRetention.DefaultRetentionDays + 5, bootId: 2);

        await PruneAsync();

        Assert.Equal(1, await CountAsync(tenant)); // not everything gone, not everything kept
    }

    [Fact]
    public async Task OnlyTheCurrentSubscriptionsPlanCounts()
    {
        var tenant = await TenantAsync(retentionDays: 365, endedSubscription: true); // a past plan with a long window
        await ChunkAsync(tenant, daysOld: 100);

        await PruneAsync();

        Assert.Equal(0, await CountAsync(tenant)); // no current plan, so the default applies
    }

    [Fact]
    public async Task AChunkIsKeptWhileItsNewestEventIsInsideTheWindow()
    {
        var tenant = await TenantAsync(30);
        var context = new ApiDbContext(postgres.Options, new FixedTenantContext(tenant));
        var events = Batch(Ev(1, Now.AddDays(-40).ToUnixTimeMilliseconds()), Ev(2, Now.AddDays(-10).ToUnixTimeMilliseconds()));
        await new PluginCombatChunkRepository(context).AppendAsync(tenant, Guid.NewGuid(), 1, false, events, Now);

        await PruneAsync();

        Assert.Equal(1, await CountAsync(tenant));
    }

    [Fact]
    public async Task ThePruneReportsHowManyChunksItRemovedAndDoesNothingTheSecondTime()
    {
        var tenant = await TenantAsync(30);
        await ChunkAsync(tenant, daysOld: 90);
        await ChunkAsync(tenant, daysOld: 91, bootId: 2);

        var first = await PruneAsync();
        Assert.True(first >= 2);
        Assert.Equal(0, await CountAsync(tenant));

        await PruneAsync(); // a second pass over an already-pruned organization changes nothing and does not fail
        Assert.Equal(0, await CountAsync(tenant));
    }

    [Fact]
    public async Task OtherPluginDataAndOtherTablesAreNeverTouched()
    {
        var tenant = await TenantAsync(30);
        await ChunkAsync(tenant, daysOld: 90);

        await PruneAsync();

        await using var context = PlatformContext();
        Assert.True(await context.Set<Tenant>().AnyAsync(t => t.Id == tenant));
        Assert.True(await context.Set<Subscription>().AnyAsync(s => s.TenantId == tenant));
    }
}
