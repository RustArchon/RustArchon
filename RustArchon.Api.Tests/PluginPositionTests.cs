// Copyright ©2026 Scott Blomfield

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using JumpStart.Authorization;
using JumpStart.Data;
using JumpStart.Repositories;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Messaging;
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// The player position pipeline on the Api side: the codec that refuses anything not exactly what the plugin sends, storage
/// against a real Postgres (a batch sent twice never doubles, another organization's data is unreachable, the player and
/// time-window views are exact), the consumer, the endpoint and its permission (and its audit log), and retention.
/// </summary>
public class PluginPositionTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Alice = "76561198000000001";
    private const string Bob = "76561198000000002";
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly long Base = Now.ToUnixTimeMilliseconds();

    private static string Sample(long seq, long tMs, string player = Alice, double x = 1, double y = 2, double z = 3, int r = 90, string extra = "") =>
        "{\"s\":" + seq + ",\"t\":" + tMs + ",\"p\":\"" + player + "\",\"x\":" + x.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
        + ",\"y\":" + y.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
        + ",\"z\":" + z.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ",\"r\":" + r + extra + "}";

    private static string Batch(params string[] samples) => "[" + string.Join(",", samples) + "]";

    // ---- the codec -----------------------------------------------------------------------------------------------

    [Fact]
    public void AWellFormedBatchParsesInOrderKeepingEachSamplesOwnJson()
    {
        var a = Sample(1, Base);
        var b = Sample(2, Base + 5000, Bob, extra: ",\"n\":\"Bob\",\"e\":\"on\"");

        var parsed = PositionChunkCodec.Parse(Batch(a, b));

        Assert.Equal([1L, 2L], parsed.Select(s => s.Sequence).ToArray());
        Assert.Equal([Alice, Bob], parsed.Select(s => s.PlayerId).ToArray());
        Assert.Equal([a, b], parsed.Select(s => s.RawJson).ToArray());
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(Base + 5000), parsed[1].OccurredAtUtc);
    }

    [Fact]
    public void ACompressedBatchDecodesBackToTheSamples()
    {
        var parsed = PositionChunkCodec.Parse(Batch(
            Sample(1, Base, Alice, 10.5, 20.5, -30.5, 270, ",\"n\":\"Ünï\",\"e\":\"on\""),
            Sample(2, Base + 5000, Alice, 11.5, 20.5, -30.5, 0, ",\"e\":\"off\"")));

        var decoded = PositionChunkCodec.Decode(PositionChunkCodec.Compress(parsed), 1);

        Assert.Equal(2, decoded.Count);
        Assert.Equal((1L, Alice, 10.5, 20.5, -30.5, 270, "Ünï", "on"), (decoded[0].Sequence, decoded[0].PlayerId, decoded[0].X, decoded[0].Y, decoded[0].Z, decoded[0].Yaw, decoded[0].PlayerName, decoded[0].Marker));
        Assert.Equal("off", decoded[1].Marker);
        Assert.Equal(string.Empty, decoded[1].PlayerName);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("\"x\"")]
    [InlineData("[1]")]
    [InlineData("[null]")]
    [InlineData("[{\"s\":1}]")]                                                              // no time, player, position
    [InlineData("[{\"s\":0,\"t\":1,\"p\":\"76561198000000001\",\"x\":1,\"y\":1,\"z\":1}]")]   // sequence starts at 1
    [InlineData("[{\"s\":1,\"t\":-1,\"p\":\"76561198000000001\",\"x\":1,\"y\":1,\"z\":1}]")]  // a time before 1970
    [InlineData("[{\"s\":1,\"t\":1,\"p\":\"\",\"x\":1,\"y\":1,\"z\":1}]")]                    // no player
    [InlineData("[{\"s\":1,\"t\":1,\"p\":\"Alice\",\"x\":1,\"y\":1,\"z\":1}]")]               // not a SteamID
    [InlineData("[{\"s\":1,\"t\":1,\"p\":\"765611980000000010000\",\"x\":1,\"y\":1,\"z\":1}]")] // too long
    [InlineData("[{\"s\":1,\"t\":1,\"p\":\"76561198000000001\",\"x\":\"1\",\"y\":1,\"z\":1}]")] // a position that is text
    [InlineData("[{\"s\":1,\"t\":1,\"p\":\"76561198000000001\",\"x\":1,\"y\":1}]")]            // a missing coordinate
    [InlineData("[{\"s\":1,\"t\":1,\"p\":\"76561198000000001\",\"x\":1,\"y\":1,\"z\":1},{\"s\":1,\"t\":2,\"p\":\"76561198000000001\",\"x\":1,\"y\":1,\"z\":1}]")] // repeats a sequence
    public void AnythingNotExactlyWhatThePluginSendsIsRefusedWhole(string json)
    {
        Assert.Throws<PositionChunkCodec.InvalidPositionBatchException>(() => PositionChunkCodec.Parse(json));
    }

    [Fact]
    public void ABatchLargerThanTheCapIsRefused()
    {
        var big = Batch(Enumerable.Range(1, PositionChunkCodec.MaxSamplesPerBatch + 1).Select(i => Sample(i, Base)).ToArray());

        Assert.Throws<PositionChunkCodec.InvalidPositionBatchException>(() => PositionChunkCodec.Parse(big));
    }

    [Fact]
    public void AnUnknownFormatCannotBeDecoded()
    {
        Assert.Throws<InvalidOperationException>(() => PositionChunkCodec.Decode(PositionChunkCodec.Compress([]), 2));
    }

    [Fact]
    public void ADecompressionBombIsStoppedAtTheSizeLimit()
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var chunk = Encoding.UTF8.GetBytes(new string(' ', 1024 * 1024));
            for (var i = 0; i < 10; i++) { gzip.Write(chunk, 0, chunk.Length); }
        }

        Assert.Throws<InvalidOperationException>(() => PositionChunkCodec.Decode(output.ToArray(), 1));
    }

    [Fact]
    public void ALongNameIsCutAndAnOddMarkerAndOutOfRangeYawAreNormalized()
    {
        var parsed = PositionChunkCodec.Parse(Batch(Sample(1, Base, r: 720, extra: ",\"n\":\"" + new string('n', 500) + "\",\"e\":\"<script>\"")));

        var dto = PositionChunkCodec.Decode(PositionChunkCodec.Compress(parsed), 1).Single();

        Assert.Equal(100, dto.PlayerName.Length);
        Assert.Equal(string.Empty, dto.Marker);
        Assert.Equal(359, dto.Yaw);
    }

    // ---- storage -------------------------------------------------------------------------------------------------

    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Task<Guid?> GetCurrentTenantIdAsync() => Task.FromResult<Guid?>(tenantId);
    }

    private sealed record Harness(ApiDbContext Context, PluginPositionChunkRepository Repository, Guid TenantId, Guid ServerId);

    private async Task<Harness> CreateAsync(Guid? tenant = null)
    {
        var tenantId = tenant ?? Guid.NewGuid();
        var context = new ApiDbContext(postgres.Options, new FixedTenantContext(tenantId));
        if (!await context.Set<Tenant>().IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId))
        {
            context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Position tenant {tenantId}", IsActive = true });
            await context.SaveChangesAsync();
        }

        return new Harness(context, new PluginPositionChunkRepository(context), tenantId, Guid.NewGuid());
    }

    private static Task<int> Append(Harness h, string json, long boot = 1, bool gap = false, Guid? server = null) =>
        h.Repository.AppendAsync(h.TenantId, server ?? h.ServerId, boot, gap, json, Now);

    [Fact]
    public async Task ABatchIsStoredAsOneChunkWithItsRangeTimesAndPlayers()
    {
        var h = await CreateAsync();

        var stored = await Append(h, Batch(Sample(1, Base), Sample(2, Base + 5000, Bob)));

        Assert.Equal(2, stored);
        var chunk = await h.Context.PluginPositionChunks.AsNoTracking().SingleAsync(c => c.RustServerId == h.ServerId);
        Assert.Equal((1L, 2L, 2), (chunk.FirstSequence, chunk.LastSequence, chunk.SampleCount));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(Base), chunk.FromUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(Base + 5000), chunk.ToUtc);
        Assert.Equal([Alice, Bob], chunk.PlayerIds.OrderBy(x => x).ToArray());
        Assert.False(chunk.PrecededByGap);
    }

    [Fact]
    public async Task AnEmptyBatchStoresNothing()
    {
        var h = await CreateAsync();

        Assert.Equal(0, await Append(h, "[]"));
        Assert.False(await h.Context.PluginPositionChunks.AnyAsync(c => c.RustServerId == h.ServerId));
    }

    [Fact]
    public async Task AMalformedBatchStoresNothingAndSaysWhy()
    {
        var h = await CreateAsync();

        await Assert.ThrowsAsync<PositionChunkCodec.InvalidPositionBatchException>(() => Append(h, "[{\"s\":1}]"));
        Assert.False(await h.Context.PluginPositionChunks.AnyAsync(c => c.RustServerId == h.ServerId));
    }

    [Fact]
    public async Task TheSameBatchSentTwiceIsStoredOnce()
    {
        var h = await CreateAsync();
        var batch = Batch(Sample(1, Base), Sample(2, Base + 5000));

        Assert.Equal(2, await Append(h, batch));
        Assert.Equal(0, await Append(h, batch));

        Assert.Equal(1, await h.Context.PluginPositionChunks.CountAsync(c => c.RustServerId == h.ServerId));
    }

    [Fact]
    public async Task AnOverlappingBatchStoresOnlyTheNewSamples()
    {
        var h = await CreateAsync();
        await Append(h, Batch(Sample(1, Base), Sample(2, Base + 5000)));

        var stored = await Append(h, Batch(Sample(2, Base + 5000), Sample(3, Base + 10000)));

        Assert.Equal(1, stored);
        var newest = await h.Context.PluginPositionChunks.AsNoTracking().OrderByDescending(c => c.LastSequence).FirstAsync(c => c.RustServerId == h.ServerId);
        Assert.Equal((3L, 3L), (newest.FirstSequence, newest.LastSequence));
    }

    [Fact]
    public async Task BatchesArrivingOutOfOrderAreBothKept()
    {
        var h = await CreateAsync();

        Assert.Equal(1, await Append(h, Batch(Sample(3, Base + 10000))));
        Assert.Equal(2, await Append(h, Batch(Sample(1, Base), Sample(2, Base + 5000))));
    }

    [Fact]
    public async Task ASecondRunOfThePluginRestartsItsSequenceNumbersWithoutClashing()
    {
        var h = await CreateAsync();

        Assert.Equal(1, await Append(h, Batch(Sample(1, Base)), boot: 1));
        Assert.Equal(1, await Append(h, Batch(Sample(1, Base + 60000)), boot: 2, gap: true));

        var second = await h.Context.PluginPositionChunks.AsNoTracking().SingleAsync(c => c.RustServerId == h.ServerId && c.BootId == 2);
        Assert.True(second.PrecededByGap);
    }

    [Fact]
    public async Task AnotherOrganizationsPositionsAreUnreachable()
    {
        var mine = await CreateAsync();
        var theirs = await CreateAsync();
        await Append(theirs, Batch(Sample(1, Base)));

        var seenByMe = await mine.Repository.QueryAsync(theirs.ServerId, null, null, null, 100);

        Assert.Empty(seenByMe.Samples);
    }

    [Fact]
    public async Task QueryReturnsNewestFirstAndCanNarrowToOnePlayer()
    {
        var h = await CreateAsync();
        await Append(h, Batch(Sample(1, Base, Alice), Sample(2, Base + 5000, Bob), Sample(3, Base + 10000, Alice)));

        var all = await h.Repository.QueryAsync(h.ServerId, null, null, null, 100);
        var justAlice = await h.Repository.QueryAsync(h.ServerId, null, null, Alice, 100);

        Assert.Equal([3L, 2L, 1L], all.Samples.Select(s => s.Sequence).ToArray());
        Assert.Equal([3L, 1L], justAlice.Samples.Select(s => s.Sequence).ToArray());
        Assert.False(all.HasMore);
    }

    [Fact]
    public async Task QueryHonoursTheTimeWindowExactly()
    {
        var h = await CreateAsync();
        await Append(h, Batch(Sample(1, Base), Sample(2, Base + 5000), Sample(3, Base + 10000), Sample(4, Base + 15000)));

        var window = await h.Repository.QueryAsync(
            h.ServerId, DateTimeOffset.FromUnixTimeMilliseconds(Base + 5000), DateTimeOffset.FromUnixTimeMilliseconds(Base + 10000), null, 100);

        Assert.Equal([3L, 2L], window.Samples.Select(s => s.Sequence).ToArray());
    }

    [Fact]
    public async Task QueryStopsAtTheLimitAndSaysThereIsMore()
    {
        var h = await CreateAsync();
        await Append(h, Batch(Enumerable.Range(1, 10).Select(i => Sample(i, Base + i * 1000L)).ToArray()));

        var page = await h.Repository.QueryAsync(h.ServerId, null, null, null, 4);

        Assert.Equal([10L, 9L, 8L, 7L], page.Samples.Select(s => s.Sequence).ToArray());
        Assert.True(page.HasMore);
    }

    [Fact]
    public async Task QueryAcrossSeveralChunksStaysInOrder()
    {
        var h = await CreateAsync();
        await Append(h, Batch(Sample(1, Base), Sample(2, Base + 1000)));
        await Append(h, Batch(Sample(3, Base + 2000), Sample(4, Base + 3000)));
        await Append(h, Batch(Sample(5, Base + 4000)));

        var all = await h.Repository.QueryAsync(h.ServerId, null, null, null, 100);

        Assert.Equal([5L, 4L, 3L, 2L, 1L], all.Samples.Select(s => s.Sequence).ToArray());
    }

    // ---- the consumer --------------------------------------------------------------------------------------------

    private static PluginPositionsCaptured Message(Harness h, string json, long boot = 1, bool lost = false, bool reset = false) =>
        new(h.ServerId, h.TenantId, boot, reset, lost, 1, 1, 1, json, Now);

    private static ConsumeContext<PluginPositionsCaptured> Context(PluginPositionsCaptured message)
    {
        var context = new Mock<ConsumeContext<PluginPositionsCaptured>>();
        context.SetupGet(c => c.Message).Returns(message);
        return context.Object;
    }

    [Fact]
    public async Task TheConsumerStoresABatchAndMarksAGapWhenTheRingWrappedOrThePluginReloaded()
    {
        var h = await CreateAsync();
        var consumer = new PluginPositionsCapturedConsumer(h.Repository, TimeProvider.System, NullLogger<PluginPositionsCapturedConsumer>.Instance);

        await consumer.Consume(Context(Message(h, Batch(Sample(1, Base)), reset: true)));
        await consumer.Consume(Context(Message(h, Batch(Sample(2, Base + 5000)), lost: true)));
        await consumer.Consume(Context(Message(h, Batch(Sample(3, Base + 10000)))));

        var chunks = await h.Context.PluginPositionChunks.AsNoTracking().Where(c => c.RustServerId == h.ServerId).OrderBy(c => c.FirstSequence).ToListAsync();
        Assert.Equal([true, true, false], chunks.Select(c => c.PrecededByGap).ToArray());
    }

    [Fact]
    public async Task TheConsumerDropsAMalformedBatchWithoutThrowingSoItIsNeverRetried()
    {
        var h = await CreateAsync();
        var consumer = new PluginPositionsCapturedConsumer(h.Repository, TimeProvider.System, NullLogger<PluginPositionsCapturedConsumer>.Instance);

        await consumer.Consume(Context(Message(h, "[{\"s\":1}]")));

        Assert.False(await h.Context.PluginPositionChunks.AnyAsync(c => c.RustServerId == h.ServerId));
    }

    // ---- the endpoint --------------------------------------------------------------------------------------------

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public readonly System.Collections.Generic.List<string> Lines = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    private static ServerPositionsController Controller(Harness h, bool serverExists, CapturingLogger<ServerPositionsController>? logger = null, string? email = "owner@example.com")
    {
        var servers = new Mock<IRustServerRepository>();
        servers.Setup(s => s.GetByIdAsync(h.ServerId, null)).ReturnsAsync(serverExists ? new RustServer { Id = h.ServerId } : null);

        var identity = new ClaimsIdentity(email is null ? [] : [new Claim(ClaimTypes.Email, email)], "test");
        return new ServerPositionsController(servers.Object, h.Repository, logger ?? new CapturingLogger<ServerPositionsController>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } }
        };
    }

    [Fact]
    public async Task ThePositionsEndpointReturnsWhatIsStored()
    {
        var h = await CreateAsync();
        await Append(h, Batch(Sample(1, Base, Alice, 5, 6, 7)));

        var result = await Controller(h, serverExists: true).Get(h.ServerId, null, null, null);

        var dto = Assert.IsType<PositionsDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(5, Assert.Single(dto.Samples).X);
    }

    [Fact]
    public async Task AnUnknownOrOtherOrganizationsServerIsANotFoundNeverAnEmptyList()
    {
        var h = await CreateAsync();

        var result = await Controller(h, serverExists: false).Get(h.ServerId, null, null, null);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Theory]
    [InlineData("Alice")]
    [InlineData("1; drop table")]
    [InlineData("765611980000000010000")]
    public async Task ANonSteamIdPlayerFilterIsABadRequest(string player)
    {
        var h = await CreateAsync();

        var result = await Controller(h, serverExists: true).Get(h.ServerId, null, null, player);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task ASinceLaterThanUntilIsABadRequest()
    {
        var h = await CreateAsync();

        var result = await Controller(h, serverExists: true).Get(h.ServerId, Now, Now.AddHours(-1), null);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task EveryViewIsAuditLoggedWithWhoLookedAndWhichPlayer()
    {
        var h = await CreateAsync();
        var log = new CapturingLogger<ServerPositionsController>();

        await Controller(h, serverExists: true, log, "boss@example.com").Get(h.ServerId, null, null, Alice);

        var line = Assert.Single(log.Lines);
        Assert.Contains("boss@example.com", line);
        Assert.Contains(h.ServerId.ToString(), line);
        Assert.Contains(Alice, line);
    }

    [Fact]
    public async Task AViewWithNoNameOnTheTokenIsStillLogged()
    {
        var h = await CreateAsync();
        var log = new CapturingLogger<ServerPositionsController>();

        await Controller(h, serverExists: true, log, email: null).Get(h.ServerId, null, null, null);

        Assert.Contains("unknown", Assert.Single(log.Lines));
    }

    [Fact]
    public async Task AViewOfAMissingServerIsNotLoggedAsAView()
    {
        var h = await CreateAsync();
        var log = new CapturingLogger<ServerPositionsController>();

        await Controller(h, serverExists: false, log).Get(h.ServerId, null, null, null);

        Assert.Empty(log.Lines);
    }

    // ---- the permission ------------------------------------------------------------------------------------------

    [Fact]
    public void TheEndpointIsGatedByItsOwnPermissionNotByOrdinaryServerAccess()
    {
        var gate = (RequirePermissionAttribute)Attribute.GetCustomAttribute(typeof(ServerPositionsController), typeof(RequirePermissionAttribute))!;

        Assert.Equal(PermissionCatalog.ServerViewPositions, gate.Permission);
        Assert.NotEqual(PermissionCatalog.ServerGet, gate.Permission);
    }

    [Fact]
    public void TheOwnerHoldsItAndItIsDeclaredAndDelegable()
    {
        Assert.Contains(PermissionCatalog.ServerViewPositions, PermissionCatalog.OwnerPermissions);
        Assert.Contains(PermissionCatalog.ServerViewPositions, PermissionCatalog.All.Select(p => p.Name));
        Assert.True(PermissionCatalog.All.Single(p => p.Name == PermissionCatalog.ServerViewPositions).DelegableByTenantAdmin);
    }

    [Fact]
    public void ItIsNotFoldedIntoOrdinaryServerAccess()
    {
        Assert.NotEqual(PermissionCatalog.ServerGet, PermissionCatalog.ServerViewPositions);
        Assert.NotEqual(PermissionCatalog.ServerViewBases, PermissionCatalog.ServerViewPositions);
    }

    // ---- retention -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task RetentionRemovesPositionChunksPastTheDefaultWindowAndKeepsNewerOnes()
    {
        var h = await CreateAsync();
        var server = Guid.NewGuid();
        await h.Repository.AppendAsync(h.TenantId, server, 1, false, Batch(Sample(1, Now.AddDays(-40).ToUnixTimeMilliseconds())), Now);
        await h.Repository.AppendAsync(h.TenantId, server, 1, false, Batch(Sample(2, Now.AddDays(-10).ToUnixTimeMilliseconds())), Now);

        var removed = await new PluginDataRetention(h.Context, NullLogger<PluginDataRetention>.Instance).PruneAsync(Now);

        Assert.True(removed >= 1);
        var left = await h.Context.PluginPositionChunks.AsNoTracking().Where(c => c.RustServerId == server).ToListAsync();
        Assert.Equal(2L, Assert.Single(left).FirstSequence);
    }

    [Fact]
    public async Task RetentionNeverTouchesAnotherOrganizationsRecentPositions()
    {
        var a = await CreateAsync();
        var b = await CreateAsync();
        await a.Repository.AppendAsync(a.TenantId, a.ServerId, 1, false, Batch(Sample(1, Now.AddDays(-40).ToUnixTimeMilliseconds())), Now);
        await b.Repository.AppendAsync(b.TenantId, b.ServerId, 1, false, Batch(Sample(1, Now.AddDays(-1).ToUnixTimeMilliseconds())), Now);

        await new PluginDataRetention(a.Context, NullLogger<PluginDataRetention>.Instance).PruneAsync(Now);

        Assert.False(await a.Context.PluginPositionChunks.AnyAsync(c => c.RustServerId == a.ServerId));
        Assert.True(await b.Context.PluginPositionChunks.AnyAsync(c => c.RustServerId == b.ServerId));
    }
}
