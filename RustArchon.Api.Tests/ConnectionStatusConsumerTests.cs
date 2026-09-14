// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Moq;
using RustArchon.Api.Data;
using RustArchon.Api.Hubs;
using RustArchon.Api.Messaging;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="ConnectionStatusConsumer"/> - that a real, newly-applied transition is both
/// logged and relayed, and that a transition <see cref="IRustServerRepository.TryApplyConnectionStatusAsync"/>
/// reports as not-applied produces neither. The repository itself is mocked here, so these don't cover
/// the ADR-018 regression (see <see cref="RustServerRepositoryTests"/> for that) - what these guard is
/// the consumer's own "nothing was applied, so relay nothing" shape from ever regressing into "always
/// relay nothing" again, whatever the reason a future change might give it.
/// </summary>
public class ConnectionStatusConsumerTests
{
    private static readonly Guid ServerId = Guid.NewGuid();
    private static readonly Guid TenantId = Guid.NewGuid();

    private static ConnectionStatusChanged Message(
        RconConnectionStatus status = RconConnectionStatus.Connected, string? detail = null) =>
        new(ServerId, TenantId, status, detail, DateTimeOffset.UtcNow);

    private static Mock<ConsumeContext<ConnectionStatusChanged>> CreateContext(ConnectionStatusChanged message)
    {
        var context = new Mock<ConsumeContext<ConnectionStatusChanged>>();
        context.Setup(c => c.Message).Returns(message);
        context.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
        return context;
    }

    /// <summary>Mocks the two-hop <c>Clients.Group(...).SendAsync(...)</c> chain SignalR needs.</summary>
    private static (Mock<IHubContext<RconHub>> Hub, Mock<IClientProxy> Group) CreateHub()
    {
        var group = new Mock<IClientProxy>();
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(group.Object);

        var hub = new Mock<IHubContext<RconHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        return (hub, group);
    }

    [Fact]
    public async Task ARealTransitionIsLoggedAndRelayedOnBothChannels()
    {
        var repository = new Mock<IRustServerRepository>();
        repository.Setup(r => r.TryApplyConnectionStatusAsync(
                ServerId, It.IsAny<RconConnectionStatus>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(true);

        var connectionLog = new Mock<IConnectionLogRepository>();
        var (hub, group) = CreateHub();

        var consumer = new ConnectionStatusConsumer(repository.Object, connectionLog.Object, hub.Object);
        var message = Message(RconConnectionStatus.Connected);

        await consumer.Consume(CreateContext(message).Object);

        connectionLog.Verify(r => r.AddAsync(It.Is<ConnectionLogEntry>(e =>
            e.RustServerId == ServerId
            && e.TenantId == TenantId
            && e.Status == RconConnectionStatus.Connected)), Times.Once);

        group.Verify(g => g.SendCoreAsync(
            "ReceiveStatusChanged", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Once);
        group.Verify(g => g.SendCoreAsync(
            "ReceiveLogEntry", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The exact behavior that made the ADR-018 regression so silent: a not-applied transition is
    /// legitimately supposed to produce nothing (a stale, out-of-order message, or a server that no
    /// longer exists) - so this same guard has to stay this narrow, not swallow every transition.
    /// </summary>
    [Fact]
    public async Task ATransitionTheRepositoryDidNotApplyIsNeitherLoggedNorRelayed()
    {
        var repository = new Mock<IRustServerRepository>();
        repository.Setup(r => r.TryApplyConnectionStatusAsync(
                ServerId, It.IsAny<RconConnectionStatus>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(false);

        var connectionLog = new Mock<IConnectionLogRepository>();
        var (hub, group) = CreateHub();

        var consumer = new ConnectionStatusConsumer(repository.Object, connectionLog.Object, hub.Object);

        await consumer.Consume(CreateContext(Message()).Object);

        connectionLog.Verify(r => r.AddAsync(It.IsAny<ConnectionLogEntry>()), Times.Never);
        group.Verify(g => g.SendCoreAsync(
            It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ADisconnectedTransitionWithNoDetailFallsBackToADefaultMessage()
    {
        var repository = new Mock<IRustServerRepository>();
        repository.Setup(r => r.TryApplyConnectionStatusAsync(
                ServerId, It.IsAny<RconConnectionStatus>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(true);

        ConnectionLogEntry? captured = null;
        var connectionLog = new Mock<IConnectionLogRepository>();
        connectionLog.Setup(r => r.AddAsync(It.IsAny<ConnectionLogEntry>()))
            .Returns<ConnectionLogEntry>(e =>
            {
                captured = e;
                return Task.FromResult(e);
            });

        var (hub, _) = CreateHub();
        var consumer = new ConnectionStatusConsumer(repository.Object, connectionLog.Object, hub.Object);

        await consumer.Consume(CreateContext(Message(RconConnectionStatus.Disconnected, detail: null)).Object);

        Assert.NotNull(captured);
        Assert.Equal("Disconnected", captured!.Message);
        Assert.Equal(ConnectionLogLevel.Warning, captured.Level);
    }
}
