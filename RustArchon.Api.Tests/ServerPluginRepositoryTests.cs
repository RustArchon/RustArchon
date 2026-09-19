// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="ServerPluginRepository.ReplaceForServerAsync"/> - what
/// <c>ServerPluginsCapturedConsumer</c> calls on every Worker poll. Runs against a real Postgres for the
/// same reason <see cref="RustServerRepositoryTests"/> does, and with no ambient tenant, the only way a
/// MassTransit consumer ever calls it: ADR-018's fail-closed tenant filter would otherwise make every
/// query here silently match zero rows.
/// </summary>
public class ServerPluginRepositoryTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private ApiDbContext CreateContext() => new(postgres.Options);

    private static async Task<Guid> SeedTenantAsync(ApiDbContext context)
    {
        var tenantId = Guid.NewGuid();
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Test tenant {tenantId}", IsActive = true });
        await context.SaveChangesAsync();
        return tenantId;
    }

    private static ServerPlugin Plugin(string name, string version, string author = "Someone") =>
        new() { Name = name, Author = author, Version = version, Framework = ServerModFramework.Carbon };

    private static async Task<List<ServerPlugin>> StoredAsync(ApiDbContext context, Guid serverId) =>
        await context.Set<ServerPlugin>().AcrossAllTenants()
            .Where(p => p.RustServerId == serverId)
            .OrderBy(p => p.Name)
            .ToListAsync();

    [Fact]
    public async Task InsertsThePluginsWithNoAmbientTenant()
    {
        await using var context = CreateContext();
        var tenantId = await SeedTenantAsync(context);
        var serverId = Guid.NewGuid();
        var capturedAt = DateTimeOffset.UtcNow;

        await new ServerPluginRepository(context).ReplaceForServerAsync(
            tenantId, serverId, [Plugin("Kits", "4.0.0"), Plugin("Better Chat", "5.2.14")], capturedAt);

        var stored = await StoredAsync(context, serverId);
        Assert.Equal(["Better Chat", "Kits"], stored.Select(p => p.Name));
        Assert.All(stored, p =>
        {
            Assert.Equal(tenantId, p.TenantId);
            Assert.Equal(capturedAt, p.CapturedAtUtc);
        });
    }

    [Fact]
    public async Task MakesTheStoredRowsMatchTheNewList()
    {
        await using var context = CreateContext();
        var tenantId = await SeedTenantAsync(context);
        var serverId = Guid.NewGuid();
        var repository = new ServerPluginRepository(context);
        var first = DateTimeOffset.UtcNow.AddMinutes(-5);

        await repository.ReplaceForServerAsync(
            tenantId, serverId, [Plugin("Kits", "4.0.0"), Plugin("Removed Plugin", "1.0.0")], first);
        await repository.ReplaceForServerAsync(
            tenantId, serverId, [Plugin("Kits", "4.1.0"), Plugin("New Plugin", "2.0.0")], first.AddMinutes(5));

        var stored = await StoredAsync(context, serverId);
        Assert.Equal(["Kits", "New Plugin"], stored.Select(p => p.Name));
        Assert.Equal("4.1.0", stored.Single(p => p.Name == "Kits").Version);
    }

    [Fact]
    public async Task AnEmptyListClearsTheServer()
    {
        await using var context = CreateContext();
        var tenantId = await SeedTenantAsync(context);
        var serverId = Guid.NewGuid();
        var repository = new ServerPluginRepository(context);
        var first = DateTimeOffset.UtcNow.AddMinutes(-5);

        await repository.ReplaceForServerAsync(tenantId, serverId, [Plugin("Kits", "4.0.0")], first);
        await repository.ReplaceForServerAsync(tenantId, serverId, [], first.AddMinutes(5));

        Assert.Empty(await StoredAsync(context, serverId));
    }

    [Fact]
    public async Task LeavesOtherServersAlone()
    {
        await using var context = CreateContext();
        var tenantId = await SeedTenantAsync(context);
        var serverA = Guid.NewGuid();
        var serverB = Guid.NewGuid();
        var repository = new ServerPluginRepository(context);
        var first = DateTimeOffset.UtcNow.AddMinutes(-5);

        await repository.ReplaceForServerAsync(tenantId, serverA, [Plugin("Kits", "4.0.0")], first);
        await repository.ReplaceForServerAsync(tenantId, serverB, [Plugin("Other", "1.0.0")], first);
        await repository.ReplaceForServerAsync(tenantId, serverA, [], first.AddMinutes(5));

        Assert.Empty(await StoredAsync(context, serverA));
        Assert.Equal(["Other"], (await StoredAsync(context, serverB)).Select(p => p.Name));
    }

    [Fact]
    public async Task IgnoresAReportOlderThanWhatIsStored()
    {
        await using var context = CreateContext();
        var tenantId = await SeedTenantAsync(context);
        var serverId = Guid.NewGuid();
        var repository = new ServerPluginRepository(context);
        var newer = DateTimeOffset.UtcNow;

        await repository.ReplaceForServerAsync(tenantId, serverId, [Plugin("Kits", "4.1.0")], newer);
        // Delivered late - captured before the report above, so it must not roll the list back.
        await repository.ReplaceForServerAsync(tenantId, serverId, [Plugin("Kits", "4.0.0")], newer.AddMinutes(-5));

        var stored = await StoredAsync(context, serverId);
        Assert.Equal("4.1.0", Assert.Single(stored).Version);
    }

    [Fact]
    public async Task GetForServer_ReadsThroughTheTenantFilterOrderedByName()
    {
        var tenantId = Guid.NewGuid();
        await using (var seed = CreateContext())
        {
            seed.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Test tenant {tenantId}", IsActive = true });
            await seed.SaveChangesAsync();
        }

        var serverId = Guid.NewGuid();
        await using (var write = CreateContext())
        {
            await new ServerPluginRepository(write).ReplaceForServerAsync(
                tenantId, serverId, [Plugin("Zeta", "1.0.0"), Plugin("Alpha", "1.0.0")], DateTimeOffset.UtcNow);
        }

        // An ordinary request-scoped read: this tenant is the ambient one.
        await using var read = new ApiDbContext(postgres.Options, new FixedTenant(tenantId));
        var plugins = await new ServerPluginRepository(read).GetForServerAsync(serverId);

        Assert.Equal(["Alpha", "Zeta"], plugins.Select(p => p.Name));
    }

    private sealed class FixedTenant(Guid tenantId) : JumpStart.Repositories.ITenantContext
    {
        public Task<Guid?> GetCurrentTenantIdAsync() => Task.FromResult<Guid?>(tenantId);
    }
}
