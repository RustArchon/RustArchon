// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Finding direct download addresses without ever being a nuisance to the index: one ask per plugin version for the whole platform however
/// many servers need it, settled answers never asked again, unsettled ones retried with a growing wait, a stop for everyone the moment the
/// index says slow down, and a cap on each pass - against a real Postgres, since the sharing is done by the database. Every test uses plugin
/// names of its own, because the database is shared with other tests.
/// </summary>
public class PluginDownloadLookupTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Task<Guid?> GetCurrentTenantIdAsync() => Task.FromResult<Guid?>(tenantId);
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string UniqueName() => "Plug" + Guid.NewGuid().ToString("N")[..10];

    private sealed record Seeded(Guid TenantId, Guid ServerId, ApiDbContext Context);

    /// <summary>A server (in an organization of its own) that has been told <paramref name="name"/> has an update.</summary>
    private async Task<Seeded> SeedNoticeAsync(
        string name, string marketplace = "uMod", string latest = "2.0.0", string url = "https://umod.org/plugins/x", string installed = "1.0.0")
    {
        var tenantId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        var context = new ApiDbContext(postgres.Options, new FixedTenantContext(tenantId));
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Download tenant {tenantId}", IsActive = true });
        context.Set<RustServer>().Add(new RustServer { Id = serverId, TenantId = tenantId, Name = "Download " + serverId.ToString("N")[..6], Host = "192.0.2.71", Port = 28016, RconPassword = "x" });
        context.Set<ServerPlugin>().Add(new ServerPlugin { TenantId = tenantId, RustServerId = serverId, Name = name, Version = installed, Author = "someone", CapturedAtUtc = T0 });
        await context.SaveChangesAsync();
        await AddNoticeAsync(context, tenantId, serverId, name, marketplace, latest, url, installed);
        return new Seeded(tenantId, serverId, context);
    }

    private static Task AddNoticeAsync(ApiDbContext context, Guid tenantId, Guid serverId, string name, string marketplace, string latest, string url, string installed = "1.0.0") =>
        new PluginUpdateNoticeRepository(context).MergeAsync(
            tenantId, serverId, [new PluginUpdateNoticeInfo(name, installed, latest, url, marketplace, T0, T0, 1)], T0);

    // ---- a resolver that answers as told, and remembers what it was asked ----------------------------------------

    private sealed class Asked
    {
        public readonly ConcurrentQueue<PluginDownloadRequest> Requests = new();
        public int Count(string name) => Requests.Count(r => r.Name == name);
        public int CountAll(IEnumerable<string> names) => Requests.Count(r => names.Contains(r.Name));
    }

    private static PluginDownloadAnswer FoundAnswer(string url = "https://umod.org/plugins/Thing.cs") =>
        new(new PluginDownloadMatch(PluginDownloadOutcome.Found, url, "Thing", "https://umod.org/plugins/thing", "2.0.0", null), 200, "[{\"name\":\"Thing\"}]", false, null);

    private static PluginDownloadAnswer NotFoundAnswer() =>
        new(new PluginDownloadMatch(PluginDownloadOutcome.NotFound, null, null, null, null, "the listing offers no direct download"), 200, "[]", false, null);

    private static PluginDownloadAnswer FailedAnswer() =>
        new(new PluginDownloadMatch(PluginDownloadOutcome.Failed, null, null, null, null, "the index answered 500"), 500, null, false, null);

    private static PluginDownloadAnswer SlowDownAnswer(TimeSpan wait) =>
        new(new PluginDownloadMatch(PluginDownloadOutcome.Failed, null, null, null, null, "the index said it is being asked too often"), 429, null, false, wait);

    private static (Mock<IPluginDownloadResolver> Resolver, Asked Asked) ResolverThat(Func<PluginDownloadRequest, PluginDownloadAnswer> answer, Func<PluginDownloadRequest, bool> mine)
    {
        var asked = new Asked();
        var resolver = new Mock<IPluginDownloadResolver>();
        resolver.Setup(r => r.AskAsync(It.IsAny<PluginDownloadRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PluginDownloadRequest request, CancellationToken _) =>
            {
                if (mine(request))
                {
                    asked.Requests.Enqueue(request);
                    return answer(request);
                }

                return NotFoundAnswer();   // someone else's plugin in the shared database: not what this test is about
            });
        return (resolver, asked);
    }

    private PluginDownloadLookupJob NewJob(
        IPluginDownloadResolver resolver, MutableClock clock, PluginDownloadThrottle? throttle = null, bool enabled = true, int max = 10_000)
    {
        var settings = new Mock<IPlatformSettingsCache>();
        settings.Setup(s => s.GetBooleanAsync(PlatformSettingsRegistry.PluginDownloadLookupEnabled, true)).ReturnsAsync(enabled);
        return new PluginDownloadLookupJob(
            new PluginDownloadLookupRepository(new ApiDbContext(postgres.Options)), resolver, settings.Object, throttle ?? new PluginDownloadThrottle(), clock,
            NullLogger<PluginDownloadLookupJob>.Instance)
        {
            Spacing = TimeSpan.Zero,
            MaxPerRun = max
        };
    }

    private async Task<PluginDownloadLookup?> RowAsync(string name, string marketplace = "uMod", string version = "2.0.0")
    {
        await using var context = new ApiDbContext(postgres.Options);
        return await new PluginDownloadLookupRepository(context)
            .FindAsync(PluginDownloadMatcher.MarketplaceKey(marketplace), PluginUpdateNoticeRepository.Normalize(name), PluginDownloadMatcher.VersionKey(version));
    }

    // ---- sharing: one ask however many servers ------------------------------------------------------------------

    [Fact]
    public async Task APluginVersionThatManyServersNeedIsAskedAboutOnceForTheWholePlatform()
    {
        var name = UniqueName();
        await SeedNoticeAsync(name);
        await SeedNoticeAsync(name);
        await SeedNoticeAsync(name);
        var (resolver, asked) = ResolverThat(_ => FoundAnswer(), r => r.Name == name);

        await NewJob(resolver.Object, new MutableClock(T0)).RunOnceAsync(default);

        Assert.Equal(1, asked.Count(name));
    }

    [Fact]
    public async Task TheSamePluginOnTwoMarketplacesIsTwoDifferentAsks()
    {
        var name = UniqueName();
        await SeedNoticeAsync(name, "uMod");
        await SeedNoticeAsync(name, "Codefling", url: "https://codefling.com/plugins/x");
        var (resolver, asked) = ResolverThat(_ => NotFoundAnswer(), r => r.Name == name);

        await NewJob(resolver.Object, new MutableClock(T0)).RunOnceAsync(default);

        Assert.Equal(["codefling", "umod"], asked.Requests.Select(r => r.MarketplaceKey).Order());
    }

    [Fact]
    public async Task ANoticeWithNoMarketplaceIsNeverAskedAbout()
    {
        var name = UniqueName();
        await SeedNoticeAsync(name, marketplace: "");
        var (resolver, asked) = ResolverThat(_ => FoundAnswer(), r => r.Name == name);

        await NewJob(resolver.Object, new MutableClock(T0)).RunOnceAsync(default);

        Assert.Equal(0, asked.Count(name));
    }

    // ---- what is stored -----------------------------------------------------------------------------------------

    [Fact]
    public async Task AFoundAddressIsStoredWithTheWholeAnswerAndNeverAskedForAgain()
    {
        var name = UniqueName();
        await SeedNoticeAsync(name);
        var (resolver, asked) = ResolverThat(_ => FoundAnswer("https://umod.org/plugins/Found.cs"), r => r.Name == name);
        var clock = new MutableClock(T0);
        var job = NewJob(resolver.Object, clock);

        await job.RunOnceAsync(default);
        clock.Now = T0.AddDays(30);
        await job.RunOnceAsync(default);

        var row = await RowAsync(name);
        Assert.Equal(PluginDownloadOutcome.Found, row!.Outcome);
        Assert.Equal("https://umod.org/plugins/Found.cs", row.DownloadUrl);
        Assert.Equal("[{\"name\":\"Thing\"}]", row.ResponseJson);       // the index's own words, kept
        Assert.Equal(200, row.HttpStatus);
        Assert.Equal(T0, row.CheckedAtUtc);
        Assert.Null(row.NextAttemptUtc);
        Assert.Equal(1, row.Attempts);
        Assert.Equal(1, asked.Count(name));                              // not even a month later
    }

    [Fact]
    public async Task ANewVersionOfAPluginIsAskedAboutAgainBecauseTheVersionIsPartOfTheAnswer()
    {
        var name = UniqueName();
        var seeded = await SeedNoticeAsync(name, latest: "2.0.0");
        var (resolver, asked) = ResolverThat(_ => FoundAnswer(), r => r.Name == name);
        var job = NewJob(resolver.Object, new MutableClock(T0));
        await job.RunOnceAsync(default);

        // UpdateChecker now reports a newer version for the same plugin on the same server (a later report than the one held).
        await new PluginUpdateNoticeRepository(seeded.Context).MergeAsync(
            seeded.TenantId, seeded.ServerId, [new PluginUpdateNoticeInfo(name, "1.0.0", "2.1.0", "https://umod.org/plugins/x", "uMod", T0, T0.AddDays(2), 1)], T0.AddDays(2));
        await job.RunOnceAsync(default);

        Assert.Equal(["2.0.0", "2.1.0"], asked.Requests.Select(r => r.VersionKey));
        Assert.NotNull(await RowAsync(name, version: "2.0.0"));
        Assert.NotNull(await RowAsync(name, version: "2.1.0"));
    }

    // ---- not settled: retry, with a growing wait -----------------------------------------------------------------

    [Fact]
    public async Task ANotFoundAnswerIsRetriedOnlyOnceItsWaitHasPassedAndThenWaitsLonger()
    {
        var name = UniqueName();
        await SeedNoticeAsync(name);
        var (resolver, asked) = ResolverThat(_ => NotFoundAnswer(), r => r.Name == name);
        var clock = new MutableClock(T0);
        var job = NewJob(resolver.Object, clock);

        await job.RunOnceAsync(default);
        Assert.Equal(T0.AddHours(12), (await RowAsync(name))!.NextAttemptUtc);

        clock.Now = T0.AddHours(11);
        await job.RunOnceAsync(default);
        Assert.Equal(1, asked.Count(name));                                   // too soon

        clock.Now = T0.AddHours(13);
        await job.RunOnceAsync(default);
        var row = (await RowAsync(name))!;
        Assert.Equal(2, asked.Count(name));
        Assert.Equal(2, row.Attempts);
        Assert.Equal(T0.AddHours(13).AddHours(24), row.NextAttemptUtc);       // twice as long this time
    }

    [Fact]
    public async Task AFailureIsRetriedSoonerThanANotFoundButAlsoBacksOff()
    {
        var name = UniqueName();
        await SeedNoticeAsync(name);
        var (resolver, asked) = ResolverThat(_ => FailedAnswer(), r => r.Name == name);
        var clock = new MutableClock(T0);
        var job = NewJob(resolver.Object, clock);

        await job.RunOnceAsync(default);
        Assert.Equal(T0.AddMinutes(15), (await RowAsync(name))!.NextAttemptUtc);

        clock.Now = T0.AddMinutes(16);
        await job.RunOnceAsync(default);

        Assert.Equal(2, asked.Count(name));
        Assert.Equal(T0.AddMinutes(16).AddMinutes(30), (await RowAsync(name))!.NextAttemptUtc);
    }

    [Fact]
    public async Task ANotFoundThatLaterFindsItselfIsSettledForGood()
    {
        var name = UniqueName();
        await SeedNoticeAsync(name);
        var found = false;
        var (resolver, asked) = ResolverThat(_ => found ? FoundAnswer() : NotFoundAnswer(), r => r.Name == name);
        var clock = new MutableClock(T0);
        var job = NewJob(resolver.Object, clock);
        await job.RunOnceAsync(default);

        found = true;
        clock.Now = T0.AddDays(1);
        await job.RunOnceAsync(default);
        clock.Now = T0.AddDays(400);
        await job.RunOnceAsync(default);

        var row = (await RowAsync(name))!;
        Assert.Equal(PluginDownloadOutcome.Found, row.Outcome);
        Assert.NotNull(row.DownloadUrl);
        Assert.Null(row.NextAttemptUtc);
        Assert.Equal(2, asked.Count(name));
    }

    // ---- being a good guest -------------------------------------------------------------------------------------

    [Fact]
    public async Task BeingToldToSlowDownStopsThePassAndEveryLaterOneUntilThePauseIsOver()
    {
        var names = new[] { UniqueName(), UniqueName(), UniqueName() };
        foreach (var name in names)
        {
            await SeedNoticeAsync(name);
        }

        var minePlusOne = 0;
        var (resolver, asked) = ResolverThat(_ => Interlocked.Increment(ref minePlusOne) == 1 ? SlowDownAnswer(TimeSpan.FromMinutes(20)) : FoundAnswer(), r => names.Contains(r.Name));
        var clock = new MutableClock(T0);
        var throttle = new PluginDownloadThrottle();
        var job = NewJob(resolver.Object, clock, throttle);

        await job.RunOnceAsync(default);
        var afterTheFirstPass = asked.CountAll(names);
        Assert.Equal(T0.AddMinutes(20), throttle.PausedUntil);

        clock.Now = T0.AddMinutes(10);
        var duringThePause = await job.RunOnceAsync(default);
        Assert.Equal(0, duringThePause);                                       // stopped, for everyone
        Assert.Equal(afterTheFirstPass, asked.CountAll(names));

        // Once it is over the rest are asked, and so is the one that was turned away (a failure is retried after its wait).
        clock.Now = T0.AddMinutes(21);
        await job.RunOnceAsync(default);
        Assert.Equal(1, afterTheFirstPass);
        Assert.Equal(names.Length + 1, asked.CountAll(names));
        foreach (var name in names)
        {
            Assert.True(asked.Count(name) >= 1, $"{name} was never asked about");
        }
    }

    [Fact]
    public async Task ASlowDownThatHappensMidPassStopsThePassThere()
    {
        var names = Enumerable.Range(0, 4).Select(_ => UniqueName()).ToArray();
        foreach (var name in names)
        {
            await SeedNoticeAsync(name);
        }

        // Whichever of these is asked first is the one told to slow down: nothing mine is asked after it in the same pass.
        var order = new ConcurrentQueue<string>();
        var first = true;
        var (resolver, asked) = ResolverThat(r =>
        {
            order.Enqueue(r.Name);
            var slow = first;
            first = false;
            return slow ? SlowDownAnswer(TimeSpan.FromMinutes(5)) : FoundAnswer();
        }, r => names.Contains(r.Name));

        await NewJob(resolver.Object, new MutableClock(T0)).RunOnceAsync(default);

        Assert.Single(order);
    }

    [Fact]
    public async Task NoMoreThanTheCapIsAskedInOnePassAndTheRestWaitForTheNext()
    {
        var names = Enumerable.Range(0, 5).Select(_ => UniqueName()).ToArray();
        foreach (var name in names)
        {
            await SeedNoticeAsync(name);
        }

        var (resolver, _) = ResolverThat(_ => FoundAnswer(), r => names.Contains(r.Name));

        var asked = await NewJob(resolver.Object, new MutableClock(T0), max: 2).RunOnceAsync(default);

        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task WhenTheSwitchIsOffNothingIsAsked()
    {
        var name = UniqueName();
        await SeedNoticeAsync(name);
        var (resolver, asked) = ResolverThat(_ => FoundAnswer(), r => r.Name == name);

        var count = await NewJob(resolver.Object, new MutableClock(T0), enabled: false).RunOnceAsync(default);

        Assert.Equal(0, count);
        Assert.Equal(0, asked.Count(name));
        Assert.Null(await RowAsync(name));
    }

    [Fact]
    public async Task AnAddressAlreadyFoundStaysWhenTheSwitchIsTurnedOff()
    {
        var name = UniqueName();
        var seeded = await SeedNoticeAsync(name);
        var (resolver, _) = ResolverThat(_ => FoundAnswer("https://umod.org/plugins/Stays.cs"), r => r.Name == name);
        await NewJob(resolver.Object, new MutableClock(T0)).RunOnceAsync(default);

        await NewJob(resolver.Object, new MutableClock(T0), enabled: false).RunOnceAsync(default);

        Assert.Equal("https://umod.org/plugins/Stays.cs", Assert.Single(await Get(Controller(seeded), seeded.ServerId)).DownloadUrl);
    }

    // ---- the policy on its own ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(PluginDownloadOutcome.NotFound, 1, 12 * 60)]
    [InlineData(PluginDownloadOutcome.NotFound, 2, 24 * 60)]
    [InlineData(PluginDownloadOutcome.NotFound, 3, 48 * 60)]
    [InlineData(PluginDownloadOutcome.NotFound, 4, 96 * 60)]
    [InlineData(PluginDownloadOutcome.NotFound, 5, 7 * 24 * 60)]      // weekly at most
    [InlineData(PluginDownloadOutcome.NotFound, 99, 7 * 24 * 60)]
    [InlineData(PluginDownloadOutcome.Failed, 1, 15)]
    [InlineData(PluginDownloadOutcome.Failed, 2, 30)]
    [InlineData(PluginDownloadOutcome.Failed, 3, 60)]
    [InlineData(PluginDownloadOutcome.Failed, 5, 240)]
    [InlineData(PluginDownloadOutcome.Failed, 6, 6 * 60)]             // every six hours at most, however long it stays down
    [InlineData(PluginDownloadOutcome.Failed, 99, 6 * 60)]
    [InlineData(PluginDownloadOutcome.Failed, 0, 15)]                  // a nonsense count is the first
    public void TheWaitBeforeTheNextAskGrowsAndIsCapped(PluginDownloadOutcome outcome, int attempts, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), PluginDownloadLookupJob.NextAttempt(outcome, attempts));

    [Fact]
    public void OnlyAFoundAnswerCarriesAnAddressOrIsSettled()
    {
        var request = new PluginDownloadRequest("Thing", "uMod", "2.0.0", "");
        // A not-found answer that somehow carried an address must not store one.
        var lying = new PluginDownloadAnswer(new PluginDownloadMatch(PluginDownloadOutcome.NotFound, "https://umod.org/plugins/Thing.cs", null, null, null, "x"), 200, "[]", false, null);

        var notFound = PluginDownloadLookupJob.Build(request, lying, previousAttempts: 0, T0);
        var found = PluginDownloadLookupJob.Build(request, FoundAnswer(), previousAttempts: 3, T0);

        Assert.Null(notFound.DownloadUrl);
        Assert.NotNull(notFound.NextAttemptUtc);
        Assert.NotNull(found.DownloadUrl);
        Assert.Null(found.NextAttemptUtc);
        Assert.Equal(4, found.Attempts);
    }

    // ---- storing --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SavingTheSameKeyTwiceReplacesTheRowInsteadOfAddingAnother()
    {
        var name = UniqueName();
        var request = new PluginDownloadRequest(name, "uMod", "2.0.0", "");
        var repository = new PluginDownloadLookupRepository(new ApiDbContext(postgres.Options));

        await repository.SaveAsync(PluginDownloadLookupJob.Build(request, NotFoundAnswer(), 0, T0));
        await new PluginDownloadLookupRepository(new ApiDbContext(postgres.Options))
            .SaveAsync(PluginDownloadLookupJob.Build(request, FoundAnswer(), 1, T0.AddDays(1)));

        await using var context = new ApiDbContext(postgres.Options);
        var row = await context.PluginDownloadLookups.SingleAsync(l => l.NormalizedName == PluginUpdateNoticeRepository.Normalize(name));
        Assert.Equal(PluginDownloadOutcome.Found, row.Outcome);
        Assert.Equal(2, row.Attempts);
    }

    [Fact]
    public async Task TwoServersFinishingTheSameAskAtOnceIsNotAnError()
    {
        var name = UniqueName();
        var request = new PluginDownloadRequest(name, "uMod", "2.0.0", "");

        await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            new PluginDownloadLookupRepository(new ApiDbContext(postgres.Options)).SaveAsync(PluginDownloadLookupJob.Build(request, FoundAnswer(), i, T0))));

        await using var context = new ApiDbContext(postgres.Options);
        Assert.Equal(1, await context.PluginDownloadLookups.CountAsync(l => l.NormalizedName == PluginUpdateNoticeRepository.Normalize(name)));
    }

    // ---- what the endpoint shows --------------------------------------------------------------------------------

    private ServerPluginUpdatesController Controller(Seeded s) =>
        new(new RustServerRepository(s.Context), new PluginUpdateNoticeRepository(s.Context), new ServerPluginRepository(s.Context), new PluginDownloadLookupRepository(s.Context));

    private static async Task<List<PluginUpdateNoticeDto>> Get(ServerPluginUpdatesController controller, Guid server)
    {
        var ok = Assert.IsType<OkObjectResult>((await controller.Get(server)).Result);
        return Assert.IsType<List<PluginUpdateNoticeDto>>(ok.Value);
    }

    private async Task StoreAsync(string name, PluginDownloadAnswer answer, string marketplace = "uMod", string version = "2.0.0") =>
        await new PluginDownloadLookupRepository(new ApiDbContext(postgres.Options))
            .SaveAsync(PluginDownloadLookupJob.Build(new PluginDownloadRequest(name, marketplace, version, ""), answer, 0, T0));

    [Fact]
    public async Task ANoticeShowsTheAddressFoundForItsPluginAtItsVersion()
    {
        var name = UniqueName();
        var seeded = await SeedNoticeAsync(name);
        await StoreAsync(name, FoundAnswer("https://umod.org/plugins/Shown.cs"));

        var notice = Assert.Single(await Get(Controller(seeded), seeded.ServerId));

        Assert.Equal("https://umod.org/plugins/Shown.cs", notice.DownloadUrl);
    }

    [Fact]
    public async Task UntilItHasBeenLookedUpThereIsNoAddressAndNothingBreaks()
    {
        var name = UniqueName();
        var seeded = await SeedNoticeAsync(name);

        var notice = Assert.Single(await Get(Controller(seeded), seeded.ServerId));

        Assert.Equal(string.Empty, notice.DownloadUrl);
    }

    [Fact]
    public async Task ANotFoundAnswerShowsNoAddress()
    {
        var name = UniqueName();
        var seeded = await SeedNoticeAsync(name);
        await StoreAsync(name, NotFoundAnswer());

        Assert.Equal(string.Empty, Assert.Single(await Get(Controller(seeded), seeded.ServerId)).DownloadUrl);
    }

    [Fact]
    public async Task AnAddressFoundForAnOlderVersionIsNotShownForTheNewerOne()
    {
        var name = UniqueName();
        var seeded = await SeedNoticeAsync(name, latest: "2.1.0");
        await StoreAsync(name, FoundAnswer("https://umod.org/plugins/Old.cs"), version: "2.0.0");

        Assert.Equal(string.Empty, Assert.Single(await Get(Controller(seeded), seeded.ServerId)).DownloadUrl);
    }

    [Fact]
    public async Task AnAddressFoundOnAnotherMarketplaceIsNotShown()
    {
        var name = UniqueName();
        var seeded = await SeedNoticeAsync(name, "Codefling", url: "https://codefling.com/plugins/x");
        await StoreAsync(name, FoundAnswer("https://umod.org/plugins/Other.cs"), marketplace: "uMod");

        Assert.Equal(string.Empty, Assert.Single(await Get(Controller(seeded), seeded.ServerId)).DownloadUrl);
    }

    [Fact]
    public async Task AStoredAddressThatWouldNoLongerPassTheRulesIsNotSent()
    {
        // The rules are applied again when it is read, so tightening them takes effect on what is already held.
        var name = UniqueName();
        var seeded = await SeedNoticeAsync(name);
        await using (var context = new ApiDbContext(postgres.Options))
        {
            context.PluginDownloadLookups.Add(new PluginDownloadLookup
            {
                MarketplaceKey = "umod", NormalizedName = PluginUpdateNoticeRepository.Normalize(name), Version = "2.0.0",
                Outcome = PluginDownloadOutcome.Found, DownloadUrl = "https://evil.example/Thing.cs", CheckedAtUtc = T0, Attempts = 1
            });
            await context.SaveChangesAsync();
        }

        Assert.Equal(string.Empty, Assert.Single(await Get(Controller(seeded), seeded.ServerId)).DownloadUrl);
    }

    [Fact]
    public async Task TheAnswerIsSharedByEveryServerWithThatPlugin()
    {
        var name = UniqueName();
        var one = await SeedNoticeAsync(name);
        var two = await SeedNoticeAsync(name);
        await StoreAsync(name, FoundAnswer("https://umod.org/plugins/Shared.cs"));

        Assert.Equal("https://umod.org/plugins/Shared.cs", Assert.Single(await Get(Controller(one), one.ServerId)).DownloadUrl);
        Assert.Equal("https://umod.org/plugins/Shared.cs", Assert.Single(await Get(Controller(two), two.ServerId)).DownloadUrl);
    }
}
