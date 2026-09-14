// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="RustServerRepository.TryApplyConnectionStatusAsync"/> - in particular that it
/// actually writes with no ambient tenant, which is the only way <c>ConnectionStatusConsumer</c> (a
/// MassTransit consumer) ever calls it. This is a regression test for a real production incident: ADR-018
/// made JumpStart's tenant query filter fail-closed, and this method was the one place in the codebase
/// that still queried through the ordinary tenant-scoped <c>_dbSet</c> instead of
/// <c>AcrossAllTenants()</c> - every connection-status transition silently stopped being recorded (zero
/// rows ever matched), which meant the Panel's status badge froze at a stale value and the Logs tab
/// stayed permanently empty, with nothing in the exception logs to point at why - the query didn't
/// throw, it just matched nothing, exactly as a normal "no such server" would.
/// </summary>
/// <remarks>
/// Runs against a real (throwaway, Testcontainers-hosted) Postgres, not EF Core's InMemory provider or
/// Sqlite - <c>ExecuteUpdateAsync</c> has no translation on InMemory at all, and confirmed by hand,
/// Sqlite's own translator rejects this exact query shape even though the identical query executes fine
/// on Postgres - so neither is a trustworthy stand-in for what this method actually needs to prove.
/// See <see cref="PostgresFixture"/>.
/// </remarks>
public class RustServerRepositoryTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private ApiDbContext CreateContext() => new(postgres.Options);

    private async Task<RustServer> SeedServerAsync(ApiDbContext context)
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"Test tenant {Guid.NewGuid()}",
            IsActive = true
        };
        context.Set<Tenant>().Add(tenant);
        await context.SaveChangesAsync();

        var server = new RustServer
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = $"Test server {Guid.NewGuid()}",
            Host = "192.0.2.10",
            Port = 28016,
            RconPassword = "unused",
            IsEnabled = true,
            ConnectionStatus = RconConnectionStatus.Disconnected,
            ConnectionStatusChangedAtUtc = null
        };

        context.Set<RustServer>().Add(server);
        await context.SaveChangesAsync();
        return server;
    }

    /// <summary>
    /// The exact shape of the production bug: a consumer with no ambient tenant (the only caller this
    /// method ever has) must still be able to apply a real transition.
    /// </summary>
    [Fact]
    public async Task AppliesWithNoAmbientTenantExactlyHowTheConsumerCallsIt()
    {
        await using var context = CreateContext();
        var server = await SeedServerAsync(context);

        // No IUserContext/ambient tenant passed - a MassTransit consumer scope never has one.
        var repository = new RustServerRepository(context);

        var changedAt = DateTimeOffset.UtcNow;
        var applied = await repository.TryApplyConnectionStatusAsync(
            server.Id, RconConnectionStatus.Connected, detail: null, changedAt);

        Assert.True(applied);

        await using var verifyContext = CreateContext();
        var reloaded = await verifyContext.Set<RustServer>().AcrossAllTenants()
            .FirstAsync(s => s.Id == server.Id);

        Assert.Equal(RconConnectionStatus.Connected, reloaded.ConnectionStatus);
        // Postgres round-trips DateTimeOffset at whole-microsecond precision - compare via UtcTicks
        // rather than exact equality, since .NET's own DateTimeOffset carries finer-than-microsecond
        // ticks that Postgres's timestamptz column can't hold.
        Assert.Equal(changedAt.UtcTicks / 10, reloaded.ConnectionStatusChangedAtUtc!.Value.UtcTicks / 10);
    }

    [Fact]
    public async Task AnUnknownServerIdIsNotApplied()
    {
        await using var context = CreateContext();
        var repository = new RustServerRepository(context);

        var applied = await repository.TryApplyConnectionStatusAsync(
            Guid.NewGuid(), RconConnectionStatus.Connected, detail: null, DateTimeOffset.UtcNow);

        Assert.False(applied);
    }

    /// <summary>
    /// See <see cref="RustServerRepository.TryApplyConnectionStatusAsync"/>'s remarks - two transitions
    /// published moments apart can be consumed out of order, and the older one must not clobber a
    /// newer status that already landed.
    /// </summary>
    [Fact]
    public async Task AnOlderTransitionDoesNotOverwriteANewerOne()
    {
        await using var context = CreateContext();
        var server = await SeedServerAsync(context);
        var repository = new RustServerRepository(context);

        var newer = DateTimeOffset.UtcNow;
        var older = newer.AddSeconds(-5);

        Assert.True(await repository.TryApplyConnectionStatusAsync(
            server.Id, RconConnectionStatus.Connected, detail: null, newer));

        var applied = await repository.TryApplyConnectionStatusAsync(
            server.Id, RconConnectionStatus.Connecting, detail: null, older);

        Assert.False(applied);

        await using var verifyContext = CreateContext();
        var reloaded = await verifyContext.Set<RustServer>().AcrossAllTenants()
            .FirstAsync(s => s.Id == server.Id);
        Assert.Equal(RconConnectionStatus.Connected, reloaded.ConnectionStatus);
    }

    /// <summary>
    /// See the [MaxLength(200)] remark on <see cref="RustServer.ConnectionStatusDetail"/> and
    /// <see cref="RustServerRepository.TryApplyConnectionStatusAsync"/>'s own remarks - an over-length
    /// detail used to reach Postgres directly through the raw UPDATE and throw, unhandled, taking the
    /// Logs tab entry down with it.
    /// </summary>
    [Fact]
    public async Task AnOverlyLongDetailIsTruncatedRatherThanFailing()
    {
        await using var context = CreateContext();
        var server = await SeedServerAsync(context);
        var repository = new RustServerRepository(context);

        var longDetail = new string('x', 500);

        var applied = await repository.TryApplyConnectionStatusAsync(
            server.Id, RconConnectionStatus.Error, longDetail, DateTimeOffset.UtcNow);

        Assert.True(applied);

        await using var verifyContext = CreateContext();
        var reloaded = await verifyContext.Set<RustServer>().AcrossAllTenants()
            .FirstAsync(s => s.Id == server.Id);

        Assert.NotNull(reloaded.ConnectionStatusDetail);
        Assert.True(reloaded.ConnectionStatusDetail!.Length <= 200);
        Assert.EndsWith("...", reloaded.ConnectionStatusDetail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Regression test for a real, reproducible production bug unrelated to the class's main subject
    /// above (reuses the same Postgres fixture purely because it's the only real relational provider
    /// available in this project - see the class remarks) - <c>IX_RustServer_TenantId_Name</c> used to
    /// be a plain, unfiltered unique index, so a soft-deleted server's row (still physically present,
    /// <see cref="RustServer.DeletedOn"/> set) blocked ever adding a new server under that same name
    /// again: delete a server, try to re-add one with the same name, and the insert failed with a raw
    /// Postgres 23505 unique-violation surfacing all the way up as an unhandled 500. Fixed in
    /// <c>ApiDbContext.OnModelCreating</c> by filtering the index to <c>DeletedOn IS NULL</c>, same
    /// technique already used for Role/Plan/Subscription's own partial unique indexes.
    /// </summary>
    [Fact]
    public async Task ANameFreedByASoftDeleteCanBeReusedByANewServer()
    {
        await using var context = CreateContext();
        var deleted = await SeedServerAsync(context);
        deleted.Name = "Reusable Name";
        deleted.DeletedOn = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync();

        var reused = new RustServer
        {
            Id = Guid.NewGuid(),
            TenantId = deleted.TenantId,
            Name = "Reusable Name",
            Host = "192.0.2.20",
            Port = 28016,
            RconPassword = "unused"
        };
        context.Set<RustServer>().Add(reused);

        // The point of the test: this must not throw DbUpdateException/23505.
        await context.SaveChangesAsync();

        await using var verifyContext = CreateContext();
        var liveCount = await verifyContext.Set<RustServer>()
            .Where(s => s.TenantId == deleted.TenantId && s.Name == "Reusable Name")
            .CountAsync();
        Assert.Equal(1, liveCount);
    }
}
