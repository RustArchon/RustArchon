// Copyright ©2026 Scott Blomfield

using MassTransit;
using Moq;
using RustArchon.Api.DiagnosticLogs;

namespace RustArchon.Api.Tests;

/// <summary>
/// The queue the Worker's log batches arrive on. A first version declared it non-durable and non-exclusive, which RabbitMQ 4 refuses
/// (INTERNAL_ERROR, deprecated feature transient_nonexcl_queues): it worked on an older broker and failed on production's, so no batch ever arrived.
/// </summary>
public class WorkerLogQueueTests
{
    [Fact]
    public void TheQueueIsDurableBecauseRabbitMq4RefusesTransientNonExclusiveQueues()
    {
        var endpoint = new Mock<IRabbitMqReceiveEndpointConfigurator>();

        WorkerLogQueue.Configure(endpoint.Object, Mock.Of<IRegistrationContext>());

        endpoint.VerifySet(e => e.Durable = true, Times.Once);
        endpoint.VerifySet(e => e.Durable = false, Times.Never);
        endpoint.VerifySet(e => e.Exclusive = It.IsAny<bool>(), Times.Never);
    }

    [Fact]
    public void StaleBatchesStillExpireAndTheQueueStaysBounded()
    {
        var endpoint = new Mock<IRabbitMqReceiveEndpointConfigurator>();

        WorkerLogQueue.Configure(endpoint.Object, Mock.Of<IRegistrationContext>());

        endpoint.Verify(e => e.SetQueueArgument("x-message-ttl", 120_000), Times.Once);
        endpoint.Verify(e => e.SetQueueArgument("x-max-length", 1_000), Times.Once);
    }

    [Fact]
    public void TheQueueHasANewNameSoAnOlderNonDurableOneOnASomeBrokerCannotClashWithIt()
    {
        Assert.NotEqual("rustarchon-api-worker-logs", WorkerLogQueue.Name);
    }
}
