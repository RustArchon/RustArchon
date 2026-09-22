// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.PluginZips;

namespace RustArchon.Api.Tests;

/// <summary>
/// Applying a newer version of a third-party plugin on a game server, against a real Postgres: every precondition before the server is told anything
/// (a server that is not there to be updated is never sent a command), the command it is sent, what happens to each outcome the server reports, what a
/// person is shown, and what the automatic pass decides. The plugin on the server is played by a scripted RCON reply; what it does with a file is proven
/// in the plugin's own tests.
/// </summary>
public class ThirdPartyPluginUpdateServiceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    // A Monday, well clear of the first Thursday of October (the wipe): a server with the default 7-day hold is not held.
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    // Inside the 7 days before that wipe (1 October, 18:00 UTC).
    private static readonly DateTimeOffset HeldTime = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private const string PanelKey = "0123456789abcdef";
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Task<Guid?> GetCurrentTenantIdAsync() => Task.FromResult<Guid?>(tenantId);
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A plugin name that is only this class's: letters and digits, so it is also a valid class name.</summary>
    private static string UniqueName() => "Tp" + Guid.NewGuid().ToString("N")[..10];

    // ---- a server with an update waiting ---------------------------------------------------------------------------

    /// <summary>What a test builds: a server in an organization of its own with one outdated plugin and a checked file for the update. Each option breaks one thing.</summary>
    private sealed class Options
    {
        public bool PlanOffers = true;
        public bool OptedIn = true;
        public bool Enabled = true;
        public string InstalledVersion = "1.0.0";
        public string LatestVersion = "2.0.0";
        public string? InfoVersion = "2.0.0";
        public PluginDownloadOutcome Outcome = PluginDownloadOutcome.Found;
        public PluginFileValidationState Validation = PluginFileValidationState.Valid;
        public string? Reason;
        public string? Kind = "cs";
        public string? Class = "";       // "" means the plugin's own name
        public string? Sha = ThirdPartyPluginUpdateServiceTests.Sha;
        public long? Size = 12345;
        public string? Url = "";         // "" means the marketplace address for the plugin
        public bool AddLookup = true;
        public bool AddNotice = true;
        public bool AddInstalled = true;

        /// <summary>For a zip (a lookup in <see cref="PluginFileValidationState.NeedsInstructions"/>): its files, and what looking at its source files found. Default: the example archive.</summary>
        public IReadOnlyList<ZipEntryInfo>? Files;
        public IReadOnlyList<ZipSourceFinding>? Findings;
    }

    /// <summary>The user's example archive: an English and a Russian set of the same plugin.</summary>
    private static readonly IReadOnlyList<ZipEntryInfo> ExampleFiles =
    [
        new("en/plugins/test.cs", 100), new("en/configs/test.json", 20), new("en/images/test/one.jpg", 1000), new("en/images/test/two.jpg", 2000),
        new("ru/plugins/test.cs", 110), new("ru/configs/test.json", 21), new("ru/images/test/one.jpg", 1001), new("ru/images/test/two.jpg", 2001)
    ];

    private static IReadOnlyList<ZipSourceFinding> ExampleFindings(string @class) =>
        [new("en/plugins/test.cs", @class, @class, "2.0.0", null), new("ru/plugins/test.cs", @class, @class, "2.0.0", null)];

    private static readonly ZipMappingRule[] EnglishRules =
    [
        new() { IsFolder = true, Source = "en/plugins/", Role = ZipRoles.Plugins }, new() { IsFolder = true, Source = "en/configs/", Role = ZipRoles.Config },
        new() { IsFolder = true, Source = "en/images/", Role = ZipRoles.Data }, new() { IsFolder = true, Source = "ru/", Role = ZipRoles.Skip }
    ];

    private sealed class Scenario
    {
        public required Guid TenantId { get; init; }
        public required Guid ServerId { get; init; }
        public required string Name { get; init; }
        public required string Normalized { get; init; }
        public required Options Options { get; init; }
        public required Guid LookupId { get; init; }
        public RustServer Server(bool enabled = true, bool optedIn = true) => new()
        {
            Id = ServerId, TenantId = TenantId, IsEnabled = enabled, ThirdPartyPluginUpdatesEnabled = optedIn, ThirdPartyPluginUpdateHoldDays = 7
        };
    }

    private async Task<Scenario> SeedAsync(Action<Options>? configure = null, string? name = null)
    {
        var options = new Options();
        configure?.Invoke(options);
        name ??= UniqueName();
        var tenantId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        await using var context = new ApiDbContext(postgres.Options, new FixedTenantContext(tenantId));
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Third-party tenant {tenantId}", IsActive = true });
        var plan = new Plan { Name = $"Third-party plan {Guid.NewGuid()}", Active = true, OffersThirdPartyPluginUpdates = options.PlanOffers };
        plan.Prices.Add(new PlanPrice { TermMonths = 1, UnitAmount = 0m, IncludedUnits = 1, Currency = "USD" });
        context.Set<Plan>().Add(plan);
        context.Set<Subscription>().Add(new Subscription { TenantId = tenantId, PlanId = plan.Id, Plan = plan, StartDate = T0.AddYears(-1) });
        context.Set<RustServer>().Add(new RustServer
        {
            Id = serverId, TenantId = tenantId, Name = "Third " + serverId.ToString("N")[..6], Host = "192.0.2.80", Port = 28016, RconPassword = "x",
            IsEnabled = options.Enabled, ThirdPartyPluginUpdatesEnabled = options.OptedIn
        });
        await context.SaveChangesAsync();

        if (options.AddNotice)
        {
            await new PluginUpdateNoticeRepository(context).MergeAsync(
                tenantId, serverId, [new PluginUpdateNoticeInfo(name, options.InstalledVersion, options.LatestVersion, "https://umod.org/plugins/x", "uMod", T0, T0, 1)], T0);
        }

        var lookupId = Guid.Empty;
        if (options.AddLookup)
        {
            var row = new PluginDownloadLookup
            {
                MarketplaceKey = "umod", NormalizedName = PluginUpdateNoticeRepository.Normalize(name), Version = PluginDownloadMatcher.VersionKey(options.LatestVersion), Outcome = options.Outcome,
                DownloadUrl = options.Outcome == PluginDownloadOutcome.Found ? (options.Url == "" ? $"https://umod.org/plugins/{name}.cs" : options.Url) : null,
                CheckedAtUtc = T0, Attempts = 1,
                ValidationState = options.Validation, ValidationReason = options.Reason, FileKind = options.Kind, FileSha256 = options.Sha, FileSizeBytes = options.Size,
                PluginClassName = options.Class == "" ? name : options.Class, PluginInfoVersion = options.InfoVersion
            };
            if (options.Validation == PluginFileValidationState.NeedsInstructions)
            {
                row.ZipEntries = ZipListing.Format(options.Files ?? ExampleFiles);
                row.ZipSourceFindings = ZipListing.FormatFindings(options.Findings ?? ExampleFindings(options.Class == "" ? name : options.Class!));
            }

            lookupId = await SaveLookupAsync(row);
        }

        return new Scenario
        {
            TenantId = tenantId, ServerId = serverId, Name = name, Normalized = PluginUpdateNoticeRepository.Normalize(name), Options = options, LookupId = lookupId
        };
    }

    /// <summary>Stores a lookup the way it is really stored: the answer to "where is it" first, then what was learned from the file, which is saved separately.</summary>
    private async Task<Guid> SaveLookupAsync(PluginDownloadLookup row)
    {
        var repository = new PluginDownloadLookupRepository(new ApiDbContext(postgres.Options));
        await repository.SaveAsync(row);
        var stored = await RowAsync(row.NormalizedName, byNormalized: true);
        stored.ValidationState = row.ValidationState;
        stored.ValidationReason = row.ValidationReason;
        stored.FileKind = row.FileKind;
        stored.FileSha256 = row.FileSha256;
        stored.FileSizeBytes = row.FileSizeBytes;
        stored.PluginClassName = row.PluginClassName;
        stored.PluginInfoVersion = row.PluginInfoVersion;
        stored.ZipEntries = row.ZipEntries;
        stored.ZipSourceFindings = row.ZipSourceFindings;
        await new PluginDownloadLookupRepository(new ApiDbContext(postgres.Options)).SaveValidationAsync(stored);
        return stored.Id;
    }

    private async Task<PluginDownloadLookup> RowAsync(string name, bool byNormalized = false)
    {
        var normalized = byNormalized ? name : PluginUpdateNoticeRepository.Normalize(name);
        await using var context = new ApiDbContext(postgres.Options);
        return await context.PluginDownloadLookups.AsNoTracking().SingleAsync(l => l.NormalizedName == normalized);
    }

    private async Task<List<ThirdPartyPluginUpdate>> UpdatesAsync(Guid serverId)
    {
        await using var context = new ApiDbContext(postgres.Options);
        return await context.ThirdPartyPluginUpdates.AcrossAllTenants().AsNoTracking().Where(u => u.RustServerId == serverId).OrderBy(u => u.StartedAtUtc).ToListAsync();
    }

    private async Task AddUpdateAsync(Scenario s, string state, DateTimeOffset startedAt, string toVersion = "2.0.0", string sha = Sha, string message = "")
    {
        await using var context = new ApiDbContext(postgres.Options);
        context.ThirdPartyPluginUpdates.Add(new ThirdPartyPluginUpdate
        {
            TenantId = s.TenantId, RustServerId = s.ServerId, PluginName = s.Name, NormalizedName = s.Normalized, ClassName = s.Name, FromVersion = "1.0.0",
            ToVersion = toVersion, PluginDownloadLookupId = s.LookupId, FileSha256 = sha, Trigger = PluginUpdateTriggers.Manual, State = state, Message = message,
            StartedAtUtc = startedAt, ResolvedAtUtc = state == ThirdPartyPluginUpdateStates.Started ? null : startedAt
        });
        await context.SaveChangesAsync();
    }

    // ---- the service, with the game server played by a script ------------------------------------------------------

    private sealed class Harness
    {
        public ThirdPartyPluginUpdateService Service { get; set; } = null!;
        public required Mock<IServerPluginStatusRepository> Statuses { get; init; }
        public required Mock<IServerPluginRepository> Plugins { get; init; }
        public required Mock<IPluginScriptService> Script { get; init; }
        public required Mock<IPluginFileValidationJob> Validation { get; init; }
        public required Mock<IRequestClient<SendRconCommand>> Client { get; init; }
        public required Mock<IServerPollService> ServerPoll { get; init; }
        public Mock<IPluginFileRecheckThrottle> RecheckThrottle { get; set; } = null!;
        public required MutableClock Clock { get; init; }
        public List<SendRconCommand> Sent { get; } = [];
        public Func<string, (bool Success, string Message)> Reply { get; set; } = _ => (true, "{\"v\":1,\"ok\":true,\"data\":{\"phase\":\"downloading\"}}");
        public bool TimeOut { get; set; }
    }

    private Harness Build(
        Scenario s, Action<ServerPluginStatus>? status = null, PluginKeyState? key = PluginKeyState.Active, string? installedVersion = null, bool noStatus = false,
        DateTimeOffset? now = null)
    {
        var clock = new MutableClock(now ?? T0);
        var statuses = new Mock<IServerPluginStatusRepository>();
        if (!noStatus)
        {
            var value = new ServerPluginStatus
            {
                TenantId = s.TenantId, RustServerId = s.ServerId, PluginVersion = "0.9.0", SigningState = PluginSigningStates.Valid, SigningKeyFingerprint = PanelKey,
                Capabilities = [RustArchonPlugin.ThirdPartyUpdateCapability, RustArchonPlugin.ThirdPartyZipCapability], CapturedAtUtc = clock.Now.AddMinutes(-1)
            };
            status?.Invoke(value);
            statuses.Setup(r => r.GetForServerAcrossTenantsAsync(s.TenantId, s.ServerId)).ReturnsAsync(value);
        }

        var plugins = new Mock<IServerPluginRepository>();
        plugins.Setup(r => r.GetForServerAcrossTenantsAsync(s.TenantId, s.ServerId)).ReturnsAsync(
            s.Options.AddInstalled
                ? [new ServerPlugin { Name = s.Name, Author = "someone", Version = installedVersion ?? s.Options.InstalledVersion, RustServerId = s.ServerId, TenantId = s.TenantId }]
                : []);

        var script = new Mock<IPluginScriptService>();
        script.Setup(x => x.GetKeyStateAsync(PanelKey)).ReturnsAsync(key);
        var validation = new Mock<IPluginFileValidationJob>();
        validation.Setup(v => v.RecheckAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((PluginDownloadLookup?)null);

        var client = new Mock<IRequestClient<SendRconCommand>>();
        var serverPoll = new Mock<IServerPollService>();
        serverPoll.Setup(p => p.PollNowAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>>())).ReturnsAsync(ServerPollOutcome.Polled);
        var harness = new Harness { Statuses = statuses, Plugins = plugins, Script = script, Validation = validation, Client = client, ServerPoll = serverPoll, Clock = clock };
        client.Setup(c => c.GetResponse<RconCommandResult>(It.IsAny<SendRconCommand>(), It.IsAny<CancellationToken>(), It.IsAny<RequestTimeout>()))
            .Returns((SendRconCommand request, CancellationToken _, RequestTimeout _) =>
            {
                harness.Sent.Add(request);
                if (harness.TimeOut)
                {
                    throw new RequestTimeoutException("timed out");
                }

                var (success, message) = harness.Reply(request.Command);
                return Task.FromResult(Mock.Of<Response<RconCommandResult>>(r => r.Message == new RconCommandResult(success, message, null, null, success ? null : "NotConnected")));
            });

        var recheckThrottle = new Mock<IPluginFileRecheckThrottle>();
        recheckThrottle.Setup(t => t.TryAcquire(It.IsAny<Guid>())).Returns(true);
        var context = new ApiDbContext(postgres.Options);
        harness.Service = new ThirdPartyPluginUpdateService(
            context, statuses.Object, plugins.Object, script.Object, new PluginDownloadLookupRepository(context), new ThirdPartyPluginUpdateRepository(context),
            new PluginZipMappingRepository(context), new PluginUpdateExclusionRepository(context), new ThirdPartyPluginUpdateGate(context), validation.Object, client.Object,
            serverPoll.Object, recheckThrottle.Object, clock, NullLogger<ThirdPartyPluginUpdateService>.Instance);
        harness.RecheckThrottle = recheckThrottle;
        return harness;
    }

    private static string Ok(string phase = "downloading") => $"{{\"v\":1,\"ok\":true,\"data\":{{\"phase\":\"{phase}\"}}}}";

    private static string Err(string code, string message = "no") => $"{{\"v\":1,\"ok\":false,\"err\":\"{code}\",\"message\":\"{message}\"}}";

    private static string Status(string phase, string @class, string version = "2.0.0", string sha = Sha, string actual = "", string reason = "") =>
        "{\"v\":1,\"ok\":true,\"data\":{\"phase\":\"" + phase + "\",\"class\":\"" + @class + "\",\"targetVersion\":\"" + version + "\",\"previousVersion\":\"1.0.0\","
        + "\"file\":\"x.cs\",\"sha256\":\"" + sha + "\",\"actualSha256\":\"" + actual + "\",\"reason\":\"" + reason + "\"}}";

    // ---- starting an update: the command ------------------------------------------------------------------------------

    [Fact]
    public async Task TheServerIsToldTheClassTheVersionTheHashTheSizeAndTheAddressAndTheStartIsRecorded()
    {
        var s = await SeedAsync();
        var h = Build(s);

        var result = await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Auto, Sha);

        Assert.True(result.Started, result.Message);
        Assert.Equal("1.0.0", result.FromVersion);
        Assert.Equal("2.0.0", result.ToVersion);
        var sent = Assert.Single(h.Sent);
        Assert.Equal(s.ServerId, sent.ServerId);
        Assert.False(sent.Interactive);
        Assert.Equal($"archon.thirdparty.update {s.Name} 2.0.0 {Sha} 12345 https://umod.org/plugins/{s.Name}.cs", sent.Command);
        var recorded = Assert.Single(await UpdatesAsync(s.ServerId));
        Assert.Equal(ThirdPartyPluginUpdateStates.Started, recorded.State);
        Assert.Equal((s.Name, "1.0.0", "2.0.0", Sha, PluginUpdateTriggers.Auto, s.LookupId), (recorded.ClassName, recorded.FromVersion, recorded.ToVersion, recorded.FileSha256, recorded.Trigger, recorded.PluginDownloadLookupId));
    }

    [Fact]
    public async Task TheVersionAskedForIsTheOneTheFileSaysItIsNotTheNoticesWhichCanBeWrittenDifferently()
    {
        var s = await SeedAsync(o => { o.LatestVersion = "v2.0"; o.InfoVersion = "2.0.0"; });
        var h = Build(s);

        await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha);

        Assert.Contains(" 2.0.0 ", Assert.Single(h.Sent).Command);
    }

    [Fact]
    public async Task WithoutAVersionInTheFileTheNoticesIsUsed()
    {
        var s = await SeedAsync(o => o.InfoVersion = null);
        var h = Build(s);

        await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha);

        Assert.Contains(" 2.0.0 ", Assert.Single(h.Sent).Command);
    }

    // ---- refusals: nothing is sent -----------------------------------------------------------------------------------

    private async Task AssertRefusedAsync(Scenario s, Harness h, string code, string trigger = PluginUpdateTriggers.Auto, string? sha = Sha, RustServer? server = null)
    {
        var result = await h.Service.StartAsync(server ?? s.Server(), s.Name, trigger, sha);

        Assert.False(result.Started);
        Assert.Equal(code, result.Code);
        Assert.Empty(h.Sent);
        Assert.Empty(await UpdatesAsync(s.ServerId));         // a refusal the Panel makes itself is not an attempt
    }

    [Fact]
    public async Task ADisabledServerIsRefused()
    {
        var s = await SeedAsync();
        await AssertRefusedAsync(s, Build(s), "server_disabled", server: s.Server(enabled: false));
    }

    [Fact]
    public async Task APlanThatDoesNotOfferTheFeatureIsRefusedEvenForAPerson()
    {
        var s = await SeedAsync(o => o.PlanOffers = false);
        await AssertRefusedAsync(s, Build(s), "plan_does_not_offer", PluginUpdateTriggers.Manual);
    }

    [Fact]
    public async Task ANonOptedInServerIsNotUpdatedAutomatically()
    {
        var s = await SeedAsync(o => o.OptedIn = false);
        await AssertRefusedAsync(s, Build(s), "not_opted_in", server: s.Server(optedIn: false));
    }

    [Fact]
    public async Task ANonOptedInServerCanBeUpdatedByAPerson()
    {
        var s = await SeedAsync(o => o.OptedIn = false);
        var h = Build(s);

        var result = await h.Service.StartAsync(s.Server(optedIn: false), s.Name, PluginUpdateTriggers.Manual, Sha);

        Assert.True(result.Started, result.Message);
    }

    [Fact]
    public async Task AServerInsideItsWindowBeforeTheWipeIsNotUpdatedAutomatically()
    {
        var s = await SeedAsync();
        await AssertRefusedAsync(s, Build(s, now: HeldTime), "held_for_wipe");
    }

    [Fact]
    public async Task AServerInsideItsWindowBeforeTheWipeCanBeUpdatedByAPerson()
    {
        var s = await SeedAsync();
        var h = Build(s, now: HeldTime);

        var result = await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha);

        Assert.True(result.Started, result.Message);
    }

    [Fact]
    public async Task AZeroDayHoldNeverHoldsAServerEvenRightBeforeTheWipe()
    {
        var s = await SeedAsync();
        var h = Build(s, now: HeldTime);
        var server = s.Server();
        server.ThirdPartyPluginUpdateHoldDays = 0;

        var result = await h.Service.StartAsync(server, s.Name, PluginUpdateTriggers.Auto, Sha);

        Assert.True(result.Started, result.Message);
    }

    [Fact]
    public async Task APluginThatHasNotReportedInIsRefused()
    {
        var s = await SeedAsync();
        await AssertRefusedAsync(s, Build(s, noStatus: true), "no_handshake");
    }

    [Fact]
    public async Task APluginNotHeardFromRecentlyIsRefused()
    {
        var s = await SeedAsync();
        await AssertRefusedAsync(s, Build(s, status: st => st.CapturedAtUtc = T0.AddMinutes(-16)), "stale_status");
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("unsigned")]
    [InlineData("unknown")]
    public async Task APluginThatDoesNotVouchForItsOwnFileIsRefused(string state)
    {
        var s = await SeedAsync();
        await AssertRefusedAsync(s, Build(s, status: st => st.SigningState = state), "not_signed_by_this_panel");
    }

    [Fact]
    public async Task APluginSignedByAKeyThisPanelDoesNotKnowIsRefused()
    {
        var s = await SeedAsync();
        await AssertRefusedAsync(s, Build(s, key: null), "not_signed_by_this_panel");
    }

    [Fact]
    public async Task APluginSignedByARevokedKeyIsRefused()
    {
        var s = await SeedAsync();
        await AssertRefusedAsync(s, Build(s, key: PluginKeyState.Revoked), "key_revoked");
    }

    [Fact]
    public async Task APluginSignedByARetiredKeyIsStillAcceptedItIsThePanelsOwn()
    {
        var s = await SeedAsync();

        var result = await Build(s, key: PluginKeyState.Retired).Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Auto, Sha);

        Assert.True(result.Started, result.Message);
    }

    [Fact]
    public async Task APluginWithoutTheCapabilityIsRefused()
    {
        var s = await SeedAsync();
        await AssertRefusedAsync(s, Build(s, status: st => st.Capabilities = ["config", "updater-update"]), "plugin_too_old");
    }

    [Fact]
    public async Task APluginThatIsNotInstalledOrHasNoNoticeOrIsAlreadyUpToDateHasNoUpdate()
    {
        var notInstalled = await SeedAsync(o => o.AddInstalled = false);
        await AssertRefusedAsync(notInstalled, Build(notInstalled), "no_update");

        var noNotice = await SeedAsync(o => o.AddNotice = false);
        await AssertRefusedAsync(noNotice, Build(noNotice), "no_update");

        var current = await SeedAsync();
        await AssertRefusedAsync(current, Build(current, installedVersion: "2.0.0"), "no_update");
    }

    [Fact]
    public async Task AnUnknownPluginNameHasNoUpdate()
    {
        var s = await SeedAsync();

        var result = await Build(s).Service.StartAsync(s.Server(), "SomethingElse", PluginUpdateTriggers.Auto, Sha);

        Assert.Equal("no_update", result.Code);
    }

    [Fact]
    public async Task ABlankPluginNameHasNoUpdate()
    {
        var s = await SeedAsync();

        Assert.Equal("no_update", (await Build(s).Service.StartAsync(s.Server(), "  ", PluginUpdateTriggers.Auto)).Code);
    }

    [Fact]
    public async Task AnUpdateWithNoDownloadAddressIsRefused()
    {
        var s = await SeedAsync(o => o.AddLookup = false);
        await AssertRefusedAsync(s, Build(s), "no_download");
    }

    [Fact]
    public async Task AnUpdateWhoseLookupFoundNothingIsRefused()
    {
        var s = await SeedAsync(o => { o.Outcome = PluginDownloadOutcome.NotFound; o.Validation = PluginFileValidationState.NotChecked; });
        await AssertRefusedAsync(s, Build(s), "no_download");
    }

    [Fact]
    public async Task AnAddressThatIsNoLongerOneThePlatformWouldPassOnIsRefused()
    {
        var s = await SeedAsync(o => o.Url = "http://umod.org/plugins/x.cs");        // not https
        await AssertRefusedAsync(s, Build(s), "no_download");
    }

    [Fact]
    public async Task AnAddressOnAnotherHostIsRefused()
    {
        var s = await SeedAsync(o => o.Url = "https://evil.example.com/x.cs");
        await AssertRefusedAsync(s, Build(s), "no_download");
    }

    [Fact]
    public async Task AZipIsNeverSentWithoutTheUsersInstructions()
    {
        var s = await SeedAsync(o => { o.Validation = PluginFileValidationState.NeedsInstructions; o.Kind = "zip"; });
        await AssertRefusedAsync(s, Build(s), "needs_instructions", PluginUpdateTriggers.Manual);
    }

    [Fact]
    public async Task AFileThatWasCheckedAndCannotBeAppliedSaysWhy()
    {
        var s = await SeedAsync(o => { o.Validation = PluginFileValidationState.Invalid; o.Reason = "it is an HTML page"; });
        var h = Build(s);

        var result = await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha);

        Assert.Equal("not_applicable", result.Code);
        Assert.Contains("it is an HTML page", result.Message);
        Assert.Empty(h.Sent);
    }

    [Theory]
    [InlineData(PluginFileValidationState.NotChecked)]
    [InlineData(PluginFileValidationState.Failed)]
    public async Task AFileThatHasNotBeenCheckedIsNeverSent(PluginFileValidationState state)
    {
        var s = await SeedAsync(o => o.Validation = state);
        await AssertRefusedAsync(s, Build(s), "not_validated");
    }

    [Theory]
    [InlineData("other", "Tp", "2.0.0", Sha, 10L)]
    [InlineData("cs", "Bad Name", "2.0.0", Sha, 10L)]
    [InlineData("cs", "Bad;Name", "2.0.0", Sha, 10L)]
    [InlineData("cs", "1Bad", "2.0.0", Sha, 10L)]
    [InlineData("cs", "Tp", "2.0 0", Sha, 10L)]
    [InlineData("cs", "Tp", "2.0.0\n", Sha, 10L)]
    [InlineData("cs", "Tp", "2.0.0", "short", 10L)]
    [InlineData("cs", "Tp", "2.0.0", "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", 10L)]
    [InlineData("cs", "Tp", "2.0.0", Sha, 0L)]
    [InlineData("cs", "Tp", "2.0.0", Sha, null)]
    public async Task AFileWhoseRecordedDetailsCannotBeSentSafelyIsRefused(string kind, string @class, string version, string sha, long? size)
    {
        var s = await SeedAsync(o => { o.Kind = kind; o.Class = @class; o.InfoVersion = version; o.Sha = sha; o.Size = size; });
        await AssertRefusedAsync(s, Build(s), "not_applicable", sha: sha);
    }

    [Fact]
    public async Task ACommandThatIsNotOneLineIsNeverBuilt()
    {
        // The class and version go into the command text: nothing that could add a second command may pass.
        var s = await SeedAsync(o => o.Class = "Tp\narchon.config set recording true");
        await AssertRefusedAsync(s, Build(s), "not_applicable");
    }

    [Fact]
    public async Task IfTheFileIsNotTheOneThePersonLookedAtNothingIsApplied()
    {
        var s = await SeedAsync();
        await AssertRefusedAsync(s, Build(s), "file_changed", PluginUpdateTriggers.Manual, sha: OtherSha);
    }

    [Fact]
    public async Task TheHashTheClientSendsIsComparedWithoutRegardToCase()
    {
        var s = await SeedAsync();

        var result = await Build(s).Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha.ToUpperInvariant());

        Assert.True(result.Started, result.Message);
    }

    [Fact]
    public async Task AnUpdateAlreadyInProgressOnTheServerIsNotOverlapped()
    {
        var s = await SeedAsync();
        await AddUpdateAsync(s, ThirdPartyPluginUpdateStates.Started, T0.AddMinutes(-1), toVersion: "9.9.9");
        await using var context = new ApiDbContext(postgres.Options);
        var h = Build(s);

        var result = await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha);

        Assert.Equal("in_progress", result.Code);
        Assert.Empty(h.Sent);
    }

    [Theory]
    [InlineData(ThirdPartyPluginUpdateStates.RolledBack)]
    [InlineData(ThirdPartyPluginUpdateStates.Failed)]
    [InlineData(ThirdPartyPluginUpdateStates.Refused)]
    [InlineData(ThirdPartyPluginUpdateStates.Changed)]
    public async Task AnUpdateThatDidNotWorkOutIsNotTriedAgainAutomaticallyButAPersonCan(string earlier)
    {
        var s = await SeedAsync();
        await AddUpdateAsync(s, earlier, T0.AddHours(-2));
        await AssertRefusedAsyncKeepingHistory(s, Build(s), "already_tried");

        var manual = await Build(s).Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha);

        Assert.True(manual.Started, manual.Message);
    }

    private async Task AssertRefusedAsyncKeepingHistory(Scenario s, Harness h, string code)
    {
        var before = (await UpdatesAsync(s.ServerId)).Count;
        var result = await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Auto, Sha);

        Assert.Equal(code, result.Code);
        Assert.Empty(h.Sent);
        Assert.Equal(before, (await UpdatesAsync(s.ServerId)).Count);
    }

    [Fact]
    public async Task AnEarlierUpdateThatSucceededDoesNotStopANewerVersion()
    {
        var s = await SeedAsync(o => { o.LatestVersion = "3.0.0"; o.InfoVersion = "3.0.0"; });
        await AddUpdateAsync(s, ThirdPartyPluginUpdateStates.RolledBack, T0.AddDays(-3), toVersion: "2.0.0");

        var result = await Build(s).Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Auto, Sha);

        Assert.True(result.Started, result.Message);        // 2.0.0 failed before; this is 3.0.0
    }

    // ---- what the server's plugin says ----------------------------------------------------------------------------------

    [Fact]
    public async Task APluginThatRefusesIsRecordedWithItsCodeAndNothingIsStarted()
    {
        var s = await SeedAsync();
        var h = Build(s);
        h.Reply = _ => (true, Err("not_installed", "no plugin file declares that class"));

        var result = await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Auto, Sha);

        Assert.False(result.Started);
        Assert.Equal("not_installed", result.Code);
        var recorded = Assert.Single(await UpdatesAsync(s.ServerId));
        Assert.Equal((ThirdPartyPluginUpdateStates.Refused, "not_installed"), (recorded.State, recorded.Code));
        Assert.Equal("no plugin file declares that class", recorded.Message);
    }

    [Fact]
    public async Task ABusyPluginIsNotHeldAgainstTheUpdate()
    {
        var s = await SeedAsync();
        var h = Build(s);
        h.Reply = _ => (true, Err("busy", "another update is in progress"));

        var result = await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Auto, Sha);

        Assert.Equal("busy", result.Code);
        Assert.Empty(await UpdatesAsync(s.ServerId));          // it can be tried again next pass
    }

    [Fact]
    public async Task ACommandTheServerDoesNotRecogniseMeansItsPluginIsTooOld()
    {
        var s = await SeedAsync();
        var h = Build(s);
        h.Reply = _ => (true, "Unknown command: archon.thirdparty.update");

        var result = await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Auto, Sha);

        Assert.Equal("plugin_too_old", result.Code);
        Assert.Empty(await UpdatesAsync(s.ServerId));
    }

    [Theory]
    [InlineData("{\"v\":1,\"ok\":false}")]
    [InlineData("{\"v\":1,\"ok\":\"true\"}")]
    [InlineData("{\"v\":1}")]
    [InlineData("[]")]
    public async Task AnythingThatIsNotClearlyOkIsARefusal(string reply)
    {
        var s = await SeedAsync();
        var h = Build(s);
        h.Reply = _ => (true, reply);

        var result = await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Auto, Sha);

        Assert.False(result.Started);
    }

    [Fact]
    public async Task ARefusalCodeFromAPluginIsShortAndSimpleOrItIsDropped()
    {
        var s = await SeedAsync();
        var h = Build(s);
        h.Reply = _ => (true, Err("<script>alert(1)</script>"));

        var result = await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Auto, Sha);

        Assert.Equal("plugin_refused", result.Code);
    }

    [Fact]
    public async Task AServerThatIsNotConnectedIsNotAnAttempt()
    {
        var s = await SeedAsync();
        var h = Build(s);
        h.Reply = _ => (false, "");

        var result = await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Auto, Sha);

        Assert.Equal("not_connected", result.Code);
        Assert.Empty(await UpdatesAsync(s.ServerId));
    }

    [Fact]
    public async Task AServerThatDoesNotAnswerIsNotAnAttempt()
    {
        var s = await SeedAsync();
        var h = Build(s);
        h.TimeOut = true;

        var result = await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Auto, Sha);

        Assert.Equal("timeout", result.Code);
        Assert.Empty(await UpdatesAsync(s.ServerId));
    }

    // ---- outcomes -------------------------------------------------------------------------------------------------------

    private async Task<ThirdPartyPluginUpdate> StartedAsync(Scenario s, DateTimeOffset? at = null)
    {
        await AddUpdateAsync(s, ThirdPartyPluginUpdateStates.Started, at ?? T0);
        return Assert.Single(await UpdatesAsync(s.ServerId));
    }

    [Fact]
    public async Task AnAppliedUpdateIsRecordedAsApplied()
    {
        var s = await SeedAsync();
        await StartedAsync(s);
        var h = Build(s);
        h.Reply = _ => (true, Status("succeeded", s.Name));

        Assert.Equal(1, await h.Service.ReconcileAsync(s.Server(), T0.AddMinutes(2)));

        var row = Assert.Single(await UpdatesAsync(s.ServerId));
        Assert.Equal(ThirdPartyPluginUpdateStates.Applied, row.State);
        Assert.NotNull(row.ResolvedAtUtc);
        Assert.Equal("archon.thirdparty.status", Assert.Single(h.Sent).Command);
    }

    // ---- refreshing the plugin list once an update settles -----------------------------------------------------------------------------

    [Fact]
    public async Task SettlingAnUpdateAsksTheWorkerToPollTheServersPluginListRightAway()
    {
        var s = await SeedAsync();
        await StartedAsync(s);
        var h = Build(s);
        h.Reply = _ => (true, Status("succeeded", s.Name));

        await h.Service.ReconcileAsync(s.Server(), T0.AddMinutes(2));

        h.ServerPoll.Verify(p => p.PollNowAsync(s.ServerId, It.Is<IReadOnlyList<string>>(polls => polls.Contains(PollServerNowKinds.Plugins))), Times.Once());
    }

    [Fact]
    public async Task NothingSettlingAsksForNoPoll()
    {
        var s = await SeedAsync();
        await StartedAsync(s);
        var h = Build(s);
        h.Reply = _ => (true, Status("loading", s.Name));       // still under way: ReconcileAsync settles nothing

        Assert.Equal(0, await h.Service.ReconcileAsync(s.Server(), T0.AddMinutes(1)));

        h.ServerPoll.Verify(p => p.PollNowAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>>()), Times.Never());
    }

    [Fact]
    public async Task APollThatCannotBeSentDoesNotUndoTheOutcomeJustRecorded()
    {
        var s = await SeedAsync();
        await StartedAsync(s);
        var h = Build(s);
        h.Reply = _ => (true, Status("succeeded", s.Name));
        h.ServerPoll.Setup(p => p.PollNowAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>>())).ThrowsAsync(new InvalidOperationException("bus down"));

        var settled = await h.Service.ReconcileAsync(s.Server(), T0.AddMinutes(2));

        Assert.Equal(1, settled);
        Assert.Equal(ThirdPartyPluginUpdateStates.Applied, Assert.Single(await UpdatesAsync(s.ServerId)).State);
    }

    [Fact]
    public async Task ARolledBackUpdateIsRecordedWithTheReason()
    {
        var s = await SeedAsync();
        await StartedAsync(s);
        var h = Build(s);
        h.Reply = _ => (true, Status("rolled-back", s.Name, reason: "the plugin did not appear among the loaded plugins within 90 seconds"));

        await h.Service.ReconcileAsync(s.Server(), T0.AddMinutes(3));

        var row = Assert.Single(await UpdatesAsync(s.ServerId));
        Assert.Equal((ThirdPartyPluginUpdateStates.RolledBack, "rolled_back"), (row.State, row.Code));
        Assert.Contains("did not appear", row.Message);
    }

    [Fact]
    public async Task AFailedUpdateKeepsTheCodeTheServerGave()
    {
        var s = await SeedAsync();
        await StartedAsync(s);
        var h = Build(s);
        h.Reply = _ => (true, Status("failed", s.Name, reason: "download_failed: the server answered 404"));

        await h.Service.ReconcileAsync(s.Server(), T0.AddMinutes(1));

        var row = Assert.Single(await UpdatesAsync(s.ServerId));
        Assert.Equal((ThirdPartyPluginUpdateStates.Failed, "download_failed"), (row.State, row.Code));
    }

    [Fact]
    public async Task AChangedFileIsRecordedWithWhatWasActuallyDownloadedAndIsLookedAtAgain()
    {
        var s = await SeedAsync();
        await StartedAsync(s);
        var h = Build(s);
        h.Reply = _ => (true, Status("mismatch", s.Name, actual: OtherSha, reason: "changed: the file is not the one the panel checked"));

        await h.Service.ReconcileAsync(s.Server(), T0.AddMinutes(1));

        var row = Assert.Single(await UpdatesAsync(s.ServerId));
        Assert.Equal((ThirdPartyPluginUpdateStates.Changed, OtherSha), (row.State, row.ActualSha256));
        h.Validation.Verify(v => v.RecheckAsync(s.LookupId, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task AFailureToLookAtTheChangedFileAgainDoesNotLoseTheOutcome()
    {
        var s = await SeedAsync();
        await StartedAsync(s);
        var h = Build(s);
        h.Reply = _ => (true, Status("mismatch", s.Name, actual: OtherSha));
        h.Validation.Setup(v => v.RecheckAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("down"));

        await h.Service.ReconcileAsync(s.Server(), T0.AddMinutes(1));

        Assert.Equal(ThirdPartyPluginUpdateStates.Changed, Assert.Single(await UpdatesAsync(s.ServerId)).State);
    }

    // ---- checking a file again on request, not only when a mismatch triggers it -------------------------------------------------------------

    [Fact]
    public async Task RecheckingAPluginDownloadsItsFileAgainRegardlessOfState()
    {
        var s = await SeedAsync();
        var h = Build(s);
        h.Validation.Setup(v => v.RecheckAsync(s.LookupId, It.IsAny<CancellationToken>())).ReturnsAsync((PluginDownloadLookup?)null);

        var found = await h.Service.RecheckFileAsync(s.Server(), s.Name);

        Assert.True(found);
        h.Validation.Verify(v => v.RecheckAsync(s.LookupId, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task RecheckingMatchesThePluginByNormalizedNameLikeEverythingElseHere()
    {
        var s = await SeedAsync();
        var h = Build(s);
        h.Validation.Setup(v => v.RecheckAsync(s.LookupId, It.IsAny<CancellationToken>())).ReturnsAsync((PluginDownloadLookup?)null);

        // The installed/display name can differ from the notice's own spelling by punctuation alone (e.g. "Monument Addons" vs "MonumentAddons") -
        // matching has to survive that the same way GetOffersAsync and StartAsync already do.
        var found = await h.Service.RecheckFileAsync(s.Server(), s.Name.Replace(" ", "").ToUpperInvariant());

        Assert.True(found);
        h.Validation.Verify(v => v.RecheckAsync(s.LookupId, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task RecheckingAPluginWithNoNoticeFindsNothingToCheck()
    {
        var s = await SeedAsync();
        var h = Build(s);

        var found = await h.Service.RecheckFileAsync(s.Server(), "SomethingElse");

        Assert.False(found);
        h.Validation.Verify(v => v.RecheckAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task RecheckingAPluginWithNoDownloadAddressFindsNothingToCheck()
    {
        var s = await SeedAsync(o => o.AddLookup = false);
        var h = Build(s);

        var found = await h.Service.RecheckFileAsync(s.Server(), s.Name);

        Assert.False(found);
        h.Validation.Verify(v => v.RecheckAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task RecheckingIsNotBlockedByAnotherUpdateBeingBusyOnTheServer()
    {
        // A recheck only touches the platform's own shared record of the file - it never talks to this server's plugin at all.
        var s = await SeedAsync();
        await AddUpdateAsync(s, ThirdPartyPluginUpdateStates.Started, T0.AddMinutes(-1), toVersion: "9.9.9");
        var h = Build(s);
        h.Validation.Setup(v => v.RecheckAsync(s.LookupId, It.IsAny<CancellationToken>())).ReturnsAsync((PluginDownloadLookup?)null);

        Assert.True(await h.Service.RecheckFileAsync(s.Server(), s.Name));
    }

    [Fact]
    public async Task RecheckingTheSamePluginTwiceInQuickSuccessionOnlyDownloadsItOnce()
    {
        var s = await SeedAsync();
        var h = Build(s);
        h.RecheckThrottle.SetupSequence(t => t.TryAcquire(s.LookupId)).Returns(true).Returns(false); // once, then throttled
        h.Validation.Setup(v => v.RecheckAsync(s.LookupId, It.IsAny<CancellationToken>())).ReturnsAsync((PluginDownloadLookup?)null);

        var first = await h.Service.RecheckFileAsync(s.Server(), s.Name);
        var second = await h.Service.RecheckFileAsync(s.Server(), s.Name);

        Assert.True(first);
        Assert.True(second);         // not a failure - just too soon; whatever was already found still stands
        h.Validation.Verify(v => v.RecheckAsync(s.LookupId, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Theory]
    [InlineData("downloading")]
    [InlineData("loading")]
    public async Task AnUpdateStillUnderWayIsLeftAlone(string phase)
    {
        var s = await SeedAsync();
        await StartedAsync(s);
        var h = Build(s);
        h.Reply = _ => (true, Status(phase, s.Name));

        Assert.Equal(0, await h.Service.ReconcileAsync(s.Server(), T0.AddMinutes(1)));

        Assert.Equal(ThirdPartyPluginUpdateStates.Started, Assert.Single(await UpdatesAsync(s.ServerId)).State);
    }

    [Fact]
    public async Task AnUpdateThatGoesOnFarTooLongIsFailed()
    {
        var s = await SeedAsync();
        await StartedAsync(s);
        var h = Build(s);
        h.Reply = _ => (true, Status("loading", s.Name));

        await h.Service.ReconcileAsync(s.Server(), T0 + ThirdPartyPluginUpdateService.OutcomeWithin + TimeSpan.FromSeconds(1));

        var row = Assert.Single(await UpdatesAsync(s.ServerId));
        Assert.Equal((ThirdPartyPluginUpdateStates.Failed, "no_outcome"), (row.State, row.Code));
    }

    [Fact]
    public async Task AServerThatCannotBeReachedKeepsItsUpdatePendingUntilTheTimeIsUp()
    {
        var s = await SeedAsync();
        await StartedAsync(s);
        var h = Build(s);
        h.Reply = _ => (false, "");

        Assert.Equal(0, await h.Service.ReconcileAsync(s.Server(), T0.AddMinutes(5)));
        Assert.Equal(ThirdPartyPluginUpdateStates.Started, Assert.Single(await UpdatesAsync(s.ServerId)).State);

        Assert.Equal(1, await h.Service.ReconcileAsync(s.Server(), T0.AddMinutes(11)));
        Assert.Equal(ThirdPartyPluginUpdateStates.Failed, Assert.Single(await UpdatesAsync(s.ServerId)).State);
    }

    [Fact]
    public async Task AStatusThatIsAboutSomethingElseFallsBackToWhatThePluginListShows()
    {
        // The RustArchon plugin reloaded and lost its notes, so its status is idle; the plugin list now shows the new version, so it worked.
        var s = await SeedAsync();
        await StartedAsync(s);
        var h = Build(s, installedVersion: "2.0.0");
        h.Reply = _ => (true, Status("idle", ""));

        await h.Service.ReconcileAsync(s.Server(), T0.AddMinutes(2));

        Assert.Equal(ThirdPartyPluginUpdateStates.Applied, Assert.Single(await UpdatesAsync(s.ServerId)).State);
    }

    [Fact]
    public async Task AStatusAboutADifferentFileIsNotTakenForThisUpdate()
    {
        var s = await SeedAsync();
        await StartedAsync(s);
        var h = Build(s);
        h.Reply = _ => (true, Status("succeeded", s.Name, sha: OtherSha));       // an earlier update of the same plugin, a different file

        await h.Service.ReconcileAsync(s.Server(), T0.AddMinutes(2));

        Assert.Equal(ThirdPartyPluginUpdateStates.Started, Assert.Single(await UpdatesAsync(s.ServerId)).State);
    }

    [Fact]
    public async Task AStatusAboutADifferentPluginIsNotTakenForThisUpdate()
    {
        var s = await SeedAsync();
        await StartedAsync(s);
        var h = Build(s);
        h.Reply = _ => (true, Status("succeeded", "SomeoneElse"));

        await h.Service.ReconcileAsync(s.Server(), T0.AddMinutes(2));

        Assert.Equal(ThirdPartyPluginUpdateStates.Started, Assert.Single(await UpdatesAsync(s.ServerId)).State);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"v\":1,\"ok\":false,\"err\":\"x\"}")]
    [InlineData("{\"v\":1,\"ok\":true}")]
    public async Task AStatusThatCannotBeReadChangesNothing(string reply)
    {
        var s = await SeedAsync();
        await StartedAsync(s);
        var h = Build(s);
        h.Reply = _ => (true, reply);

        Assert.Equal(0, await h.Service.ReconcileAsync(s.Server(), T0.AddMinutes(1)));
    }

    [Fact]
    public async Task WhatThePluginSaysIsCutShortAndStrippedOfControlCharactersBeforeItIsKept()
    {
        var s = await SeedAsync();
        await StartedAsync(s);
        var h = Build(s);
        h.Reply = _ => (true, Status("rolled-back", s.Name, reason: new string('x', 900) + "\\u0007\\n end"));

        await h.Service.ReconcileAsync(s.Server(), T0.AddMinutes(1));

        var row = Assert.Single(await UpdatesAsync(s.ServerId));
        Assert.Equal(500, row.Message.Length);
        Assert.DoesNotContain(row.Message, char.IsControl);
    }

    [Fact]
    public async Task AnUpdateAlreadySettledIsNotSettledTwice()
    {
        var s = await SeedAsync();
        await StartedAsync(s);
        var h = Build(s);
        h.Reply = _ => (true, Status("succeeded", s.Name));
        await h.Service.ReconcileAsync(s.Server(), T0.AddMinutes(1));
        h.Reply = _ => (true, Status("rolled-back", s.Name));

        Assert.Equal(0, await h.Service.ReconcileAsync(s.Server(), T0.AddMinutes(2)));

        Assert.Equal(ThirdPartyPluginUpdateStates.Applied, Assert.Single(await UpdatesAsync(s.ServerId)).State);
    }

    // ---- what a person is shown -----------------------------------------------------------------------------------------

    private async Task<ThirdPartyUpdateOffer?> OfferAsync(Scenario s, Harness? h = null, DateTimeOffset? now = null)
    {
        h ??= Build(s);
        var offers = await h.Service.GetOffersAsync(s.Server(), now ?? T0);
        return offers.GetValueOrDefault(s.Normalized);
    }

    [Fact]
    public async Task ACheckedFileWaitingIsReadyAndCanBeApplied()
    {
        var offer = await OfferAsync(await SeedAsync());

        Assert.NotNull(offer);
        Assert.Equal(ThirdPartyUpdateOfferStates.Ready, offer.State);
        Assert.True(offer.CanApply);
        Assert.Equal(Sha, offer.FileSha256);
        Assert.Null(offer.HeldUntilUtc);
    }

    [Fact]
    public async Task NothingIsOfferedWhenThePlanDoesNotOfferTheFeature()
    {
        Assert.Null(await OfferAsync(await SeedAsync(o => o.PlanOffers = false)));
    }

    [Fact]
    public async Task NothingIsOfferedForAPluginThatIsNotOutdated()
    {
        var s = await SeedAsync();

        Assert.Null(await OfferAsync(s, Build(s, installedVersion: "2.0.0")));
    }

    [Theory]
    [InlineData(PluginFileValidationState.NotChecked)]
    [InlineData(PluginFileValidationState.Failed)]
    public async Task NothingIsSaidAboutAFileThatHasNotBeenChecked(PluginFileValidationState state)
    {
        Assert.Null(await OfferAsync(await SeedAsync(o => o.Validation = state)));
    }

    [Fact]
    public async Task NothingIsOfferedWhenNoAddressWasFound()
    {
        Assert.Null(await OfferAsync(await SeedAsync(o => o.AddLookup = false)));
    }

    [Fact]
    public async Task ACheckedFileThatCannotBeAppliedIsShownWithTheReason()
    {
        var offer = await OfferAsync(await SeedAsync(o => { o.Validation = PluginFileValidationState.Invalid; o.Reason = "it is an HTML page"; }));

        Assert.NotNull(offer);
        Assert.Equal((ThirdPartyUpdateOfferStates.NotApplicable, "it is an HTML page", false), (offer.State, offer.Detail, offer.CanApply));
    }

    [Fact]
    public async Task AZipIsShownAsNeedingInstructions()
    {
        var offer = await OfferAsync(await SeedAsync(o => { o.Validation = PluginFileValidationState.NeedsInstructions; o.Kind = "zip"; }));

        Assert.NotNull(offer);
        Assert.Equal((ThirdPartyUpdateOfferStates.NeedsInstructions, true), (offer.State, offer.CanApply));       // the person can give instructions
    }

    [Fact]
    public async Task AFileTheServerCouldNotBeToldAboutIsNotOfferedForApplying()
    {
        var offer = await OfferAsync(await SeedAsync(o => o.Class = "Bad Name"));

        Assert.NotNull(offer);
        Assert.Equal((ThirdPartyUpdateOfferStates.NotApplicable, false), (offer.State, offer.CanApply));
    }

    [Fact]
    public async Task ApplyingIsNotOfferedWhenThePluginOnTheServerCannotDoItAndSaysSo()
    {
        var s = await SeedAsync();

        var offer = await OfferAsync(s, Build(s, status: st => st.Capabilities = ["config"]));

        Assert.NotNull(offer);
        Assert.Equal((ThirdPartyUpdateOfferStates.Ready, false), (offer.State, offer.CanApply));
        Assert.Contains("has not reported recently", offer.Detail);
    }

    [Fact]
    public async Task ApplyingIsNotOfferedWhenTheServerHasNotBeenHeardFromRecentlyAndSaysSo()
    {
        var s = await SeedAsync();

        var offer = await OfferAsync(s, Build(s, status: st => st.CapturedAtUtc = T0.AddHours(-1)));

        Assert.NotNull(offer);
        Assert.False(offer.CanApply);
        Assert.Contains("has not reported recently", offer.Detail);
    }

    [Fact]
    public async Task ApplyingIsNotOfferedWhileAnotherUpdateIsInProgressAndSaysSo()
    {
        var s = await SeedAsync();
        await AddUpdateAsync(s, ThirdPartyPluginUpdateStates.Started, T0.AddMinutes(-1), toVersion: "9.9.9");

        var offer = await OfferAsync(s);

        Assert.NotNull(offer);
        Assert.False(offer.CanApply);
        Assert.Contains("in progress on this server", offer.Detail);
    }

    [Fact]
    public async Task AZipIsNotOfferedWhenThePluginCannotUnpackArchivesAndSaysSo()
    {
        var s = await SeedAsync(AsZip);

        var offer = await OfferAsync(s, Build(s, status: st => st.Capabilities = [RustArchonPlugin.ThirdPartyUpdateCapability]));

        Assert.NotNull(offer);
        Assert.Equal((ThirdPartyUpdateOfferStates.NeedsInstructions, false), (offer.State, offer.CanApply));
        Assert.Contains("cannot unpack archives", offer.Detail);
    }

    [Fact]
    public async Task WhenNothingIsBlockingItReadyHasNoDetailToShow()
    {
        var s = await SeedAsync();

        var offer = await OfferAsync(s);

        Assert.NotNull(offer);
        Assert.True(offer.CanApply);
        Assert.Equal(string.Empty, offer.Detail);
    }

    [Fact]
    public async Task ARetryThatIsCurrentlyBlockedExplainsBothWhatHappenedLastTimeAndWhyItCannotBeTriedRightNow()
    {
        var s = await SeedAsync();
        await AddUpdateAsync(s, ThirdPartyPluginUpdateStates.Failed, T0.AddHours(-1), message: "download_failed: the server answered 404");

        var offer = await OfferAsync(s, Build(s, status: st => st.Capabilities = ["config"]));

        Assert.NotNull(offer);
        Assert.False(offer.CanApply);
        Assert.Contains("download_failed", offer.Detail);
        Assert.Contains("has not reported recently", offer.Detail);
    }

    [Fact]
    public async Task AChangedFileThePlatformStillSeesAsTheSameOneExplainsSoButCanStillBeTried()
    {
        var s = await SeedAsync();
        await AddUpdateAsync(s, ThirdPartyPluginUpdateStates.Changed, T0.AddMinutes(-5), sha: Sha, message: "changed: the file is not the one the panel checked");

        var offer = await OfferAsync(s);

        Assert.NotNull(offer);
        Assert.True(offer.CanApply);        // a hash that has not moved is not proof retrying is pointless - see this scenario's Api-side remarks
        Assert.Contains("hashes the same", offer.Detail);
        Assert.Contains("changed: the file is not the one the panel checked", offer.Detail);       // what happened last time is still said too
    }

    [Fact]
    public async Task AChangedFileThatIsAlsoCurrentlyBlockedExplainsTheBlockingReasonNotTheSameFileOne()
    {
        // A different file WAS found (so the same-file reason does not apply), but something else stops trying it right now.
        var s = await SeedAsync(o => o.Sha = OtherSha);
        await AddUpdateAsync(s, ThirdPartyPluginUpdateStates.Changed, T0.AddMinutes(-5), sha: Sha, message: "changed: the file is not the one the panel checked");

        var offer = await OfferAsync(s, Build(s, status: st => st.Capabilities = ["config"]));

        Assert.NotNull(offer);
        Assert.False(offer.CanApply);
        Assert.Contains("has not reported recently", offer.Detail);
        Assert.DoesNotContain("not found a different file", offer.Detail);
    }

    [Fact]
    public async Task AnUpdateUnderWayIsShownAsApplying()
    {
        var s = await SeedAsync();
        await AddUpdateAsync(s, ThirdPartyPluginUpdateStates.Started, T0.AddMinutes(-1));

        var offer = await OfferAsync(s);

        Assert.NotNull(offer);
        Assert.Equal((ThirdPartyUpdateOfferStates.Applying, false), (offer.State, offer.CanApply));
    }

    [Theory]
    [InlineData(ThirdPartyPluginUpdateStates.RolledBack, ThirdPartyUpdateOfferStates.RolledBack)]
    [InlineData(ThirdPartyPluginUpdateStates.Failed, ThirdPartyUpdateOfferStates.Failed)]
    [InlineData(ThirdPartyPluginUpdateStates.Refused, ThirdPartyUpdateOfferStates.Refused)]
    public async Task AnUpdateThatDidNotWorkOutIsShownWithWhyAndCanBeTriedAgainByAPerson(string state, string shown)
    {
        var s = await SeedAsync();
        await AddUpdateAsync(s, state, T0.AddHours(-1), message: "did not appear");

        var offer = await OfferAsync(s);

        Assert.NotNull(offer);
        Assert.Equal((shown, "did not appear", true), (offer.State, offer.Detail, offer.CanApply));
    }

    [Fact]
    public async Task AnAppliedUpdateIsShownAsApplied()
    {
        var s = await SeedAsync();
        await AddUpdateAsync(s, ThirdPartyPluginUpdateStates.Applied, T0.AddMinutes(-3));

        var offer = await OfferAsync(s);

        Assert.NotNull(offer);
        Assert.Equal((ThirdPartyUpdateOfferStates.Applied, false), (offer.State, offer.CanApply));
    }

    [Fact]
    public async Task AChangedFileWhoseNewVersionHasBeenLookedAtCanBeAppliedByAPersonWhoIsShownIt()
    {
        var s = await SeedAsync(o => o.Sha = OtherSha);            // the platform has since looked again and now sees a different file...
        await AddUpdateAsync(s, ThirdPartyPluginUpdateStates.Changed, T0.AddMinutes(-5), sha: Sha);       // ...from the one the server was sent

        var offer = await OfferAsync(s);

        Assert.NotNull(offer);
        Assert.Equal((ThirdPartyUpdateOfferStates.Changed, true, OtherSha), (offer.State, offer.CanApply, offer.FileSha256));
    }

    [Fact]
    public async Task AChangedFileCanBeTriedAgainEvenWhenTheHashHasNotMoved()
    {
        // A mismatch is not proof the author changed anything - a CDN edge, a header/encoding difference between how the Api and the plugin each fetch
        // it, or plain timing can produce one with nobody having changed anything. Blocking retrying used to leave no way to resolve this at all
        // (Scott: "there's no way to resolve... it says check it before applying, but there's no way to apply it after checking").
        var s = await SeedAsync();
        await AddUpdateAsync(s, ThirdPartyPluginUpdateStates.Changed, T0.AddMinutes(-5), sha: Sha);

        var offer = await OfferAsync(s);

        Assert.NotNull(offer);
        Assert.Equal((ThirdPartyUpdateOfferStates.Changed, true), (offer.State, offer.CanApply));
    }

    [Fact]
    public async Task AnUpdateOfAnEarlierVersionOfThePluginIsNotWhatIsShownForTheNewOne()
    {
        var s = await SeedAsync(o => { o.LatestVersion = "3.0.0"; o.InfoVersion = "3.0.0"; });
        await AddUpdateAsync(s, ThirdPartyPluginUpdateStates.RolledBack, T0.AddDays(-2), toVersion: "2.0.0");

        var offer = await OfferAsync(s);

        Assert.NotNull(offer);
        Assert.Equal(ThirdPartyUpdateOfferStates.Ready, offer.State);
    }

    [Fact]
    public async Task ApplyingIsStillOfferedInsideTheWindowBeforeTheWipeAndSaysWhenAutomaticUpdatesResume()
    {
        var s = await SeedAsync();

        var offer = await OfferAsync(s, Build(s, now: HeldTime), HeldTime);

        Assert.NotNull(offer);
        Assert.True(offer.CanApply);
        Assert.NotNull(offer.HeldUntilUtc);
    }

    // ---- a zip, applied by a person's folder rules -----------------------------------------------------------------------------

    private static void AsZip(Options o)
    {
        o.Validation = PluginFileValidationState.NeedsInstructions;
        o.Kind = "zip";
    }

    private async Task SaveRulesAsync(Scenario s, IReadOnlyList<ZipMappingRule> rules, bool trusted)
    {
        var repository = new PluginZipMappingRepository(new ApiDbContext(postgres.Options));
        await repository.SaveAsync(s.TenantId, s.ServerId, s.Normalized, rules, T0);
        if (trusted)
        {
            await new PluginZipMappingRepository(new ApiDbContext(postgres.Options)).MarkTrustedAsync(s.ServerId, s.Normalized, T0);
        }
    }

    private async Task<SavedZipMapping?> SavedAsync(Scenario s) => await new PluginZipMappingRepository(new ApiDbContext(postgres.Options)).FindAsync(s.ServerId, s.Normalized);

    [Fact]
    public async Task AZipIsSentWithTheRulesTheirTotalSizeAndTheArchivesHashAndSize()
    {
        var s = await SeedAsync(AsZip);
        var h = Build(s);

        var result = await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha, EnglishRules);

        Assert.True(result.Started, result.Message);
        var command = Assert.Single(h.Sent).Command.Split(' ');
        Assert.Equal(["archon.thirdparty.zip", s.Name, "2.0.0", Sha, "12345", (100 + 20 + 1000 + 2000).ToString()], command[..6]);
        Assert.Equal($"https://umod.org/plugins/{s.Name}.cs", command[7]);
        Assert.Equal(8, command.Length);                                              // no spaces anywhere in the rules argument
        var decoded = ZipMapping.Decode(command[6])!;
        Assert.Equal(EnglishRules.Select(r => (r.IsFolder, r.Source, r.Role)), decoded.Select(r => (r.IsFolder, r.Source, r.Role)));
        var recorded = Assert.Single(await UpdatesAsync(s.ServerId));
        Assert.Equal(("zip", false, ThirdPartyPluginUpdateStates.Started), (recorded.Kind, recorded.SaveMapping, recorded.State));
    }

    [Fact]
    public async Task AZipWithNoInstructionsAnywhereIsNotSent()
    {
        var s = await SeedAsync(AsZip);
        var h = Build(s);

        var result = await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha);

        Assert.Equal("needs_instructions", result.Code);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task AServerWhosePluginCannotUnpackAnArchiveIsNeverSentOne()
    {
        var s = await SeedAsync(AsZip);
        var h = Build(s, status: st => st.Capabilities = [RustArchonPlugin.ThirdPartyUpdateCapability]);

        var result = await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha, EnglishRules);

        Assert.Equal("zip_unsupported", result.Code);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task ARuleSetWithAFileNobodyAssignedIsRefusedAndSaysWhich()
    {
        var s = await SeedAsync(AsZip);
        var h = Build(s);

        var result = await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha, [.. EnglishRules.Where(r => r.Source != "ru/")]);

        Assert.Equal("mapping_invalid", result.Code);
        Assert.Contains("ru/plugins/test.cs", result.Message);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task InstallingBothLanguagesOverEachOtherIsRefused()
    {
        var s = await SeedAsync(AsZip);
        var both = new ZipMappingRule[]
        {
            new() { IsFolder = true, Source = "en/", Role = ZipRoles.Plugins }, new() { IsFolder = true, Source = "ru/", Role = ZipRoles.Plugins }
        };

        var result = await Build(s).Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha, both);

        Assert.Equal("mapping_invalid", result.Code);
    }

    [Fact]
    public async Task AProgramFileInTheArchiveCanBeSkippedButNeverInstalled()
    {
        var files = new List<ZipEntryInfo>(ExampleFiles) { new("extension/Oxide.Ext.Discord.dll", 500) };
        var s = await SeedAsync(o => { AsZip(o); o.Files = files; });
        var skip = new ZipMappingRule { IsFolder = true, Source = "extension/", Role = ZipRoles.Skip };
        var install = new ZipMappingRule { IsFolder = true, Source = "extension/", Role = ZipRoles.Data };

        var refused = await Build(s).Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha, [.. EnglishRules, install]);
        var allowed = await Build(s).Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha, [.. EnglishRules, skip]);

        Assert.Equal("mapping_invalid", refused.Code);
        Assert.Contains("program file", refused.Message);
        Assert.True(allowed.Started, allowed.Message);
    }

    [Fact]
    public async Task InstructionsThatLeaveOutThePluginItselfAreRefused()
    {
        var s = await SeedAsync(AsZip);
        var onlySettings = new ZipMappingRule[]
        {
            new() { IsFolder = true, Source = "en/configs/", Role = ZipRoles.Config }, new() { IsFolder = true, Source = "en/plugins/", Role = ZipRoles.Skip },
            new() { IsFolder = true, Source = "en/images/", Role = ZipRoles.Data }, new() { IsFolder = true, Source = "ru/", Role = ZipRoles.Skip }
        };

        var result = await Build(s).Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha, onlySettings);

        Assert.Equal("mapping_invalid", result.Code);
        Assert.Contains("plugin's own file", result.Message);
    }

    [Fact]
    public async Task APluginFileThatDidNotReadAsCSharpCannotBeInstalled()
    {
        var s = await SeedAsync(o =>
        {
            AsZip(o);
            o.Findings = [new("en/plugins/test.cs", "Tp", "Tp", "2.0.0", null), new("ru/plugins/test.cs", null, null, null, "does not read as C#: expected }")];
        }, "Tp0broken0aa");
        var both = new ZipMappingRule[]
        {
            new() { IsFolder = true, Source = "en/plugins/", Role = ZipRoles.Plugins }, new() { IsFolder = true, Source = "ru/plugins/", Role = ZipRoles.Plugins, SubFolder = "ru" },
            new() { IsFolder = true, Source = "en/configs/", Role = ZipRoles.Config }, new() { IsFolder = true, Source = "en/images/", Role = ZipRoles.Data },
            new() { IsFolder = true, Source = "ru/configs/", Role = ZipRoles.Skip }, new() { IsFolder = true, Source = "ru/images/", Role = ZipRoles.Skip }
        };

        var result = await Build(s).Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha, both);

        Assert.Equal("mapping_invalid", result.Code);
        Assert.Contains("does not read as C#", result.Message);
    }

    [Fact]
    public async Task AnArchiveWhoseFileListWasCutShortIsNotApplied()
    {
        var many = Enumerable.Range(0, PluginDownloadLookup.MaxZipEntries).Select(i => new ZipEntryInfo($"f{i}.txt", 1)).Prepend(new ZipEntryInfo("en/plugins/test.cs", 1)).Take(PluginDownloadLookup.MaxZipEntries).ToList();
        var s = await SeedAsync(o => { AsZip(o); o.Files = many; });

        var result = await Build(s).Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha, EnglishRules);

        Assert.Equal("not_applicable", result.Code);
    }

    [Fact]
    public async Task AnArchiveWhosePluginFilesWereNeverReadIsNotApplied()
    {
        // Checked before source files were read: it is looked at again before anything is sent.
        var s = await SeedAsync(AsZip);
        await using (var context = new ApiDbContext(postgres.Options))
        {
            await context.PluginDownloadLookups.Where(l => l.Id == s.LookupId).ExecuteUpdateAsync(u => u.SetProperty(l => l.ZipSourceFindings, (string?)null));
        }

        var result = await Build(s).Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha, EnglishRules);

        Assert.Equal("not_applicable", result.Code);
    }

    [Fact]
    public async Task InstructionsThatWouldInstallMoreThanAServerWillUnpackAreRefused()
    {
        var big = ExampleFiles.Select(e => e.Path == "en/images/test/one.jpg" ? e with { Size = ZipMapping.MaxInstallBytes } : e).ToList();
        var s = await SeedAsync(o => { AsZip(o); o.Files = big; });

        var result = await Build(s).Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha, EnglishRules);

        Assert.Equal("mapping_invalid", result.Code);
        Assert.Contains("more than 512 MB", result.Message);
    }

    [Fact]
    public async Task AnArchiveLargerThanAServerWillFetchIsNotApplicableWhateverTheRules()
    {
        var s = await SeedAsync(o => { AsZip(o); o.Size = ZipMapping.MaxArchiveBytes + 1; });

        Assert.Equal("not_applicable", (await Build(s).Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha, EnglishRules)).Code);
    }

    [Fact]
    public async Task ASingleFileLargerThanAServerWillFetchIsNotApplicableEither()
    {
        var s = await SeedAsync(o => o.Size = ZipMapping.MaxArchiveBytes + 1);

        Assert.Equal("not_applicable", (await Build(s).Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha)).Code);
    }

    // ---- excluding a plugin from updates on a server -------------------------------------------------------------------

    [Fact]
    public async Task AnExcludedPluginIsShownAsExcludedAndCannotBeApplied()
    {
        var s = await SeedAsync();
        var h = Build(s);
        await h.Service.ExcludeAsync(s.Server(), s.Name, "This is the demo build; I run the paid one from the author directly.", T0);

        var offer = await OfferAsync(s, h);

        Assert.NotNull(offer);
        Assert.Equal(ThirdPartyUpdateOfferStates.Excluded, offer.State);
        Assert.False(offer.CanApply);
        Assert.Contains("demo build", offer.Detail);
    }

    [Fact]
    public async Task AnExcludedPluginIsShownEvenBeforeAFileHasBeenCheckedForIt()
    {
        var s = await SeedAsync(o => o.AddLookup = false);
        var h = Build(s);
        await h.Service.ExcludeAsync(s.Server(), s.Name, null, T0);

        var offer = await OfferAsync(s, h);

        Assert.NotNull(offer);
        Assert.Equal(ThirdPartyUpdateOfferStates.Excluded, offer.State);
    }

    [Fact]
    public async Task ExcludingAPluginRefusesBothAnAutomaticAndAByHandApply()
    {
        var s = await SeedAsync();
        var h = Build(s);
        await h.Service.ExcludeAsync(s.Server(), s.Name, "not what I run", T0);

        var automatic = await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Auto, Sha);
        var manual = await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha);

        Assert.Equal(("excluded", false), (automatic.Code, automatic.Started));
        Assert.Equal(("excluded", false), (manual.Code, manual.Started));
        Assert.Empty(h.Sent);        // the server was never even asked
    }

    [Fact]
    public async Task LiftingAnExclusionOffersTheUpdateAgain()
    {
        var s = await SeedAsync();
        var h = Build(s);
        await h.Service.ExcludeAsync(s.Server(), s.Name, "wrong build", T0);
        await h.Service.IncludeAsync(s.Server(), s.Name);

        var offer = await OfferAsync(s, h);
        var result = await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha);

        Assert.Equal(ThirdPartyUpdateOfferStates.Ready, offer!.State);
        Assert.True(result.Started, result.Message);
    }

    [Fact]
    public async Task ExcludingAPluginWithNoNoteIsStillExcluded()
    {
        var s = await SeedAsync();
        var h = Build(s);
        await h.Service.ExcludeAsync(s.Server(), s.Name, null, T0);

        var offer = await OfferAsync(s, h);

        Assert.NotNull(offer);
        Assert.Equal(ThirdPartyUpdateOfferStates.Excluded, offer.State);
        Assert.Equal("You've excluded this plugin from updates on this server.", offer.Detail);
    }

    [Fact]
    public async Task ExcludingTwiceReplacesTheNoteRatherThanDuplicatingTheRow()
    {
        var s = await SeedAsync();
        var h = Build(s);
        await h.Service.ExcludeAsync(s.Server(), s.Name, "first reason", T0);
        await h.Service.ExcludeAsync(s.Server(), s.Name, "second reason", T0.AddMinutes(1));

        var offer = await OfferAsync(s, h);

        Assert.NotNull(offer);
        Assert.Contains("second reason", offer.Detail);
        Assert.DoesNotContain("first reason", offer.Detail);
    }

    [Fact]
    public async Task ExcludingAnUnknownPluginNameDoesNothingHarmful()
    {
        var s = await SeedAsync();
        var h = Build(s);

        await h.Service.ExcludeAsync(s.Server(), "   ", "note", T0);

        // Nothing blew up, and the real plugin's own offer is untouched.
        var offer = await OfferAsync(s, h);
        Assert.Equal(ThirdPartyUpdateOfferStates.Ready, offer!.State);
    }

    [Fact]
    public async Task InstructionsTooLongToSendInOneCommandAreRefused()
    {
        var s = await SeedAsync(AsZip);
        var rules = EnglishRules.Concat(Enumerable.Range(0, 50).Select(i => new ZipMappingRule { IsFolder = true, Source = new string('a', 100) + i + "/", Role = ZipRoles.Skip })).ToList();

        var result = await Build(s).Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha, rules);

        Assert.False(result.Started);
        Assert.Contains(result.Code, new[] { "mapping_invalid" });
    }

    // ---- saving the rules, and trusting them once they have worked --------------------------------------------------------------

    [Fact]
    public async Task AskedToSaveTheRulesTheyAreKeptButNotTrustedUntilAnUpdateWithThemComesUp()
    {
        var s = await SeedAsync(AsZip);
        var h = Build(s);

        await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha, EnglishRules, saveMapping: true);

        var saved = await SavedAsync(s);
        Assert.NotNull(saved);
        Assert.False(saved.Trusted);
        Assert.Equal(EnglishRules.Length, saved.Rules.Count);
        Assert.True(Assert.Single(await UpdatesAsync(s.ServerId)).SaveMapping);

        h.Reply = _ => (true, Status("succeeded", s.Name));
        await h.Service.ReconcileAsync(s.Server(), T0.AddMinutes(2));

        Assert.True((await SavedAsync(s))!.Trusted);
    }

    [Theory]
    [InlineData("rolled-back")]
    [InlineData("failed")]
    [InlineData("mismatch")]
    public async Task RulesThatDidNotProduceAWorkingUpdateNeverBecomeTrusted(string phase)
    {
        var s = await SeedAsync(AsZip);
        var h = Build(s);
        await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha, EnglishRules, saveMapping: true);

        h.Reply = _ => (true, Status(phase, s.Name, actual: OtherSha, reason: "did not load"));
        await h.Service.ReconcileAsync(s.Server(), T0.AddMinutes(2));

        Assert.False((await SavedAsync(s))!.Trusted);
    }

    [Fact]
    public async Task NotAskedToSaveNothingIsKept()
    {
        var s = await SeedAsync(AsZip);
        var h = Build(s);
        await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha, EnglishRules, saveMapping: false);

        h.Reply = _ => (true, Status("succeeded", s.Name));
        await h.Service.ReconcileAsync(s.Server(), T0.AddMinutes(2));

        Assert.Null(await SavedAsync(s));
    }

    [Fact]
    public async Task ARefusalByTheServerSavesNothing()
    {
        var s = await SeedAsync(AsZip);
        var h = Build(s);
        h.Reply = _ => (true, Err("not_installed"));

        await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha, EnglishRules, saveMapping: true);

        Assert.Null(await SavedAsync(s));
    }

    [Fact]
    public async Task SavingDifferentRulesLaterReplacesThemAndTheyStartUntrustedAgain()
    {
        var s = await SeedAsync(AsZip);
        await SaveRulesAsync(s, EnglishRules, trusted: true);
        var changed = EnglishRules.Select(r => r.Source == "en/images/" ? new ZipMappingRule { IsFolder = true, Source = "en/images/", Role = ZipRoles.Data, SubFolder = "pictures" } : r).ToList();

        await SaveRulesAsync(s, changed, trusted: false);

        var saved = (await SavedAsync(s))!;
        Assert.False(saved.Trusted);
        Assert.Equal("pictures", saved.Rules.Single(r => r.Source == "en/images/").SubFolder);
    }

    [Fact]
    public async Task SavingTheSameRulesAgainKeepsTheirTrust()
    {
        var s = await SeedAsync(AsZip);
        await SaveRulesAsync(s, EnglishRules, trusted: true);

        await SaveRulesAsync(s, EnglishRules, trusted: false);

        Assert.True((await SavedAsync(s))!.Trusted);
    }

    // ---- using saved rules ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATrustedMappingThatStillCoversEveryFileLetsAnAutomaticUpdateGoAheadWithoutAsking()
    {
        var s = await SeedAsync(AsZip);
        await SaveRulesAsync(s, EnglishRules, trusted: true);
        var h = Build(s);

        var result = await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Auto);

        Assert.True(result.Started, result.Message);
        Assert.StartsWith("archon.thirdparty.zip ", Assert.Single(h.Sent).Command);
    }

    [Fact]
    public async Task AMappingNobodyHasSeenWorkYetIsNeverUsedAutomatically()
    {
        var s = await SeedAsync(AsZip);
        await SaveRulesAsync(s, EnglishRules, trusted: false);
        var h = Build(s);

        var result = await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Auto);

        Assert.Equal("needs_instructions", result.Code);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task APersonPressingUpdateNowUsesTheSavedRulesEvenIfTheyAreNotYetTrusted()
    {
        var s = await SeedAsync(AsZip);
        await SaveRulesAsync(s, EnglishRules, trusted: false);

        var result = await Build(s).Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Manual, Sha);

        Assert.True(result.Started, result.Message);
    }

    [Fact]
    public async Task ANewFileInsideAMappedFolderDoesNotStopTheSavedRules()
    {
        var more = new List<ZipEntryInfo>(ExampleFiles) { new("en/images/test/three.jpg", 30), new("ru/images/test/three.jpg", 30) };
        var s = await SeedAsync(o => { AsZip(o); o.Files = more; });
        await SaveRulesAsync(s, EnglishRules, trusted: true);

        var result = await Build(s).Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Auto);

        Assert.True(result.Started, result.Message);
    }

    [Theory]
    [InlineData("de/plugins/test.cs")]                  // a new top-level folder
    [InlineData("readme.txt")]                          // a new top-level file
    public async Task ANewTopLevelFolderOrFileStopsTheSavedRulesForAnAutomaticUpdate(string added)
    {
        var more = new List<ZipEntryInfo>(ExampleFiles) { new(added, 30) };
        var s = await SeedAsync(o => { AsZip(o); o.Files = more; });
        await SaveRulesAsync(s, EnglishRules, trusted: true);
        var h = Build(s);

        var result = await h.Service.StartAsync(s.Server(), s.Name, PluginUpdateTriggers.Auto);

        Assert.Equal("mapping_stopped", result.Code);
        Assert.Contains(added, result.Message);
        Assert.Empty(h.Sent);
        Assert.Empty(await UpdatesAsync(s.ServerId));                // stopped to wait for a person: not an attempt, and not held against the version
    }

    // ---- what a person is shown for a zip --------------------------------------------------------------------------------------------

    [Fact]
    public async Task AZipWithNoInstructionsIsOfferedWithItsFilesSoInstructionsCanBeGiven()
    {
        var s = await SeedAsync(AsZip);

        var offer = (await OfferAsync(s))!;

        Assert.Equal((ThirdPartyUpdateOfferStates.NeedsInstructions, "zip", true), (offer.State, offer.Kind, offer.CanApply));
        Assert.Equal(ExampleFiles.Select(f => f.Path), offer.Files!.Select(f => f.Path));
        Assert.Null(offer.SavedRules);
    }

    [Fact]
    public async Task ATrustedMappingThatStillCoversTheArchiveMakesItReady()
    {
        var s = await SeedAsync(AsZip);
        await SaveRulesAsync(s, EnglishRules, trusted: true);

        var offer = (await OfferAsync(s))!;

        Assert.Equal((ThirdPartyUpdateOfferStates.Ready, true, true), (offer.State, offer.MappingTrusted, offer.CanApply));
        Assert.Equal(EnglishRules.Length, offer.SavedRules!.Count);
    }

    [Fact]
    public async Task SavedRulesThatHaveNotYetWorkedAreOfferedBackToBeReviewed()
    {
        var s = await SeedAsync(AsZip);
        await SaveRulesAsync(s, EnglishRules, trusted: false);

        var offer = (await OfferAsync(s))!;

        Assert.Equal((ThirdPartyUpdateOfferStates.NeedsInstructions, false), (offer.State, offer.MappingTrusted));
        Assert.Equal(EnglishRules.Length, offer.SavedRules!.Count);              // so the page can start from what was given
    }

    [Fact]
    public async Task WhenSavedRulesNoLongerCoverTheArchiveTheFilesTheyMissAreListed()
    {
        var more = new List<ZipEntryInfo>(ExampleFiles) { new("de/plugins/test.cs", 30), new("readme.txt", 5) };
        var s = await SeedAsync(o => { AsZip(o); o.Files = more; });
        await SaveRulesAsync(s, EnglishRules, trusted: true);

        var offer = (await OfferAsync(s))!;

        Assert.Equal(ThirdPartyUpdateOfferStates.NeedsInstructions, offer.State);
        Assert.Equal(["de/plugins/test.cs", "readme.txt"], offer.UncoveredPaths!.Order());
    }

    [Fact]
    public async Task AZipIsNotOfferedForApplyingWhenThePluginOnTheServerCannotUnpackOne()
    {
        var s = await SeedAsync(AsZip);

        var offer = await OfferAsync(s, Build(s, status: st => st.Capabilities = [RustArchonPlugin.ThirdPartyUpdateCapability]));

        Assert.NotNull(offer);
        Assert.False(offer.CanApply);
    }

    [Fact]
    public async Task AnArchiveWhosePluginIsNotInItIsShownAsNotApplicableWithTheReason()
    {
        var s = await SeedAsync(o => { o.Validation = PluginFileValidationState.Invalid; o.Kind = "zip"; o.Reason = "the archive has no plugin file for the plugin asked for"; });

        var offer = (await OfferAsync(s))!;

        Assert.Equal((ThirdPartyUpdateOfferStates.NotApplicable, "the archive has no plugin file for the plugin asked for"), (offer.State, offer.Detail));
    }

    // ---- the automatic pass -------------------------------------------------------------------------------------------------

    private ThirdPartyPluginAutoUpdater AutoUpdater(Harness h, bool enabled = true)
    {
        var settings = new Mock<IPlatformSettingsCache>();
        settings.Setup(x => x.GetBooleanAsync(PlatformSettingsRegistry.ThirdPartyPluginAutoUpdatesEnabled, true)).ReturnsAsync(enabled);
        return new ThirdPartyPluginAutoUpdater(new ApiDbContext(postgres.Options), h.Service, settings.Object, NullLogger<ThirdPartyPluginAutoUpdater>.Instance);
    }

    // The pass considers every opted-in server in the database; these tests share one, so each pass is judged by what it did to ITS server.
    private static bool SentFor(Harness h, Guid serverId) => h.Sent.Any(c => c.ServerId == serverId && c.Command.StartsWith("archon.thirdparty.update ", StringComparison.Ordinal));

    [Fact]
    public async Task AnOptedInServerWithACheckedFileWaitingIsUpdated()
    {
        var s = await SeedAsync();
        var h = Build(s);

        await AutoUpdater(h).RunPassAsync(T0);

        Assert.True(SentFor(h, s.ServerId));
        Assert.Equal(PluginUpdateTriggers.Auto, Assert.Single(await UpdatesAsync(s.ServerId)).Trigger);
    }

    [Fact]
    public async Task AServerThatHasNotOptedInIsNotTouched()
    {
        var s = await SeedAsync(o => o.OptedIn = false);
        var h = Build(s);

        await AutoUpdater(h).RunPassAsync(T0);

        Assert.False(SentFor(h, s.ServerId));
    }

    [Fact]
    public async Task ADisabledServerIsNotTouched()
    {
        var s = await SeedAsync(o => o.Enabled = false);
        var h = Build(s);

        await AutoUpdater(h).RunPassAsync(T0);

        Assert.False(SentFor(h, s.ServerId));
    }

    [Fact]
    public async Task TheSiteWideSwitchStopsEveryNewStart()
    {
        var s = await SeedAsync();
        var h = Build(s);

        await AutoUpdater(h, enabled: false).RunPassAsync(T0);

        Assert.False(SentFor(h, s.ServerId));
        Assert.Empty(await UpdatesAsync(s.ServerId));
    }

    [Fact]
    public async Task TheSiteWideSwitchStillFollowsUpdatesAlreadyUnderWayToTheirOutcome()
    {
        var s = await SeedAsync();
        await StartedAsync(s);
        var h = Build(s);
        h.Reply = _ => (true, Status("rolled-back", s.Name, reason: "did not load"));

        await AutoUpdater(h, enabled: false).RunPassAsync(T0.AddMinutes(4));

        Assert.Equal(ThirdPartyPluginUpdateStates.RolledBack, Assert.Single(await UpdatesAsync(s.ServerId)).State);
    }

    [Fact]
    public async Task AnUpdateAPersonStartedIsFollowedEvenIfTheServerHasSinceOptedOut()
    {
        var s = await SeedAsync(o => o.OptedIn = false);
        await StartedAsync(s);
        var h = Build(s);
        h.Reply = _ => (true, Status("succeeded", s.Name));

        await AutoUpdater(h).RunPassAsync(T0.AddMinutes(2));

        Assert.Equal(ThirdPartyPluginUpdateStates.Applied, Assert.Single(await UpdatesAsync(s.ServerId)).State);
        Assert.False(SentFor(h, s.ServerId));                        // and nothing new is started on it
    }

    [Fact]
    public async Task AServerInsideItsWindowBeforeTheWipeIsNotStartedButItsPendingUpdatesAreStillFollowed()
    {
        var s = await SeedAsync();
        var h = Build(s, now: HeldTime);

        await AutoUpdater(h).RunPassAsync(HeldTime);

        Assert.False(SentFor(h, s.ServerId));
    }

    [Fact]
    public async Task OnlyOneUpdateIsStartedPerServerAtATime()
    {
        var s = await SeedAsync();
        var second = UniqueName();
        // A second outdated plugin on the same server, with its own checked file.
        await using (var context = new ApiDbContext(postgres.Options, new FixedTenantContext(s.TenantId)))
        {
            await new PluginUpdateNoticeRepository(context).MergeAsync(
                s.TenantId, s.ServerId, [new PluginUpdateNoticeInfo(second, "1.0.0", "2.0.0", "https://umod.org/plugins/x", "uMod", T0, T0, 1)], T0);
        }

        await SaveLookupAsync(new PluginDownloadLookup
        {
            MarketplaceKey = "umod", NormalizedName = PluginUpdateNoticeRepository.Normalize(second), Version = "2.0.0", Outcome = PluginDownloadOutcome.Found,
            DownloadUrl = $"https://umod.org/plugins/{second}.cs", CheckedAtUtc = T0, Attempts = 1, ValidationState = PluginFileValidationState.Valid, FileKind = "cs",
            FileSha256 = OtherSha, FileSizeBytes = 99, PluginClassName = second, PluginInfoVersion = "2.0.0"
        });
        var h = Build(s);
        h.Plugins.Setup(r => r.GetForServerAcrossTenantsAsync(s.TenantId, s.ServerId)).ReturnsAsync(
        [
            new ServerPlugin { Name = s.Name, Version = "1.0.0", RustServerId = s.ServerId, TenantId = s.TenantId },
            new ServerPlugin { Name = second, Version = "1.0.0", RustServerId = s.ServerId, TenantId = s.TenantId }
        ]);

        await AutoUpdater(h).RunPassAsync(T0);

        Assert.Single(h.Sent.Where(c => c.ServerId == s.ServerId && c.Command.StartsWith("archon.thirdparty.update ", StringComparison.Ordinal)));
        Assert.Single(await UpdatesAsync(s.ServerId));
    }

    [Fact]
    public async Task AnUpdateThatAlreadyDidNotWorkOutIsNotStartedAgainByThePass()
    {
        var s = await SeedAsync();
        await AddUpdateAsync(s, ThirdPartyPluginUpdateStates.RolledBack, T0.AddHours(-1));
        var h = Build(s);

        await AutoUpdater(h).RunPassAsync(T0);

        Assert.False(SentFor(h, s.ServerId));
    }

    [Fact]
    public async Task AFailureOnOneServerDoesNotStopThePass()
    {
        var bad = await SeedAsync();
        var good = await SeedAsync();
        var h = Build(good);
        h.Statuses.Setup(r => r.GetForServerAcrossTenantsAsync(bad.TenantId, bad.ServerId)).ThrowsAsync(new InvalidOperationException("boom"));

        await AutoUpdater(h).RunPassAsync(T0);

        Assert.True(SentFor(h, good.ServerId));
    }
}
