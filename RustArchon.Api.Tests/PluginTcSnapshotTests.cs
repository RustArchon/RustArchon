// Copyright ©2026 Scott Blomfield

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
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

namespace RustArchon.Api.Tests;

/// <summary>
/// The tool cupboard snapshot: what the codec accepts (a plugin on someone else's server sends this, so nothing is
/// trusted), that a snapshot replaces the last and an older one never replaces a newer, that one organization can never
/// see or overwrite another's, and that reading it needs the bases permission - which an Owner holds and a
/// server-viewer does not.
/// </summary>
public class PluginTcSnapshotTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Alice = "76561198000000001";
    private const string Bob = "76561198000000002";
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static string Tc(int id, string owner = Alice, double x = 10.5, double y = 2.0, double z = -30.25, params (string Id, string Name)[] authorized) =>
        "{\"i\":" + id + ",\"x\":" + x.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"y\":" + y.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ",\"z\":" + z.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"o\":\"" + owner + "\",\"a\":["
        + string.Join(",", authorized.Select(a => "{\"i\":\"" + a.Id + "\",\"n\":\"" + a.Name + "\"}")) + "]}";

    private static string List(params string[] tcs) => "[" + string.Join(",", tcs) + "]";

    private static TcSnapshotCodec.InvalidTcSnapshotException Refused(string json) =>
        Assert.Throws<TcSnapshotCodec.InvalidTcSnapshotException>(() => TcSnapshotCodec.Parse(json));

    // ---- the codec -----------------------------------------------------------------------------------------

    [Fact]
    public void AWellFormedListIsParsed()
    {
        var tcs = TcSnapshotCodec.Parse(List(Tc(7, Alice, 1.5, 2.5, 3.5, (Alice, "Alice"), (Bob, "Bob")), Tc(8, Bob)));

        Assert.Equal([7, 8], tcs.Select(t => t.Id).ToArray());
        Assert.Equal((1.5, 2.5, 3.5), (tcs[0].X, tcs[0].Y, tcs[0].Z));
        Assert.Equal(Alice, tcs[0].OwnerId);
        Assert.Equal([Alice, Bob], tcs[0].Authorized.Select(a => a.PlayerId).ToArray());
        Assert.Equal("Bob", tcs[0].Authorized[1].Name);
        Assert.Empty(tcs[1].Authorized);
    }

    [Fact]
    public void AnEmptyListIsValid()
    {
        Assert.Empty(TcSnapshotCodec.Parse("[]"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[1]")]
    [InlineData("[null]")]
    [InlineData("[[]]")]
    public void AnythingButAnArrayOfObjectsIsRefused(string json)
    {
        Refused(json);
    }

    [Theory]
    [InlineData("{\"x\":1,\"y\":1,\"z\":1,\"o\":\"76561198000000001\"}")]                       // no id
    [InlineData("{\"i\":\"1\",\"x\":1,\"y\":1,\"z\":1,\"o\":\"76561198000000001\"}")]           // id not a number
    [InlineData("{\"i\":1,\"y\":1,\"z\":1,\"o\":\"76561198000000001\"}")]                       // no x
    [InlineData("{\"i\":1,\"x\":\"1\",\"y\":1,\"z\":1,\"o\":\"76561198000000001\"}")]           // x a string
    [InlineData("{\"i\":1,\"x\":1,\"y\":1,\"z\":1}")]                                           // no owner
    [InlineData("{\"i\":1,\"x\":1,\"y\":1,\"z\":1,\"o\":\"bear\"}")]                            // owner not a SteamID
    [InlineData("{\"i\":1,\"x\":1,\"y\":1,\"z\":1,\"o\":\"123456789012345678901\"}")]           // 21 digits
    [InlineData("{\"i\":1,\"x\":1,\"y\":1,\"z\":1,\"o\":\"1; DROP TABLE\"}")]
    [InlineData("{\"i\":1,\"x\":1,\"y\":1,\"z\":1,\"o\":76561198000000001}")]                   // owner a number, not text
    public void ACupboardMissingOrMisusingARequiredFieldRefusesTheWholeList(string bad)
    {
        Refused(List(Tc(1), bad));
    }

    [Theory]
    [InlineData("{\"i\":\"x\"}")]
    [InlineData("{\"n\":\"no id\"}")]
    [InlineData("{\"i\":\"bear\"}")]
    [InlineData("1")]
    [InlineData("null")]
    public void AnAuthorizedEntryWithoutAValidPlayerIdRefusesTheWholeList(string entry)
    {
        var json = "[{\"i\":1,\"x\":1,\"y\":1,\"z\":1,\"o\":\"" + Alice + "\",\"a\":[" + entry + "]}]";

        Refused(json);
    }

    [Fact]
    public void AnAuthorizedListThatIsNotAnArrayIsRefused()
    {
        Refused("[{\"i\":1,\"x\":1,\"y\":1,\"z\":1,\"o\":\"" + Alice + "\",\"a\":{}}]");
    }

    [Fact]
    public void TooManyAuthorizedOnOneCupboardIsRefused()
    {
        var many = Enumerable.Range(0, TcSnapshotCodec.MaxAuthorizedPerCupboard + 1).Select(i => ((76561198000000000L + i).ToString(), "n")).ToArray();

        Refused(List(Tc(1, Alice, 0, 0, 0, many)));
    }

    [Fact]
    public void TooManyCupboardsAreRefused()
    {
        var json = List(Enumerable.Range(1, TcSnapshotCodec.MaxCupboards + 1).Select(i => Tc(i)).ToArray());

        Refused(json);
    }

    [Fact]
    public void HostileNamesAreCutToAReasonableLengthAndKeptAsText()
    {
        var huge = new string('x', 5000);

        var tc = Assert.Single(TcSnapshotCodec.Parse(List(Tc(1, Alice, 0, 0, 0, (Bob, huge)))));

        Assert.Equal(100, tc.Authorized[0].Name.Length);
    }

    [Fact]
    public void AMissingNameIsJustEmptyNotAnError()
    {
        var json = "[{\"i\":1,\"x\":1,\"y\":1,\"z\":1,\"o\":\"" + Alice + "\",\"a\":[{\"i\":\"" + Bob + "\"}]}]";

        Assert.Equal("", TcSnapshotCodec.Parse(json)[0].Authorized[0].Name);
    }

    [Fact]
    public void ASnapshotRoundTripsThroughCompressionAndBack()
    {
        var original = TcSnapshotCodec.Parse(List(Tc(7, Alice, 1.5, 2.5, 3.5, (Alice, "Alice"), (Bob, "Bob")), Tc(8, Bob)));

        var back = TcSnapshotCodec.Decode(TcSnapshotCodec.Compress(original), 1);

        Assert.Equal(2, back.Count);
        Assert.Equal(7, back[0].Id);
        Assert.Equal((1.5, 2.5, 3.5), (back[0].X, back[0].Y, back[0].Z));
        Assert.Equal(["Alice", "Bob"], back[0].Authorized.Select(a => a.Name).ToArray());
        Assert.Equal(Bob, back[1].OwnerId);
    }

    [Fact]
    public void AnUnsupportedFormatIsRefusedRatherThanGuessed()
    {
        Assert.Throws<InvalidOperationException>(() => TcSnapshotCodec.Decode(TcSnapshotCodec.Compress([]), 2));
    }

    [Fact]
    public void ADecompressionBombIsStoppedAtTheSizeLimit()
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            var chunk = Encoding.UTF8.GetBytes(new string(' ', 1024 * 1024));
            for (var i = 0; i < 40; i++) { gzip.Write(chunk, 0, chunk.Length); }
        }

        Assert.Throws<InvalidOperationException>(() => TcSnapshotCodec.Decode(output.ToArray(), 1));
    }

    // ---- storage -------------------------------------------------------------------------------------------

    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Task<Guid?> GetCurrentTenantIdAsync() => Task.FromResult<Guid?>(tenantId);
    }

    private sealed record Harness(ApiDbContext Context, PluginTcSnapshotRepository Repository, Guid TenantId, Guid ServerId);

    private async Task<Harness> CreateAsync(Guid? tenant = null)
    {
        var tenantId = tenant ?? Guid.NewGuid();
        var context = new ApiDbContext(postgres.Options, new FixedTenantContext(tenantId));
        if (!await context.Set<Tenant>().IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId))
        {
            context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Bases tenant {tenantId}", IsActive = true });
            await context.SaveChangesAsync();
        }

        return new Harness(context, new PluginTcSnapshotRepository(context), tenantId, Guid.NewGuid());
    }

    [Fact]
    public async Task ASnapshotIsStoredAndReadBack()
    {
        var h = await CreateAsync();

        Assert.True(await h.Repository.ReplaceAsync(h.TenantId, h.ServerId, true, List(Tc(1, Alice, 5, 6, 7, (Alice, "Alice"))), T0));

        var bases = await h.Repository.GetAsync(h.ServerId);
        Assert.True(bases.Ready);
        Assert.Equal(T0, bases.CapturedAtUtc);
        var tc = Assert.Single(bases.Tcs);
        Assert.Equal((5.0, 6.0, 7.0), (tc.X, tc.Y, tc.Z));
        Assert.Equal("Alice", Assert.Single(tc.Authorized).Name);
    }

    [Fact]
    public async Task ANewSnapshotReplacesTheLastNotAddsToIt()
    {
        var h = await CreateAsync();
        await h.Repository.ReplaceAsync(h.TenantId, h.ServerId, true, List(Tc(1), Tc(2), Tc(3)), T0);

        await h.Repository.ReplaceAsync(h.TenantId, h.ServerId, true, List(Tc(2)), T0.AddMinutes(1));

        Assert.Equal(new[] { 2 }, (await h.Repository.GetAsync(h.ServerId)).Tcs.Select(t => t.Id).ToArray());
        Assert.Equal(1, await h.Context.PluginTcSnapshots.CountAsync(s => s.RustServerId == h.ServerId)); // one row per server
    }

    [Fact]
    public async Task AnOlderSnapshotNeverReplacesANewerOne()
    {
        var h = await CreateAsync();
        await h.Repository.ReplaceAsync(h.TenantId, h.ServerId, true, List(Tc(2)), T0.AddMinutes(5));

        var replaced = await h.Repository.ReplaceAsync(h.TenantId, h.ServerId, true, List(Tc(1)), T0);   // arrived late

        Assert.False(replaced);
        Assert.Equal(new[] { 2 }, (await h.Repository.GetAsync(h.ServerId)).Tcs.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task ANotReadySnapshotSaysSoAndIsStillStored()
    {
        var h = await CreateAsync();

        await h.Repository.ReplaceAsync(h.TenantId, h.ServerId, false, List(Tc(1)), T0);

        var bases = await h.Repository.GetAsync(h.ServerId);
        Assert.False(bases.Ready);
        Assert.Single(bases.Tcs);
    }

    [Fact]
    public async Task ANeverReadServerIsAnEmptyNotReadyPictureNotAnError()
    {
        var h = await CreateAsync();

        var bases = await h.Repository.GetAsync(h.ServerId);

        Assert.False(bases.Ready);
        Assert.Null(bases.CapturedAtUtc);
        Assert.Empty(bases.Tcs);
    }

    [Fact]
    public async Task AMalformedSnapshotStoresNothingAndKeepsTheOldOne()
    {
        var h = await CreateAsync();
        await h.Repository.ReplaceAsync(h.TenantId, h.ServerId, true, List(Tc(1)), T0);

        await Assert.ThrowsAsync<TcSnapshotCodec.InvalidTcSnapshotException>(
            () => h.Repository.ReplaceAsync(h.TenantId, h.ServerId, true, "{oops", T0.AddMinutes(1)));

        Assert.Equal(new[] { 1 }, (await h.Repository.GetAsync(h.ServerId)).Tcs.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task OneOrganizationsSnapshotIsInvisibleToAnother()
    {
        var a = await CreateAsync();
        var b = await CreateAsync();
        await b.Repository.ReplaceAsync(b.TenantId, b.ServerId, true, List(Tc(1)), T0);

        var seenByA = await a.Repository.GetAsync(b.ServerId);

        Assert.Empty(seenByA.Tcs);
        Assert.Null(seenByA.CapturedAtUtc);
    }

    [Fact]
    public async Task AMessageClaimingAnotherOrganizationsServerCannotOverwriteItsSnapshot()
    {
        var owner = await CreateAsync();
        var intruder = await CreateAsync();
        await owner.Repository.ReplaceAsync(owner.TenantId, owner.ServerId, true, List(Tc(1)), T0);

        var replaced = await intruder.Repository.ReplaceAsync(intruder.TenantId, owner.ServerId, true, List(Tc(99)), T0.AddMinutes(9));

        Assert.False(replaced);
        Assert.Equal(new[] { 1 }, (await owner.Repository.GetAsync(owner.ServerId)).Tcs.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task SeveralFirstReadsOfANewServerArrivingTogetherLeaveOneRow()
    {
        var tenant = Guid.NewGuid();
        var server = Guid.NewGuid();
        await using (var seed = (await CreateAsync(tenant)).Context) { }

        await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            var h = await CreateAsync(tenant);
            await h.Repository.ReplaceAsync(tenant, server, true, List(Tc(1)), T0.AddSeconds(i));
        }));

        var check = await CreateAsync(tenant);
        Assert.Equal(1, await check.Context.PluginTcSnapshots.CountAsync(s => s.RustServerId == server));
    }

    [Fact]
    public async Task TheConsumerStoresTheSnapshotAndDropsAMalformedOneWithoutFailing()
    {
        var h = await CreateAsync();
        var consumer = new PluginTcSnapshotCapturedConsumer(h.Repository, NullLogger<PluginTcSnapshotCapturedConsumer>.Instance);

        await consumer.Consume(ContextFor(new PluginTcSnapshotCaptured(h.ServerId, h.TenantId, true, 1, List(Tc(1)), T0)));
        var ex = await Record.ExceptionAsync(() => consumer.Consume(ContextFor(new PluginTcSnapshotCaptured(h.ServerId, h.TenantId, true, 1, "{oops", T0.AddMinutes(1)))));

        Assert.Null(ex);
        Assert.Single((await h.Repository.GetAsync(h.ServerId)).Tcs);
    }

    private static MassTransit.ConsumeContext<PluginTcSnapshotCaptured> ContextFor(PluginTcSnapshotCaptured message)
    {
        var context = new Mock<MassTransit.ConsumeContext<PluginTcSnapshotCaptured>>();
        context.SetupGet(c => c.Message).Returns(message);
        return context.Object;
    }

    // ---- the endpoint and who may call it -------------------------------------------------------------------

    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public readonly System.Collections.Generic.List<string> Messages = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private static ServerBasesController Controller(Harness h, string? email = "owner@example.com", Microsoft.Extensions.Logging.ILogger<ServerBasesController>? logger = null)
    {
        var identity = new System.Security.Claims.ClaimsIdentity(email is null ? [] : [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Email, email)], "test");
        return new ServerBasesController(new RustServerRepository(h.Context), h.Repository, new PlayerSessionRepository(h.Context), logger ?? NullLogger<ServerBasesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(identity) } }
        };
    }

    private static async Task AddServerAsync(Harness h)
    {
        h.Context.Set<RustServer>().Add(new RustServer { Id = h.ServerId, TenantId = h.TenantId, Name = "Bases test " + h.ServerId.ToString("N")[..6], Host = "192.0.2.60", Port = 28016, RconPassword = "x" });
        await h.Context.SaveChangesAsync();
    }

    [Fact]
    public async Task TheEndpointReturnsTheBasesOfAServerInTheCallersOrganization()
    {
        var h = await CreateAsync();
        await AddServerAsync(h);
        await h.Repository.ReplaceAsync(h.TenantId, h.ServerId, true, List(Tc(1)), T0);

        var result = await Controller(h).Get(h.ServerId);

        Assert.Single(Assert.IsType<BasesDto>(Assert.IsType<OkObjectResult>(result.Result).Value).Tcs);
    }

    [Fact]
    public async Task EveryReadIsLoggedWithWhoMadeItAndWhichServer()
    {
        var h = await CreateAsync();
        await AddServerAsync(h);
        var logger = new CapturingLogger<ServerBasesController>();

        await Controller(h, "moderator@example.com", logger).Get(h.ServerId);

        var line = Assert.Single(logger.Messages);
        Assert.Contains("moderator@example.com", line);
        Assert.Contains(h.ServerId.ToString(), line);
    }

    [Fact]
    public async Task ARefusedReadOfAnotherOrganizationsServerIsNotLoggedAsAView()
    {
        var a = await CreateAsync();
        var b = await CreateAsync();
        await AddServerAsync(b);
        var logger = new CapturingLogger<ServerBasesController>();

        await Controller(a, logger: logger).Get(b.ServerId);

        Assert.Empty(logger.Messages);
    }

    [Fact]
    public async Task AnotherOrganizationsServerIsA404NeverAnEmptyPicture()
    {
        var a = await CreateAsync();
        var b = await CreateAsync();
        await AddServerAsync(b);

        Assert.IsType<NotFoundResult>((await Controller(a).Get(b.ServerId)).Result);
    }

    [Fact]
    public void TheEndpointNeedsTheBasesPermissionNotJustPermissionToViewTheServer()
    {
        var type = typeof(ServerBasesController);

        var permission = (JumpStart.Authorization.RequirePermissionAttribute)type.GetCustomAttributes(typeof(JumpStart.Authorization.RequirePermissionAttribute), true).Single();

        Assert.Equal(PermissionCatalog.ServerViewBases, permission.Permission);
        Assert.NotEqual(PermissionCatalog.ServerGet, permission.Permission);
        Assert.NotNull(type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true).SingleOrDefault());
    }

    [Fact]
    public void TheOwnerHoldsTheBasesPermissionAndItCanBeDelegated()
    {
        Assert.Contains(PermissionCatalog.ServerViewBases, PermissionCatalog.OwnerPermissions);
        var descriptor = PermissionCatalog.All.Single(p => p.Name == PermissionCatalog.ServerViewBases);
        Assert.True(descriptor.DelegableByTenantAdmin);
        Assert.Equal(JumpStart.Authorization.PermissionScope.Tenant, descriptor.Scope);
    }

    [Fact]
    public void ThePermissionCatalogHasNoDuplicatesAndEveryOwnerPermissionIsDeclared()
    {
        var declared = PermissionCatalog.All.Select(p => p.Name).ToList();

        Assert.Equal(declared.Count, declared.Distinct().Count());
        Assert.All(PermissionCatalog.OwnerPermissions, p => Assert.Contains(p, declared));
    }

    // ---- names for players who are not online --------------------------------------------------------------

    private static async Task AddSessionAsync(Harness h, string steamId, string name, double minutesAgo, Guid? server = null)
    {
        h.Context.Set<PlayerSession>().Add(new PlayerSession
        {
            TenantId = h.TenantId, RustServerId = server ?? h.ServerId, SteamId = steamId, DisplayName = name, IpAddress = "203.0.113.1",
            ConnectedAtUtc = T0.AddMinutes(-minutesAgo), DisconnectedAtUtc = T0.AddMinutes(-minutesAgo + 5)
        });
        await h.Context.SaveChangesAsync();
    }

    private static string RawTc(int id, string owner, params (string Id, string? Name)[] authorized) =>
        "{\"i\":" + id + ",\"x\":1,\"y\":1,\"z\":1,\"o\":\"" + owner + "\",\"a\":["
        + string.Join(",", authorized.Select(a => "{\"i\":\"" + a.Id + "\"" + (a.Name is null ? "" : ",\"n\":\"" + a.Name + "\"") + "}")) + "]}";

    private static async Task<BasesDto> GetBasesAsync(Harness h) =>
        Assert.IsType<BasesDto>(Assert.IsType<OkObjectResult>((await Controller(h).Get(h.ServerId)).Result).Value);

    [Fact]
    public async Task APlayerTheGameGaveNoNameForIsNamedFromTheirLastSessionOnThisServer()
    {
        var h = await CreateAsync();
        await AddServerAsync(h);
        await h.Repository.ReplaceAsync(h.TenantId, h.ServerId, true, List(RawTc(1, Alice, (Alice, "Alice"), (Bob, null))), T0);
        await AddSessionAsync(h, Bob, "Bobby Tables", minutesAgo: 600);

        var bases = await GetBasesAsync(h);

        Assert.Equal(["Alice", "Bobby Tables"], bases.Tcs.Single().Authorized.Select(a => a.Name).ToArray());
    }

    [Fact]
    public async Task TheMostRecentSessionsNameWins()
    {
        var h = await CreateAsync();
        await AddServerAsync(h);
        await h.Repository.ReplaceAsync(h.TenantId, h.ServerId, true, List(RawTc(1, Alice, (Alice, "Alice"), (Bob, null))), T0);
        await AddSessionAsync(h, Bob, "Old Name", minutesAgo: 5000);
        await AddSessionAsync(h, Bob, "New Name", minutesAgo: 100);
        await AddSessionAsync(h, Bob, "Older Still", minutesAgo: 9000);

        var bases = await GetBasesAsync(h);

        Assert.Equal("New Name", bases.Tcs.Single().Authorized.Single(a => a.PlayerId == Bob).Name);
    }

    [Fact]
    public async Task AnAuthorizedPlayerNothingKnowsStaysAnId()
    {
        var h = await CreateAsync();
        await AddServerAsync(h);
        await h.Repository.ReplaceAsync(h.TenantId, h.ServerId, true, List(RawTc(1, Alice, (Bob, null))), T0);

        var tc = (await GetBasesAsync(h)).Tcs.Single();

        Assert.Equal(string.Empty, tc.Authorized.Single().Name);
        Assert.Equal(string.Empty, tc.OwnerName);
    }

    [Fact]
    public async Task ANameFromThePluginIsNeverReplacedByASessionsName()
    {
        var h = await CreateAsync();
        await AddServerAsync(h);
        await h.Repository.ReplaceAsync(h.TenantId, h.ServerId, true, List(Tc(1, Alice, authorized: [(Alice, "Alice Live")])), T0);
        await AddSessionAsync(h, Alice, "Alice Old", minutesAgo: 60);

        var tc = (await GetBasesAsync(h)).Tcs.Single();

        Assert.Equal("Alice Live", tc.Authorized.Single().Name);
        Assert.Equal("Alice Live", tc.OwnerName);
    }

    [Fact]
    public async Task AnOwnerWhoIsNotAuthorizedOnTheirOwnCupboardIsStillNamed()
    {
        var h = await CreateAsync();
        await AddServerAsync(h);
        await h.Repository.ReplaceAsync(h.TenantId, h.ServerId, true, List(Tc(1, Alice, authorized: [])), T0);
        await AddSessionAsync(h, Alice, "Alice From History", minutesAgo: 60);

        var tc = (await GetBasesAsync(h)).Tcs.Single();

        Assert.Equal("Alice From History", tc.OwnerName);
    }

    [Fact]
    public async Task ASessionOnAnotherServerOrInAnotherOrganizationNamesNobodyHere()
    {
        var h = await CreateAsync();
        var other = await CreateAsync();
        await AddServerAsync(h);
        await h.Repository.ReplaceAsync(h.TenantId, h.ServerId, true, List(Tc(1, Alice, authorized: [])), T0);
        await AddSessionAsync(h, Alice, "Same Org Other Server", minutesAgo: 60, server: Guid.NewGuid());
        await AddSessionAsync(other, Alice, "Other Org", minutesAgo: 30, server: h.ServerId);

        var tc = (await GetBasesAsync(h)).Tcs.Single();

        Assert.Equal(string.Empty, tc.OwnerName);
    }

    [Fact]
    public async Task ASessionWithNoNameDoesNotBlankAKnownOne()
    {
        var h = await CreateAsync();
        await AddServerAsync(h);
        await h.Repository.ReplaceAsync(h.TenantId, h.ServerId, true, List(Tc(1, Alice, authorized: [])), T0);
        await AddSessionAsync(h, Alice, "Alice Named", minutesAgo: 600);
        await AddSessionAsync(h, Alice, "", minutesAgo: 10);        // the newest session has no name recorded

        Assert.Equal("Alice Named", (await GetBasesAsync(h)).Tcs.Single().OwnerName);
    }
}
