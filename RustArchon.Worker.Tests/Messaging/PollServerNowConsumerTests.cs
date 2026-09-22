// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Moq;
using RustArchon.Messaging.Contracts;
using RustArchon.Worker.Connections;
using RustArchon.Worker.Messaging;

namespace RustArchon.Worker.Tests.Messaging;

/// <summary>
/// <see cref="PollServerNowConsumer"/>: the fanout dispatch (same shape as <c>SendRconCommandConsumer</c>) that lets exactly the Worker instance
/// holding a server's connection answer a <see cref="PollServerNow"/> request, while every other instance stays silent.
/// </summary>
public class PollServerNowConsumerTests
{
    private static Mock<ConsumeContext<PollServerNow>> CreateContext(PollServerNow message)
    {
        var context = new Mock<ConsumeContext<PollServerNow>>();
        context.Setup(c => c.Message).Returns(message);
        context.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
        context.Setup(c => c.RespondAsync(It.IsAny<PollServerNowResult>())).Returns(Task.CompletedTask);
        return context;
    }

    [Fact]
    public async Task AnInstanceThatDoesNotOwnTheConnectionNeverResponds()
    {
        var serverId = Guid.NewGuid();
        var supervisor = new Mock<IConnectionSupervisor>();
        ServerConnectionActor? none = null;
        supervisor.Setup(s => s.TryGetActor(serverId, out none)).Returns(false);
        var context = CreateContext(new PollServerNow(serverId, [PollServerNowKinds.Plugins]));

        await new PollServerNowConsumer(supervisor.Object).Consume(context.Object);

        context.Verify(c => c.RespondAsync(It.IsAny<PollServerNowResult>()), Times.Never);
    }
}
