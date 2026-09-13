// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Data;
using RustArchon.Api.Messaging;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="CommunicationDeliveredConsumer"/> - in particular that
/// <see cref="CommunicationDelivered.Suppressed"/> lands on its own <see cref="CommunicationStatus.Suppressed"/>
/// status rather than being folded into <see cref="CommunicationStatus.Sent"/>, even though a
/// suppressed send also reports <see cref="CommunicationDelivered.Success"/> as true. See
/// <c>SuppressedEmailDeliveryProvider</c>'s remarks in RustArchon.Worker for why the distinction
/// matters.
/// </summary>
public class CommunicationDeliveredConsumerTests
{
    private static Communication QueuedCommunication() => new()
    {
        Id = Guid.NewGuid(),
        ToAddress = "test@example.com",
        Subject = "Your invoice",
        HtmlBody = "<p>...</p>",
        Status = CommunicationStatus.Queued,
        QueuedOn = DateTimeOffset.UtcNow
    };

    private static Mock<ConsumeContext<CommunicationDelivered>> CreateContext(CommunicationDelivered message)
    {
        var context = new Mock<ConsumeContext<CommunicationDelivered>>();
        context.Setup(c => c.Message).Returns(message);
        context.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
        return context;
    }

    [Fact]
    public async Task ASuppressedDeliveryIsRecordedAsSuppressedNotSent()
    {
        var communication = QueuedCommunication();
        var repository = new Mock<ICommunicationRepository>();
        repository.Setup(r => r.GetByIdAcrossTenantsAsync(communication.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(communication);

        var consumer = new CommunicationDeliveredConsumer(repository.Object, NullLogger<CommunicationDeliveredConsumer>.Instance);
        var message = new CommunicationDelivered(communication.Id, Success: true, Error: null, Suppressed: true);

        await consumer.Consume(CreateContext(message).Object);

        Assert.Equal(CommunicationStatus.Suppressed, communication.Status);
        Assert.NotNull(communication.SuppressedOn);
        Assert.Null(communication.SentOn);
        repository.Verify(r => r.SaveAsync(communication, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AnOrdinarySuccessfulDeliveryIsStillRecordedAsSent()
    {
        var communication = QueuedCommunication();
        var repository = new Mock<ICommunicationRepository>();
        repository.Setup(r => r.GetByIdAcrossTenantsAsync(communication.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(communication);

        var consumer = new CommunicationDeliveredConsumer(repository.Object, NullLogger<CommunicationDeliveredConsumer>.Instance);
        var message = new CommunicationDelivered(communication.Id, Success: true, Error: null, Suppressed: false);

        await consumer.Consume(CreateContext(message).Object);

        Assert.Equal(CommunicationStatus.Sent, communication.Status);
        Assert.NotNull(communication.SentOn);
        Assert.Null(communication.SuppressedOn);
    }

    [Fact]
    public async Task AFailedDeliveryIsStillRecordedAsBounced()
    {
        var communication = QueuedCommunication();
        var repository = new Mock<ICommunicationRepository>();
        repository.Setup(r => r.GetByIdAcrossTenantsAsync(communication.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(communication);

        var consumer = new CommunicationDeliveredConsumer(repository.Object, NullLogger<CommunicationDeliveredConsumer>.Instance);
        var message = new CommunicationDelivered(communication.Id, Success: false, Error: "SMTP rejected", Suppressed: false);

        await consumer.Consume(CreateContext(message).Object);

        Assert.Equal(CommunicationStatus.Bounced, communication.Status);
        Assert.Equal("SMTP rejected", communication.FailureReason);
        Assert.Null(communication.SuppressedOn);
    }
}
