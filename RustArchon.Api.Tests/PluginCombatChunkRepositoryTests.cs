// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Messaging;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;
using static RustArchon.Api.Tests.CombatChunkCodecTests;

namespace RustArchon.Api.Tests;

/// <summary>
/// Combat chunk storage and reading against a real Postgres: what is stored, that a batch sent twice (or again after a
/// Worker restart, or out of order) never doubles up, that another organization's data is unreachable, and that the
/// per-player and time-window views return exactly what they should, newest first.
/// </summary>
public class PluginCombatChunkRepositoryTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Carol = "76561198000000003";
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly long Base = Now.ToUnixTimeMilliseconds();

    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Task<Guid?> GetCurrentTenantIdAsync() => Task.FromResult<Guid?>(tenantId);
    }

    private sealed record Harness(ApiDbContext Context, PluginCombatChunkRepository Repository, Guid TenantId, Guid ServerId);

    private async Task<Harness> CreateAsync(Guid? tenant = null)
    {
        var tenantId = tenant ?? Guid.NewGuid();
        var context = new ApiDbContext(postgres.Options, new FixedTenantContext(tenantId));
        if (!await context.Set<Tenant>().IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId))
        {
            context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Combat tenant {tenantId}", IsActive = true });
            await context.SaveChangesAsync();
        }

        var serverId = Guid.NewGuid();
        return new Harness(context, new PluginCombatChunkRepository(context), tenantId, serverId);
    }

    private static Task<int> Append(Harness h, string json, long boot = 1, bool gap = false, Guid? server = null) =>
        h.Repository.AppendAsync(h.TenantId, server ?? h.ServerId, boot, gap, json, Now);

    // ---- appending -----------------------------------------------------------------------------------------

    [Fact]
    public async Task ABatchIsStoredAsOneChunkWithItsRangeTimesAndPlayers()
    {
        var h = await CreateAsync();

        var stored = await Append(h, Batch(Ev(1, Base), Ev(2, Base + 5000, a: "bear", ap: false, v: Alice, vp: true)));

        Assert.Equal(2, stored);
        var chunk = await h.Context.PluginCombatChunks.AsNoTracking().SingleAsync(c => c.RustServerId == h.ServerId);
        Assert.Equal((1L, 2L, 2), (chunk.FirstSequence, chunk.LastSequence, chunk.EventCount));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(Base), chunk.FromUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(Base + 5000), chunk.ToUtc);
        Assert.Equal([Alice, Bob], chunk.PlayerIds.OrderBy(x => x).ToArray());
        Assert.Equal(1, chunk.Format);
        Assert.False(chunk.PrecededByGap);
    }

    [Fact]
    public async Task AnEmptyBatchStoresNothing()
    {
        var h = await CreateAsync();

        Assert.Equal(0, await Append(h, "[]"));
        Assert.Equal(0, await h.Context.PluginCombatChunks.CountAsync());
    }

    [Fact]
    public async Task AGapIsRememberedOnTheChunkThatFollowsIt()
    {
        var h = await CreateAsync();

        await Append(h, Batch(Ev(10, Base)), gap: true);

        Assert.True((await h.Context.PluginCombatChunks.AsNoTracking().SingleAsync(c => c.RustServerId == h.ServerId)).PrecededByGap);
    }

    [Fact]
    public async Task AMalformedBatchStoresNothingAndSaysSo()
    {
        var h = await CreateAsync();

        await Assert.ThrowsAsync<CombatChunkCodec.InvalidCombatBatchException>(() => Append(h, "{\"not\":\"an array\"}"));
        Assert.Equal(0, await h.Context.PluginCombatChunks.CountAsync());
    }

    [Fact]
    public async Task ABatchSentTwiceIsStoredOnce()
    {
        var h = await CreateAsync();
        var batch = Batch(Ev(1, Base), Ev(2, Base + 1000));

        Assert.Equal(2, await Append(h, batch));
        Assert.Equal(0, await Append(h, batch));

        Assert.Equal(1, await h.Context.PluginCombatChunks.CountAsync(c => c.RustServerId == h.ServerId));
    }

    [Fact]
    public async Task AWorkerRestartResendingOldEventsAlongsideNewOnesStoresOnlyTheNewOnes()
    {
        var h = await CreateAsync();
        await Append(h, Batch(Ev(1, Base), Ev(2, Base + 1000)));

        var stored = await Append(h, Batch(Ev(1, Base), Ev(2, Base + 1000), Ev(3, Base + 2000), Ev(4, Base + 3000)));

        Assert.Equal(2, stored);
        var log = await h.Repository.QueryAsync(h.ServerId, null, null, null, 100);
        Assert.Equal([4L, 3L, 2L, 1L], log.Events.Select(e => e.Sequence).ToArray());
    }

    [Fact]
    public async Task BatchesHandledOutOfOrderAreBothStoredNeitherLooksLikeARepeat()
    {
        // The consumer runs several messages at once, so the second batch can finish first.
        var h = await CreateAsync();

        await Append(h, Batch(Ev(6, Base + 6000), Ev(7, Base + 7000)));
        var earlier = await Append(h, Batch(Ev(1, Base + 1000), Ev(2, Base + 2000)));

        Assert.Equal(2, earlier);
        Assert.Equal(4, (await h.Repository.QueryAsync(h.ServerId, null, null, null, 100)).Events.Count);
    }

    [Fact]
    public async Task ASequenceNumberInADifferentBootIsADifferentEvent()
    {
        // The plugin reloaded: its numbering restarted at 1, and those are new events, not repeats.
        var h = await CreateAsync();
        await Append(h, Batch(Ev(1, Base), Ev(2, Base + 1000)), boot: 111);

        var stored = await Append(h, Batch(Ev(1, Base + 60_000), Ev(2, Base + 61_000)), boot: 222, gap: true);

        Assert.Equal(2, stored);
        Assert.Equal(4, (await h.Repository.QueryAsync(h.ServerId, null, null, null, 100)).Events.Count);
    }

    [Fact]
    public async Task TheSameSequencesOnADifferentServerAreNeverTreatedAsRepeats()
    {
        var h = await CreateAsync();
        var otherServer = Guid.NewGuid();
        await Append(h, Batch(Ev(1, Base)));

        Assert.Equal(1, await Append(h, Batch(Ev(1, Base)), server: otherServer));
    }

    [Fact]
    public async Task SeveralIdenticalBatchesArrivingAtOnceStoreTheEventsExactlyOnce()
    {
        var tenant = Guid.NewGuid();
        var server = Guid.NewGuid();
        await using (var seed = (await CreateAsync(tenant)).Context) { }
        var batch = Batch(Ev(1, Base), Ev(2, Base + 1000));

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            var h = await CreateAsync(tenant);
            return await Append(h, batch, server: server);
        }));

        Assert.Equal(2, results.Sum());
        var check = await CreateAsync(tenant);
        Assert.Equal(2, (await check.Repository.QueryAsync(server, null, null, null, 100)).Events.Count);
    }

    // ---- reading -------------------------------------------------------------------------------------------

    [Fact]
    public async Task EventsComeBackNewestFirstAcrossChunks()
    {
        var h = await CreateAsync();
        await Append(h, Batch(Ev(1, Base), Ev(2, Base + 1000)));
        await Append(h, Batch(Ev(3, Base + 2000), Ev(4, Base + 3000)));

        var log = await h.Repository.QueryAsync(h.ServerId, null, null, null, 100);

        Assert.Equal([4L, 3L, 2L, 1L], log.Events.Select(e => e.Sequence).ToArray());
        Assert.False(log.HasMore);
    }

    [Fact]
    public async Task ALimitReturnsTheNewestAndSaysThereIsMore()
    {
        var h = await CreateAsync();
        await Append(h, Batch(Enumerable.Range(1, 10).Select(i => Ev(i, Base + i * 1000)).ToArray()));

        var log = await h.Repository.QueryAsync(h.ServerId, null, null, null, 3);

        Assert.Equal([10L, 9L, 8L], log.Events.Select(e => e.Sequence).ToArray());
        Assert.True(log.HasMore);
    }

    [Fact]
    public async Task ALimitOfExactlyEverythingSaysThereIsNoMore()
    {
        var h = await CreateAsync();
        await Append(h, Batch(Ev(1, Base), Ev(2, Base + 1000), Ev(3, Base + 2000)));

        Assert.False((await h.Repository.QueryAsync(h.ServerId, null, null, null, 3)).HasMore);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100000)]
    public async Task ANonsenseLimitIsClampedNotObeyed(int limit)
    {
        var h = await CreateAsync();
        await Append(h, Batch(Ev(1, Base), Ev(2, Base + 1000)));

        var log = await h.Repository.QueryAsync(h.ServerId, null, null, null, limit);

        Assert.InRange(log.Events.Count, 1, 500);
    }

    [Fact]
    public async Task ThePlayerViewSeesThemAsAttackerOrVictimAndNobodyElse()
    {
        var h = await CreateAsync();
        await Append(h, Batch(
            Ev(1, Base, a: Alice, v: Bob),                                   // Alice hits Bob
            Ev(2, Base + 1000, a: Bob, v: Carol),                            // Bob hits Carol
            Ev(3, Base + 2000, a: Carol, v: Alice),                          // Carol hits Alice
            Ev(4, Base + 3000, a: "bear", ap: false, v: Carol, vp: true)));  // a bear mauls Carol

        var alice = await h.Repository.QueryAsync(h.ServerId, null, null, Alice, 100);
        var carol = await h.Repository.QueryAsync(h.ServerId, null, null, Carol, 100);

        Assert.Equal([3L, 1L], alice.Events.Select(e => e.Sequence).ToArray());
        Assert.Equal([4L, 3L, 2L], carol.Events.Select(e => e.Sequence).ToArray());
    }

    [Fact]
    public async Task APlayerNameThatMatchesAnEntityNameDoesNotMatchThePlayerFilter()
    {
        // The filter is a SteamID64 and only ever matches a party flagged as a player.
        var h = await CreateAsync();
        await Append(h, Batch(Ev(1, Base, a: Alice, ap: false, v: "bear", vp: false)));

        Assert.Empty((await h.Repository.QueryAsync(h.ServerId, null, null, Alice, 100)).Events);
    }

    [Fact]
    public async Task TheTimeWindowKeepsOnlyEventsInsideIt()
    {
        var h = await CreateAsync();
        await Append(h, Batch(Enumerable.Range(1, 10).Select(i => Ev(i, Base + i * 60_000)).ToArray()));

        var log = await h.Repository.QueryAsync(
            h.ServerId, DateTimeOffset.FromUnixTimeMilliseconds(Base + 3 * 60_000), DateTimeOffset.FromUnixTimeMilliseconds(Base + 6 * 60_000), null, 100);

        Assert.Equal([6L, 5L, 4L, 3L], log.Events.Select(e => e.Sequence).ToArray()); // both ends inclusive
    }

    [Fact]
    public async Task AWindowThatMatchesNothingIsEmptyNotAnError()
    {
        var h = await CreateAsync();
        await Append(h, Batch(Ev(1, Base)));

        var log = await h.Repository.QueryAsync(h.ServerId, Now.AddDays(1), null, null, 100);

        Assert.Empty(log.Events);
        Assert.False(log.HasMore);
    }

    [Fact]
    public async Task AnotherServerInTheSameOrganizationIsNotMixedIn()
    {
        var h = await CreateAsync();
        var other = Guid.NewGuid();
        await Append(h, Batch(Ev(1, Base)));
        await Append(h, Batch(Ev(1, Base + 1000, a: Carol)), server: other);

        var log = await h.Repository.QueryAsync(h.ServerId, null, null, null, 100);

        Assert.Equal(Alice, Assert.Single(log.Events).AttackerId);
    }

    [Fact]
    public async Task AnotherOrganizationsEventsAreUnreachableEvenByServerId()
    {
        var a = await CreateAsync();
        var b = await CreateAsync();
        await Append(b, Batch(Ev(1, Base)));

        var log = await a.Repository.QueryAsync(b.ServerId, null, null, null, 100);

        Assert.Empty(log.Events);
    }

    [Fact]
    public async Task ManyChunksAreReadUntilTheNewestPageIsCertain()
    {
        var h = await CreateAsync();
        for (var i = 0; i < 60; i++) // 60 chunks, one event each, older to newer
        {
            await Append(h, Batch(Ev(i + 1, Base + i * 1000)), boot: 1);
        }

        var log = await h.Repository.QueryAsync(h.ServerId, null, null, null, 5);

        Assert.Equal([60L, 59L, 58L, 57L, 56L], log.Events.Select(e => e.Sequence).ToArray());
        Assert.True(log.HasMore);
    }

    // ---- the consumer --------------------------------------------------------------------------------------

    [Fact]
    public async Task TheConsumerStoresWhatTheWorkerSent()
    {
        var h = await CreateAsync();
        var consumer = new PluginCombatEventsCapturedConsumer(h.Repository, TimeProvider.System, NullLogger<PluginCombatEventsCapturedConsumer>.Instance);
        var message = new PluginCombatEventsCaptured(h.ServerId, h.TenantId, 5, false, false, 1, 2, 2, Batch(Ev(1, Base), Ev(2, Base + 1000)), Now);

        await consumer.Consume(ConsumeContextFor(message));

        Assert.Equal(2, (await h.Repository.QueryAsync(h.ServerId, null, null, null, 100)).Events.Count);
    }

    [Fact]
    public async Task TheConsumerRecordsAGapWhenTheWorkerSaysEventsWereLost()
    {
        var h = await CreateAsync();
        var consumer = new PluginCombatEventsCapturedConsumer(h.Repository, TimeProvider.System, NullLogger<PluginCombatEventsCapturedConsumer>.Instance);

        await consumer.Consume(ConsumeContextFor(new PluginCombatEventsCaptured(h.ServerId, h.TenantId, 5, false, true, 1, 1, 1, Batch(Ev(1, Base)), Now)));

        Assert.True((await h.Context.PluginCombatChunks.AsNoTracking().SingleAsync(c => c.RustServerId == h.ServerId)).PrecededByGap);
    }

    [Fact]
    public async Task TheConsumerDropsAMalformedBatchInsteadOfFailingSoItIsNeverRetriedForever()
    {
        var h = await CreateAsync();
        var consumer = new PluginCombatEventsCapturedConsumer(h.Repository, TimeProvider.System, NullLogger<PluginCombatEventsCapturedConsumer>.Instance);

        var ex = await Record.ExceptionAsync(() => consumer.Consume(ConsumeContextFor(
            new PluginCombatEventsCaptured(h.ServerId, h.TenantId, 5, false, false, 1, 1, 1, "{oops", Now))));

        Assert.Null(ex);
        Assert.Equal(0, await h.Context.PluginCombatChunks.CountAsync());
    }

    private static MassTransit.ConsumeContext<PluginCombatEventsCaptured> ConsumeContextFor(PluginCombatEventsCaptured message)
    {
        var context = new Mock<MassTransit.ConsumeContext<PluginCombatEventsCaptured>>();
        context.SetupGet(c => c.Message).Returns(message);
        return context.Object;
    }

    // ---- the endpoint --------------------------------------------------------------------------------------

    private static ServerCombatController Controller(Harness h)
    {
        var servers = new RustServerRepository(h.Context);
        return new ServerCombatController(servers, h.Repository);
    }

    private static async Task<Guid> AddServerAsync(Harness h)
    {
        var server = new RustServer { Id = h.ServerId, TenantId = h.TenantId, Name = "Combat test " + h.ServerId.ToString("N")[..6], Host = "192.0.2.50", Port = 28016, RconPassword = "x" };
        h.Context.Set<RustServer>().Add(server);
        await h.Context.SaveChangesAsync();
        return server.Id;
    }

    [Fact]
    public async Task TheEndpointReturnsTheCombatLogForAServerInTheCallersOrganization()
    {
        var h = await CreateAsync();
        await AddServerAsync(h);
        await Append(h, Batch(Ev(1, Base), Ev(2, Base + 1000)));

        var result = await Controller(h).Get(h.ServerId, null, null, null, 100);

        var log = Assert.IsType<CombatLogDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal([2L, 1L], log.Events.Select(e => e.Sequence).ToArray());
    }

    [Fact]
    public async Task AnotherOrganizationsServerIsA404NeverAnEmptyListThatWouldConfirmItExists()
    {
        var a = await CreateAsync();
        var b = await CreateAsync();
        await AddServerAsync(b);
        await Append(b, Batch(Ev(1, Base)));

        var result = await Controller(a).Get(b.ServerId, null, null, null, 100);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task AnUnknownServerIs404()
    {
        var h = await CreateAsync();

        Assert.IsType<NotFoundResult>((await Controller(h).Get(Guid.NewGuid(), null, null, null, 100)).Result);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1234567890123456789012")]
    [InlineData("7656119800000000 1")]
    [InlineData("76561198000000001'; --")]
    public async Task APlayerIdThatIsNotDigitsIsRefusedBeforeAnyQuery(string playerId)
    {
        var h = await CreateAsync();

        var result = await Controller(h).Get(h.ServerId, null, null, playerId, 100);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task AWindowThatEndsBeforeItStartsIsRefused()
    {
        var h = await CreateAsync();

        var result = await Controller(h).Get(h.ServerId, Now, Now.AddHours(-1), null, 100);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public void TheEndpointRequiresSignInAndTheServerReadPermission()
    {
        var type = typeof(ServerCombatController);

        Assert.NotNull(type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true).SingleOrDefault());
        var permission = (JumpStart.Authorization.RequirePermissionAttribute)type.GetCustomAttributes(typeof(JumpStart.Authorization.RequirePermissionAttribute), true).Single();
        Assert.Equal(PermissionCatalog.ServerGet, permission.Permission);
    }
}
