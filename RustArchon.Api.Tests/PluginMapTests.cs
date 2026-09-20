// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Authorization;
using JumpStart.Data;
using JumpStart.Repositories;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.ObjectStorage;
using RustArchon.Api.Messaging;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;
using SkiaSharp;

namespace RustArchon.Api.Tests;

/// <summary>
/// The world map on the Api side: what is recorded about each world, the one-time token that lets a game server send its
/// picture (bound to one server and one map, single use, expiring, never stored in the clear), when the Api asks for the
/// picture and how it asks, what the upload door accepts and refuses, and what the Panel is allowed to read.
/// </summary>
public class PluginMapTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8];

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Current;
    }

    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Task<Guid?> GetCurrentTenantIdAsync() => Task.FromResult<Guid?>(tenantId);
    }

    private sealed record Harness(ApiDbContext Context, Guid TenantId, Guid ServerId, TestClock Clock)
    {
        public PluginMapRepository Maps => new(Context);
        public PluginMapUploadTokenRepository Tokens => new(Context, Clock);
    }

    private async Task<Harness> CreateAsync(Guid? tenant = null)
    {
        var tenantId = tenant ?? Guid.NewGuid();
        var context = new ApiDbContext(postgres.Options, new FixedTenantContext(tenantId));
        if (!await context.Set<Tenant>().IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId))
        {
            context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Map tenant {tenantId}", IsActive = true });
            await context.SaveChangesAsync();
        }

        return new Harness(context, tenantId, Guid.NewGuid(), new TestClock(Now));
    }

    private static Task<PluginMap> Report(Harness h, int size = 4500, long seed = 1234, bool exists = true, long bytes = 5000, string? monuments = null, DateTimeOffset? at = null) =>
        h.Maps.UpsertStatusAsync(h.TenantId, h.ServerId, size, seed, $"map_{size}_{seed}.png", exists, bytes, monuments, at ?? Now);

    // ---- the map row ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheFirstReportOfAWorldCreatesItsRow()
    {
        var h = await CreateAsync();

        var map = await Report(h, monuments: "[{\"n\":\"A\",\"x\":1,\"y\":2,\"z\":3}]");

        var row = await h.Context.PluginMaps.AsNoTracking().SingleAsync(m => m.RustServerId == h.ServerId);
        Assert.Equal((4500, 1234L, "map_4500_1234.png", true, 5000L), (row.WorldSize, row.WorldSeed, row.FileName, row.ExistsOnServer, row.ServerBytes));
        Assert.Equal(map.Id, row.Id);
        Assert.Equal(Now, row.LastSeenUtc);
        Assert.Null(row.UploadedAtUtc);
        Assert.Contains("\"A\"", row.MonumentsJson);
    }

    [Fact]
    public async Task AnotherReportOfTheSameWorldUpdatesTheRowInsteadOfAddingOne()
    {
        var h = await CreateAsync();
        await Report(h, bytes: 100, monuments: "[]");

        await Report(h, exists: true, bytes: 200, at: Now.AddMinutes(5));

        var row = await h.Context.PluginMaps.AsNoTracking().SingleAsync(m => m.RustServerId == h.ServerId);
        Assert.Equal(200, row.ServerBytes);
        Assert.Equal(Now.AddMinutes(5), row.LastSeenUtc);
        Assert.Equal("[]", row.MonumentsJson);                         // no monuments this time: the stored list is kept
    }

    [Fact]
    public async Task AReportWithMonumentsReplacesTheStoredList()
    {
        var h = await CreateAsync();
        await Report(h, monuments: "[{\"n\":\"Old\",\"x\":1,\"y\":2,\"z\":3}]");

        await Report(h, monuments: "[{\"n\":\"New\",\"x\":1,\"y\":2,\"z\":3}]");

        var row = await h.Context.PluginMaps.AsNoTracking().SingleAsync(m => m.RustServerId == h.ServerId);
        Assert.Contains("New", row.MonumentsJson);
        Assert.DoesNotContain("Old", row.MonumentsJson);
    }

    [Fact]
    public async Task AWipeIsANewRowAndTheOldWorldsPictureIsKept()
    {
        var h = await CreateAsync();
        var first = await Report(h, seed: 1, at: Now);
        await h.Maps.RecordUploadAsync(first.Id, 5000, "aa", "maps/x/4500_1.png", Now);

        await Report(h, seed: 2, at: Now.AddDays(14));

        Assert.Equal(2, await h.Context.PluginMaps.CountAsync(m => m.RustServerId == h.ServerId));
        var old = await h.Context.PluginMaps.AsNoTracking().SingleAsync(m => m.RustServerId == h.ServerId && m.WorldSeed == 1);
        Assert.Equal("maps/x/4500_1.png", old.ObjectKey);
    }

    [Fact]
    public async Task TheCurrentMapIsTheWorldReportedMostRecently()
    {
        var h = await CreateAsync();
        await Report(h, seed: 1, at: Now);
        await Report(h, seed: 2, at: Now.AddDays(14));

        var current = await h.Maps.GetCurrentAsync(h.ServerId);

        Assert.Equal(2, current!.WorldSeed);
    }

    [Fact]
    public async Task AnotherOrganizationCannotReadAServersMap()
    {
        var mine = await CreateAsync();
        var theirs = await CreateAsync();
        await Report(theirs);

        Assert.Null(await mine.Maps.GetCurrentAsync(theirs.ServerId));
    }

    [Fact]
    public async Task AReportThatClaimsAnotherOrganizationsServerIsRefused()
    {
        var a = await CreateAsync();
        await Report(a);
        var impostor = await CreateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            impostor.Maps.UpsertStatusAsync(impostor.TenantId, a.ServerId, 4500, 1234, "map_4500_1234.png", true, 1, null, Now));
    }

    [Fact]
    public async Task AVeryLongFileNameIsCutToFit()
    {
        var h = await CreateAsync();

        var map = await h.Maps.UpsertStatusAsync(h.TenantId, h.ServerId, 4500, 1, new string('f', 500), true, 1, null, Now);

        Assert.Equal(100, map.FileName.Length);
    }

    [Fact]
    public async Task TwoReportsOfANewWorldAtTheSameMomentLeaveOneRow()
    {
        var h = await CreateAsync();
        var other = new Harness(new ApiDbContext(postgres.Options, new FixedTenantContext(h.TenantId)), h.TenantId, h.ServerId, h.Clock);

        await Task.WhenAll(Report(h, seed: 77), Report(other, seed: 77));

        Assert.Equal(1, await h.Context.PluginMaps.CountAsync(m => m.RustServerId == h.ServerId && m.WorldSeed == 77));
    }

    // ---- asking for the picture is throttled ----------------------------------------------------------------------

    [Fact]
    public async Task OnlyOneCallerWinsTheRightToAskAndItIsLostForTheCooldown()
    {
        var h = await CreateAsync();
        var map = await Report(h);
        var cooldown = TimeSpan.FromMinutes(10);

        Assert.True(await h.Maps.TryClaimUploadRequestAsync(map.Id, Now, cooldown));
        Assert.False(await h.Maps.TryClaimUploadRequestAsync(map.Id, Now.AddMinutes(5), cooldown));
        Assert.True(await h.Maps.TryClaimUploadRequestAsync(map.Id, Now.AddMinutes(11), cooldown));
    }

    [Fact]
    public async Task OfManySimultaneousReportsExactlyOneGetsToAsk()
    {
        var h = await CreateAsync();
        var map = await Report(h);
        var callers = Enumerable.Range(0, 8)
            .Select(_ => new PluginMapRepository(new ApiDbContext(postgres.Options, new FixedTenantContext(h.TenantId))))
            .ToList();

        var results = await Task.WhenAll(callers.Select(c => c.TryClaimUploadRequestAsync(map.Id, Now, TimeSpan.FromMinutes(10))));

        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public async Task RecordingAnUploadStoresWhatArrived()
    {
        var h = await CreateAsync();
        var map = await Report(h);

        await h.Maps.RecordUploadAsync(map.Id, 4321, new string('a', 64), "maps/k.png", Now.AddMinutes(1));

        var row = await h.Maps.GetByIdAsync(map.Id);
        Assert.Equal((4321L, new string('a', 64), "maps/k.png", Now.AddMinutes(1)), (row!.UploadedBytes, row.Sha256, row.ObjectKey, row.UploadedAtUtc));
    }

    // ---- the token -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATokenIsRedeemableOnceAndNamesTheServerAndTheMap()
    {
        var h = await CreateAsync();
        var map = await Report(h);
        var token = await h.Tokens.MintAsync(h.TenantId, h.ServerId, map.Id, TimeSpan.FromMinutes(10));

        var first = await h.Tokens.RedeemAsync(token);
        var second = await h.Tokens.RedeemAsync(token);

        Assert.Equal(new PluginMapUploadRedemption(map.Id, h.ServerId), first);
        Assert.Null(second);
    }

    [Fact]
    public async Task ATokenMintedForOneServerNeverNamesAnother()
    {
        var a = await CreateAsync();
        var b = await CreateAsync();
        var mapA = await Report(a);
        var mapB = await Report(b);
        var tokenA = await a.Tokens.MintAsync(a.TenantId, a.ServerId, mapA.Id, TimeSpan.FromMinutes(10));
        await b.Tokens.MintAsync(b.TenantId, b.ServerId, mapB.Id, TimeSpan.FromMinutes(10));

        var redeemed = await a.Tokens.RedeemAsync(tokenA);

        Assert.Equal(a.ServerId, redeemed!.RustServerId);
        Assert.NotEqual(b.ServerId, redeemed.RustServerId);
    }

    [Fact]
    public async Task AnExpiredTokenIsRefused()
    {
        var h = await CreateAsync();
        var map = await Report(h);
        var token = await h.Tokens.MintAsync(h.TenantId, h.ServerId, map.Id, TimeSpan.FromMinutes(10));

        h.Clock.Current = Now.AddMinutes(11);

        Assert.Null(await h.Tokens.RedeemAsync(token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task AnUnknownOrEmptyTokenIsRefused(string token)
    {
        var h = await CreateAsync();

        Assert.Null(await h.Tokens.RedeemAsync(token));
    }

    [Fact]
    public async Task ARevokedTokenIsRefused()
    {
        var h = await CreateAsync();
        var map = await Report(h);
        var token = await h.Tokens.MintAsync(h.TenantId, h.ServerId, map.Id, TimeSpan.FromMinutes(10));

        await h.Tokens.RevokeAsync(token);

        Assert.Null(await h.Tokens.RedeemAsync(token));
    }

    [Fact]
    public async Task OnlyAHashOfTheTokenIsEverStored()
    {
        var h = await CreateAsync();
        var map = await Report(h);
        var token = await h.Tokens.MintAsync(h.TenantId, h.ServerId, map.Id, TimeSpan.FromMinutes(10));

        var row = await h.Context.PluginMapUploadTokens.AsNoTracking().SingleAsync(t => t.PluginMapId == map.Id);

        Assert.NotEqual(token, row.TokenHash);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))), row.TokenHash);
        Assert.DoesNotContain(token, row.TokenHash);
    }

    [Fact]
    public async Task ATokenIsLongRandomAndUrlSafe()
    {
        var h = await CreateAsync();
        var map = await Report(h);

        var a = await h.Tokens.MintAsync(h.TenantId, h.ServerId, map.Id, TimeSpan.FromMinutes(10));
        var b = await h.Tokens.MintAsync(h.TenantId, h.ServerId, map.Id, TimeSpan.FromMinutes(10));

        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 40);
        Assert.All(a, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
    }

    [Fact]
    public async Task OfManySimultaneousRedemptionsExactlyOneSucceeds()
    {
        var h = await CreateAsync();
        var map = await Report(h);
        var token = await h.Tokens.MintAsync(h.TenantId, h.ServerId, map.Id, TimeSpan.FromMinutes(10));
        var callers = Enumerable.Range(0, 8)
            .Select(_ => new PluginMapUploadTokenRepository(new ApiDbContext(postgres.Options, new FixedTenantContext(h.TenantId)), h.Clock))
            .ToList();

        var results = await Task.WhenAll(callers.Select(c => c.RedeemAsync(token)));

        Assert.Equal(1, results.Count(r => r is not null));
    }

    // ---- when the Api asks for the picture -----------------------------------------------------------------------

    private sealed class RequesterKit
    {
        public readonly Mock<IPlatformSettingsCache> Settings = new();
        public readonly Mock<IRequestClient<SendRconCommand>> Client = new();
        public readonly List<SendRconCommand> Sent = [];
        public readonly Harness H;
        public readonly PluginMapUploadRequester Requester;

        public RequesterKit(Harness h, string baseUrl = "http://192.168.0.46:5200", string reply = "{\"v\":1,\"ok\":true,\"data\":{\"started\":true}}", bool success = true)
        {
            H = h;
            Settings.Setup(s => s.GetStringAsync(PlatformSettingsRegistry.PanelBaseUrl)).ReturnsAsync(baseUrl);
            Client.Setup(c => c.GetResponse<RconCommandResult>(It.IsAny<SendRconCommand>(), It.IsAny<CancellationToken>(), It.IsAny<RequestTimeout>()))
                .Callback<SendRconCommand, CancellationToken, RequestTimeout>((request, _, _) => Sent.Add(request))
                .ReturnsAsync(Mock.Of<Response<RconCommandResult>>(r => r.Message == new RconCommandResult(success, reply, null, null, success ? null : "NotConnected")));
            Requester = new PluginMapUploadRequester(h.Maps, h.Tokens, Settings.Object, Client.Object, h.Clock, NullLogger<PluginMapUploadRequester>.Instance);
        }

        public void ClientTimesOut() =>
            Client.Setup(c => c.GetResponse<RconCommandResult>(It.IsAny<SendRconCommand>(), It.IsAny<CancellationToken>(), It.IsAny<RequestTimeout>()))
                .ThrowsAsync(new RequestTimeoutException());
    }

    [Fact]
    public async Task AWantedPictureIsAskedForWithACommandCarryingTheAddressAndAToken()
    {
        var h = await CreateAsync();
        var map = await Report(h, bytes: 24_000_000);
        var kit = new RequesterKit(h);

        var asked = await kit.Requester.RequestIfNeededAsync(map, "idle");

        Assert.True(asked);
        var sent = Assert.Single(kit.Sent);
        Assert.Equal(h.ServerId, sent.ServerId);
        Assert.False(sent.Interactive);                                       // a background command, never shown in the console tab
        var parts = sent.Command.Split(' ');
        Assert.Equal(3, parts.Length);
        Assert.Equal("archon.map.upload", parts[0]);
        Assert.Equal("http://192.168.0.46:5200/ingest/plugin-map", parts[1]);  // a constant address: nothing about the server in it
        Assert.DoesNotContain(parts[2], parts[1]);                             // and the token is not in the address
        Assert.Equal(new PluginMapUploadRedemption(map.Id, h.ServerId), await h.Tokens.RedeemAsync(parts[2]));
    }

    [Fact]
    public async Task ABaseUrlWithAPathKeepsItsPrefix()
    {
        var h = await CreateAsync();
        var map = await Report(h);
        var kit = new RequesterKit(h, baseUrl: "https://panel.example.com/rustarchon/");

        await kit.Requester.RequestIfNeededAsync(map, "idle");

        Assert.Equal("https://panel.example.com/rustarchon/ingest/plugin-map", Assert.Single(kit.Sent).Command.Split(' ')[1]);
    }

    [Fact]
    public async Task NothingIsAskedWhenTheServerHasNoPicture()
    {
        var h = await CreateAsync();
        var map = await Report(h, exists: false, bytes: 0);
        var kit = new RequesterKit(h);

        Assert.False(await kit.Requester.RequestIfNeededAsync(map, "idle"));
        Assert.Empty(kit.Sent);
    }

    [Fact]
    public async Task NothingIsAskedWhileThePluginIsAlreadyUploading()
    {
        var h = await CreateAsync();
        var map = await Report(h);
        var kit = new RequesterKit(h);

        Assert.False(await kit.Requester.RequestIfNeededAsync(map, "uploading"));
        Assert.Empty(kit.Sent);
    }

    [Fact]
    public async Task NothingIsAskedWhenThePanelAlreadyHasThisPicture()
    {
        var h = await CreateAsync();
        var map = await Report(h, bytes: 5000);
        await h.Maps.RecordUploadAsync(map.Id, 5000, "aa", "k", Now);
        var have = (await h.Maps.GetByIdAsync(map.Id))!;
        var kit = new RequesterKit(h);

        Assert.False(await kit.Requester.RequestIfNeededAsync(have, "done"));
        Assert.Empty(kit.Sent);
    }

    [Fact]
    public async Task ARedrawnPictureOfADifferentSizeIsAskedForAgain()
    {
        var h = await CreateAsync();
        var map = await Report(h, bytes: 5000);
        await h.Maps.RecordUploadAsync(map.Id, 5000, "aa", "k", Now);
        var redrawn = await Report(h, bytes: 6000, at: Now.AddDays(1));
        var kit = new RequesterKit(h);

        Assert.True(await kit.Requester.RequestIfNeededAsync(redrawn, "done"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://panel.example.com")]
    [InlineData("panel.example.com")]
    public async Task WithoutAUsableBaseUrlNothingIsMintedOrSent(string baseUrl)
    {
        var h = await CreateAsync();
        var map = await Report(h);
        var kit = new RequesterKit(h, baseUrl);

        Assert.False(await kit.Requester.RequestIfNeededAsync(map, "idle"));

        Assert.Empty(kit.Sent);
        Assert.False(await h.Context.PluginMapUploadTokens.AnyAsync(t => t.PluginMapId == map.Id));
    }

    [Fact]
    public async Task AFailingUploadIsNotRetriedOnEveryReport()
    {
        var h = await CreateAsync();
        var map = await Report(h);
        var kit = new RequesterKit(h);

        Assert.True(await kit.Requester.RequestIfNeededAsync(map, "idle"));
        Assert.False(await kit.Requester.RequestIfNeededAsync(map, "failed"));     // five minutes later: still cooling down
        h.Clock.Current = Now.AddMinutes(11);
        Assert.True(await kit.Requester.RequestIfNeededAsync(map, "failed"));

        Assert.Equal(2, kit.Sent.Count);
    }

    [Fact]
    public async Task AServerThatIsNotConnectedGetsItsTokenRevoked()
    {
        var h = await CreateAsync();
        var map = await Report(h);
        var kit = new RequesterKit(h, success: false, reply: "");

        Assert.False(await kit.Requester.RequestIfNeededAsync(map, "idle"));

        var token = Assert.Single(kit.Sent).Command.Split(' ')[2];
        Assert.Null(await h.Tokens.RedeemAsync(token));
    }

    [Theory]
    [InlineData("{\"v\":1,\"ok\":false,\"err\":\"players_online\"}")]
    [InlineData("Unknown command: archon.map.upload")]
    [InlineData("")]
    [InlineData("{\"ok\":\"true\"}")]
    [InlineData("[]")]
    public async Task APluginReplyThatIsNotClearlyOkRevokesTheToken(string reply)
    {
        var h = await CreateAsync();
        var map = await Report(h);
        var kit = new RequesterKit(h, reply: reply);

        Assert.False(await kit.Requester.RequestIfNeededAsync(map, "idle"));

        var token = Assert.Single(kit.Sent).Command.Split(' ')[2];
        Assert.Null(await h.Tokens.RedeemAsync(token));
    }

    [Fact]
    public async Task ATimeoutRevokesTheToken()
    {
        var h = await CreateAsync();
        var map = await Report(h);
        var kit = new RequesterKit(h);
        kit.ClientTimesOut();

        Assert.False(await kit.Requester.RequestIfNeededAsync(map, "idle"));

        Assert.Empty(kit.Sent);
        var row = await h.Context.PluginMapUploadTokens.AsNoTracking().Where(t => t.PluginMapId == map.Id).ToListAsync();
        Assert.Empty(row);                                                   // minted, then discarded
    }

    // ---- the consumer --------------------------------------------------------------------------------------------

    private static ConsumeContext<PluginMapStatusCaptured> Context(PluginMapStatusCaptured message)
    {
        var context = new Mock<ConsumeContext<PluginMapStatusCaptured>>();
        context.SetupGet(c => c.Message).Returns(message);
        return context.Object;
    }

    private static PluginMapStatusCaptured Status(Harness h, int size = 4500, long seed = 1234, string file = "map_4500_1234.png", bool exists = true, long bytes = 5000, string upload = "idle", string? monuments = null) =>
        new(h.ServerId, h.TenantId, size, seed, file, exists, bytes, upload, monuments, Now);

    [Fact]
    public async Task TheConsumerRecordsTheWorldAndThenAsksForThePictureWithTheUploadState()
    {
        var h = await CreateAsync();
        var requester = new Mock<IPluginMapUploadRequester>();
        var consumer = new PluginMapStatusCapturedConsumer(h.Maps, requester.Object, NullLogger<PluginMapStatusCapturedConsumer>.Instance);

        await consumer.Consume(Context(Status(h, upload: "failed", monuments: "[]")));

        var row = await h.Context.PluginMaps.AsNoTracking().SingleAsync(m => m.RustServerId == h.ServerId);
        Assert.Equal(4500, row.WorldSize);
        requester.Verify(r => r.RequestIfNeededAsync(It.Is<PluginMap>(m => m.Id == row.Id), "failed"), Times.Once);
    }

    [Theory]
    [InlineData(0, 1L, "map_0_1.png")]
    [InlineData(4500, -1L, "map_4500_-1.png")]
    [InlineData(4500, 1L, "")]
    public async Task AStatusThatNamesNoWorldIsDroppedWithoutStoringOrAsking(int size, long seed, string file)
    {
        var h = await CreateAsync();
        var requester = new Mock<IPluginMapUploadRequester>();
        var consumer = new PluginMapStatusCapturedConsumer(h.Maps, requester.Object, NullLogger<PluginMapStatusCapturedConsumer>.Instance);

        await consumer.Consume(Context(Status(h, size: size, seed: seed, file: file)));

        Assert.False(await h.Context.PluginMaps.AnyAsync(m => m.RustServerId == h.ServerId));
        requester.Verify(r => r.RequestIfNeededAsync(It.IsAny<PluginMap>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AnOversizedMonumentListIsIgnoredButTheStatusIsStillRecorded()
    {
        var h = await CreateAsync();
        var requester = new Mock<IPluginMapUploadRequester>();
        var consumer = new PluginMapStatusCapturedConsumer(h.Maps, requester.Object, NullLogger<PluginMapStatusCapturedConsumer>.Instance);
        var huge = "[" + string.Join(",", Enumerable.Repeat("{\"n\":\"aaaaaaaaaaaaaaaaaaaa\",\"x\":1,\"y\":2,\"z\":3}", 8000)) + "]";
        Assert.True(huge.Length > PluginMapStatusCapturedConsumer.MaxMonumentsJsonLength);

        await consumer.Consume(Context(Status(h, monuments: huge)));

        var row = await h.Context.PluginMaps.AsNoTracking().SingleAsync(m => m.RustServerId == h.ServerId);
        Assert.Null(row.MonumentsJson);
    }

    [Fact]
    public async Task AStatusForAnotherOrganizationsServerIsDroppedNotThrown()
    {
        var owner = await CreateAsync();
        await Report(owner);
        var impostor = await CreateAsync();
        var requester = new Mock<IPluginMapUploadRequester>();
        var consumer = new PluginMapStatusCapturedConsumer(impostor.Maps, requester.Object, NullLogger<PluginMapStatusCapturedConsumer>.Instance);

        await consumer.Consume(Context(new PluginMapStatusCaptured(owner.ServerId, impostor.TenantId, 4500, 1234, "map_4500_1234.png", true, 1, "idle", null, Now)));

        requester.Verify(r => r.RequestIfNeededAsync(It.IsAny<PluginMap>(), It.IsAny<string>()), Times.Never);
    }

    // ---- the upload door -----------------------------------------------------------------------------------------

    private static MapPreviewService Previews(Harness h, IObjectStorage storage) =>
        new(new MapPreviewRenderer(), storage, h.Maps, NullLogger<MapPreviewService>.Instance);

    private sealed class MemoryStorage : IObjectStorage
    {
        public readonly Dictionary<string, (byte[] Content, string ContentType)> Objects = [];
        public Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken = default) { Objects[key] = (content, contentType); return Task.CompletedTask; }
        public Task<ObjectContent?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Objects.TryGetValue(key, out var o) ? new ObjectContent(o.Content, o.ContentType) : null);
        public Task DeleteByPrefixAsync(string prefix, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static InternalPluginMapController Door(Harness h, MemoryStorage storage, byte[] body, long? declared = null, bool noLength = false)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(body);
        if (!noLength) { context.Request.ContentLength = declared ?? body.Length; }
        return new InternalPluginMapController(h.Tokens, h.Maps, storage, Previews(h, storage), h.Clock, NullLogger<InternalPluginMapController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private async Task<(Harness H, PluginMap Map, string Token)> ReadyToUploadAsync()
    {
        var h = await CreateAsync();
        var map = await Report(h, bytes: Png.Length);
        var token = await h.Tokens.MintAsync(h.TenantId, h.ServerId, map.Id, TimeSpan.FromMinutes(10));
        return (h, map, token);
    }

    [Fact]
    public async Task AGoodTokenAndAPngIsStoredAndRecordedWithItsHash()
    {
        var (h, map, token) = await ReadyToUploadAsync();
        var storage = new MemoryStorage();

        var result = await Door(h, storage, Png).Upload(token);

        Assert.IsType<NoContentResult>(result);
        var key = $"maps/{h.ServerId}/4500_1234.png";
        Assert.Equal(Png, storage.Objects[key].Content);
        Assert.Equal("image/png", storage.Objects[key].ContentType);
        var row = await h.Maps.GetByIdAsync(map.Id);
        Assert.Equal((Png.Length, Convert.ToHexStringLower(SHA256.HashData(Png)), key), ((int)row!.UploadedBytes!, row.Sha256, row.ObjectKey));
        Assert.Equal(Now, row.UploadedAtUtc);
    }

    [Fact]
    public async Task ATokenCannotBeUsedTwiceToStoreTwoPictures()
    {
        var (h, _, token) = await ReadyToUploadAsync();
        var storage = new MemoryStorage();

        Assert.IsType<NoContentResult>(await Door(h, storage, Png).Upload(token));
        var again = await Door(h, storage, Png.Concat(new byte[] { 9 }).ToArray()).Upload(token);

        Assert.IsType<NotFoundResult>(again);
        Assert.Single(storage.Objects);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nope")]
    public async Task ABadOrMissingTokenIsANotFoundAndNothingIsStored(string? token)
    {
        var (h, _, _) = await ReadyToUploadAsync();
        var storage = new MemoryStorage();

        var result = await Door(h, storage, Png).Upload(token);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(storage.Objects);
    }

    [Fact]
    public async Task ATokenLongerThanAnyRealOneIsANotFoundWithoutEvenALookup()
    {
        var (h, _, _) = await ReadyToUploadAsync();

        var result = await Door(h, new MemoryStorage(), Png).Upload(new string('a', InternalPluginMapController.MaxTokenLength + 1));

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task AnExpiredTokenIsANotFoundAndNothingIsStored()
    {
        var (h, _, token) = await ReadyToUploadAsync();
        h.Clock.Current = Now.AddMinutes(11);
        var storage = new MemoryStorage();

        Assert.IsType<NotFoundResult>(await Door(h, storage, Png).Upload(token));
        Assert.Empty(storage.Objects);
    }

    [Fact]
    public async Task ABodyThatIsNotAPngIsRefusedAndNothingIsStored()
    {
        var (h, map, token) = await ReadyToUploadAsync();
        var storage = new MemoryStorage();

        var result = await Door(h, storage, Encoding.ASCII.GetBytes("<html>definitely not a png</html>")).Upload(token);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(storage.Objects);
        Assert.Null((await h.Maps.GetByIdAsync(map.Id))!.UploadedAtUtc);
    }

    [Fact]
    public async Task ABodyShorterThanThePngSignatureIsRefused()
    {
        var (h, _, token) = await ReadyToUploadAsync();

        Assert.IsType<BadRequestObjectResult>(await Door(h, new MemoryStorage(), [0x89, 0x50]).Upload(token));
    }

    [Fact]
    public async Task ABodyLargerThanItDeclaredIsRefused()
    {
        var (h, _, token) = await ReadyToUploadAsync();
        var storage = new MemoryStorage();

        var result = await Door(h, storage, Png, declared: Png.Length - 4).Upload(token);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(storage.Objects);
    }

    [Fact]
    public async Task ABodyShorterThanItDeclaredIsRefused()
    {
        var (h, _, token) = await ReadyToUploadAsync();
        var storage = new MemoryStorage();

        var result = await Door(h, storage, Png, declared: Png.Length + 100).Upload(token);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(storage.Objects);
    }

    [Fact]
    public async Task AZeroDeclaredLengthIsRefused()
    {
        var (h, _, token) = await ReadyToUploadAsync();

        Assert.IsType<BadRequestObjectResult>(await Door(h, new MemoryStorage(), Png, declared: 0).Upload(token));
    }

    [Fact]
    public async Task NoDeclaredLengthIsRefused()
    {
        var (h, _, token) = await ReadyToUploadAsync();

        Assert.IsType<BadRequestObjectResult>(await Door(h, new MemoryStorage(), Png, noLength: true).Upload(token));
    }

    [Fact]
    public async Task ALengthOverTheCeilingIsRefusedBeforeAnyBodyIsRead()
    {
        var (h, _, token) = await ReadyToUploadAsync();
        var storage = new MemoryStorage();

        var result = await Door(h, storage, Png, declared: InternalPluginMapController.MaxBytes + 1).Upload(token);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(storage.Objects);
    }

    [Fact]
    public async Task TheDoorRequiresTheInternalServiceKeyAndIsNotOpenToUsers()
    {
        var authorize = (Microsoft.AspNetCore.Authorization.AuthorizeAttribute)Attribute.GetCustomAttribute(
            typeof(InternalPluginMapController), typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute))!;

        Assert.Equal("InternalApiKey", authorize.AuthenticationSchemes);
        await Task.CompletedTask;
    }

    // ---- what the Panel reads ------------------------------------------------------------------------------------

    private static ServerMapController Reader(Harness h, MemoryStorage storage, bool serverExists = true, string? ifNoneMatch = null)
    {
        var servers = new Mock<IRustServerRepository>();
        servers.Setup(s => s.GetByIdAsync(h.ServerId, null)).ReturnsAsync(serverExists ? new RustServer { Id = h.ServerId } : null);
        var context = new DefaultHttpContext();
        if (ifNoneMatch is not null) { context.Request.Headers.IfNoneMatch = ifNoneMatch; }
        return new ServerMapController(servers.Object, h.Maps, storage, Previews(h, storage)) { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    [Fact]
    public async Task AnUnknownOrOtherOrganizationsServerIsANotFoundForBothReads()
    {
        var h = await CreateAsync();
        await Report(h);

        Assert.IsType<NotFoundResult>((await Reader(h, new MemoryStorage(), serverExists: false).Get(h.ServerId)).Result);
        Assert.IsType<NotFoundResult>(await Reader(h, new MemoryStorage(), serverExists: false).Image(h.ServerId));
    }

    [Fact]
    public async Task AServerWithNoMapYetReadsAsNotAvailable()
    {
        var h = await CreateAsync();

        var dto = Assert.IsType<MapDto>(Assert.IsType<OkObjectResult>((await Reader(h, new MemoryStorage()).Get(h.ServerId)).Result).Value);

        Assert.False(dto.Available);
        Assert.Empty(dto.Monuments);
    }

    [Fact]
    public async Task AMapThatHasBeenReportedButNotUploadedIsNotAvailableYetButKnowsItsWorld()
    {
        var h = await CreateAsync();
        await Report(h, monuments: "[{\"n\":\"Launch Site\",\"x\":10,\"y\":2,\"z\":-30}]");

        var dto = Assert.IsType<MapDto>(Assert.IsType<OkObjectResult>((await Reader(h, new MemoryStorage()).Get(h.ServerId)).Result).Value);

        Assert.False(dto.Available);
        Assert.Equal((4500, 1234L), (dto.WorldSize, dto.WorldSeed));
        var monument = Assert.Single(dto.Monuments);
        Assert.Equal(("Launch Site", 10.0, 2.0, -30.0), (monument.Name, monument.X, monument.Y, monument.Z));
    }

    [Fact]
    public async Task AnUploadedMapIsAvailableWithItsSizeTimeAndEntityTag()
    {
        var h = await CreateAsync();
        var map = await Report(h);
        await h.Maps.RecordUploadAsync(map.Id, 5000, "abc123", "maps/k.png", Now.AddMinutes(1));

        var dto = Assert.IsType<MapDto>(Assert.IsType<OkObjectResult>((await Reader(h, new MemoryStorage()).Get(h.ServerId)).Result).Value);

        Assert.True(dto.Available);
        Assert.Equal((5000L, "abc123", (DateTimeOffset?)Now.AddMinutes(1)), (dto.ImageBytes, dto.ImageEtag, dto.UploadedAtUtc));
    }

    [Fact]
    public async Task MonumentsFromThePluginAreReReadDefensively()
    {
        var h = await CreateAsync();
        var json = "[{\"n\":\"" + new string('n', 500) + "\",\"x\":1,\"y\":2,\"z\":3},{\"x\":1},{\"n\":\"NoPos\"},{\"n\":5,\"x\":1,\"y\":1,\"z\":1},7,null,{\"n\":\"Fine\",\"x\":4,\"y\":5,\"z\":6}]";
        await Report(h, monuments: json);

        var dto = Assert.IsType<MapDto>(Assert.IsType<OkObjectResult>((await Reader(h, new MemoryStorage()).Get(h.ServerId)).Result).Value);

        Assert.Equal(2, dto.Monuments.Count);
        Assert.Equal(100, dto.Monuments[0].Name.Length);
        Assert.Equal("Fine", dto.Monuments[1].Name);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("\"x\"")]
    public async Task MonumentsThatAreNotAnArrayReadAsNone(string json)
    {
        var h = await CreateAsync();
        await Report(h, monuments: json);

        var dto = Assert.IsType<MapDto>(Assert.IsType<OkObjectResult>((await Reader(h, new MemoryStorage()).Get(h.ServerId)).Result).Value);

        Assert.Empty(dto.Monuments);
    }

    [Fact]
    public async Task TheMonumentCountIsCapped()
    {
        var h = await CreateAsync();
        await Report(h, monuments: "[" + string.Join(",", Enumerable.Repeat("{\"n\":\"M\",\"x\":1,\"y\":2,\"z\":3}", 800)) + "]");

        var dto = Assert.IsType<MapDto>(Assert.IsType<OkObjectResult>((await Reader(h, new MemoryStorage()).Get(h.ServerId)).Result).Value);

        Assert.Equal(500, dto.Monuments.Count);
    }

    private async Task<(Harness H, MemoryStorage Storage)> WithPictureAsync()
    {
        var h = await CreateAsync();
        var map = await Report(h);
        var storage = new MemoryStorage();
        var key = $"maps/{h.ServerId}/4500_1234.png";
        await storage.PutAsync(key, Png, "image/png");
        await h.Maps.RecordUploadAsync(map.Id, Png.Length, "deadbeef", key, Now);
        return (h, storage);
    }

    [Fact]
    public async Task TheImageIsServedWithItsEntityTagAndCacheHeaders()
    {
        var (h, storage) = await WithPictureAsync();
        var reader = Reader(h, storage);

        var result = await reader.Image(h.ServerId);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/png", file.ContentType);
        Assert.Equal(Png, file.FileContents);
        Assert.Equal("\"deadbeef\"", reader.Response.Headers.ETag.ToString());
        Assert.Contains("private", reader.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task AMatchingEntityTagGetsANotModifiedAndNoBody()
    {
        var (h, storage) = await WithPictureAsync();

        var result = await Reader(h, storage, ifNoneMatch: "\"deadbeef\"").Image(h.ServerId);

        Assert.Equal(304, Assert.IsType<StatusCodeResult>(result).StatusCode);
    }

    [Fact]
    public async Task ADifferentEntityTagGetsTheImage()
    {
        var (h, storage) = await WithPictureAsync();

        Assert.IsType<FileContentResult>(await Reader(h, storage, ifNoneMatch: "\"other\"").Image(h.ServerId));
    }

    [Fact]
    public async Task NoPictureYetIsANotFoundForTheImage()
    {
        var h = await CreateAsync();
        await Report(h);

        Assert.IsType<NotFoundResult>(await Reader(h, new MemoryStorage()).Image(h.ServerId));
    }

    [Fact]
    public async Task APictureMissingFromStorageIsANotFoundNotAServerError()
    {
        var (h, _) = await WithPictureAsync();

        Assert.IsType<NotFoundResult>(await Reader(h, new MemoryStorage()).Image(h.ServerId));
    }

    [Fact]
    public void TheMapIsGatedLikeReadingAServerNotLikeThePlayerLayers()
    {
        var gate = (RequirePermissionAttribute)Attribute.GetCustomAttribute(typeof(ServerMapController), typeof(RequirePermissionAttribute))!;

        Assert.Equal(PermissionCatalog.ServerGet, gate.Permission);
    }

    // ---- the display-sized preview -------------------------------------------------------------------------------

    /// <summary>A real PNG (the fake one above is only a signature) of <paramref name="width"/> x <paramref name="height"/>.</summary>
    private static byte[] RealPng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.SteelBlue);
        using var paint = new SKPaint { Color = SKColors.DarkGreen };
        canvas.DrawRect(width / 4f, height / 4f, width / 2f, height / 2f, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static (int Width, int Height) Dimensions(byte[] image)
    {
        using var bitmap = SKBitmap.Decode(image);
        return (bitmap.Width, bitmap.Height);
    }

    [Fact]
    public void ALargePictureIsShrunkToTheLongestSideLimitAsAWebP()
    {
        var preview = new MapPreviewRenderer().Render(RealPng(7000, 7000));

        Assert.NotNull(preview);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(preview![..4]));       // a WebP is a RIFF container...
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(preview[8..12]));      // ...whose form type is WEBP
        Assert.Equal((MapPreviewRenderer.MaxSide, MapPreviewRenderer.MaxSide), Dimensions(preview));
    }

    [Fact]
    public void ASmallPictureIsNeverScaledUp()
    {
        var preview = new MapPreviewRenderer().Render(RealPng(500, 500));

        Assert.Equal((500, 500), Dimensions(preview!));
    }

    [Fact]
    public void ANonSquarePictureKeepsItsShape()
    {
        var preview = new MapPreviewRenderer().Render(RealPng(8000, 4000));

        Assert.Equal((6144, 3072), Dimensions(preview!));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8 })]     // the PNG signature and nothing real
    public void BytesThatAreNotAReadableImageGiveNoPreviewAndNoException(byte[] junk)
    {
        Assert.Null(new MapPreviewRenderer().Render(junk));
    }

    [Fact]
    public void ATruncatedPictureGivesNoPreviewAndNoException()
    {
        var png = RealPng(800, 800);

        Assert.Null(new MapPreviewRenderer().Render(png[..(png.Length / 3)]));
    }

    [Fact]
    public async Task APreviewIsStoredAndRecordedAlongsideTheOriginalWhenThePictureArrives()
    {
        var (h, map, token) = await ReadyToUploadAsync();
        var storage = new MemoryStorage();
        var png = RealPng(2500, 2500);

        var result = await Door(h, storage, png).Upload(token);

        Assert.IsType<NoContentResult>(result);
        var row = (await h.Maps.GetByIdAsync(map.Id))!;
        Assert.Equal($"maps/{h.ServerId}/4500_1234.preview{MapPreviewRenderer.MaxSide}.webp", row.PreviewObjectKey);
        Assert.Equal(storage.Objects[row.PreviewObjectKey!].Content.Length, row.PreviewBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(storage.Objects[row.PreviewObjectKey!].Content)), row.PreviewSha256);
        Assert.Equal("image/webp", storage.Objects[row.PreviewObjectKey!].ContentType);
        Assert.Equal(png, storage.Objects[row.ObjectKey!].Content);              // the original is untouched
    }

    [Fact]
    public async Task APictureThatCannotBePreviewedStillUploadsAndIsServedFromTheOriginal()
    {
        var (h, map, token) = await ReadyToUploadAsync();
        var storage = new MemoryStorage();

        var result = await Door(h, storage, Png).Upload(token);           // a PNG signature only: passes the door, cannot be decoded

        Assert.IsType<NoContentResult>(result);
        var row = (await h.Maps.GetByIdAsync(map.Id))!;
        Assert.NotNull(row.ObjectKey);
        Assert.Null(row.PreviewObjectKey);
    }

    private sealed class FailingPreviewStorage : IObjectStorage
    {
        public readonly MemoryStorage Inner = new();
        public Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken = default) =>
            key.Contains(".preview") ? throw new IOException("storage is down") : Inner.PutAsync(key, content, contentType, cancellationToken);
        public Task<ObjectContent?> GetAsync(string key, CancellationToken cancellationToken = default) => Inner.GetAsync(key, cancellationToken);
        public Task DeleteByPrefixAsync(string prefix, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task AFailureStoringThePreviewNeverFailsTheUpload()
    {
        var (h, map, token) = await ReadyToUploadAsync();
        var storage = new FailingPreviewStorage();
        var context = new DefaultHttpContext();
        var png = RealPng(600, 600);
        context.Request.Body = new MemoryStream(png);
        context.Request.ContentLength = png.Length;
        var door = new InternalPluginMapController(h.Tokens, h.Maps, storage, Previews(h, storage), h.Clock, NullLogger<InternalPluginMapController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var result = await door.Upload(token);

        Assert.IsType<NoContentResult>(result);
        var row = (await h.Maps.GetByIdAsync(map.Id))!;
        Assert.NotNull(row.UploadedAtUtc);
        Assert.Null(row.PreviewObjectKey);
    }

    [Fact]
    public async Task ANewPictureClearsThePreviewOfTheOldOne()
    {
        var h = await CreateAsync();
        var map = await Report(h);
        await h.Maps.RecordUploadAsync(map.Id, 10, "old", "maps/old.png", Now);
        await h.Maps.RecordPreviewAsync(map.Id, "maps/old.preview.jpg", 5, "oldpreview");

        await h.Maps.RecordUploadAsync(map.Id, 20, "new", "maps/new.png", Now.AddDays(14));

        var row = (await h.Maps.GetByIdAsync(map.Id))!;
        Assert.Equal("maps/new.png", row.ObjectKey);
        Assert.Null(row.PreviewObjectKey);
        Assert.Null(row.PreviewBytes);
        Assert.Null(row.PreviewSha256);
    }

    private async Task<(Harness H, PluginMap Map, MemoryStorage Storage, byte[] Png)> WithRealPictureAsync(bool withPreview)
    {
        var h = await CreateAsync();
        var map = await Report(h);
        var storage = new MemoryStorage();
        var png = RealPng(2600, 2600);
        var key = $"maps/{h.ServerId}/4500_1234.png";
        await storage.PutAsync(key, png, "image/png");
        await h.Maps.RecordUploadAsync(map.Id, png.Length, Convert.ToHexStringLower(SHA256.HashData(png)), key, Now);
        if (withPreview)
        {
            Assert.True(await Previews(h, storage).CreateAsync((await h.Maps.GetByIdAsync(map.Id))!, png));
        }

        return (h, (await h.Maps.GetByIdAsync(map.Id))!, storage, png);
    }

    [Fact]
    public async Task TheImageIsThePreviewByDefaultWithThePreviewsOwnEntityTag()
    {
        var (h, map, storage, png) = await WithRealPictureAsync(withPreview: true);
        var reader = Reader(h, storage);

        var result = await reader.Image(h.ServerId);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/webp", file.ContentType);
        Assert.Equal(map.PreviewBytes, file.FileContents.Length);                  // the stored preview, not the original
        Assert.NotEqual(png, file.FileContents);
        Assert.Equal($"\"{map.PreviewSha256}\"", reader.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task TheFullOriginalIsAvailableOnRequestWithItsOwnEntityTag()
    {
        var (h, map, storage, png) = await WithRealPictureAsync(withPreview: true);
        var reader = Reader(h, storage);

        var result = await reader.Image(h.ServerId, full: true);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/png", file.ContentType);
        Assert.Equal(png, file.FileContents);
        Assert.Equal($"\"{map.Sha256}\"", reader.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task APictureCollectedBeforePreviewsExistedGetsItsPreviewOnFirstRequestOnly()
    {
        var (h, map, storage, _) = await WithRealPictureAsync(withPreview: false);
        Assert.Null(map.PreviewObjectKey);

        var first = await Reader(h, storage).Image(h.ServerId);
        var afterFirst = (await h.Maps.GetByIdAsync(map.Id))!;
        var puts = storage.Objects.Count;
        var second = await Reader(h, storage).Image(h.ServerId);

        Assert.Equal("image/webp", Assert.IsType<FileContentResult>(first).ContentType);
        Assert.NotNull(afterFirst.PreviewObjectKey);
        Assert.Equal(puts, storage.Objects.Count);                                // nothing was made again
        Assert.Equal("image/webp", Assert.IsType<FileContentResult>(second).ContentType);
    }

    [Fact]
    public async Task AMatchingPreviewEntityTagGetsANotModified()
    {
        var (h, map, storage, _) = await WithRealPictureAsync(withPreview: true);

        var result = await Reader(h, storage, ifNoneMatch: $"\"{map.PreviewSha256}\"").Image(h.ServerId);

        Assert.Equal(304, Assert.IsType<StatusCodeResult>(result).StatusCode);
    }

    [Fact]
    public async Task TheOriginalsEntityTagDoesNotMatchTheDefaultPreview()
    {
        var (h, map, storage, _) = await WithRealPictureAsync(withPreview: true);

        var result = await Reader(h, storage, ifNoneMatch: $"\"{map.Sha256}\"").Image(h.ServerId);

        Assert.IsType<FileContentResult>(result);                                 // a different resource: not a 304
    }

    [Fact]
    public async Task APictureThatCannotBePreviewedIsServedFromTheOriginalInstead()
    {
        var h = await CreateAsync();
        var map = await Report(h);
        var storage = new MemoryStorage();
        var key = $"maps/{h.ServerId}/4500_1234.png";
        await storage.PutAsync(key, Png, "image/png");
        await h.Maps.RecordUploadAsync(map.Id, Png.Length, "cafe", key, Now);

        var result = await Reader(h, storage).Image(h.ServerId);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/png", file.ContentType);
        Assert.Equal(Png, file.FileContents);
    }

    [Fact]
    public async Task TheMapDescriptionReportsThePreviewsSizeAndEntityTagNotTheOriginals()
    {
        var (h, map, storage, _) = await WithRealPictureAsync(withPreview: true);

        var dto = Assert.IsType<MapDto>(Assert.IsType<OkObjectResult>((await Reader(h, storage).Get(h.ServerId)).Result).Value);

        Assert.Equal(map.PreviewBytes, dto.ImageBytes);
        Assert.Equal(map.PreviewSha256, dto.ImageEtag);
        Assert.NotEqual(map.UploadedBytes, dto.ImageBytes);
    }

    [Fact]
    public async Task APreviewMadeAtAnOlderSizeIsRecognisedAsStaleMadeAgainAndTheOldOneRemoved()
    {
        var (h, map, storage, _) = await WithRealPictureAsync(withPreview: false);
        var oldKey = $"maps/{h.ServerId}/4500_1234.preview.jpg";                     // what an earlier build named it (JPEG, at an older size)
        await storage.PutAsync(oldKey, [0xFF, 0xD8, 1, 2, 3], "image/jpeg");
        await h.Maps.RecordPreviewAsync(map.Id, oldKey, 5, "stale");
        var stale = (await h.Maps.GetByIdAsync(map.Id))!;
        var deleted = new List<string>();
        var tracking = new TrackingStorage(storage, deleted);

        var fresh = await Previews(h, tracking).EnsureAsync(stale);

        Assert.Equal(MapPreviewService.PreviewKey(fresh), fresh.PreviewObjectKey);
        Assert.NotEqual("stale", fresh.PreviewSha256);
        Assert.Equal([oldKey], deleted);
    }

    [Fact]
    public async Task ACurrentPreviewIsLeftAloneByEnsure()
    {
        var (h, map, storage, _) = await WithRealPictureAsync(withPreview: true);
        var puts = storage.Objects.Count;

        var same = await Previews(h, storage).EnsureAsync(map);

        Assert.Equal(map.PreviewSha256, same.PreviewSha256);
        Assert.Equal(puts, storage.Objects.Count);
    }

    private sealed class TrackingStorage(MemoryStorage inner, List<string> deleted) : IObjectStorage
    {
        public Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken = default) => inner.PutAsync(key, content, contentType, cancellationToken);
        public Task<ObjectContent?> GetAsync(string key, CancellationToken cancellationToken = default) => inner.GetAsync(key, cancellationToken);
        public Task DeleteByPrefixAsync(string prefix, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) { deleted.Add(key); return Task.CompletedTask; }
    }

    [Fact]
    public async Task EnsuringAPreviewForAMapWithNoOriginalStoredChangesNothing()
    {
        var h = await CreateAsync();
        var map = await Report(h);

        var same = await Previews(h, new MemoryStorage()).EnsureAsync(map);

        Assert.Same(map, same);
    }
}
