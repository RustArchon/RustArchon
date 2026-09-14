// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="PlayerSessionRepository.GetOpenSessionAsync"/>/<see cref="PlayerSessionRepository.GetOpenSessionsAsync"/>
/// - the same ADR-018 regression as <see cref="RustServerRepositoryTests"/>, this time on the two
/// methods <c>PlayerConnectedConsumer</c>/<c>PlayerDisconnectedConsumer</c>/
/// <c>PlayerSessionSnapshotUpdatedConsumer</c> call with no ambient tenant. Confirmed live: a
/// disconnect could never find the session its own matching connect had just opened, so it never
/// closed - every player session was silently stuck open forever.
/// </summary>
public class PlayerSessionRepositoryTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    private async Task<PlayerSession> SeedOpenSessionAsync(ApiDbContext context, Guid serverId, string steamId)
    {
        var session = new PlayerSession
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            RustServerId = serverId,
            SteamId = steamId,
            DisplayName = "Test Player",
            ConnectedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            DisconnectedAtUtc = null
        };
        context.Set<PlayerSession>().Add(session);
        await context.SaveChangesAsync();
        return session;
    }

    [Fact]
    public async Task GetOpenSessionFindsItWithNoAmbientTenant()
    {
        await using var context = CreateContext();
        var serverId = Guid.NewGuid();
        var session = await SeedOpenSessionAsync(context, serverId, "76561198000000001");

        // No IUserContext - a MassTransit consumer scope never has one.
        var repository = new PlayerSessionRepository(context);

        var found = await repository.GetOpenSessionAsync(serverId, "76561198000000001");

        Assert.NotNull(found);
        Assert.Equal(session.Id, found!.Id);
    }

    [Fact]
    public async Task GetOpenSessionsFindsThemWithNoAmbientTenant()
    {
        await using var context = CreateContext();
        var serverId = Guid.NewGuid();
        await SeedOpenSessionAsync(context, serverId, "76561198000000002");

        var repository = new PlayerSessionRepository(context);

        var found = await repository.GetOpenSessionsAsync(serverId, "76561198000000002");

        Assert.Single(found);
    }

    [Fact]
    public async Task GetOpenSessionReturnsNullForAnAlreadyClosedSession()
    {
        await using var context = CreateContext();
        var serverId = Guid.NewGuid();
        var session = await SeedOpenSessionAsync(context, serverId, "76561198000000003");
        session.DisconnectedAtUtc = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync();

        var repository = new PlayerSessionRepository(context);

        Assert.Null(await repository.GetOpenSessionAsync(serverId, "76561198000000003"));
    }
}
