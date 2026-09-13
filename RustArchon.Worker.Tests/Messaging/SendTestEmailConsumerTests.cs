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
/// Tests for <see cref="SendTestEmailConsumer"/> - specifically that a provider standing in for "this
/// deployment won't really send" (<see cref="NoOpEmailDeliveryProvider"/>,
/// <see cref="SuppressedEmailDeliveryProvider"/>) is reported back as a failure rather than a
/// misleadingly successful test send. See that class's own remarks for why.
/// </summary>
public class SendTestEmailConsumerTests
{
    private static readonly SendTestEmail Request = new("test@example.com", "Test", "<p>Test</p>");

    private static InternalEmailSettings AnySettings(string serviceProvider = EmailProviders.SendGrid) => new(
        ServiceProvider: serviceProvider,
        SmtpHost: "",
        SmtpPort: 0,
        SmtpEnableSsl: false,
        SmtpUsername: "",
        SmtpPassword: "",
        ApiKey: "SG.real-key",
        DefaultFromAddress: "noreply@example.com",
        DefaultFromName: "RustArchon");

    private static (Mock<ConsumeContext<SendTestEmail>> Context, Func<SendTestEmailResult?> Captured) CreateContext()
    {
        SendTestEmailResult? captured = null;
        var context = new Mock<ConsumeContext<SendTestEmail>>();
        context.Setup(c => c.Message).Returns(Request);
        context.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
        context
            .Setup(c => c.RespondAsync(It.IsAny<SendTestEmailResult>()))
            .Callback<SendTestEmailResult>(r => captured = r)
            .Returns(Task.CompletedTask);

        return (context, () => captured);
    }

    private static SendTestEmailConsumer CreateConsumer(
        Mock<IInternalApiClient> apiClient, IEmailDeliveryProvider provider, InternalEmailSettings settings)
    {
        apiClient.Setup(c => c.GetEmailSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);

        var providerFactory = new Mock<IEmailDeliveryProviderFactory>();
        providerFactory.Setup(f => f.Resolve(settings)).Returns(provider);

        return new SendTestEmailConsumer(apiClient.Object, providerFactory.Object, NullLogger<SendTestEmailConsumer>.Instance);
    }

    [Fact]
    public async Task ReportsFailureWithoutSendingWhenDeliveryIsSuppressed()
    {
        var settings = AnySettings();
        var consumer = CreateConsumer(
            new Mock<IInternalApiClient>(), new SuppressedEmailDeliveryProvider(NullLogger<SuppressedEmailDeliveryProvider>.Instance), settings);
        var (context, captured) = CreateContext();

        await consumer.Consume(context.Object);

        var result = captured();
        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Contains("RUSTARCHON_SUPPRESS_EMAIL_DELIVERY", result.Error);
        Assert.Equal("Suppressed (dev mode)", result.ProviderName);
    }

    [Fact]
    public async Task ReportsFailureWhenNoProviderIsConfigured()
    {
        var settings = AnySettings(serviceProvider: "");
        var consumer = CreateConsumer(
            new Mock<IInternalApiClient>(), new NoOpEmailDeliveryProvider(NullLogger<NoOpEmailDeliveryProvider>.Instance), settings);
        var (context, captured) = CreateContext();

        await consumer.Consume(context.Object);

        var result = captured();
        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Equal("None", result.ProviderName);
    }

    [Fact]
    public async Task ReportsSuccessThroughARealProvider()
    {
        var settings = AnySettings();
        var realProvider = new Mock<IEmailDeliveryProvider>();
        realProvider.Setup(p => p.ProviderName).Returns("SendGrid");
        realProvider.Setup(p => p.SendEmailAsync(It.IsAny<EmailMessage>())).ReturnsAsync(true);

        var consumer = CreateConsumer(new Mock<IInternalApiClient>(), realProvider.Object, settings);
        var (context, captured) = CreateContext();

        await consumer.Consume(context.Object);

        var result = captured();
        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Equal("SendGrid", result.ProviderName);
    }
}
