// Copyright ©2026 Scott Blomfield

using System;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Worker.Ticketing;

namespace RustArchon.Worker.Tests.Ticketing;

/// <summary>
/// Tests for <see cref="WebhookTicketingIntegrationProvider"/> - specifically that the payload it sends
/// is actually what the <c>X-RustArchon-Signature</c> header signs, since a receiving system trusts the
/// payload only after verifying that signature matches. A mismatch here would silently make every
/// webhook delivery fail verification on the far end while still returning 200 from this provider's own
/// point of view - worth a real, non-mocked HMAC check rather than trusting the implementation by
/// inspection.
/// </summary>
public class WebhookTicketingIntegrationProviderTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }
        public HttpStatusCode ResponseStatusCode { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(ResponseStatusCode) { Content = new StringContent("") };
        }
    }

    private static string ComputeSignature(string body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public async Task TicketCreated_PostsAPayloadWhoseBodyMatchesItsOwnSignatureHeader()
    {
        var handler = new CapturingHandler();
        var httpClient = new HttpClient(handler);
        const string secret = "test-webhook-secret";
        var provider = new WebhookTicketingIntegrationProvider(
            NullLogger<WebhookTicketingIntegrationProvider>.Instance, httpClient,
            "https://example.com/hooks/rustarchon", secret);

        var ticketId = Guid.NewGuid();
        await provider.NotifyTicketCreatedAsync(ticketId);

        Assert.NotNull(handler.Request);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://example.com/hooks/rustarchon", handler.Request.RequestUri!.ToString());

        var signatureHeader = Assert.Single(handler.Request.Headers.GetValues("X-RustArchon-Signature"));
        var expectedSignature = $"sha256={ComputeSignature(handler.Body!, secret)}";
        Assert.Equal(expectedSignature, signatureHeader);

        using var document = JsonDocument.Parse(handler.Body!);
        Assert.Equal("ticket.created", document.RootElement.GetProperty("event").GetString());
        Assert.Equal(ticketId, document.RootElement.GetProperty("ticketId").GetGuid());
    }

    [Fact]
    public async Task TicketMessageAdded_PayloadCarriesBothIds()
    {
        var handler = new CapturingHandler();
        var httpClient = new HttpClient(handler);
        var provider = new WebhookTicketingIntegrationProvider(
            NullLogger<WebhookTicketingIntegrationProvider>.Instance, httpClient,
            "https://example.com/hooks/rustarchon", "test-webhook-secret");

        var ticketId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        await provider.NotifyTicketMessageAddedAsync(ticketId, messageId);

        using var document = JsonDocument.Parse(handler.Body!);
        Assert.Equal("ticket.message_added", document.RootElement.GetProperty("event").GetString());
        Assert.Equal(ticketId, document.RootElement.GetProperty("ticketId").GetGuid());
        Assert.Equal(messageId, document.RootElement.GetProperty("messageId").GetGuid());
    }

    [Fact]
    public async Task ANonSuccessResponse_ThrowsSoTheReceiveEndpointsRetryPolicyKicksIn()
    {
        var handler = new CapturingHandler { ResponseStatusCode = HttpStatusCode.InternalServerError };
        var httpClient = new HttpClient(handler);
        var provider = new WebhookTicketingIntegrationProvider(
            NullLogger<WebhookTicketingIntegrationProvider>.Instance, httpClient,
            "https://example.com/hooks/rustarchon", "test-webhook-secret");

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.NotifyTicketCreatedAsync(Guid.NewGuid()));
    }
}
