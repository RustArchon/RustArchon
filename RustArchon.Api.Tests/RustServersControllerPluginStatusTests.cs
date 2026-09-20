// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Correlate;
using JumpStart.Data;
using JumpStart.Repositories;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Mapping;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for the RustArchon-plugin parts of <see cref="RustServersController"/>: <c>GET plugin-status</c> and
/// <c>PUT plugin-settings</c>. Real Postgres, same scaffolding as <see cref="RustServersControllerDuplicateNameTests"/>.
/// </summary>
public class RustServersControllerPluginStatusTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Task<Guid?> GetCurrentTenantIdAsync() => Task.FromResult<Guid?>(tenantId);
    }

    private sealed record Harness(
        RustServersController Controller,
        ServerPluginStatusRepository Statuses,
        Mock<IPublishEndpoint> Publish,
        Mock<IPluginScriptService> Script,
        Mock<IPluginUpdateService> Update,
        Mock<IServerPluginRepository> Plugins,
        Guid TenantId);

    private static IMapper CreateMapper() =>
        new MapperConfiguration(cfg =>
        {
            cfg.AddProfile<RustServerMappingProfile>();
            cfg.AddProfile<ServerPluginStatusMappingProfile>();
        }, NullLoggerFactory.Instance).CreateMapper();

    private async Task<Harness> CreateHarnessAsync()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new FixedTenantContext(tenantId);
        var context = new ApiDbContext(postgres.Options, tenantContext);
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Test tenant {tenantId}", IsActive = true });
        await context.SaveChangesAsync();

        var rconCredentialProtector = new Mock<IRconCredentialProtector>();
        rconCredentialProtector.Setup(p => p.Protect(It.IsAny<string>())).Returns<string>(s => s);

        var subscriptionRepository = new Mock<ISubscriptionRepository>();
        subscriptionRepository.Setup(r => r.GetForTenantAsync(tenantId)).ReturnsAsync((Subscription?)null);
        subscriptionRepository.Setup(r => r.GetCurrentTermAsync(tenantId)).ReturnsAsync((SubscriptionPeriod?)null);

        var statuses = new ServerPluginStatusRepository(context);
        var publish = new Mock<IPublishEndpoint>();
        var script = new Mock<IPluginScriptService>();
        var update = new Mock<IPluginUpdateService>();
        var pluginList = new Mock<IServerPluginRepository>();

        var controller = new RustServersController(
            new RustServerRepository(context),
            CreateMapper(),
            NullLogger<RustServersController>.Instance,
            Mock.Of<ICorrelationContextAccessor>(),
            rconCredentialProtector.Object,
            Mock.Of<IApiKeyProtector>(),
            publish.Object,
            Mock.Of<IRequestClient<SendRconCommand>>(),
            tenantContext,
            Mock.Of<IRconEventRepository>(),
            Mock.Of<IPlayerSessionRepository>(),
            Mock.Of<IPlayerKillEventRepository>(),
            Mock.Of<IServerInfoSnapshotRepository>(),
            Mock.Of<IConnectionLogRepository>(),
            pluginList.Object,
            statuses,
            script.Object,
            update.Object,
            subscriptionRepository.Object);

        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

        return new Harness(controller, statuses, publish, script, update, pluginList, tenantId);
    }

    private static async Task<Guid> CreateServerAsync(RustServersController controller, string name)
    {
        var created = await controller.Create(new CreateRustServerDto { Name = name, Host = "192.0.2.40", Port = 28016, RconPassword = "unused" });
        var result = Assert.IsType<CreatedAtActionResult>(created.Result);
        return Assert.IsType<RustServerDto>(result.Value).Id;
    }

    private static UpdateServerPluginSettingsDto Switches(bool recording, bool combat, bool updates = false) =>
        new() { RecordingEnabled = recording, CombatLogEnabled = combat, UpdatesEnabled = updates };

    private static ServerPluginStatus Reported(
        bool recording, bool combat, string signingState = "unknown", string fingerprint = "") => new()
    {
        ProtocolVersion = 1,
        PluginVersion = "0.1.0",
        Capabilities = ["config"],
        ReportedRecordingEnabled = recording,
        ReportedCombatLogEnabled = combat,
        SettingsPersisted = true,
        SigningState = signingState,
        SigningKeyFingerprint = fingerprint,
        CapturedAtUtc = DateTimeOffset.UtcNow
    };

    private static RustServerDto ServerFrom<T>(ActionResult<T> result) where T : class =>
        Assert.IsType<RustServerDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    // ---- setup complete ------------------------------------------------------------------------------------

    [Fact]
    public async Task ANewServerHasNotFinishedSetupUntilTheWizardSaysSo()
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Half added");

        Assert.Null(ServerFrom(await h.Controller.GetById(id)).SetupCompletedAtUtc);
    }

    [Fact]
    public async Task CompletingSetupStampsTheTimeOnceAndLaterCallsChangeNothing()
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Finished");

        var first = ServerFrom(await h.Controller.CompleteSetup(id));
        var second = ServerFrom(await h.Controller.CompleteSetup(id));

        Assert.NotNull(first.SetupCompletedAtUtc);
        Assert.Equal(first.SetupCompletedAtUtc, second.SetupCompletedAtUtc);
        Assert.Equal(first.SetupCompletedAtUtc, ServerFrom(await h.Controller.GetById(id)).SetupCompletedAtUtc);
    }

    [Fact]
    public async Task CompletingSetupOfAnUnknownServerIsNotFound()
    {
        var h = await CreateHarnessAsync();

        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>((await h.Controller.CompleteSetup(Guid.NewGuid())).Result);
    }

    [Fact]
    public async Task AnOrdinaryEditNeverSetsOrClearsIt()
    {
        var h = await CreateHarnessAsync();
        var half = await CreateServerAsync(h.Controller, "Edit half");
        var done = await CreateServerAsync(h.Controller, "Edit done");
        await h.Controller.CompleteSetup(done);

        await h.Controller.Update(half, new UpdateRustServerDto { Name = "Renamed half", Host = "192.0.2.41", Port = 28016 });
        await h.Controller.Update(done, new UpdateRustServerDto { Name = "Renamed done", Host = "192.0.2.42", Port = 28016 });

        Assert.Null(ServerFrom(await h.Controller.GetById(half)).SetupCompletedAtUtc);
        Assert.NotNull(ServerFrom(await h.Controller.GetById(done)).SetupCompletedAtUtc);
    }

    // ---- GET plugin-status ---------------------------------------------------------------------------------

    [Fact]
    public async Task PluginStatus_IsNotFoundForAnUnknownServer()
    {
        var h = await CreateHarnessAsync();

        var result = await h.Controller.GetPluginStatus(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task PluginStatus_IsNoContentWhenThePluginHasNeverAnswered()
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "No plugin");

        var result = await h.Controller.GetPluginStatus(id);

        Assert.IsType<NoContentResult>(result.Result);
    }

    [Fact]
    public async Task PluginStatus_ReturnsWhatThePluginReportedNotWhatWasAskedFor()
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Reports on");
        // The server is saved as recording OFF, but the plugin still says ON (it has not been synced yet).
        await h.Controller.UpdatePluginSettings(id, Switches(recording: false, combat: true));
        await h.Statuses.UpsertAsync(h.TenantId, id, Reported(recording: true, combat: true));

        var result = await h.Controller.GetPluginStatus(id);

        var dto = Assert.IsType<ServerPluginStatusDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(dto.RecordingEnabled);
        Assert.True(dto.CombatLogEnabled);
        Assert.Equal("0.1.0", dto.PluginVersion);
        Assert.Equal(["config"], dto.Capabilities);
    }

    [Fact]
    public async Task PluginStatus_NeverShowsAnotherTenantsServer()
    {
        var a = await CreateHarnessAsync();
        var b = await CreateHarnessAsync();
        var serverInB = await CreateServerAsync(b.Controller, "Tenant B server");
        await b.Statuses.UpsertAsync(b.TenantId, serverInB, Reported(true, true));

        var result = await a.Controller.GetPluginStatus(serverInB);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ---- does the installed plugin trust THIS Panel's key ---------------------------------------------------

    private async Task<ServerPluginStatusDto> StatusAsync(string pluginFingerprint, string? panelFingerprint, string state = "valid")
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Trust " + Guid.NewGuid().ToString("N")[..8]);
        h.Script.Setup(s => s.GetKeyFingerprintIfAnyAsync()).ReturnsAsync(panelFingerprint);
        await h.Statuses.UpsertAsync(h.TenantId, id, Reported(true, true, state, pluginFingerprint));

        var result = await h.Controller.GetPluginStatus(id);

        return Assert.IsType<ServerPluginStatusDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task SigningKey_MatchesWhenThePluginTrustsThisPanelsKey()
    {
        var dto = await StatusAsync("0123456789abcdef", "0123456789abcdef");

        Assert.True(dto.SigningKeyMatchesThisPanel);
        Assert.Equal("valid", dto.SigningState);
        Assert.Equal("0123456789abcdef", dto.SigningKeyFingerprint);
    }

    [Fact]
    public async Task SigningKey_DoesNotMatchWhenThePluginCameFromADifferentPanel()
    {
        var dto = await StatusAsync("0123456789abcdef", "fedcba9876543210");

        Assert.False(dto.SigningKeyMatchesThisPanel);
    }

    [Fact]
    public async Task SigningKey_ComparisonIgnoresCase()
    {
        var dto = await StatusAsync("0123456789abcdef", "0123456789ABCDEF");

        Assert.True(dto.SigningKeyMatchesThisPanel);
    }

    [Fact]
    public async Task SigningKey_IsUnknownWhenThePluginTrustsNoKey()
    {
        // An unsigned developer copy: nothing to compare, so neither a yes nor a no.
        var dto = await StatusAsync("", "0123456789abcdef", state: "unsigned");

        Assert.Null(dto.SigningKeyMatchesThisPanel);
    }

    [Fact]
    public async Task SigningKey_IsUnknownWhenThisPanelHasNoKeyYet()
    {
        var dto = await StatusAsync("0123456789abcdef", panelFingerprint: null);

        Assert.Null(dto.SigningKeyMatchesThisPanel);
    }

    [Fact]
    public async Task SigningKey_AReadNeverCreatesThisPanelsKey()
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Read only");
        await h.Statuses.UpsertAsync(h.TenantId, id, Reported(true, true, "valid", "0123456789abcdef"));

        await h.Controller.GetPluginStatus(id);

        h.Script.Verify(s => s.BuildAsync(), Times.Never); // BuildAsync is the call that would create a key
    }

    // ---- GET plugin/download -------------------------------------------------------------------------------

    [Fact]
    public async Task Download_ServesTheSignedScriptAsAFileWithTheKeyFingerprintAndVersion()
    {
        var h = await CreateHarnessAsync();
        var bytes = new byte[] { 1, 2, 3, 4 };
        h.Script.Setup(s => s.BuildAsync()).ReturnsAsync(new PluginScript(bytes, "abcd1234abcd1234", "0.2.0"));

        var result = await h.Controller.DownloadPlugin();

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal(bytes, file.FileContents);
        Assert.Equal("RustArchon.cs", file.FileDownloadName);
        Assert.StartsWith("text/plain", file.ContentType);
        Assert.Equal("abcd1234abcd1234", h.Controller.Response.Headers["X-RustArchon-Key-Fingerprint"].ToString());
        Assert.Equal("0.2.0", h.Controller.Response.Headers["X-RustArchon-Plugin-Version"].ToString());
    }

    [Fact]
    public async Task Download_OmitsTheVersionHeaderWhenTheSourceHasNone()
    {
        var h = await CreateHarnessAsync();
        h.Script.Setup(s => s.BuildAsync()).ReturnsAsync(new PluginScript([1], "abcd1234abcd1234", null));

        await h.Controller.DownloadPlugin();

        Assert.False(h.Controller.Response.Headers.ContainsKey("X-RustArchon-Plugin-Version"));
    }

    [Fact]
    public async Task Download_IsA503WhenTheSigningKeyIsUnusable()
    {
        // Never an unsigned script and never a silently new key: an unusable stored key is surfaced.
        var h = await CreateHarnessAsync();
        h.Script.Setup(s => s.BuildAsync()).ThrowsAsync(new PluginSigningKeyException("cannot be read"));

        var result = await h.Controller.DownloadPlugin();

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, status.StatusCode);
        Assert.DoesNotContain("cannot be read", status.Value?.ToString() ?? ""); // detail stays in the log
    }

    // ---- PUT plugin-settings -------------------------------------------------------------------------------

    [Fact]
    public async Task UpdatePluginSettings_SavesBothSwitches()
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Persists");

        var dto = ServerFrom(await h.Controller.UpdatePluginSettings(id, Switches(recording: false, combat: false)));

        Assert.False(dto.PluginRecordingEnabled);
        Assert.False(dto.PluginCombatLogEnabled);
        var reread = ServerFrom(await h.Controller.GetById(id));
        Assert.False(reread.PluginRecordingEnabled);
        Assert.False(reread.PluginCombatLogEnabled);
    }

    [Fact]
    public async Task UpdatePluginSettings_TellsThePluginWhenASwitchChanges()
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Switch flips");

        await h.Controller.UpdatePluginSettings(id, Switches(recording: true, combat: false));

        h.Publish.Verify(
            p => p.Publish(It.Is<ServerPluginSettingsChanged>(m => m.ServerId == id && m.TenantId == h.TenantId), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdatePluginSettings_DoesNotTellThePluginWhenNothingChanged()
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "No change");

        await h.Controller.UpdatePluginSettings(id, Switches(recording: true, combat: true)); // both already on

        h.Publish.Verify(
            p => p.Publish(It.IsAny<ServerPluginSettingsChanged>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdatePluginSettings_IsNotFoundForAnUnknownServer()
    {
        var h = await CreateHarnessAsync();

        var result = await h.Controller.UpdatePluginSettings(Guid.NewGuid(), Switches(false, false));

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task UpdatePluginSettings_CannotReachAnotherTenantsServer()
    {
        var a = await CreateHarnessAsync();
        var b = await CreateHarnessAsync();
        var serverInB = await CreateServerAsync(b.Controller, "Tenant B server");

        var result = await a.Controller.UpdatePluginSettings(serverInB, Switches(false, false));

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.True(ServerFrom(await b.Controller.GetById(serverInB)).PluginRecordingEnabled); // untouched
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData(true, null, true)]
    [InlineData(true, true, null)] // an old client that does not know about updates must not silently turn them off
    [InlineData(null, null, null)]
    public void UpdatePluginSettings_ARequestThatOmitsAValueIsInvalidRatherThanReadAsOff(bool? recording, bool? combat, bool? updates)
    {
        var dto = new UpdateServerPluginSettingsDto { RecordingEnabled = recording, CombatLogEnabled = combat, UpdatesEnabled = updates };

        var problems = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        var valid = Validator.TryValidateObject(dto, new ValidationContext(dto), problems, validateAllProperties: true);

        Assert.False(valid);
        Assert.NotEmpty(problems);
    }

    // ---- an ordinary edit must never touch the switches ----------------------------------------------------

    [Fact]
    public async Task AnOrdinaryUpdateLeavesTheSwitchesAloneAndDoesNotTellThePlugin()
    {
        // Regression guard. Update is a full-record PUT that does not send the switches; if it carried them, a
        // rename would silently reset a switch someone had turned off.
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Before rename");
        await h.Controller.UpdatePluginSettings(id, Switches(recording: false, combat: false));
        h.Publish.Invocations.Clear();

        var renamed = await h.Controller.Update(id, new UpdateRustServerDto { Id = id, Name = "After rename", Host = "192.0.2.41", Port = 28016 });

        var dto = ServerFrom(renamed);
        Assert.Equal("After rename", dto.Name);
        Assert.False(dto.PluginRecordingEnabled);
        Assert.False(dto.PluginCombatLogEnabled);
        h.Publish.Verify(
            p => p.Publish(It.IsAny<ServerPluginSettingsChanged>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ANewServerHasBothSwitchesOnByDefault()
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Fresh");

        var dto = ServerFrom(await h.Controller.GetById(id));

        Assert.True(dto.PluginRecordingEnabled);
        Assert.True(dto.PluginCombatLogEnabled);
        Assert.True(dto.PluginUpdatesEnabled); // updates are on for a new server, and the owner can turn them off
    }

    // ---- self-update ---------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdatesEnabled_IsSavedWithoutSendingAnythingToThePlugin()
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Updates on");
        h.Publish.Invocations.Clear();

        var saved = ServerFrom(await h.Controller.UpdatePluginSettings(id, Switches(true, true, updates: true)));

        Assert.True(saved.PluginUpdatesEnabled);
        h.Publish.Verify(p => p.Publish(It.IsAny<ServerPluginSettingsChanged>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnOrdinaryEditNeverTurnsUpdatesOnOrOff()
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Edit keeps updates");
        await h.Controller.UpdatePluginSettings(id, Switches(true, true, updates: false));

        var renamed = await h.Controller.Update(id, new UpdateRustServerDto { Id = id, Name = "Renamed", Host = "192.0.2.41", Port = 28016 });

        Assert.False(ServerFrom(renamed).PluginUpdatesEnabled);          // the owner's choice is what stays, on or off
    }

    [Fact]
    public async Task StartPluginUpdate_IsNotFoundForAnUnknownServer()
    {
        var h = await CreateHarnessAsync();

        var result = await h.Controller.StartPluginUpdate(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result.Result);
        h.Update.Verify(u => u.StartAsync(It.IsAny<RustServer>()), Times.Never);
    }

    [Fact]
    public async Task StartPluginUpdate_NeverReachesAnotherTenantsServer()
    {
        var a = await CreateHarnessAsync();
        var b = await CreateHarnessAsync();
        var serverInB = await CreateServerAsync(b.Controller, "Tenant B");

        var result = await a.Controller.StartPluginUpdate(serverInB);

        Assert.IsType<NotFoundResult>(result.Result);
        a.Update.Verify(u => u.StartAsync(It.IsAny<RustServer>()), Times.Never);
    }

    [Fact]
    public async Task StartPluginUpdate_HandsTheServerToTheServiceAndReturnsItsAnswer()
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Update me");
        var answer = new PluginUpdateResultDto { Started = false, Code = "up_to_date", Message = "nothing newer" };
        h.Update.Setup(u => u.StartAsync(It.Is<RustServer>(s => s.Id == id))).ReturnsAsync(answer);

        var result = await h.Controller.StartPluginUpdate(id);

        Assert.Same(answer, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task StartUpdaterUpdate_IsNotFoundForAnUnknownServer()
    {
        var h = await CreateHarnessAsync();

        var result = await h.Controller.StartUpdaterUpdate(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result.Result);
        h.Update.Verify(u => u.StartUpdaterAsync(It.IsAny<RustServer>()), Times.Never);
    }

    [Fact]
    public async Task StartUpdaterUpdate_NeverReachesAnotherTenantsServer()
    {
        var a = await CreateHarnessAsync();
        var b = await CreateHarnessAsync();
        var serverInB = await CreateServerAsync(b.Controller, "Tenant B");

        var result = await a.Controller.StartUpdaterUpdate(serverInB);

        Assert.IsType<NotFoundResult>(result.Result);
        a.Update.Verify(u => u.StartUpdaterAsync(It.IsAny<RustServer>()), Times.Never);
    }

    [Fact]
    public async Task StartUpdaterUpdate_HandsTheServerToTheServiceAndReturnsItsAnswer()
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Update my Updater");
        var answer = new PluginUpdateResultDto { Started = true, Code = "started", Message = "going" };
        h.Update.Setup(u => u.StartUpdaterAsync(It.Is<RustServer>(s => s.Id == id))).ReturnsAsync(answer);

        var result = await h.Controller.StartUpdaterUpdate(id);

        Assert.Same(answer, Assert.IsType<OkObjectResult>(result.Result).Value);
        h.Update.Verify(u => u.StartAsync(It.IsAny<RustServer>()), Times.Never);      // the plugin update is a different action
    }

    [Fact]
    public async Task DownloadPluginUpdater_ServesTheSignedUpdaterWithItsFingerprint()
    {
        var h = await CreateHarnessAsync();
        h.Script.Setup(s => s.BuildUpdaterAsync()).ReturnsAsync(new PluginScript([1, 2, 3], "0123456789abcdef", "0.1.0"));

        var result = await h.Controller.DownloadPluginUpdater();

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal([1, 2, 3], file.FileContents);
        Assert.Equal("RustArchonUpdater.cs", file.FileDownloadName);
        Assert.Equal("0123456789abcdef", h.Controller.Response.Headers["X-RustArchon-Key-Fingerprint"]);
        Assert.Equal("0.1.0", h.Controller.Response.Headers["X-RustArchon-Plugin-Version"]);
    }

    [Fact]
    public async Task DownloadPluginUpdater_IsUnavailableWhenTheKeyIs()
    {
        var h = await CreateHarnessAsync();
        h.Script.Setup(s => s.BuildUpdaterAsync()).ThrowsAsync(new PluginSigningKeyException("unreadable"));

        var result = await h.Controller.DownloadPluginUpdater();

        Assert.Equal(503, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    private async Task<ServerPluginStatusDto> StatusWithAsync(string installed, string? latest, params string[] plugins)
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Avail " + Guid.NewGuid().ToString("N")[..8]);
        h.Script.Setup(s => s.GetLatestVersionAsync()).ReturnsAsync(latest);
        h.Plugins.Setup(p => p.GetForServerAsync(id)).ReturnsAsync(
            plugins.Select(n => new ServerPlugin { Name = n, RustServerId = id, TenantId = h.TenantId }).ToList());
        var status = Reported(true, true);
        status.PluginVersion = installed;
        await h.Statuses.UpsertAsync(h.TenantId, id, status);

        var result = await h.Controller.GetPluginStatus(id);

        return Assert.IsType<ServerPluginStatusDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task PluginStatus_OffersAnUpdateWhenTheLatestIsNewer()
    {
        var dto = await StatusWithAsync("0.2.0", "0.2.1", "RustArchon", "RustArchonUpdater");

        Assert.True(dto.UpdateAvailable);
        Assert.Equal("0.2.1", dto.LatestPluginVersion);
        Assert.True(dto.UpdaterInstalled);
    }

    [Theory]
    [InlineData("0.2.1", "0.2.1")]
    [InlineData("0.3.0", "0.2.1")]
    [InlineData("0.2.0", null)]
    public async Task PluginStatus_OffersNothingWhenThereIsNothingNewer(string installed, string? latest)
    {
        var dto = await StatusWithAsync(installed, latest, "RustArchon");

        Assert.False(dto.UpdateAvailable);
    }

    [Fact]
    public async Task PluginStatus_SaysWhenTheUpdaterIsNotInstalled()
    {
        var dto = await StatusWithAsync("0.2.0", "0.2.1", "RustArchon", "RustArchonUpdaterExtra");

        Assert.False(dto.UpdaterInstalled);
        Assert.True(dto.UpdateAvailable);
    }
    [Theory]
    [InlineData(PluginKeyState.Active, "active")]
    [InlineData(PluginKeyState.Retired, "retired")]
    [InlineData(PluginKeyState.Revoked, "revoked")]
    public async Task PluginStatus_SaysWhereTheInstalledKeyStandsInThisPanelsHistory(PluginKeyState state, string expected)
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Key state " + expected);
        h.Script.Setup(s => s.GetKeyStateAsync("0123456789abcdef")).ReturnsAsync(state);
        await h.Statuses.UpsertAsync(h.TenantId, id, Reported(true, true, "valid", "0123456789abcdef"));

        var dto = Assert.IsType<ServerPluginStatusDto>(Assert.IsType<OkObjectResult>((await h.Controller.GetPluginStatus(id)).Result).Value);

        Assert.Equal(expected, dto.SigningKeyState);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("unsigned")]
    [InlineData("unknown")]
    public async Task PluginStatus_HasNoKeyStateForAPluginWhoseSignatureIsNotValid(string signingState)
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Bad sig " + signingState);
        h.Script.Setup(s => s.GetKeyStateAsync(It.IsAny<string>())).ReturnsAsync(PluginKeyState.Active);
        await h.Statuses.UpsertAsync(h.TenantId, id, Reported(true, true, signingState, "0123456789abcdef"));

        var dto = Assert.IsType<ServerPluginStatusDto>(Assert.IsType<OkObjectResult>((await h.Controller.GetPluginStatus(id)).Result).Value);

        Assert.Equal("", dto.SigningKeyState);
    }

    [Fact]
    public async Task PluginStatus_HasNoKeyStateForAKeyThisPanelNeverHad()
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Foreign key");
        h.Script.Setup(s => s.GetKeyStateAsync(It.IsAny<string>())).ReturnsAsync((PluginKeyState?)null);
        await h.Statuses.UpsertAsync(h.TenantId, id, Reported(true, true, "valid", "ffffffffffffffff"));

        var dto = Assert.IsType<ServerPluginStatusDto>(Assert.IsType<OkObjectResult>((await h.Controller.GetPluginStatus(id)).Result).Value);

        Assert.Equal("", dto.SigningKeyState);
    }

    [Theory]
    [InlineData("v0.1.0", "0.2.0", "0.1.0", true)]
    [InlineData("v0.2.0", "0.2.0", "0.2.0", false)]
    [InlineData("0.3.0", "0.2.0", "0.3.0", false)]
    [InlineData(null, "0.2.0", null, false)]
    public async Task PluginStatus_ReportsTheInstalledUpdaterVersionAndWhetherANewerOneIsServed(
        string? listed, string latest, string? expectedInstalled, bool expectedNewer)
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Updater " + Guid.NewGuid().ToString("N")[..6]);
        h.Script.Setup(s => s.GetLatestUpdaterVersionAsync()).ReturnsAsync(latest);
        h.Plugins.Setup(p => p.GetForServerAsync(id)).ReturnsAsync(
            [new ServerPlugin { Name = "RustArchonUpdater", Version = listed, RustServerId = id, TenantId = h.TenantId }]);
        await h.Statuses.UpsertAsync(h.TenantId, id, Reported(true, true));

        var dto = Assert.IsType<ServerPluginStatusDto>(Assert.IsType<OkObjectResult>((await h.Controller.GetPluginStatus(id)).Result).Value);

        Assert.True(dto.UpdaterInstalled);
        Assert.Equal(expectedInstalled, dto.UpdaterVersion);
        Assert.Equal(latest, dto.LatestUpdaterVersion);
        Assert.Equal(expectedNewer, dto.UpdaterUpdateAvailable);
    }

    [Fact]
    public async Task PluginStatus_HasNoUpdaterVersionWhenTheUpdaterIsNotInstalled()
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "No updater");
        h.Script.Setup(s => s.GetLatestUpdaterVersionAsync()).ReturnsAsync("0.2.0");
        h.Plugins.Setup(p => p.GetForServerAsync(id)).ReturnsAsync([]);
        await h.Statuses.UpsertAsync(h.TenantId, id, Reported(true, true));

        var dto = Assert.IsType<ServerPluginStatusDto>(Assert.IsType<OkObjectResult>((await h.Controller.GetPluginStatus(id)).Result).Value);

        Assert.False(dto.UpdaterInstalled);
        Assert.Null(dto.UpdaterVersion);
        Assert.False(dto.UpdaterUpdateAvailable); // nothing installed to update: the ordinary "download the Updater" prompt applies
    }

    // ---- the automatic-update switch ------------------------------------------------------------------------

    [Fact]
    public async Task AutoUpdateIsOnByDefaultForANewServer()
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Auto default");

        Assert.True(ServerFrom(await h.Controller.GetById(id)).PluginAutoUpdateEnabled);
    }

    [Fact]
    public async Task AutoUpdateCanBeTurnedOnWhenUpdatesAreAllowedAndTurnedOffAgain()
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Auto on");

        var on = ServerFrom(await h.Controller.UpdatePluginSettings(id, new UpdateServerPluginSettingsDto { RecordingEnabled = true, CombatLogEnabled = true, UpdatesEnabled = true, AutoUpdateEnabled = true }));
        var off = ServerFrom(await h.Controller.UpdatePluginSettings(id, new UpdateServerPluginSettingsDto { RecordingEnabled = true, CombatLogEnabled = true, UpdatesEnabled = true, AutoUpdateEnabled = false }));

        Assert.True(on.PluginAutoUpdateEnabled);
        Assert.False(off.PluginAutoUpdateEnabled);
    }

    [Fact]
    public async Task AClientThatDoesNotSendTheAutoSwitchLeavesItAsItWas()
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Auto omitted");
        await h.Controller.UpdatePluginSettings(id, new UpdateServerPluginSettingsDto { RecordingEnabled = true, CombatLogEnabled = true, UpdatesEnabled = true, AutoUpdateEnabled = false });

        var after = ServerFrom(await h.Controller.UpdatePluginSettings(id, Switches(recording: true, combat: true, updates: true)));      // no AutoUpdateEnabled

        Assert.False(after.PluginAutoUpdateEnabled);          // left as the owner set it, not reset to the new-server default
    }

    [Fact]
    public async Task TurningUpdatesOffTurnsAutoUpdateOffWhateverIsSent()
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Auto follows updates");
        await h.Controller.UpdatePluginSettings(id, new UpdateServerPluginSettingsDto { RecordingEnabled = true, CombatLogEnabled = true, UpdatesEnabled = true, AutoUpdateEnabled = true });

        var after = ServerFrom(await h.Controller.UpdatePluginSettings(id, new UpdateServerPluginSettingsDto { RecordingEnabled = true, CombatLogEnabled = true, UpdatesEnabled = false, AutoUpdateEnabled = true }));

        Assert.False(after.PluginUpdatesEnabled);
        Assert.False(after.PluginAutoUpdateEnabled);
    }

    [Fact]
    public async Task AutoUpdateCannotBeTurnedOnWhileUpdatesAreOff()
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Auto needs updates");

        var after = ServerFrom(await h.Controller.UpdatePluginSettings(id, new UpdateServerPluginSettingsDto { RecordingEnabled = true, CombatLogEnabled = true, UpdatesEnabled = false, AutoUpdateEnabled = true }));

        Assert.False(after.PluginAutoUpdateEnabled);
    }

    [Fact]
    public async Task ChangingTheAutoSwitchAloneDoesNotTellThePlugin()
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Auto is panel-side");
        h.Publish.Invocations.Clear();

        await h.Controller.UpdatePluginSettings(id, new UpdateServerPluginSettingsDto { RecordingEnabled = true, CombatLogEnabled = true, UpdatesEnabled = true, AutoUpdateEnabled = true });

        h.Publish.Verify(p => p.Publish(It.IsAny<ServerPluginSettingsChanged>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnOrdinaryServerEditNeverChangesTheAutoSwitch()
    {
        var h = await CreateHarnessAsync();
        var id = await CreateServerAsync(h.Controller, "Auto survives edits");
        await h.Controller.UpdatePluginSettings(id, new UpdateServerPluginSettingsDto { RecordingEnabled = true, CombatLogEnabled = true, UpdatesEnabled = true, AutoUpdateEnabled = false });

        await h.Controller.Update(id, new UpdateRustServerDto { Name = "Renamed", Host = "192.0.2.9", Port = 28016 });

        Assert.False(ServerFrom(await h.Controller.GetById(id)).PluginAutoUpdateEnabled);
    }
}
