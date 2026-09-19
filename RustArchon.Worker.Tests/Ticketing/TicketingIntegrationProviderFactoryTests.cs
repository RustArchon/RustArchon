// Copyright ©2026 Scott Blomfield

using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Worker.Security;
using RustArchon.Worker.Ticketing;

namespace RustArchon.Worker.Tests.Ticketing;

/// <summary>Tests for <see cref="TicketingIntegrationProviderFactory"/>.</summary>
public class TicketingIntegrationProviderFactoryTests
{
    private static TicketingIntegrationProviderFactory CreateFactory() =>
        new(
            NullLogger<WebhookTicketingIntegrationProvider>.Instance,
            NullLogger<InternalNoOpTicketingIntegrationProvider>.Instance,
            Mock.Of<IHttpClientFactory>(c => c.CreateClient(It.IsAny<string>()) == new HttpClient()));

    [Fact]
    public void InternalProvider_ResolvesToNoOp()
    {
        var settings = new InternalTicketingSettings(TicketingProviders.Internal, "", "");

        var provider = CreateFactory().Resolve(settings);

        Assert.IsType<InternalNoOpTicketingIntegrationProvider>(provider);
    }

    [Fact]
    public void WebhookProvider_WithUrlAndSecret_ResolvesToWebhookProvider()
    {
        var settings = new InternalTicketingSettings(
            TicketingProviders.Webhook, "https://example.com/hooks/rustarchon", "a-real-secret");

        var provider = CreateFactory().Resolve(settings);

        Assert.IsType<WebhookTicketingIntegrationProvider>(provider);
    }

    /// <summary>
    /// A half-configured Webhook choice (the admin picked it but hasn't filled in the URL/secret yet,
    /// or cleared one after the fact) must not resolve to a provider that would throw on every attempt
    /// - falling back to NoOp until both fields are actually set is the safer default.
    /// </summary>
    [Fact]
    public void WebhookProviderWithNoUrlConfigured_FallsBackToNoOp()
    {
        var settings = new InternalTicketingSettings(TicketingProviders.Webhook, "", "a-real-secret");

        var provider = CreateFactory().Resolve(settings);

        Assert.IsType<InternalNoOpTicketingIntegrationProvider>(provider);
    }

    [Fact]
    public void WebhookProviderWithNoSecretConfigured_FallsBackToNoOp()
    {
        var settings = new InternalTicketingSettings(
            TicketingProviders.Webhook, "https://example.com/hooks/rustarchon", "");

        var provider = CreateFactory().Resolve(settings);

        Assert.IsType<InternalNoOpTicketingIntegrationProvider>(provider);
    }
}
