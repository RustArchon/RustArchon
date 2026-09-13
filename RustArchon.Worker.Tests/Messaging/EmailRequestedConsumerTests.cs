// Copyright ©2026 Scott Blomfield

using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Messaging.Contracts;
using RustArchon.Worker.Email;
using RustArchon.Worker.Messaging;
using RustArchon.Worker.Security;

namespace RustArchon.Worker.Tests.Messaging;

/// <summary>
/// Tests for <see cref="EmailRequestedConsumer"/> - specifically that it tells
/// <c>RustArchon.Api</c>'s <see cref="CommunicationDelivered"/> handler apart a suppressed send from an
/// ordinary one, since both report <see cref="CommunicationDelivered.Success"/> as true. See
/// <see cref="SuppressedEmailDeliveryProvider"/>'s remarks.
/// </summary>
public class EmailRequestedConsumerTests
{
    private static readonly EmailRequested Request = new(System.Guid.NewGuid(), "test@example.com", "Your invoice", "<p>...</p>");

    private static InternalEmailSettings AnySettings() => new(
        ServiceProvider: EmailProviders.SendGrid,
        SmtpHost: "",
        SmtpPort: 0,
        SmtpEnableSsl: false,
        SmtpUsername: "",
        SmtpPassword: "",
        ApiKey: "SG.real-key",
        DefaultFromAddress: "noreply@example.com",
        DefaultFromName: "RustArchon");

    private static Mock<ConsumeContext<EmailRequested>> CreateContext()
    {
        var context = new Mock<ConsumeContext<EmailRequested>>();
        context.Setup(c => c.Message).Returns(Request);
        context.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
        return context;
    }

    private static async Task<CommunicationDelivered> RunAsync(IEmailDeliveryProvider provider)
    {
        var apiClient = new Mock<IInternalApiClient>();
        apiClient.Setup(c => c.GetEmailSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(AnySettings());

        var providerFactory = new Mock<IEmailDeliveryProviderFactory>();
        providerFactory.Setup(f => f.Resolve(It.IsAny<InternalEmailSettings>())).Returns(provider);

        CommunicationDelivered? published = null;
        var publishEndpoint = new Mock<IPublishEndpoint>();
        publishEndpoint
            .Setup(p => p.Publish(It.IsAny<CommunicationDelivered>(), It.IsAny<CancellationToken>()))
            .Callback<CommunicationDelivered, CancellationToken>((m, _) => published = m)
            .Returns(Task.CompletedTask);

        var consumer = new EmailRequestedConsumer(
            apiClient.Object, providerFactory.Object, publishEndpoint.Object, NullLogger<EmailRequestedConsumer>.Instance);

        await consumer.Consume(CreateContext().Object);

        Assert.NotNull(published);
        return published!;
    }

    [Fact]
    public async Task ReportsSuppressedWhenTheResolvedProviderIsTheSuppressedOne()
    {
        var result = await RunAsync(new SuppressedEmailDeliveryProvider(NullLogger<SuppressedEmailDeliveryProvider>.Instance));

        Assert.True(result.Success);
        Assert.True(result.Suppressed);
    }

    [Fact]
    public async Task DoesNotReportSuppressedForAnOrdinarySuccessfulSend()
    {
        var provider = new Mock<IEmailDeliveryProvider>();
        provider.Setup(p => p.SendEmailAsync(It.IsAny<EmailMessage>())).ReturnsAsync(true);
        provider.Setup(p => p.ProviderName).Returns("SendGrid");

        var result = await RunAsync(provider.Object);

        Assert.True(result.Success);
        Assert.False(result.Suppressed);
    }

    [Fact]
    public async Task DoesNotReportSuppressedForAFailedSend()
    {
        var provider = new Mock<IEmailDeliveryProvider>();
        provider.Setup(p => p.SendEmailAsync(It.IsAny<EmailMessage>())).ReturnsAsync(false);
        provider.Setup(p => p.ProviderName).Returns("SendGrid");

        var result = await RunAsync(provider.Object);

        Assert.False(result.Success);
        Assert.False(result.Suppressed);
    }
}
