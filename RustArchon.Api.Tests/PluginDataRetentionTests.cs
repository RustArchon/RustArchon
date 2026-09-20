// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure.ObjectStorage;
using RustArchon.Messaging.Contracts;
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

    private async Task<int> PruneAsync(RecordingStorage? storage = null)
    {
        await using var context = PlatformContext();
        return await new PluginDataRetention(context, storage ?? new RecordingStorage(), NullLogger<PluginDataRetention>.Instance).PruneAsync(Now);
    }

    /// <summary>Remembers what was deleted; can be told to fail on a key.</summary>
    private sealed class RecordingStorage : IObjectStorage
    {
        public readonly List<string> Deleted = [];
        public string? FailOn;
        public Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ObjectContent?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<ObjectContent?>(null);
        public Task DeleteByPrefixAsync(string prefix, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            if (key == FailOn)
            {
                throw new InvalidOperationException("storage is down");
            }

            Deleted.Add(key);
            return Task.CompletedTask;
        }
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

    // ---- the other recorded data ---------------------------------------------------------------------------------

    private async Task<Guid> RconEventAsync(Guid tenant, Guid server, double daysOld)
    {
        await using var context = PlatformContext();
        var row = new RconEvent { TenantId = tenant, RustServerId = server, CapturedAtUtc = Now.AddDays(-daysOld), Type = "Generic", Message = "line" };
        context.RconEvents.Add(row);
        await context.SaveChangesAsync();
        return row.Id;
    }

    private async Task<int> CountAsync<T>(Func<ApiDbContext, IQueryable<T>> set, Guid tenant) where T : class, JumpStart.Data.MultiTenant.ITenantScoped
    {
        await using var context = PlatformContext();
        return await set(context).AcrossAllTenants().CountAsync(e => e.TenantId == tenant);
    }

    [Fact]
    public async Task ConsoleAndChatEventsPastTheWindowAreDeletedAndRecentOnesKept()
    {
        var tenant = await TenantAsync(30);
        var server = Guid.NewGuid();
        await RconEventAsync(tenant, server, 31);
        await RconEventAsync(tenant, server, 90);
        await RconEventAsync(tenant, server, 29);
        await RconEventAsync(tenant, server, 0);

        await PruneAsync();

        Assert.Equal(2, await CountAsync(c => c.RconEvents, tenant));
    }

    [Fact]
    public async Task ARowOnTheOtherSideOfTheWindowSurvivesForAnOrganizationWithALongerPlan()
    {
        var shortPlan = await TenantAsync(30);
        var longPlan = await TenantAsync(365);
        await RconEventAsync(shortPlan, Guid.NewGuid(), 100);
        await RconEventAsync(longPlan, Guid.NewGuid(), 100);

        await PruneAsync();

        Assert.Equal(0, await CountAsync(c => c.RconEvents, shortPlan));
        Assert.Equal(1, await CountAsync(c => c.RconEvents, longPlan));
    }

    [Fact]
    public async Task ADeleteLargerThanOneBatchRemovesEverythingOldAndOnlyThat()
    {
        var tenant = await TenantAsync(30);
        var server = Guid.NewGuid();
        var old = PluginDataRetention.DeleteBatchSize + 123;
        await using (var context = PlatformContext())
        {
            for (var i = 0; i < old; i++)
            {
                context.RconEvents.Add(new RconEvent { TenantId = tenant, RustServerId = server, CapturedAtUtc = Now.AddDays(-40).AddSeconds(i), Type = "Generic", Message = "x" });
            }

            for (var i = 0; i < 7; i++)
            {
                context.RconEvents.Add(new RconEvent { TenantId = tenant, RustServerId = server, CapturedAtUtc = Now.AddDays(-1).AddSeconds(i), Type = "Generic", Message = "y" });
            }

            await context.SaveChangesAsync();
        }

        var removed = await PruneAsync();

        Assert.True(removed >= old);
        Assert.Equal(7, await CountAsync(c => c.RconEvents, tenant));
    }

    [Fact]
    public async Task KillFeedEventsAndStatsSnapshotsPastTheWindowAreDeleted()
    {
        var tenant = await TenantAsync(30);
        var server = Guid.NewGuid();
        await using (var context = PlatformContext())
        {
            foreach (var days in new[] { 45, 5 })
            {
                context.PlayerKillEvents.Add(new PlayerKillEvent { TenantId = tenant, RustServerId = server, OccurredAtUtc = Now.AddDays(-days), VictimName = "v", RawMessage = "v died" });
                context.ServerInfoSnapshots.Add(new ServerInfoSnapshot { TenantId = tenant, RustServerId = server, CapturedAtUtc = Now.AddDays(-days), Players = 1, MaxPlayers = 10 });
            }

            await context.SaveChangesAsync();
        }

        await PruneAsync();

        Assert.Equal(1, await CountAsync(c => c.PlayerKillEvents, tenant));
        Assert.Equal(1, await CountAsync(c => c.ServerInfoSnapshots, tenant));
    }

    [Fact]
    public async Task PlayerSessionsAreNeverPruned()
    {
        var tenant = await TenantAsync(30);
        await using (var context = PlatformContext())
        {
            context.PlayerSessions.Add(new PlayerSession
            {
                TenantId = tenant, RustServerId = Guid.NewGuid(), SteamId = "76561198000000001", DisplayName = "old timer",
                IpAddress = "203.0.113.9", ConnectedAtUtc = Now.AddDays(-400), DisconnectedAtUtc = Now.AddDays(-399)
            });
            await context.SaveChangesAsync();
        }

        await PruneAsync();

        Assert.Equal(1, await CountAsync(c => c.PlayerSessions, tenant));
    }

    // ---- maps ----------------------------------------------------------------------------------------------------

    private async Task<Guid> MapAsync(Guid tenant, Guid server, int seed, double lastSeenDaysOld, double? uploadedDaysOld = null, bool pictures = true)
    {
        await using var context = PlatformContext();
        var map = new PluginMap
        {
            TenantId = tenant, RustServerId = server, WorldSize = 4500, WorldSeed = seed, FileName = $"proceduralmap.4500.{seed}.map",
            LastSeenUtc = Now.AddDays(-lastSeenDaysOld),
            UploadedAtUtc = uploadedDaysOld is { } up ? Now.AddDays(-up) : null,
            ObjectKey = pictures ? $"maps/{server}/4500_{seed}.png" : null,
            PreviewObjectKey = pictures ? $"maps/{server}/4500_{seed}.preview6144.webp" : null
        };
        context.PluginMaps.Add(map);
        await context.SaveChangesAsync();
        return map.Id;
    }

    private async Task<HashSet<Guid>> MapIdsAsync(Guid tenant)
    {
        await using var context = PlatformContext();
        return (await context.PluginMaps.AcrossAllTenants().Where(m => m.TenantId == tenant).Select(m => m.Id).ToListAsync()).ToHashSet();
    }

    [Fact]
    public async Task AnOldWipesMapAndItsPicturesAreRemovedWhileTheCurrentMapStays()
    {
        var tenant = await TenantAsync(30);
        var server = Guid.NewGuid();
        var oldWipe = await MapAsync(tenant, server, seed: 1, lastSeenDaysOld: 60);
        var current = await MapAsync(tenant, server, seed: 2, lastSeenDaysOld: 0);
        var storage = new RecordingStorage();

        await PruneAsync(storage);

        Assert.Equal([current], await MapIdsAsync(tenant));
        Assert.Contains($"maps/{server}/4500_1.png", storage.Deleted);
        Assert.Contains($"maps/{server}/4500_1.preview6144.webp", storage.Deleted);
        Assert.DoesNotContain(storage.Deleted, k => k.Contains("4500_2"));
        Assert.DoesNotContain(oldWipe, await MapIdsAsync(tenant));
    }

    [Fact]
    public async Task TheOnlyMapOfAServerIsKeptHoweverOldItIs()
    {
        var tenant = await TenantAsync(30);
        var only = await MapAsync(tenant, Guid.NewGuid(), seed: 1, lastSeenDaysOld: 500, uploadedDaysOld: 500);
        var storage = new RecordingStorage();

        await PruneAsync(storage);

        Assert.Equal([only], await MapIdsAsync(tenant));
        Assert.Empty(storage.Deleted);
    }

    [Fact]
    public async Task AnOldWipesMapInsideTheWindowIsKept()
    {
        var tenant = await TenantAsync(30);
        var server = Guid.NewGuid();
        var recentWipe = await MapAsync(tenant, server, seed: 1, lastSeenDaysOld: 20);
        var current = await MapAsync(tenant, server, seed: 2, lastSeenDaysOld: 0);

        await PruneAsync();

        Assert.Equal(new HashSet<Guid> { recentWipe, current }, await MapIdsAsync(tenant));
    }

    [Fact]
    public async Task AMapUploadedInsideTheWindowIsKeptEvenIfItWasLastReportedLongAgo()
    {
        var tenant = await TenantAsync(30);
        var server = Guid.NewGuid();
        var reuploaded = await MapAsync(tenant, server, seed: 1, lastSeenDaysOld: 60, uploadedDaysOld: 3);
        var current = await MapAsync(tenant, server, seed: 2, lastSeenDaysOld: 0);

        await PruneAsync();

        Assert.Equal(new HashSet<Guid> { reuploaded, current }, await MapIdsAsync(tenant));
    }

    [Fact]
    public async Task EachServersCurrentMapIsKeptIndependently()
    {
        var tenant = await TenantAsync(30);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var aOld = await MapAsync(tenant, a, seed: 1, lastSeenDaysOld: 90);
        var aNew = await MapAsync(tenant, a, seed: 2, lastSeenDaysOld: 80);       // the current one of A, though it is old too
        var bOnly = await MapAsync(tenant, b, seed: 3, lastSeenDaysOld: 200);

        await PruneAsync();

        Assert.Equal(new HashSet<Guid> { aNew, bOnly }, await MapIdsAsync(tenant));
        Assert.DoesNotContain(aOld, await MapIdsAsync(tenant));
    }

    [Fact]
    public async Task IfDeletingAPictureFailsTheMapRowIsKeptAndTriedAgainNextTime()
    {
        var tenant = await TenantAsync(30);
        var server = Guid.NewGuid();
        var oldWipe = await MapAsync(tenant, server, seed: 1, lastSeenDaysOld: 60);
        await MapAsync(tenant, server, seed: 2, lastSeenDaysOld: 0);
        var broken = new RecordingStorage { FailOn = $"maps/{server}/4500_1.preview6144.webp" };

        await PruneAsync(broken);

        Assert.Contains(oldWipe, await MapIdsAsync(tenant));         // still there, so nothing is orphaned

        await PruneAsync(new RecordingStorage());                    // storage is back

        Assert.DoesNotContain(oldWipe, await MapIdsAsync(tenant));
    }

    [Fact]
    public async Task AnOldMapWithNoPicturesIsRemovedWithoutTouchingStorage()
    {
        var tenant = await TenantAsync(30);
        var server = Guid.NewGuid();
        var oldWipe = await MapAsync(tenant, server, seed: 1, lastSeenDaysOld: 60, pictures: false);
        await MapAsync(tenant, server, seed: 2, lastSeenDaysOld: 0);
        var storage = new RecordingStorage();

        await PruneAsync(storage);

        Assert.DoesNotContain(oldWipe, await MapIdsAsync(tenant));
        Assert.Empty(storage.Deleted);
    }

    // ---- spent tokens --------------------------------------------------------------------------------------------

    [Fact]
    public async Task TokensThatExpiredMoreThanADayAgoAreDeletedAndFreshOnesKept()
    {
        var tenant = await TenantAsync(30);
        var server = Guid.NewGuid();
        var map = await MapAsync(tenant, server, seed: 1, lastSeenDaysOld: 0);
        await using (var context = PlatformContext())
        {
            context.PluginMapUploadTokens.Add(new PluginMapUploadToken { TenantId = tenant, RustServerId = server, PluginMapId = map, TokenHash = "old-map-token", CreatedAtUtc = Now.AddDays(-3), ExpiresAtUtc = Now.AddDays(-2) });
            context.PluginMapUploadTokens.Add(new PluginMapUploadToken { TenantId = tenant, RustServerId = server, PluginMapId = map, TokenHash = "fresh-map-token", CreatedAtUtc = Now.AddMinutes(-1), ExpiresAtUtc = Now.AddMinutes(9) });
            context.PluginMapUploadTokens.Add(new PluginMapUploadToken { TenantId = tenant, RustServerId = server, PluginMapId = map, TokenHash = "just-expired-map-token", CreatedAtUtc = Now.AddHours(-2), ExpiresAtUtc = Now.AddHours(-1) });
            context.PluginUpdateTokens.Add(new PluginUpdateToken { TenantId = tenant, RustServerId = server, SigningKeyFingerprint = "abc", TokenHash = "old-update-token", CreatedAtUtc = Now.AddDays(-3), ExpiresAtUtc = Now.AddDays(-2) });
            context.PluginUpdateTokens.Add(new PluginUpdateToken { TenantId = tenant, RustServerId = server, SigningKeyFingerprint = "abc", TokenHash = "fresh-update-token", CreatedAtUtc = Now, ExpiresAtUtc = Now.AddMinutes(10) });
            await context.SaveChangesAsync();
        }

        await PruneAsync();

        await using var check = PlatformContext();
        var mapTokens = await check.PluginMapUploadTokens.AcrossAllTenants().Where(t => t.TenantId == tenant).Select(t => t.TokenHash).ToListAsync();
        var updateTokens = await check.PluginUpdateTokens.AcrossAllTenants().Where(t => t.TenantId == tenant).Select(t => t.TokenHash).ToListAsync();
        Assert.Equal(["fresh-map-token", "just-expired-map-token"], mapTokens.Order().ToList());   // a token stays a day past expiry
        Assert.Equal(["fresh-update-token"], updateTokens);
    }

    [Fact]
    public async Task UpdateNoticesNotHeardAgainWithinTheWindowAreDeletedAndRecentOnesKept()
    {
        var tenant = await TenantAsync(30);
        await using (var context = PlatformContext())
        {
            context.PluginUpdateNotices.Add(new PluginUpdateNotice { TenantId = tenant, RustServerId = Guid.NewGuid(), Name = "Old", NormalizedName = "old", ReportedAtUtc = Now.AddDays(-45) });
            context.PluginUpdateNotices.Add(new PluginUpdateNotice { TenantId = tenant, RustServerId = Guid.NewGuid(), Name = "Fresh", NormalizedName = "fresh", ReportedAtUtc = Now.AddDays(-2) });
            await context.SaveChangesAsync();
        }

        await PruneAsync();

        await using var check = PlatformContext();
        var left = await check.PluginUpdateNotices.AcrossAllTenants().Where(n => n.TenantId == tenant).Select(n => n.Name).ToListAsync();
        Assert.Equal(["Fresh"], left);
    }

    [Fact]
    public async Task OldSucceededUpdateAttemptsArePrunedButRefusalsAndFailuresAreKeptSoAVersionIsNotRetried()
    {
        var tenant = await TenantAsync(30);
        await using (var context = PlatformContext())
        {
            foreach (var state in new[] { PluginUpdateAttemptStates.Succeeded, PluginUpdateAttemptStates.Failed, PluginUpdateAttemptStates.Refused, PluginUpdateAttemptStates.Started })
            {
                context.PluginUpdateAttempts.Add(new PluginUpdateAttempt { TenantId = tenant, RustServerId = Guid.NewGuid(), ToVersion = "1.0.0", State = state, StartedAtUtc = Now.AddDays(-90) });
            }

            context.PluginUpdateAttempts.Add(new PluginUpdateAttempt { TenantId = tenant, RustServerId = Guid.NewGuid(), ToVersion = "1.0.1", State = PluginUpdateAttemptStates.Succeeded, StartedAtUtc = Now.AddDays(-2) });
            await context.SaveChangesAsync();
        }

        await PruneAsync();

        await using var check = PlatformContext();
        var left = await check.PluginUpdateAttempts.AcrossAllTenants().Where(a => a.TenantId == tenant).Select(a => a.State + ":" + a.ToVersion).ToListAsync();
        Assert.Equal(["failed:1.0.0", "refused:1.0.0", "started:1.0.0", "succeeded:1.0.1"], left.Order().ToArray());
    }
}
