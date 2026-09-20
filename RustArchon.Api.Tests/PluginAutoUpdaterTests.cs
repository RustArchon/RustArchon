// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
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
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// The automatic updater against a real Postgres: it only presses the buttons (the update service does every check), for servers that turned
/// it on, while the site-wide switch is on; it goes Updater first on the current key and plugin first on an older one; waits for one update's
/// outcome before the next; never tries a version again that failed on a server; and does not hold a release back because players are online.
/// </summary>
public class PluginAutoUpdaterTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private const string ActiveKey = "0123456789abcdef";

    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Task<Guid?> GetCurrentTenantIdAsync() => Task.FromResult<Guid?>(tenantId);
    }

    private ApiDbContext Platform() => new(postgres.Options);

    private sealed class Kit
    {
        public Guid TenantId;
        public ApiDbContext Context = null!;
        public Mock<IPluginScriptService> Script = new();
        public Mock<IPluginUpdateService> Updates = new();
        public Mock<IPlatformSettingsCache> Settings = new();
        public PluginUpdateAttemptRepository Attempts = null!;
        public List<(string Kind, Guid Server)> Started = [];

        public PluginAutoUpdater Create(ApiDbContext? context = null) => new(
            context ?? Context, new ServerPluginStatusRepository(context ?? Context), new ServerPluginRepository(context ?? Context), Script.Object, Attempts, Updates.Object,
            Settings.Object, new PluginRolloutService(context ?? Context, Settings.Object), NullLogger<PluginAutoUpdater>.Instance);
    }

    private async Task<Kit> KitAsync(string latestMain = "0.9.0", string latestUpdater = "0.3.0", bool globalOn = true, bool purge = true)
    {
        // The auto updater looks at every organization's servers, so each test starts from an empty set of them.
        if (purge)
        {
            await using var clean = Platform();
            await clean.PluginUpdateAttempts.AcrossAllTenants().ExecuteDeleteAsync();
            await clean.PluginRollouts.ExecuteDeleteAsync();
            await clean.Set<ServerPlugin>().AcrossAllTenants().ExecuteDeleteAsync();
            await clean.Set<ServerPluginStatus>().AcrossAllTenants().ExecuteDeleteAsync();
            await clean.Set<PlayerSession>().AcrossAllTenants().ExecuteDeleteAsync();
            await clean.RustServers.AcrossAllTenants().ExecuteDeleteAsync();
        }

        var tenantId = Guid.NewGuid();
        var context = new ApiDbContext(postgres.Options, new FixedTenantContext(tenantId));
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Auto tenant {tenantId}", IsActive = true });
        await context.SaveChangesAsync();

        var kit = new Kit { TenantId = tenantId, Context = context, Attempts = new PluginUpdateAttemptRepository(context) };
        kit.Script.Setup(s => s.GetLatestVersionAsync()).ReturnsAsync(latestMain);
        kit.Script.Setup(s => s.GetLatestUpdaterVersionAsync()).ReturnsAsync(latestUpdater);
        kit.Script.Setup(s => s.GetKeyStateAsync(ActiveKey)).ReturnsAsync(PluginKeyState.Active);
        kit.Script.Setup(s => s.GetKeyStateAsync("aaaaaaaaaaaaaaaa")).ReturnsAsync(PluginKeyState.Retired);
        kit.Settings.Setup(s => s.GetBooleanAsync(PlatformSettingsRegistry.PluginAutoUpdatesEnabled, true)).ReturnsAsync(globalOn);
        kit.Updates.Setup(u => u.StartAsync(It.IsAny<RustServer>(), PluginUpdateTriggers.Auto))
            .ReturnsAsync((RustServer s, string _) => { kit.Started.Add((PluginUpdateKinds.Main, s.Id)); return new PluginUpdateResultDto { Started = true, Code = "started" }; });
        kit.Updates.Setup(u => u.StartUpdaterAsync(It.IsAny<RustServer>(), PluginUpdateTriggers.Auto))
            .ReturnsAsync((RustServer s, string _) => { kit.Started.Add((PluginUpdateKinds.Updater, s.Id)); return new PluginUpdateResultDto { Started = true, Code = "started" }; });
        return kit;
    }

    private async Task<Guid> ServerAsync(
        Kit kit, bool enabled = true, bool updates = true, bool auto = true, string pluginVersion = "0.8.0", string? updaterVersion = "v0.2.0",
        bool capable = true, string key = ActiveKey, string signing = "valid", double statusAgeMinutes = 1)
    {
        var id = Guid.NewGuid();
        kit.Context.Set<RustServer>().Add(new RustServer
        {
            Id = id, TenantId = kit.TenantId, Name = "Auto " + id.ToString("N")[..6], Host = "192.0.2.70", Port = 28016, RconPassword = "x",
            IsEnabled = enabled, PluginUpdatesEnabled = updates, PluginAutoUpdateEnabled = auto
        });
        kit.Context.Set<ServerPluginStatus>().Add(new ServerPluginStatus
        {
            TenantId = kit.TenantId, RustServerId = id, PluginVersion = pluginVersion, SigningState = signing, SigningKeyFingerprint = key,
            Capabilities = capable ? [RustArchonPlugin.UpdaterUpdateCapability] : ["config"], CapturedAtUtc = Now.AddMinutes(-statusAgeMinutes)
        });
        if (updaterVersion is not null)
        {
            kit.Context.Set<ServerPlugin>().Add(new ServerPlugin { TenantId = kit.TenantId, RustServerId = id, Name = RustArchonPlugin.UpdaterName, Version = updaterVersion, Author = "RustArchon", CapturedAtUtc = Now });
        }

        await kit.Context.SaveChangesAsync();
        return id;
    }

    private static Task<int> Run(Kit kit) => kit.Create().RunPassAsync(Now);

    // ---- who is eligible -----------------------------------------------------------------------------------

    [Fact]
    public async Task AServerWithBothSwitchesOnAndSomethingNewerIsUpdated()
    {
        var kit = await KitAsync();
        var id = await ServerAsync(kit);

        var started = await Run(kit);

        Assert.Equal(1, started);
        Assert.Contains(kit.Started, s => s.Server == id);
    }

    [Theory]
    [InlineData(false, true, true)]      // server disabled
    [InlineData(true, false, true)]      // updates not allowed
    [InlineData(true, true, false)]      // automatic updating not chosen
    public async Task AServerThatIsNotFullyOptedInIsLeftAlone(bool enabled, bool updates, bool auto)
    {
        var kit = await KitAsync();
        await ServerAsync(kit, enabled: enabled, updates: updates, auto: auto);

        Assert.Equal(0, await Run(kit));
        Assert.Empty(kit.Started);
    }

    [Fact]
    public async Task TheSiteWideSwitchOffStopsEverything()
    {
        var kit = await KitAsync(globalOn: false);
        await ServerAsync(kit);

        Assert.Equal(0, await Run(kit));
        Assert.Empty(kit.Started);
    }

    [Fact]
    public async Task ANeverHeardFromOrStaleServerIsNotTouched()
    {
        var kit = await KitAsync();
        await ServerAsync(kit, statusAgeMinutes: PluginAutoUpdater.StatusFreshFor.TotalMinutes + 1);

        Assert.Equal(0, await Run(kit));
    }

    [Fact]
    public async Task AServerWithNoHandshakeAtAllIsNotTouched()
    {
        var kit = await KitAsync();
        kit.Context.Set<RustServer>().Add(new RustServer { Id = Guid.NewGuid(), TenantId = kit.TenantId, Name = "No handshake", Host = "192.0.2.71", Port = 28016, RconPassword = "x", PluginUpdatesEnabled = true, PluginAutoUpdateEnabled = true });
        await kit.Context.SaveChangesAsync();

        Assert.Equal(0, await Run(kit));
    }

    [Fact]
    public async Task PlayersBeingOnlineDoesNotHoldAnUpdateBack()
    {
        var kit = await KitAsync();
        var id = await ServerAsync(kit);
        kit.Context.Set<PlayerSession>().Add(new PlayerSession { TenantId = kit.TenantId, RustServerId = id, SteamId = "76561198000000001", DisplayName = "on now", IpAddress = "203.0.113.5", ConnectedAtUtc = Now.AddMinutes(-3) });
        await kit.Context.SaveChangesAsync();

        Assert.Equal(1, await Run(kit));
    }

    // ---- which first ---------------------------------------------------------------------------------------

    [Fact]
    public async Task OnTheCurrentKeyTheUpdaterIsUpdatedBeforeThePlugin()
    {
        var kit = await KitAsync();
        var id = await ServerAsync(kit);            // both are behind

        await Run(kit);

        Assert.Equal([(PluginUpdateKinds.Updater, id)], kit.Started);
    }

    [Fact]
    public async Task OnAnOlderKeyThePluginGoesFirstBecauseThatIsTheBridge()
    {
        var kit = await KitAsync();
        var id = await ServerAsync(kit, key: "aaaaaaaaaaaaaaaa");

        await Run(kit);

        Assert.Equal([(PluginUpdateKinds.Main, id)], kit.Started);
    }

    [Fact]
    public async Task AServerWhosePluginCannotUpdateTheUpdaterGetsThePluginUpdateFirst()
    {
        var kit = await KitAsync();
        var id = await ServerAsync(kit, capable: false);

        await Run(kit);

        Assert.Equal([(PluginUpdateKinds.Main, id)], kit.Started);
    }

    [Fact]
    public async Task WithNoUpdaterInstalledAndACapablePluginTheUpdaterIsInstalledFirst()
    {
        var kit = await KitAsync();
        var id = await ServerAsync(kit, updaterVersion: null);

        await Run(kit);

        Assert.Equal([(PluginUpdateKinds.Updater, id)], kit.Started);
    }

    [Fact]
    public async Task OnlyThePluginIsUpdatedWhenTheUpdaterIsAlreadyCurrent()
    {
        var kit = await KitAsync();
        var id = await ServerAsync(kit, updaterVersion: "v0.3.0");

        await Run(kit);

        Assert.Equal([(PluginUpdateKinds.Main, id)], kit.Started);
    }

    [Fact]
    public async Task NothingIsStartedWhenEverythingIsCurrent()
    {
        var kit = await KitAsync();
        await ServerAsync(kit, pluginVersion: "0.9.0", updaterVersion: "v0.3.0");

        Assert.Equal(0, await Run(kit));
    }

    [Fact]
    public async Task ANewerInstalledVersionIsNeverDowngraded()
    {
        var kit = await KitAsync();
        await ServerAsync(kit, pluginVersion: "1.5.0", updaterVersion: "v0.9.0");

        Assert.Equal(0, await Run(kit));
    }

    // ---- one at a time, and the outcome ----------------------------------------------------------------------

    [Fact]
    public async Task AnUpdateStillWaitingForItsOutcomeHoldsTheNextOne()
    {
        var kit = await KitAsync();
        var id = await ServerAsync(kit);
        await kit.Attempts.RecordStartedAsync(kit.TenantId, id, PluginUpdateKinds.Updater, "0.2.0", "0.3.0", PluginUpdateTriggers.Auto, Now.AddMinutes(-2));

        Assert.Equal(0, await Run(kit));
        Assert.Empty(kit.Started);
        Assert.Equal(PluginUpdateAttemptStates.Started, (await kit.Attempts.GetPendingAsync(id)).Single().State);
    }

    [Fact]
    public async Task WhenTheNewVersionShowsUpTheAttemptSucceedsAndTheNextUpdateFollowsInTheSamePass()
    {
        var kit = await KitAsync();
        var id = await ServerAsync(kit, updaterVersion: "v0.3.0");                     // the Updater update has taken
        await kit.Attempts.RecordStartedAsync(kit.TenantId, id, PluginUpdateKinds.Updater, "0.2.0", "0.3.0", PluginUpdateTriggers.Auto, Now.AddMinutes(-2));

        await Run(kit);

        Assert.Empty(await kit.Attempts.GetPendingAsync(id));
        Assert.Equal(PluginUpdateAttemptStates.Succeeded, (await AttemptsAsync(id)).Single(a => a.Kind == PluginUpdateKinds.Updater).State);
        Assert.Equal([(PluginUpdateKinds.Main, id)], kit.Started);
    }

    [Fact]
    public async Task AnUpdateThatNeverTookEffectIsMarkedFailedAndIsNotTriedAgainForThatVersion()
    {
        var kit = await KitAsync(latestMain: "0.9.0");
        var id = await ServerAsync(kit, updaterVersion: "v0.3.0");                     // only the plugin is behind
        await kit.Attempts.RecordStartedAsync(kit.TenantId, id, PluginUpdateKinds.Main, "0.8.0", "0.9.0", PluginUpdateTriggers.Auto, Now - PluginAutoUpdater.OutcomeWithin - TimeSpan.FromMinutes(1));

        var started = await Run(kit);

        var attempt = Assert.Single(await AttemptsAsync(id));
        Assert.Equal((PluginUpdateAttemptStates.Failed, "not_installed"), (attempt.State, attempt.Code));
        Assert.Equal(0, started);                                                       // it will not be sent again
        Assert.Equal(0, await Run(kit));                                                // nor on the pass after that
    }

    [Fact]
    public async Task ARefusedVersionIsNotTriedAgainUntilADifferentOneIsServed()
    {
        var kit = await KitAsync(latestMain: "0.9.0");
        var id = await ServerAsync(kit, updaterVersion: "v0.3.0");
        await kit.Attempts.RecordRefusedAsync(kit.TenantId, id, PluginUpdateKinds.Main, "0.8.0", "0.9.0", PluginUpdateTriggers.Manual, "signature_invalid", Now.AddHours(-1));

        Assert.Equal(0, await Run(kit));

        kit.Script.Setup(s => s.GetLatestVersionAsync()).ReturnsAsync("0.9.1");         // a fixed release is published
        Assert.Equal(1, await Run(kit));
        Assert.Equal([(PluginUpdateKinds.Main, id)], kit.Started);
    }

    [Fact]
    public async Task AFailedUpdaterUpdateDoesNotStopThePluginFromBeingUpdated()
    {
        var kit = await KitAsync();
        var id = await ServerAsync(kit);
        await kit.Attempts.RecordRefusedAsync(kit.TenantId, id, PluginUpdateKinds.Updater, "0.2.0", "0.3.0", PluginUpdateTriggers.Auto, "signature_invalid", Now.AddHours(-1));

        await Run(kit);

        Assert.Equal([(PluginUpdateKinds.Main, id)], kit.Started);
    }

    [Fact]
    public async Task AServerThatCouldNotBeReachedIsSimplyTriedAgainNextPass()
    {
        var kit = await KitAsync();
        var id = await ServerAsync(kit, updaterVersion: "v0.3.0");
        kit.Updates.Setup(u => u.StartAsync(It.IsAny<RustServer>(), PluginUpdateTriggers.Auto))
            .ReturnsAsync(new PluginUpdateResultDto { Started = false, Code = "not_connected" });

        Assert.Equal(0, await Run(kit));
        Assert.Equal(0, await Run(kit));

        kit.Updates.Setup(u => u.StartAsync(It.IsAny<RustServer>(), PluginUpdateTriggers.Auto))
            .ReturnsAsync((RustServer s, string _) => { kit.Started.Add((PluginUpdateKinds.Main, s.Id)); return new PluginUpdateResultDto { Started = true }; });
        Assert.Equal(1, await Run(kit));
        Assert.Contains(kit.Started, s => s.Server == id);
    }

    [Fact]
    public async Task APluginWhoseSignatureIsNotValidIsNotGivenTheUpdaterFirst()
    {
        var kit = await KitAsync();
        var id = await ServerAsync(kit, signing: "invalid");

        await Run(kit);

        Assert.Equal([(PluginUpdateKinds.Main, id)], kit.Started);        // the service will refuse it; the updater does not try the Updater path
    }

    // ---- staggering and failure isolation --------------------------------------------------------------------

    [Fact]
    public async Task NoMoreThanTheLimitAreStartedInOnePassAndTheRestFollow()
    {
        var kit = await KitAsync();
        for (var i = 0; i < PluginAutoUpdater.MaxStartsPerPass + 3; i++)
        {
            await ServerAsync(kit);
        }

        var first = await Run(kit);
        Assert.Equal(PluginAutoUpdater.MaxStartsPerPass, first);
    }

    [Fact]
    public async Task OneServersFailureDoesNotStopTheOthers()
    {
        var kit = await KitAsync();
        var bad = await ServerAsync(kit);
        var good = await ServerAsync(kit);
        kit.Updates.Setup(u => u.StartUpdaterAsync(It.Is<RustServer>(s => s.Id == bad), PluginUpdateTriggers.Auto)).ThrowsAsync(new InvalidOperationException("boom"));

        var started = await Run(kit);

        Assert.Equal(1, started);
        Assert.Contains(kit.Started, s => s.Server == good);
    }

    [Fact]
    public async Task AnotherOrganizationsServerIsNotConfusedWithThisOnes()
    {
        var kitA = await KitAsync();
        var kitB = await KitAsync(purge: false);
        var a = await ServerAsync(kitA);
        await ServerAsync(kitB, updaterVersion: "v0.3.0", pluginVersion: "0.9.0");         // B is fully up to date

        var started = await kitA.Create(Platform()).RunPassAsync(Now);

        Assert.Contains(kitA.Started, s => s.Server == a);
        Assert.True(started >= 1);
        Assert.DoesNotContain(kitB.Started, _ => true);
    }

    private async Task<List<PluginUpdateAttempt>> AttemptsAsync(Guid serverId)
    {
        await using var context = Platform();
        return await context.PluginUpdateAttempts.AcrossAllTenants().AsNoTracking().Where(a => a.RustServerId == serverId).OrderBy(a => a.StartedAtUtc).ToListAsync();
    }

    // ---- the attempts repository and endpoint ----------------------------------------------------------------

    [Fact]
    public async Task AnAttemptIsRecordedWithEverythingAboutIt()
    {
        var kit = await KitAsync();
        var id = Guid.NewGuid();

        await kit.Attempts.RecordStartedAsync(kit.TenantId, id, PluginUpdateKinds.Updater, "0.2.0", "0.3.0", PluginUpdateTriggers.Auto, Now);

        var row = Assert.Single(await AttemptsAsync(id));
        Assert.Equal((PluginUpdateKinds.Updater, "0.2.0", "0.3.0", PluginUpdateTriggers.Auto, PluginUpdateAttemptStates.Started), (row.Kind, row.FromVersion, row.ToVersion, row.Trigger, row.State));
        Assert.Equal((Now, (DateTimeOffset?)null), (row.StartedAtUtc, row.ResolvedAtUtc));
    }

    [Fact]
    public async Task ResolvingClosesAStartedAttemptOnceAndNeverReopensOrOverwritesAClosedOne()
    {
        var kit = await KitAsync();
        var id = Guid.NewGuid();
        var row = await kit.Attempts.RecordStartedAsync(kit.TenantId, id, PluginUpdateKinds.Main, "0.8.0", "0.9.0", PluginUpdateTriggers.Manual, Now);

        await kit.Attempts.ResolveAsync(row.Id, PluginUpdateAttemptStates.Succeeded, "", Now.AddMinutes(1));
        await kit.Attempts.ResolveAsync(row.Id, PluginUpdateAttemptStates.Failed, "late", Now.AddMinutes(2));

        var reread = Assert.Single(await AttemptsAsync(id));
        Assert.Equal((PluginUpdateAttemptStates.Succeeded, "", (DateTimeOffset?)Now.AddMinutes(1)), (reread.State, reread.Code, reread.ResolvedAtUtc));
    }

    [Fact]
    public async Task ASucceededAttemptDoesNotBlockAndAnythingElseDoes()
    {
        var kit = await KitAsync();
        var id = Guid.NewGuid();
        var ok = await kit.Attempts.RecordStartedAsync(kit.TenantId, id, PluginUpdateKinds.Main, "0.7.0", "0.8.0", PluginUpdateTriggers.Manual, Now);
        await kit.Attempts.ResolveAsync(ok.Id, PluginUpdateAttemptStates.Succeeded, "", Now);
        await kit.Attempts.RecordStartedAsync(kit.TenantId, id, PluginUpdateKinds.Main, "0.8.0", "0.9.0", PluginUpdateTriggers.Manual, Now);
        await kit.Attempts.RecordRefusedAsync(kit.TenantId, id, PluginUpdateKinds.Updater, "0.2.0", "0.3.0", PluginUpdateTriggers.Manual, "x", Now);

        Assert.False(await kit.Attempts.HasUnsuccessfulAsync(id, PluginUpdateKinds.Main, "0.8.0"));
        Assert.True(await kit.Attempts.HasUnsuccessfulAsync(id, PluginUpdateKinds.Main, "0.9.0"));         // still pending
        Assert.True(await kit.Attempts.HasUnsuccessfulAsync(id, PluginUpdateKinds.Updater, "0.3.0"));      // refused
        Assert.False(await kit.Attempts.HasUnsuccessfulAsync(id, PluginUpdateKinds.Updater, "0.3.1"));     // a different version
        Assert.False(await kit.Attempts.HasUnsuccessfulAsync(Guid.NewGuid(), PluginUpdateKinds.Main, "0.9.0"));
    }

    [Fact]
    public async Task TheEndpointListsTheNewestAttemptsFirstForTheCallersOwnServerOnly()
    {
        var mine = await KitAsync();
        var theirs = await KitAsync(purge: false);
        var id = await ServerAsync(mine);
        await mine.Attempts.RecordStartedAsync(mine.TenantId, id, PluginUpdateKinds.Updater, "0.2.0", "0.3.0", PluginUpdateTriggers.Auto, Now.AddMinutes(-30));
        await mine.Attempts.RecordRefusedAsync(mine.TenantId, id, PluginUpdateKinds.Main, "0.8.0", "0.9.0", PluginUpdateTriggers.Manual, "signature_invalid", Now.AddMinutes(-5));

        var list = Assert.IsType<List<PluginUpdateAttemptDto>>(Assert.IsType<OkObjectResult>(
            (await new ServerPluginUpdateAttemptsController(new RustServerRepository(mine.Context), mine.Attempts).Get(id)).Result).Value);
        var others = (await new ServerPluginUpdateAttemptsController(new RustServerRepository(theirs.Context), theirs.Attempts).Get(id)).Result;

        Assert.Equal(["main", "updater"], list.Select(a => a.Kind).ToArray());
        Assert.Equal(("refused", "signature_invalid", "manual"), (list[0].State, list[0].Code, list[0].Trigger));
        Assert.IsType<NotFoundResult>(others);
    }

    [Fact]
    public async Task TheEndpointNeedsTheSamePermissionAsReadingTheServer()
    {
        var permission = Assert.Single(typeof(ServerPluginUpdateAttemptsController)
            .GetCustomAttributes(typeof(JumpStart.Authorization.RequirePermissionAttribute), inherit: true)
            .Cast<JumpStart.Authorization.RequirePermissionAttribute>());

        Assert.Equal(PermissionCatalog.ServerGet, permission.Permission);
        await Task.CompletedTask;
    }
}
