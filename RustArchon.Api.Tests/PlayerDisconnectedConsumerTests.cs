// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Moq;
using RustArchon.Api.Data;
using RustArchon.Api.Hubs;
using RustArchon.Api.Messaging;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Tests;

/// <summary>
/// End-to-end regression test for the ADR-018 gap in <see cref="PlayerSessionRepository.GetOpenSessionAsync"/>
/// (see <see cref="PlayerSessionRepositoryTests"/>) - runs the real repository, not a mock, so this
/// actually exercises the same "no ambient tenant" path <see cref="PlayerDisconnectedConsumer"/> hits in
/// production, rather than a mock that could keep passing even if the repository regressed again.
/// </summary>
public class PlayerDisconnectedConsumerTests
{
    private static Mock<ConsumeContext<PlayerDisconnected>> CreateContext(PlayerDisconnected message)
    {
        var context = new Mock<ConsumeContext<PlayerDisconnected>>();
        context.Setup(c => c.Message).Returns(message);
        context.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
        return context;
    }

    private static Mock<IHubContext<RconHub>> CreateHub()
    {
        var group = new Mock<IClientProxy>();
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(group.Object);
        var hub = new Mock<IHubContext<RconHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        return hub;
    }

    [Fact]
    public async Task ADisconnectClosesTheSessionItsMatchingConnectOpened()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(dbName).Options;

        var serverId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        const string steamId = "76561198000000005";

        await using (var seedContext = new ApiDbContext(options))
        {
            seedContext.Set<PlayerSession>().Add(new PlayerSession
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                RustServerId = serverId,
                SteamId = steamId,
                DisplayName = "Test Player",
                ConnectedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
                DisconnectedAtUtc = null
            });
            await seedContext.SaveChangesAsync();
        }

        await using var context = new ApiDbContext(options);
        // No IUserContext - matches the real, un-mocked repository a MassTransit consumer actually
        // gets injected in production (no ambient tenant either way).
        var repository = new PlayerSessionRepository(context);
        var consumer = new PlayerDisconnectedConsumer(repository, CreateHub().Object);

        var disconnectedAt = DateTimeOffset.UtcNow;
        var message = new PlayerDisconnected(serverId, tenantId, steamId, disconnectedAt);

        await consumer.Consume(CreateContext(message).Object);

        var session = await context.Set<PlayerSession>().IgnoreQueryFilters().SingleAsync(s => s.SteamId == steamId);
        Assert.NotNull(session.DisconnectedAtUtc);
        Assert.Equal(disconnectedAt, session.DisconnectedAtUtc);
    }
}
