// Copyright ©2026 Scott Blomfield

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Messaging.Contracts;
using RustArchon.Worker.Messaging;
using RustArchon.Worker.Security;
using RustArchon.Worker.Ticketing;

namespace RustArchon.Worker.Tests.Messaging;

/// <summary>
/// Tests for <see cref="TicketEventConsumer"/> - that it resolves a provider from freshly-fetched
/// settings and hands each event to the right <see cref="ITicketingIntegrationProvider"/> method, the
/// same "owns none of the actual delivery logic" split <see cref="EmailRequestedConsumerTests"/> checks
/// for <see cref="RustArchon.Worker.Messaging.EmailRequestedConsumer"/>.
/// </summary>
public class TicketEventConsumerTests
{
    private static InternalTicketingSettings AnySettings() =>
        new(TicketingProviders.Webhook, "https://example.com/hooks", "secret");

    private static Mock<ConsumeContext<TicketCreated>> CreateContext(TicketCreated message)
    {
        var context = new Mock<ConsumeContext<TicketCreated>>();
        context.Setup(c => c.Message).Returns(message);
        context.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
        return context;
    }

    private static Mock<ConsumeContext<TicketMessageAdded>> CreateContext(TicketMessageAdded message)
    {
        var context = new Mock<ConsumeContext<TicketMessageAdded>>();
        context.Setup(c => c.Message).Returns(message);
        context.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
        return context;
    }

    [Fact]
    public async Task TicketCreated_NotifiesTheResolvedProviderWithTheTicketId()
    {
        var ticketId = Guid.NewGuid();

        var apiClient = new Mock<IInternalApiClient>();
        apiClient.Setup(c => c.GetTicketingSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(AnySettings());

        var provider = new Mock<ITicketingIntegrationProvider>();
        var providerFactory = new Mock<ITicketingIntegrationProviderFactory>();
        providerFactory.Setup(f => f.Resolve(It.IsAny<InternalTicketingSettings>())).Returns(provider.Object);

        var consumer = new TicketEventConsumer(
            apiClient.Object, providerFactory.Object, NullLogger<TicketEventConsumer>.Instance);

        await consumer.Consume(CreateContext(new TicketCreated(ticketId)).Object);

        provider.Verify(p => p.NotifyTicketCreatedAsync(ticketId), Times.Once);
        provider.Verify(p => p.NotifyTicketMessageAddedAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task TicketMessageAdded_NotifiesTheResolvedProviderWithBothIds()
    {
        var ticketId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        var apiClient = new Mock<IInternalApiClient>();
        apiClient.Setup(c => c.GetTicketingSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(AnySettings());

        var provider = new Mock<ITicketingIntegrationProvider>();
        var providerFactory = new Mock<ITicketingIntegrationProviderFactory>();
        providerFactory.Setup(f => f.Resolve(It.IsAny<InternalTicketingSettings>())).Returns(provider.Object);

        var consumer = new TicketEventConsumer(
            apiClient.Object, providerFactory.Object, NullLogger<TicketEventConsumer>.Instance);

        await consumer.Consume(CreateContext(new TicketMessageAdded(ticketId, messageId)).Object);

        provider.Verify(p => p.NotifyTicketMessageAddedAsync(ticketId, messageId), Times.Once);
        provider.Verify(p => p.NotifyTicketCreatedAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task AFailingProvider_PropagatesSoTheReceiveEndpointsRetryPolicyCanCatchIt()
    {
        var apiClient = new Mock<IInternalApiClient>();
        apiClient.Setup(c => c.GetTicketingSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(AnySettings());

        var provider = new Mock<ITicketingIntegrationProvider>();
        provider.Setup(p => p.NotifyTicketCreatedAsync(It.IsAny<Guid>()))
            .ThrowsAsync(new HttpRequestException("webhook endpoint unreachable"));

        var providerFactory = new Mock<ITicketingIntegrationProviderFactory>();
        providerFactory.Setup(f => f.Resolve(It.IsAny<InternalTicketingSettings>())).Returns(provider.Object);

        var consumer = new TicketEventConsumer(
            apiClient.Object, providerFactory.Object, NullLogger<TicketEventConsumer>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => consumer.Consume(CreateContext(new TicketCreated(Guid.NewGuid())).Object));
    }
}
