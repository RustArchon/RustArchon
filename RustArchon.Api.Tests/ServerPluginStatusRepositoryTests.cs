// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="ServerPluginStatusRepository"/> - what the handshake consumer and the settings
/// synchronizer call. Runs against a real Postgres and with no ambient tenant, the only way a MassTransit
/// consumer ever calls it (see <see cref="ServerPluginRepositoryTests"/>).
/// </summary>
public class ServerPluginStatusRepositoryTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private ApiDbContext CreateContext() => new(postgres.Options);

    private static async Task<Guid> SeedTenantAsync(ApiDbContext context)
    {
        var tenantId = Guid.NewGuid();
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Test tenant {tenantId}", IsActive = true });
        await context.SaveChangesAsync();
        return tenantId;
    }

    private static ServerPluginStatus Reported(
        DateTimeOffset capturedAt, string version = "0.1.0", bool recording = true, bool combat = true, params string[] capabilities) =>
        new()
        {
            ProtocolVersion = 1,
            PluginVersion = version,
            Capabilities = capabilities.Length == 0 ? ["config"] : capabilities,
            ReportedRecordingEnabled = recording,
            ReportedCombatLogEnabled = combat,
            SettingsPersisted = true,
            CapturedAtUtc = capturedAt
        };

    private static Task<int> CountAsync(ApiDbContext context, Guid serverId) =>
        context.Set<ServerPluginStatus>().AcrossAllTenants().CountAsync(s => s.RustServerId == serverId);

    [Fact]
    public async Task InsertsARowForAServerThatHasNone()
    {
        await using var context = CreateContext();
        var tenantId = await SeedTenantAsync(context);
        var serverId = Guid.NewGuid();
        var repository = new ServerPluginStatusRepository(context);

        await repository.UpsertAsync(tenantId, serverId, Reported(DateTimeOffset.UtcNow, "0.1.0", capabilities: ["config", "combat"]));

        var stored = await repository.GetForServerAcrossTenantsAsync(tenantId, serverId);
        Assert.NotNull(stored);
        Assert.Equal(tenantId, stored!.TenantId);
        Assert.Equal("0.1.0", stored.PluginVersion);
        Assert.Equal(["config", "combat"], stored.Capabilities);
    }

    [Fact]
    public async Task UpdatesTheSameRowInPlaceInsteadOfAddingAnother()
    {
        await using var context = CreateContext();
        var tenantId = await SeedTenantAsync(context);
        var serverId = Guid.NewGuid();
        var repository = new ServerPluginStatusRepository(context);
        var first = DateTimeOffset.UtcNow.AddMinutes(-5);

        await repository.UpsertAsync(tenantId, serverId, Reported(first, "0.1.0"));
        await repository.UpsertAsync(tenantId, serverId, Reported(first.AddMinutes(5), "0.2.0", recording: false));

        Assert.Equal(1, await CountAsync(context, serverId));
        var stored = await repository.GetForServerAcrossTenantsAsync(tenantId, serverId);
        Assert.Equal("0.2.0", stored!.PluginVersion);
        Assert.False(stored.ReportedRecordingEnabled);
    }

    [Fact]
    public async Task AnOlderReportDoesNotRollBackANewerOne()
    {
        await using var context = CreateContext();
        var tenantId = await SeedTenantAsync(context);
        var serverId = Guid.NewGuid();
        var repository = new ServerPluginStatusRepository(context);
        var newer = DateTimeOffset.UtcNow;

        await repository.UpsertAsync(tenantId, serverId, Reported(newer, "0.2.0"));
        await repository.UpsertAsync(tenantId, serverId, Reported(newer.AddMinutes(-10), "0.1.0")); // delivered late

        var stored = await repository.GetForServerAcrossTenantsAsync(tenantId, serverId);
        Assert.Equal("0.2.0", stored!.PluginVersion);
        Assert.Equal(newer, stored.CapturedAtUtc);
    }

    [Fact]
    public async Task IsScopedByServerAndTenant()
    {
        await using var context = CreateContext();
        var tenantA = await SeedTenantAsync(context);
        var tenantB = await SeedTenantAsync(context);
        var serverA = Guid.NewGuid();
        var serverB = Guid.NewGuid();
        var repository = new ServerPluginStatusRepository(context);

        await repository.UpsertAsync(tenantA, serverA, Reported(DateTimeOffset.UtcNow, "0.1.0"));
        await repository.UpsertAsync(tenantB, serverB, Reported(DateTimeOffset.UtcNow, "9.9.9"));

        Assert.Equal("0.1.0", (await repository.GetForServerAcrossTenantsAsync(tenantA, serverA))!.PluginVersion);
        Assert.Null(await repository.GetForServerAcrossTenantsAsync(tenantB, serverA)); // right server, wrong tenant
        Assert.Null(await repository.GetForServerAcrossTenantsAsync(tenantA, serverB));
    }

    [Fact]
    public async Task MarkSettingsAppliedUpdatesTheReportedSwitchesOnly()
    {
        await using var context = CreateContext();
        var tenantId = await SeedTenantAsync(context);
        var serverId = Guid.NewGuid();
        var repository = new ServerPluginStatusRepository(context);
        await repository.UpsertAsync(tenantId, serverId, Reported(DateTimeOffset.UtcNow, "0.1.0", recording: true, combat: true));

        await repository.MarkSettingsAppliedAsync(tenantId, serverId, recordingEnabled: false, combatLogEnabled: true);

        var stored = await repository.GetForServerAcrossTenantsAsync(tenantId, serverId);
        Assert.False(stored!.ReportedRecordingEnabled);
        Assert.True(stored.ReportedCombatLogEnabled);
        Assert.Equal("0.1.0", stored.PluginVersion);
    }

    [Fact]
    public async Task MarkSettingsAppliedIsANoOpWhenThereIsNoRow()
    {
        await using var context = CreateContext();
        var tenantId = await SeedTenantAsync(context);
        var serverId = Guid.NewGuid();

        await new ServerPluginStatusRepository(context).MarkSettingsAppliedAsync(tenantId, serverId, false, false);

        Assert.Equal(0, await CountAsync(context, serverId));
    }

    [Fact]
    public void TheRecordingAndCombatColumnsDefaultToTrueInTheModel()
    {
        // Regression guard for a bug caught while writing this migration: EF's generated AddColumn for a
        // non-nullable bool backfills with false unless the model says otherwise, which would have silently
        // switched every EXISTING server's plugin recording and combat log OFF, against the on-by-default decision.
        using var context = CreateContext();
        var serverType = context.Model.FindEntityType(typeof(RustServer))!;

        Assert.Equal(true, serverType.FindProperty(nameof(RustServer.PluginRecordingEnabled))!.GetDefaultValue());
        Assert.Equal(true, serverType.FindProperty(nameof(RustServer.PluginCombatLogEnabled))!.GetDefaultValue());
    }

    [Fact]
    public void ANewServerHasBothSwitchesOn()
    {
        var server = new RustServer();

        Assert.True(server.PluginRecordingEnabled);
        Assert.True(server.PluginCombatLogEnabled);
    }
}
