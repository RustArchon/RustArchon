// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Data;
using RustArchon.Api.Hubs;
using RustArchon.Api.Mapping;
using RustArchon.Api.Messaging;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="RconFrameIngestionConsumer"/> - specifically which SignalR group(s) an
/// interactive vs. non-interactive <see cref="RconFrameCaptured"/> is relayed to. This is the
/// server-side enforcement the Panel's "unfiltered" toggle depends on: a non-interactive event must
/// never be sent down <see cref="RconHub.GroupName"/> (every ordinary viewer), only
/// <see cref="RconHub.UnfilteredGroupName"/> (a site admin who has independently proven, server-side,
/// that they're allowed in - see <see cref="RconHub.JoinUnfilteredServerGroup"/>).
/// </summary>
public class RconFrameIngestionConsumerTests
{
    private static readonly Guid ServerId = Guid.NewGuid();
    private static readonly Guid TenantId = Guid.NewGuid();

    private static IMapper CreateMapper() =>
        new MapperConfiguration(cfg => cfg.AddProfile<RconEventMappingProfile>(), NullLoggerFactory.Instance)
            .CreateMapper();

    private static RconFrameCaptured Message(bool interactive, RconEventDirection direction = RconEventDirection.Received) =>
        new(ServerId, TenantId, DateTimeOffset.UtcNow, Identifier: 1, Type: string.Empty, "message", Stacktrace: null, interactive, direction);

    private static Mock<ConsumeContext<RconFrameCaptured>> CreateContext(RconFrameCaptured message)
    {
        var context = new Mock<ConsumeContext<RconFrameCaptured>>();
        context.Setup(c => c.Message).Returns(message);
        return context;
    }

    /// <summary>Mocks the two-hop <c>Clients.Group(...).SendAsync(...)</c> chain, with a distinct
    /// <see cref="IClientProxy"/> per group name so a test can tell which group(s) actually received
    /// the relay.</summary>
    private static (Mock<IHubContext<RconHub>> Hub, Mock<IClientProxy> OrdinaryGroup, Mock<IClientProxy> UnfilteredGroup) CreateHub()
    {
        var ordinaryGroup = new Mock<IClientProxy>();
        var unfilteredGroup = new Mock<IClientProxy>();

        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(RconHub.GroupName(ServerId))).Returns(ordinaryGroup.Object);
        clients.Setup(c => c.Group(RconHub.UnfilteredGroupName(ServerId))).Returns(unfilteredGroup.Object);

        var hub = new Mock<IHubContext<RconHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        return (hub, ordinaryGroup, unfilteredGroup);
    }

    [Fact]
    public async Task AnInteractiveFrameIsRelayedToBothTheOrdinaryAndUnfilteredGroups()
    {
        var repository = new Mock<IRconEventRepository>();
        var (hub, ordinaryGroup, unfilteredGroup) = CreateHub();
        var consumer = new RconFrameIngestionConsumer(repository.Object, CreateMapper(), hub.Object);

        await consumer.Consume(CreateContext(Message(interactive: true)).Object);

        ordinaryGroup.Verify(g => g.SendCoreAsync("ReceiveEvent", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Once);
        unfilteredGroup.Verify(g => g.SendCoreAsync("ReceiveEvent", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The property this whole design exists to guarantee: a non-interactive frame is never sent to
    /// the group every ordinary viewer belongs to - only to the group <see cref="RconHub"/> gates on
    /// the acting-as-tenant claim.
    /// </summary>
    [Fact]
    public async Task ANonInteractiveFrameIsRelayedOnlyToTheUnfilteredGroupNeverTheOrdinaryOne()
    {
        var repository = new Mock<IRconEventRepository>();
        var (hub, ordinaryGroup, unfilteredGroup) = CreateHub();
        var consumer = new RconFrameIngestionConsumer(repository.Object, CreateMapper(), hub.Object);

        await consumer.Consume(CreateContext(Message(interactive: false)).Object);

        ordinaryGroup.Verify(g => g.SendCoreAsync("ReceiveEvent", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
        unfilteredGroup.Verify(g => g.SendCoreAsync("ReceiveEvent", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EveryFrameIsPersistedRegardlessOfInteractivity()
    {
        var repository = new Mock<IRconEventRepository>();
        var (hub, _, _) = CreateHub();
        var consumer = new RconFrameIngestionConsumer(repository.Object, CreateMapper(), hub.Object);

        await consumer.Consume(CreateContext(Message(interactive: false, RconEventDirection.Sent)).Object);

        repository.Verify(r => r.AddAsync(It.Is<RconEvent>(e =>
            e.RustServerId == ServerId
            && e.TenantId == TenantId
            && !e.Interactive
            && e.Direction == RconEventDirection.Sent)), Times.Once);
    }
}
