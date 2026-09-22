// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Tests;

/// <summary>
/// Looking at the file behind a found download address, against a real Postgres: only files somebody is waiting on are downloaded (a server that
/// opted in, on a plan that offers it), each once, and only what was learned is kept - never the file. A failed download backs off. Downloading again
/// is allowed: the same hash means the checks are not repeated, a different one means the author replaced the file under the same version number.
/// </summary>
public class PluginFileValidationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
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

    /// <summary>A plugin name that is only this class's: letters and digits, so the file's class can carry it.</summary>
    private static string UniqueName() => "Fv" + Guid.NewGuid().ToString("N")[..10];

    private static string Source(string name, string version = "2.0.0") => $$"""
        using Oxide.Core;
        namespace Oxide.Plugins
        {
            [Info("{{name}}", "someone", "{{version}}")]
            class {{name}} : RustPlugin
            {
                void Init() { Puts("secret-source-marker"); }
            }
        }
        """;

    // Source files in the archive are the real plugin (an archive is only worth applying if the plugin is in it); anything else is a line of text.
    private static byte[] Zip(string pluginSource, params string[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
                writer.Write(entry.EndsWith(".cs", StringComparison.Ordinal) ? pluginSource : "x");
            }
        }

        return stream.ToArray();
    }

    // ---- what a download does, as told -----------------------------------------------------------------------------

    private sealed class Downloads
    {
        public readonly ConcurrentQueue<Uri> Requested = new();
        public Func<Uri, PluginFileDownload> Answer { get; set; } = _ => PluginFileDownload.Failure("not set up");
        public int Count(string name) => Requested.Count(u => u.AbsoluteUri.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CountingInspector : IPluginFileInspector
    {
        private readonly PluginFileInspector _real = new();
        public int Calls;

        public PluginFileInspection Inspect(byte[] content, string expectedNormalizedName, string expectedVersion)
        {
            Interlocked.Increment(ref Calls);
            return _real.Inspect(content, expectedNormalizedName, expectedVersion);
        }
    }

    private PluginFileValidationJob NewJob(Downloads downloads, MutableClock clock, CountingInspector? inspector = null, bool enabled = true, int max = 10_000)
    {
        var downloader = new Mock<IPluginFileDownloader>();
        downloader.Setup(d => d.DownloadAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Uri address, CancellationToken _) =>
            {
                downloads.Requested.Enqueue(address);
                return downloads.Answer(address);
            });
        var settings = new Mock<IPlatformSettingsCache>();
        settings.Setup(s => s.GetBooleanAsync(PlatformSettingsRegistry.PluginFileValidationEnabled, true)).ReturnsAsync(enabled);

        return new PluginFileValidationJob(
            new PluginDownloadLookupRepository(new ApiDbContext(postgres.Options)), downloader.Object, inspector ?? new CountingInspector(), settings.Object, clock,
            NullLogger<PluginFileValidationJob>.Instance)
        {
            Spacing = TimeSpan.Zero,
            MaxPerRun = max
        };
    }

    // ---- who is waiting ----------------------------------------------------------------------------------------------

    private async Task CleanAsync()
    {
        await using var context = new ApiDbContext(postgres.Options);
        await context.PluginDownloadLookups.Where(l => l.NormalizedName.StartsWith("fv")).ExecuteDeleteAsync();
    }

    /// <summary>
    /// A server (in an organization of its own) that has been told <paramref name="name"/> has an update, with the lookup for it already found.
    /// The organization is on a plan that offers the feature and the server has opted in, unless said otherwise.
    /// </summary>
    private async Task<(Guid TenantId, Guid ServerId)> WaitingAsync(
        string name, bool planOffers = true, bool optedIn = true, bool serverEnabled = true, bool addLookup = true,
        PluginDownloadOutcome outcome = PluginDownloadOutcome.Found, string? url = null)
    {
        var tenantId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        await using var context = new ApiDbContext(postgres.Options, new FixedTenantContext(tenantId));
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Validation tenant {tenantId}", IsActive = true });
        var plan = new Plan { Name = $"Validation plan {Guid.NewGuid()}", Active = true, OffersThirdPartyPluginUpdates = planOffers };
        plan.Prices.Add(new PlanPrice { TermMonths = 1, UnitAmount = 0m, IncludedUnits = 1, Currency = "USD" });
        context.Set<Plan>().Add(plan);
        context.Set<Subscription>().Add(new Subscription { TenantId = tenantId, PlanId = plan.Id, Plan = plan, StartDate = T0.AddYears(-1) });
        context.Set<RustServer>().Add(new RustServer
        {
            Id = serverId, TenantId = tenantId, Name = "Validation " + serverId.ToString("N")[..6], Host = "192.0.2.72", Port = 28016, RconPassword = "x",
            IsEnabled = serverEnabled, ThirdPartyPluginUpdatesEnabled = optedIn
        });
        await context.SaveChangesAsync();
        await new PluginUpdateNoticeRepository(context).MergeAsync(
            tenantId, serverId, [new PluginUpdateNoticeInfo(name, "1.0.0", "2.0.0", "https://umod.org/plugins/x", "uMod", T0, T0, 1)], T0);

        if (addLookup)
        {
            await new PluginDownloadLookupRepository(new ApiDbContext(postgres.Options)).SaveAsync(new PluginDownloadLookup
            {
                MarketplaceKey = "umod", NormalizedName = PluginUpdateNoticeRepository.Normalize(name), Version = "2.0.0", Outcome = outcome,
                DownloadUrl = outcome == PluginDownloadOutcome.Found ? url ?? $"https://umod.org/plugins/{name}.cs" : null,
                CheckedAtUtc = T0, Attempts = 1
            });
        }

        return (tenantId, serverId);
    }

    private async Task<PluginDownloadLookup> RowAsync(string name)
    {
        await using var context = new ApiDbContext(postgres.Options);
        return await context.PluginDownloadLookups.AsNoTracking().SingleAsync(l => l.NormalizedName == PluginUpdateNoticeRepository.Normalize(name));
    }

    private static Func<Uri, PluginFileDownload> Serves(string name, byte[] content) =>
        uri => uri.AbsoluteUri.Contains(name, StringComparison.OrdinalIgnoreCase) ? PluginFileDownload.Success(content) : PluginFileDownload.Failure("someone else's file");

    // ---- validated once, only what was learned kept ----------------------------------------------------------------

    [Fact]
    public async Task AFileSomebodyIsWaitingOnIsDownloadedAndCheckedAndOnlyTheFindingsAreKept()
    {
        await CleanAsync();
        var name = UniqueName();
        await WaitingAsync(name);
        var content = Encoding.UTF8.GetBytes(Source(name));
        var downloads = new Downloads { Answer = Serves(name, content) };

        await NewJob(downloads, new MutableClock(T0)).RunOnceAsync(CancellationToken.None);

        var row = await RowAsync(name);
        Assert.Equal(PluginFileValidationState.Valid, row.ValidationState);
        Assert.Equal("cs", row.FileKind);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), row.FileSha256);
        Assert.Equal(content.Length, row.FileSizeBytes);
        Assert.Equal(name, row.PluginClassName);
        Assert.Equal(name, row.PluginInfoName);
        Assert.Equal("someone", row.PluginInfoAuthor);
        Assert.Equal("2.0.0", row.PluginInfoVersion);
        Assert.Equal(T0, row.ValidatedAtUtc);
        Assert.Equal(1, row.ValidationAttempts);
        Assert.Null(row.ValidationNextAttemptUtc);
        Assert.Equal(0, row.FileHashChanges);
    }

    [Fact]
    public async Task TheFileItselfIsNeverStoredAnywhereOnTheRow()
    {
        await CleanAsync();
        var name = UniqueName();
        await WaitingAsync(name);
        var downloads = new Downloads { Answer = Serves(name, Encoding.UTF8.GetBytes(Source(name))) };

        await NewJob(downloads, new MutableClock(T0)).RunOnceAsync(CancellationToken.None);

        var row = await RowAsync(name);
        Assert.DoesNotContain("secret-source-marker", JsonSerializer.Serialize(row));
        Assert.DoesNotContain(typeof(PluginDownloadLookup).GetProperties(), p => p.PropertyType == typeof(byte[]));
    }

    [Fact]
    public async Task AFileThatHasBeenLookedAtIsNotDownloadedAgainByLaterPasses()
    {
        await CleanAsync();
        var name = UniqueName();
        await WaitingAsync(name);
        var downloads = new Downloads { Answer = Serves(name, Encoding.UTF8.GetBytes(Source(name))) };
        var clock = new MutableClock(T0);

        var job = NewJob(downloads, clock);
        await job.RunOnceAsync(CancellationToken.None);
        clock.Now = T0.AddDays(3);
        await job.RunOnceAsync(CancellationToken.None);
        await NewJob(downloads, clock).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, downloads.Count(name));
    }

    [Fact]
    public async Task ManyServersWaitingOnTheSamePluginCauseOneDownload()
    {
        await CleanAsync();
        var name = UniqueName();
        await WaitingAsync(name);
        await WaitingAsync(name, addLookup: false);
        await WaitingAsync(name, addLookup: false);
        var downloads = new Downloads { Answer = Serves(name, Encoding.UTF8.GetBytes(Source(name))) };

        await NewJob(downloads, new MutableClock(T0)).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, downloads.Count(name));
    }

    // ---- nothing is downloaded that nobody would apply -----------------------------------------------------------------

    [Theory]
    [InlineData(false, true, true)]      // the plan does not offer it
    [InlineData(true, false, true)]      // the server has not opted in
    [InlineData(true, true, false)]      // the server is disabled
    public async Task NothingIsDownloadedForAPluginNobodyWouldApply(bool planOffers, bool optedIn, bool serverEnabled)
    {
        await CleanAsync();
        var name = UniqueName();
        await WaitingAsync(name, planOffers, optedIn, serverEnabled);
        var downloads = new Downloads { Answer = Serves(name, Encoding.UTF8.GetBytes(Source(name))) };

        var asked = await NewJob(downloads, new MutableClock(T0)).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, downloads.Count(name));
        Assert.Equal(PluginFileValidationState.NotChecked, (await RowAsync(name)).ValidationState);
        Assert.True(asked >= 0);
    }

    [Fact]
    public async Task ACountedOutSubscriptionDoesNotCount()
    {
        await CleanAsync();
        var name = UniqueName();
        var (tenantId, _) = await WaitingAsync(name);
        await using (var context = new ApiDbContext(postgres.Options))
        {
            await context.Set<Subscription>().IgnoreQueryFilters().Where(s => s.TenantId == tenantId).ExecuteUpdateAsync(s => s.SetProperty(x => x.EndDate, T0.AddDays(-1)));
        }

        var downloads = new Downloads { Answer = Serves(name, Encoding.UTF8.GetBytes(Source(name))) };
        await NewJob(downloads, new MutableClock(T0)).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, downloads.Count(name));
    }

    [Theory]
    [InlineData(PluginDownloadOutcome.NotFound)]
    [InlineData(PluginDownloadOutcome.Failed)]
    public async Task OnlyAFoundAddressIsEverDownloaded(PluginDownloadOutcome outcome)
    {
        await CleanAsync();
        var name = UniqueName();
        await WaitingAsync(name, outcome: outcome);
        var downloads = new Downloads { Answer = Serves(name, Encoding.UTF8.GetBytes(Source(name))) };

        await NewJob(downloads, new MutableClock(T0)).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, downloads.Count(name));
    }

    [Fact]
    public async Task TheKillSwitchStopsEveryDownload()
    {
        await CleanAsync();
        var name = UniqueName();
        await WaitingAsync(name);
        var downloads = new Downloads { Answer = Serves(name, Encoding.UTF8.GetBytes(Source(name))) };

        var count = await NewJob(downloads, new MutableClock(T0), enabled: false).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Equal(0, downloads.Count(name));
    }

    [Fact]
    public async Task ThePassDownloadsNoMoreThanItsLimit()
    {
        await CleanAsync();
        var names = new[] { UniqueName(), UniqueName(), UniqueName() };
        foreach (var name in names)
        {
            await WaitingAsync(name);
        }

        var downloads = new Downloads { Answer = uri => PluginFileDownload.Success(Encoding.UTF8.GetBytes(Source(names.First(n => uri.AbsoluteUri.Contains(n))))) };
        var job = NewJob(downloads, new MutableClock(T0), max: 2);

        var first = await job.RunOnceAsync(CancellationToken.None);
        var second = await job.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, first);
        Assert.Equal(1, second);        // the rest, next pass
        Assert.Equal(3, names.Sum(downloads.Count));
    }

    // ---- what the file turns out to be -------------------------------------------------------------------------------------

    [Fact]
    public async Task AWebPageInPlaceOfTheFileIsRecordedAsInvalidAndIsNotRetried()
    {
        await CleanAsync();
        var name = UniqueName();
        await WaitingAsync(name);
        var downloads = new Downloads { Answer = Serves(name, Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>Please log in</body></html>")) };
        var clock = new MutableClock(T0);

        var job = NewJob(downloads, clock);
        await job.RunOnceAsync(CancellationToken.None);
        clock.Now = T0.AddDays(2);
        await job.RunOnceAsync(CancellationToken.None);

        var row = await RowAsync(name);
        Assert.Equal(PluginFileValidationState.Invalid, row.ValidationState);
        Assert.Contains("web page", row.ValidationReason);
        Assert.Equal(1, downloads.Count(name));
    }

    [Fact]
    public async Task AZipIsRecordedWithItsFileListAndWaitsForSomeonesInstructions()
    {
        await CleanAsync();
        var name = UniqueName();
        await WaitingAsync(name);
        var downloads = new Downloads { Answer = Serves(name, Zip(Source(name), "en/Plugin.cs", "ru/Plugin.cs", "Plugin.json")) };

        await NewJob(downloads, new MutableClock(T0)).RunOnceAsync(CancellationToken.None);

        var row = await RowAsync(name);
        Assert.Equal(PluginFileValidationState.NeedsInstructions, row.ValidationState);
        Assert.Equal("zip", row.FileKind);
        Assert.Equal(["en/Plugin.cs", "ru/Plugin.cs", "Plugin.json"], ZipListing.Parse(row.ZipEntries).Select(e => e.Path));
        Assert.Equal((name, "2.0.0"), (row.PluginClassName, row.PluginInfoVersion));         // what the archive's plugin file is, read once
        Assert.Equal(["en/Plugin.cs", "ru/Plugin.cs"], ZipListing.ParseFindings(row.ZipSourceFindings).Select(f => f.Path));
    }

    [Fact]
    public async Task AFileForAnotherPluginIsInvalid()
    {
        await CleanAsync();
        var name = UniqueName();
        await WaitingAsync(name);
        var downloads = new Downloads { Answer = Serves(name, Encoding.UTF8.GetBytes(Source("SomethingElse"))) };

        await NewJob(downloads, new MutableClock(T0)).RunOnceAsync(CancellationToken.None);

        Assert.Equal(PluginFileValidationState.Invalid, (await RowAsync(name)).ValidationState);
    }

    // ---- a download that fails -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFailedDownloadIsTriedAgainLaterAndBacksOff()
    {
        await CleanAsync();
        var name = UniqueName();
        await WaitingAsync(name);
        var downloads = new Downloads { Answer = _ => PluginFileDownload.Failure("the host answered 500") };
        var clock = new MutableClock(T0);
        var job = NewJob(downloads, clock);

        await job.RunOnceAsync(CancellationToken.None);
        var first = await RowAsync(name);
        Assert.Equal(PluginFileValidationState.Failed, first.ValidationState);
        Assert.Contains("500", first.ValidationReason);
        Assert.Equal(T0 + PluginDownloadLookupJob.NextAttempt(PluginDownloadOutcome.Failed, 1), first.ValidationNextAttemptUtc);

        clock.Now = T0.AddMinutes(5);
        await job.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, downloads.Count(name));                                    // not yet due

        clock.Now = T0.AddMinutes(16);
        await job.RunOnceAsync(CancellationToken.None);
        var second = await RowAsync(name);
        Assert.Equal(2, downloads.Count(name));
        Assert.Equal(2, second.ValidationAttempts);
        Assert.Equal(clock.Now + PluginDownloadLookupJob.NextAttempt(PluginDownloadOutcome.Failed, 2), second.ValidationNextAttemptUtc);
        Assert.True(second.ValidationNextAttemptUtc > first.ValidationNextAttemptUtc!.Value.AddMinutes(16));      // the wait grew
    }

    [Fact]
    public async Task AFailedDownloadThatWorksLaterBecomesTheAnswerAndStopsBeingRetried()
    {
        await CleanAsync();
        var name = UniqueName();
        await WaitingAsync(name);
        var downloads = new Downloads { Answer = _ => PluginFileDownload.Failure("the host could not be reached") };
        var clock = new MutableClock(T0);
        var job = NewJob(downloads, clock);
        await job.RunOnceAsync(CancellationToken.None);

        downloads.Answer = Serves(name, Encoding.UTF8.GetBytes(Source(name)));
        clock.Now = T0.AddHours(1);
        await job.RunOnceAsync(CancellationToken.None);
        clock.Now = T0.AddDays(1);
        await job.RunOnceAsync(CancellationToken.None);

        var row = await RowAsync(name);
        Assert.Equal(PluginFileValidationState.Valid, row.ValidationState);
        Assert.Null(row.ValidationNextAttemptUtc);
        Assert.Equal(2, downloads.Count(name));
    }

    [Fact]
    public async Task AHostThatAsksToBeLeftAloneLongerThanTheBackOffIsObeyed()
    {
        await CleanAsync();
        var name = UniqueName();
        await WaitingAsync(name);
        var downloads = new Downloads { Answer = _ => PluginFileDownload.Failure("the host said it is being asked too often", TimeSpan.FromHours(3)) };

        await NewJob(downloads, new MutableClock(T0)).RunOnceAsync(CancellationToken.None);

        Assert.Equal(T0.AddHours(3), (await RowAsync(name)).ValidationNextAttemptUtc);
    }

    [Fact]
    public async Task AnAddressThatIsNoLongerOneWeWouldFetchIsNotFetched()
    {
        await CleanAsync();
        var name = UniqueName();
        await WaitingAsync(name, url: $"https://evil.example.com/{name}.cs");
        var downloads = new Downloads { Answer = Serves(name, Encoding.UTF8.GetBytes(Source(name))) };

        await NewJob(downloads, new MutableClock(T0)).RunOnceAsync(CancellationToken.None);

        var row = await RowAsync(name);
        Assert.Equal(0, downloads.Count(name));
        Assert.Equal(PluginFileValidationState.Failed, row.ValidationState);
        Assert.Contains("no longer one", row.ValidationReason);
    }

    // ---- downloading again ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DownloadingTheSameFileAgainSkipsTheChecksAndOnlyNotesWhenItWasSeen()
    {
        await CleanAsync();
        var name = UniqueName();
        await WaitingAsync(name);
        var downloads = new Downloads { Answer = Serves(name, Encoding.UTF8.GetBytes(Source(name))) };
        var clock = new MutableClock(T0);
        var inspector = new CountingInspector();
        var job = NewJob(downloads, clock, inspector);
        await job.RunOnceAsync(CancellationToken.None);
        var before = await RowAsync(name);

        clock.Now = T0.AddDays(2);
        var after = await job.RecheckAsync(before.Id, CancellationToken.None);

        Assert.Equal(2, downloads.Count(name));                 // downloaded again...
        Assert.Equal(1, inspector.Calls);                       // ...but not looked at again
        Assert.Equal(PluginFileValidationState.Valid, after!.ValidationState);
        Assert.Equal(before.FileSha256, after.FileSha256);
        Assert.Equal(0, after.FileHashChanges);
        Assert.Null(after.PreviousFileSha256);
        Assert.Equal(T0.AddDays(2), (await RowAsync(name)).ValidatedAtUtc);
    }

    [Fact]
    public async Task AFileThatChangedUnderTheSameVersionNumberIsLookedAtAgainAndTheChangeIsRecorded()
    {
        await CleanAsync();
        var name = UniqueName();
        await WaitingAsync(name);
        var original = Encoding.UTF8.GetBytes(Source(name));
        var downloads = new Downloads { Answer = Serves(name, original) };
        var inspector = new CountingInspector();
        var job = NewJob(downloads, new MutableClock(T0), inspector);
        await job.RunOnceAsync(CancellationToken.None);
        var before = await RowAsync(name);

        var replaced = Encoding.UTF8.GetBytes("<html><body>the author's site is down</body></html>");
        downloads.Answer = Serves(name, replaced);
        var after = await job.RecheckAsync(before.Id, CancellationToken.None);

        Assert.Equal(2, inspector.Calls);                       // looked at afresh
        Assert.Equal(PluginFileValidationState.Invalid, after!.ValidationState);
        Assert.Equal(before.FileSha256, after.PreviousFileSha256);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(replaced)).ToLowerInvariant(), after.FileSha256);
        Assert.Equal(1, after.FileHashChanges);
        Assert.Equal(after.FileSha256, (await RowAsync(name)).FileSha256);
    }

    [Fact]
    public async Task AReplacedFileCountsEachTimeItChanges()
    {
        await CleanAsync();
        var name = UniqueName();
        await WaitingAsync(name);
        var downloads = new Downloads { Answer = Serves(name, Encoding.UTF8.GetBytes(Source(name))) };
        var job = NewJob(downloads, new MutableClock(T0));
        await job.RunOnceAsync(CancellationToken.None);
        var id = (await RowAsync(name)).Id;

        downloads.Answer = Serves(name, Encoding.UTF8.GetBytes(Source(name) + "\n// b"));
        await job.RecheckAsync(id, CancellationToken.None);
        downloads.Answer = Serves(name, Encoding.UTF8.GetBytes(Source(name) + "\n// c"));
        var third = await job.RecheckAsync(id, CancellationToken.None);

        Assert.Equal(2, third!.FileHashChanges);
        Assert.Equal(PluginFileValidationState.Valid, third.ValidationState);
    }

    [Fact]
    public async Task AFailedRecheckLeavesAnEarlierAnswerStanding()
    {
        await CleanAsync();
        var name = UniqueName();
        await WaitingAsync(name);
        var downloads = new Downloads { Answer = Serves(name, Encoding.UTF8.GetBytes(Source(name))) };
        var job = NewJob(downloads, new MutableClock(T0));
        await job.RunOnceAsync(CancellationToken.None);
        var before = await RowAsync(name);

        downloads.Answer = _ => PluginFileDownload.Failure("the host could not be reached");
        var after = await job.RecheckAsync(before.Id, CancellationToken.None);

        Assert.Equal(PluginFileValidationState.Valid, after!.ValidationState);
        var stored = await RowAsync(name);
        Assert.Equal(PluginFileValidationState.Valid, stored.ValidationState);
        Assert.Equal(before.FileSha256, stored.FileSha256);
    }

    [Fact]
    public async Task ARecheckOfSomethingThatWasNeverFoundOrDoesNotExistDoesNothing()
    {
        await CleanAsync();
        var name = UniqueName();
        await WaitingAsync(name, outcome: PluginDownloadOutcome.NotFound);
        var downloads = new Downloads { Answer = Serves(name, Encoding.UTF8.GetBytes(Source(name))) };
        var job = NewJob(downloads, new MutableClock(T0));

        Assert.Null(await job.RecheckAsync((await RowAsync(name)).Id, CancellationToken.None));
        Assert.Null(await job.RecheckAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(0, downloads.Count(name));
    }

    [Fact]
    public async Task RecheckingAFileThatWasNeverLookedAtLooksAtItNow()
    {
        await CleanAsync();
        var name = UniqueName();
        await WaitingAsync(name, optedIn: false);              // nobody is waiting, so the pass would not have downloaded it
        var downloads = new Downloads { Answer = Serves(name, Encoding.UTF8.GetBytes(Source(name))) };
        var job = NewJob(downloads, new MutableClock(T0));

        var row = await job.RecheckAsync((await RowAsync(name)).Id, CancellationToken.None);

        Assert.Equal(PluginFileValidationState.Valid, row!.ValidationState);
    }

    // ---- the setting and the migration's columns ----------------------------------------------------------------------------------

    [Fact]
    public void TheSettingThatStopsDownloadsIsOnByDefaultAndItsKeyIsStable()
    {
        Assert.Equal("PluginFileValidationEnabled", PlatformSettingsRegistry.PluginFileValidationEnabled);
    }

    [Fact]
    public void ARowStartsUnchecked()
    {
        var row = new PluginDownloadLookup();

        Assert.Equal(PluginFileValidationState.NotChecked, row.ValidationState);
        Assert.Equal(0, row.ValidationAttempts);
        Assert.Null(row.FileSha256);
    }
}
