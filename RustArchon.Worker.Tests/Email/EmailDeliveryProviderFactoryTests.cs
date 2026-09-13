// Copyright ©2026 Scott Blomfield

using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Worker.Email;
using RustArchon.Worker.Security;

namespace RustArchon.Worker.Tests.Email;

/// <summary>
/// Tests for <see cref="EmailDeliveryProviderFactory"/> - in particular the
/// <see cref="EmailDeliveryOptions.SuppressDelivery"/> override, since a deployment-level kill switch
/// silently doing nothing (or only suppressing some provider choices and not others) is exactly the
/// kind of bug that would go unnoticed until a real send slipped through.
/// </summary>
public class EmailDeliveryProviderFactoryTests
{
    private static EmailDeliveryProviderFactory CreateFactory(bool suppressDelivery) =>
        new(
            NullLogger<SmtpEmailDeliveryProvider>.Instance,
            NullLogger<SendGridEmailDeliveryProvider>.Instance,
            NullLogger<ResendEmailDeliveryProvider>.Instance,
            NullLogger<NoOpEmailDeliveryProvider>.Instance,
            NullLogger<SuppressedEmailDeliveryProvider>.Instance,
            Mock.Of<IHttpClientFactory>(),
            new EmailDeliveryOptions(suppressDelivery));

    private static InternalEmailSettings SmtpSettings() => new(
        ServiceProvider: EmailProviders.Smtp,
        SmtpHost: "smtp.example.com",
        SmtpPort: 587,
        SmtpEnableSsl: true,
        SmtpUsername: "user",
        SmtpPassword: "pass",
        ApiKey: "",
        DefaultFromAddress: "noreply@example.com",
        DefaultFromName: "RustArchon");

    private static InternalEmailSettings SendGridSettings() => new(
        ServiceProvider: EmailProviders.SendGrid,
        SmtpHost: "",
        SmtpPort: 0,
        SmtpEnableSsl: false,
        SmtpUsername: "",
        SmtpPassword: "",
        ApiKey: "SG.real-key",
        DefaultFromAddress: "noreply@example.com",
        DefaultFromName: "RustArchon");

    private static InternalEmailSettings UnconfiguredSettings() => new(
        ServiceProvider: "",
        SmtpHost: "",
        SmtpPort: 0,
        SmtpEnableSsl: false,
        SmtpUsername: "",
        SmtpPassword: "",
        ApiKey: "",
        DefaultFromAddress: "noreply@example.com",
        DefaultFromName: "RustArchon");

    [Fact]
    public void SuppressedOverrideWinsEvenWithAFullyConfiguredRealProvider()
    {
        var provider = CreateFactory(suppressDelivery: true).Resolve(SendGridSettings());

        Assert.IsType<SuppressedEmailDeliveryProvider>(provider);
    }

    [Fact]
    public void SuppressedOverrideWinsOverSmtpToo()
    {
        var provider = CreateFactory(suppressDelivery: true).Resolve(SmtpSettings());

        Assert.IsType<SuppressedEmailDeliveryProvider>(provider);
    }

    [Fact]
    public async Task SuppressedProviderReportsSuccessSoTheRetryPolicyDoesNotKeepHammeringIt()
    {
        var provider = CreateFactory(suppressDelivery: true).Resolve(SendGridSettings());

        var sent = await provider.SendEmailAsync(new EmailMessage
        {
            To = "test@example.com",
            From = "noreply@example.com",
            Subject = "Your invoice",
            Body = "<p>...</p>",
            IsHtml = true
        });

        Assert.True(sent);
    }

    [Fact]
    public void WithTheOverrideOffARealProviderIsStillResolvedNormally()
    {
        var provider = CreateFactory(suppressDelivery: false).Resolve(SendGridSettings());

        Assert.IsType<SendGridEmailDeliveryProvider>(provider);
    }

    [Fact]
    public void WithTheOverrideOffAnUnconfiguredPlatformStillFallsBackToNoOp()
    {
        var provider = CreateFactory(suppressDelivery: false).Resolve(UnconfiguredSettings());

        Assert.IsType<NoOpEmailDeliveryProvider>(provider);
    }
}
