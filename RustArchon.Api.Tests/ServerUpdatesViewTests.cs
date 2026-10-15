// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using RustArchon.Api.Data;
using RustArchon.Api.DiagnosticViews.Views;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;
using Microsoft.EntityFrameworkCore;
using RustArchon.Messaging.Contracts;
using static RustArchon.Api.Tests.DiagnosticViewKit;

namespace RustArchon.Api.Tests;

/// <summary>
/// The <c>server-updates</c> diagnostic view: for each outdated plugin on a server, the offer and the reason it is or is not being applied, the update attempts
/// so far, and whether and when the automatic updater will act on the server - the same answers the Plugins tab shows, taken from the same code.
/// </summary>
public class ServerUpdatesViewTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private readonly Mock<IThirdPartyPluginUpdateService> _updates = new();
    private readonly Mock<IPlatformSettingsCache> _settings = new();
    private readonly ThirdPartyAutoUpdateStatus _pass = new();

    private ServerUpdatesView Create()
    {
        _settings.Setup(s => s.GetBooleanAsync(PlatformSettingsRegistry.ThirdPartyPluginAutoUpdatesEnabled, true)).ReturnsAsync(true);
        _updates.Setup(u => u.GetOffersAsync(It.IsAny<RustServer>(), It.IsAny<DateTimeOffset>())).ReturnsAsync(new Dictionary<string, ThirdPartyUpdateOffer>());
        return new ServerUpdatesView(new ApiDbContext(postgres.Options), _updates.Object, _settings.Object, _pass, new FixedClock());
    }

    private async Task AddNoticeAsync(Seeded seeded, string name, string installed = "1.0.0", string latest = "2.0.0")
    {
        await using var context = new ApiDbContext(postgres.Options);
        await new PluginUpdateNoticeRepository(context).MergeAsync(
            seeded.TenantId, seeded.ServerId, [new PluginUpdateNoticeInfo(name, installed, latest, "https://umod.org/plugins/x", "uMod", Now, Now, 1)], Now);
    }

    private async Task AddThirdPartyAttemptAsync(Seeded seeded, string name, string state, string code, DateTimeOffset at)
    {
        await using var context = new ApiDbContext(postgres.Options);
        context.ThirdPartyPluginUpdates.Add(new ThirdPartyPluginUpdate
        {
            TenantId = seeded.TenantId, RustServerId = seeded.ServerId, PluginName = name, NormalizedName = name.ToLowerInvariant(), ClassName = name, FromVersion = "1.0.0",
            ToVersion = "2.0.0", PluginDownloadLookupId = Guid.NewGuid(), FileSha256 = new string('a', 64), Trigger = "manual", State = state, Code = code,
            Message = "a message from the plugin", StartedAtUtc = at, ResolvedAtUtc = at
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task EachOutdatedPluginIsListedWithItsOfferItsStateAndWhyItIsBlocked()
    {
        var seeded = await SeedServerAsync(postgres, optedIn: true);
        await AddNoticeAsync(seeded, "Kits", "1.0.0", "2.0.0");
        await AddNoticeAsync(seeded, "AdminRadar", "5.4.3", "5.4.4");
        var view = Create();
        _updates.Setup(u => u.GetOffersAsync(It.IsAny<RustServer>(), It.IsAny<DateTimeOffset>())).ReturnsAsync(new Dictionary<string, ThirdPartyUpdateOffer>
        {
            ["kits"] = new(ThirdPartyUpdateOfferStates.Ready, "Not signed by this Panel.", "sha-kits", false, null, BlockCode: "not_signed_by_this_panel"),
            ["adminradar"] = new(ThirdPartyUpdateOfferStates.Ready, string.Empty, "sha-radar", true, null)
        });

        var (status, json) = await RunAsync(view, ServerUpdatesView.Definition, ("serverId", seeded.ServerId.ToString()));

        Assert.Equal(200, status);
        var offers = json.GetProperty("data").GetProperty("offers").EnumerateArray().ToDictionary(o => o.GetProperty("pluginName").GetString()!);
        Assert.Equal(["AdminRadar", "Kits"], offers.Keys.Order());
        var kits = offers["Kits"];
        Assert.Equal("ready", kits.GetProperty("offerState").GetString());
        Assert.Equal("not_signed_by_this_panel", kits.GetProperty("blockCode").GetString());
        Assert.Equal("Not signed by this Panel.", kits.GetProperty("detail").GetString());
        Assert.False(kits.GetProperty("canApply").GetBoolean());
        Assert.Equal("1.0.0", kits.GetProperty("installedVersion").GetString());
        Assert.Equal("2.0.0", kits.GetProperty("latestVersion").GetString());
        Assert.True(offers["AdminRadar"].GetProperty("canApply").GetBoolean());
        Assert.Equal(JsonValueKindNull, offers["AdminRadar"].GetProperty("blockCode").ValueKind);
    }

    private const System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;

    private async Task AddInstalledAsync(Seeded seeded, string name, string version)
    {
        await using var context = new ApiDbContext(postgres.Options);
        context.ServerPlugins.Add(new ServerPlugin
        {
            TenantId = seeded.TenantId, RustServerId = seeded.ServerId, Name = name, Author = "x", Version = version, Framework = ServerModFramework.Carbon, CapturedAtUtc = Now
        });
        await context.SaveChangesAsync();
    }

    private async Task AddLookupAsync(string name, string version, PluginDownloadOutcome outcome, PluginFileValidationState validation, string? reason = null)
    {
        await using var context = new ApiDbContext(postgres.Options);
        var row = new PluginDownloadLookup
        {
            MarketplaceKey = "umod", NormalizedName = PluginUpdateNoticeRepository.Normalize(name), Version = PluginDownloadMatcher.VersionKey(version), Outcome = outcome,
            DownloadUrl = outcome == PluginDownloadOutcome.Found ? $"https://umod.org/plugins/{name}.cs" : null, CheckedAtUtc = Now, Attempts = 1
        };
        await new PluginDownloadLookupRepository(context).SaveAsync(row);
        var stored = await context.PluginDownloadLookups.FirstAsync(l => l.NormalizedName == row.NormalizedName && l.Version == row.Version);
        stored.ValidationState = validation;
        stored.ValidationReason = reason;
        await new PluginDownloadLookupRepository(context).SaveValidationAsync(stored);
    }

    private async Task<System.Text.Json.JsonElement> OnlyOfferAsync(Seeded seeded)
    {
        var (_, json) = await RunAsync(Create(), ServerUpdatesView.Definition, ("serverId", seeded.ServerId.ToString()));
        return Assert.Single(json.GetProperty("data").GetProperty("offers").EnumerateArray());
    }

    [Fact]
    public async Task APluginNotInTheServersPluginListSaysThereIsNothingToUpdate()
    {
        var seeded = await SeedServerAsync(postgres);
        await AddNoticeAsync(seeded, "NpcSpawn");

        var offer = await OnlyOfferAsync(seeded);

        Assert.Equal("none", offer.GetProperty("offerState").GetString());
        Assert.False(offer.GetProperty("installedOnServer").GetBoolean());
        Assert.Contains("not in this server's plugin list", offer.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task AnInstalledPluginThatIsAlreadyUpToDateSaysSo()
    {
        var seeded = await SeedServerAsync(postgres);
        var name = "Tp" + Guid.NewGuid().ToString("N")[..8];
        await AddNoticeAsync(seeded, name, "2.0.0", "2.0.0");
        await AddInstalledAsync(seeded, name, "2.0.0");

        var offer = await OnlyOfferAsync(seeded);

        Assert.True(offer.GetProperty("installedOnServer").GetBoolean());
        Assert.Contains("not older", offer.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task AnOutdatedPluginWithNoDownloadLookedUpYetSaysSo()
    {
        var seeded = await SeedServerAsync(postgres);
        var name = "Tp" + Guid.NewGuid().ToString("N")[..8];
        await AddNoticeAsync(seeded, name, "1.0.0", "2.0.0");
        await AddInstalledAsync(seeded, name, "1.0.0");

        var offer = await OnlyOfferAsync(seeded);

        Assert.Equal("not looked up", offer.GetProperty("downloadLookup").GetString());
        Assert.Contains("looked up", offer.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ADownloadThatWasNotFoundNamesTheLookupResult()
    {
        var seeded = await SeedServerAsync(postgres);
        var name = "Tp" + Guid.NewGuid().ToString("N")[..8];
        await AddNoticeAsync(seeded, name, "1.0.0", "2.0.0");
        await AddInstalledAsync(seeded, name, "1.0.0");
        await AddLookupAsync(name, "2.0.0", PluginDownloadOutcome.NotFound, PluginFileValidationState.NotChecked);

        var offer = await OnlyOfferAsync(seeded);

        Assert.Equal("NotFound", offer.GetProperty("downloadLookup").GetString());
        Assert.Contains("No download address was found", offer.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task AFoundDownloadWhoseFileCouldNotBeCheckedNamesTheValidationStateAndReason()
    {
        var seeded = await SeedServerAsync(postgres);
        var name = "Tp" + Guid.NewGuid().ToString("N")[..8];
        await AddNoticeAsync(seeded, name, "1.0.0", "2.0.0");
        await AddInstalledAsync(seeded, name, "1.0.0");
        await AddLookupAsync(name, "2.0.0", PluginDownloadOutcome.Found, PluginFileValidationState.Invalid, "it is an HTML page");

        var offer = await OnlyOfferAsync(seeded);

        Assert.Equal("Found", offer.GetProperty("downloadLookup").GetString());
        Assert.Equal("Invalid", offer.GetProperty("fileValidation").GetString());
        Assert.Contains("Invalid", offer.GetProperty("detail").GetString());
        Assert.Contains("it is an HTML page", offer.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task TheAutomaticUpdaterIsEligibleWhenNothingStopsIt()
    {
        var seeded = await SeedServerAsync(postgres, optedIn: true);
        var view = Create();
        _updates.Setup(u => u.GetServerRefusalAsync(It.IsAny<RustServer>(), It.IsAny<DateTimeOffset>(), true)).ReturnsAsync((PluginUpdateRefusal?)null);
        _pass.PassStarted(Now.AddMinutes(-3));
        _pass.NextDue(Now.AddMinutes(2));

        var (_, json) = await RunAsync(view, ServerUpdatesView.Definition, ("serverId", seeded.ServerId.ToString()));

        var automatic = json.GetProperty("data").GetProperty("automaticUpdater");
        Assert.Equal("eligible", automatic.GetProperty("state").GetString());
        Assert.True(automatic.GetProperty("platformSwitchOn").GetBoolean());
        Assert.Equal(Now.AddMinutes(-3), automatic.GetProperty("lastPassUtc").GetDateTimeOffset());
        Assert.Equal(Now.AddMinutes(2), automatic.GetProperty("nextPassUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task TheFirstThingStoppingTheAutomaticUpdaterIsNamedWithItsReason()
    {
        var seeded = await SeedServerAsync(postgres);
        var view = Create();
        _updates.Setup(u => u.GetServerRefusalAsync(It.IsAny<RustServer>(), It.IsAny<DateTimeOffset>(), true))
            .ReturnsAsync(new PluginUpdateRefusal("not_signed_by_this_panel", "The plugin was not signed by this Panel."));

        var (_, json) = await RunAsync(view, ServerUpdatesView.Definition, ("serverId", seeded.ServerId.ToString()));

        var automatic = json.GetProperty("data").GetProperty("automaticUpdater");
        Assert.Equal("not_signed_by_this_panel", automatic.GetProperty("state").GetString());
        Assert.Equal("The plugin was not signed by this Panel.", automatic.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ThePlatformSwitchBeingOffComesFirst()
    {
        var seeded = await SeedServerAsync(postgres);
        var view = Create();
        _settings.Setup(s => s.GetBooleanAsync(PlatformSettingsRegistry.ThirdPartyPluginAutoUpdatesEnabled, true)).ReturnsAsync(false);

        var (_, json) = await RunAsync(view, ServerUpdatesView.Definition, ("serverId", seeded.ServerId.ToString()));

        var automatic = json.GetProperty("data").GetProperty("automaticUpdater");
        Assert.False(automatic.GetProperty("platformSwitchOn").GetBoolean());
        Assert.Equal("platform_switch_off", automatic.GetProperty("state").GetString());
    }

    [Fact]
    public async Task TheMostRecentAttemptsOfBothKindsAreListedNewestFirstAndBounded()
    {
        var seeded = await SeedServerAsync(postgres);
        for (var i = 0; i < 25; i++)
        {
            await AddThirdPartyAttemptAsync(seeded, "Kits", "failed", "download_failed", Now.AddMinutes(-i));
        }

        await using (var context = new ApiDbContext(postgres.Options))
        {
            context.PluginUpdateAttempts.Add(new PluginUpdateAttempt
            {
                TenantId = seeded.TenantId, RustServerId = seeded.ServerId, Kind = "main", FromVersion = "0.10.0", ToVersion = "0.11.0", Trigger = "manual", State = "refused",
                Code = "not_signed_by_this_panel", StartedAtUtc = Now
            });
            await context.SaveChangesAsync();
        }

        var (_, json) = await RunAsync(Create(), ServerUpdatesView.Definition, ("serverId", seeded.ServerId.ToString()));

        var third = json.GetProperty("data").GetProperty("thirdPartyAttempts").EnumerateArray().ToList();
        Assert.Equal(ServerUpdatesView.MaxAttempts, third.Count);
        Assert.Equal(Now, third[0].GetProperty("startedAtUtc").GetDateTimeOffset());
        Assert.Equal("download_failed", third[0].GetProperty("code").GetString());
        var main = Assert.Single(json.GetProperty("data").GetProperty("pluginAttempts").EnumerateArray());
        Assert.Equal("not_signed_by_this_panel", main.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AnExcludedPluginIsListedWithItsNote()
    {
        var seeded = await SeedServerAsync(postgres);
        await using (var context = new ApiDbContext(postgres.Options))
        {
            context.PluginUpdateExclusions.Add(new PluginUpdateExclusion
            {
                TenantId = seeded.TenantId, RustServerId = seeded.ServerId, NormalizedName = "kits", Note = "Purchased elsewhere", ExcludedAtUtc = Now
            });
            await context.SaveChangesAsync();
        }

        var (_, json) = await RunAsync(Create(), ServerUpdatesView.Definition, ("serverId", seeded.ServerId.ToString()));

        var exclusion = Assert.Single(json.GetProperty("data").GetProperty("exclusions").EnumerateArray());
        Assert.Equal("Purchased elsewhere", exclusion.GetProperty("note").GetString());
    }

    [Fact]
    public async Task AnUnknownServerIsNotFound()
    {
        var (status, _) = await RunAsync(Create(), ServerUpdatesView.Definition, ("serverId", Guid.NewGuid().ToString()));

        Assert.Equal(404, status);
    }
}
