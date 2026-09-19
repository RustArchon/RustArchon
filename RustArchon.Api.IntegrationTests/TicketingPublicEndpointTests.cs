// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.IntegrationTests;

/// <summary>
/// The protections that <see cref="EndpointCoverageTests"/> names in its notes for the anonymous
/// ticketing endpoints, exercised through the real pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The coverage test can only say an endpoint is on a list; the note beside it says <em>why</em> that
/// is acceptable. A note nobody checks is a comment, and comments drift - the rate limit gets renamed,
/// a repository query loses its expiry clause - so each claim made there is asserted here.
/// </para>
/// <para>
/// Kept in its own class, and so its own <see cref="ApiFactory"/>, deliberately: the rate limiter's
/// counters live in the host, and a test that burns through the submission budget must not be able to
/// starve any other test's requests.
/// </para>
/// </remarks>
public class TicketingPublicEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string SubmitUrl = "/api/public/tickets";
    private const string GuestUrl = "/api/public/tickets/guest";
    private const string ConfigUrl = "/api/public/ticketing";

    /// <summary>
    /// The per-caller rate limit really is wired to the submission endpoint.
    /// </summary>
    /// <remarks>
    /// Sends the honeypot shape - the cheapest request the endpoint accepts, which never touches the
    /// database or the captcha - so the only thing that can turn the sixth attempt away is the limiter
    /// itself. The policy allows five per window.
    /// </remarks>
    [Fact]
    public async Task SubmissionIsRateLimited()
    {
        using var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 6; i++)
        {
            using var response = await client.PostAsJsonAsync(new Uri(SubmitUrl, UriKind.Relative), HoneypotSubmission());
            statuses.Add(response.StatusCode);
        }

        Assert.All(statuses.Take(5), s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[5]);
    }

    /// <summary>A guest link that names no ticket is a plain 404, for reading and for replying.</summary>
    [Fact]
    public async Task AnUnknownGuestTokenIsNotFound()
    {
        using var client = factory.CreateClient();
        var token = GuestAccessTokenGenerator.New();

        using var read = await client.GetAsync(new Uri($"{GuestUrl}/{token}", UriKind.Relative));
        using var reply = await client.PostAsJsonAsync(
            new Uri($"{GuestUrl}/{token}/messages", UriKind.Relative),
            new SaveTicketMessageRequestDto { Body = "Hello?" });

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, reply.StatusCode);
    }

    /// <summary>
    /// A token past its expiry stops working, exactly as an unknown one does.
    /// </summary>
    /// <remarks>
    /// Paired with a live token on an otherwise identical ticket, so a 404 here can only be the
    /// expiry - not a seeding mistake that would make every guest lookup fail.
    /// </remarks>
    [Fact]
    public async Task AnExpiredGuestTokenStopsWorking()
    {
        var live = await SeedTicketAsync(expiresOn: DateTimeOffset.UtcNow.AddDays(1));
        var expired = await SeedTicketAsync(expiresOn: DateTimeOffset.UtcNow.AddDays(-1));

        using var client = factory.CreateClient();

        using var liveResponse = await client.GetAsync(new Uri($"{GuestUrl}/{live}", UriKind.Relative));
        using var expiredResponse = await client.GetAsync(new Uri($"{GuestUrl}/{expired}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, liveResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, expiredResponse.StatusCode);
    }

    /// <summary>
    /// Anonymous, but the public config says nothing beyond what the contact form renders from.
    /// </summary>
    /// <remarks>
    /// Asserted on the shape of the JSON rather than on values, because "exposes only these three
    /// things" is the claim: a fourth property - a secret key, say - is what this exists to catch.
    /// Also checks a retired queue is not offered, since the form would then let a visitor pick one
    /// the submission endpoint refuses.
    /// </remarks>
    [Fact]
    public async Task PublicConfigExposesOnlyWhatTheFormNeeds()
    {
        var activeSlug = $"active-{Guid.NewGuid():N}";
        var retiredSlug = $"retired-{Guid.NewGuid():N}";

        await factory.WithDatabaseAsync(async db =>
        {
            db.Set<Queue>().AddRange(NewQueue(activeSlug, isActive: true), NewQueue(retiredSlug, isActive: false));
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri(ConfigUrl, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var properties = json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(["captchaProvider", "captchaSiteKey", "queues"], properties);

        var slugs = json.RootElement.GetProperty("queues").EnumerateArray()
            .Select(q => q.GetProperty("slug").GetString())
            .ToList();

        Assert.Contains(activeSlug, slugs);
        Assert.DoesNotContain(retiredSlug, slugs);
    }

    /// <summary>A submission that trips the honeypot, valid in every other respect.</summary>
    private static SubmitPublicTicketRequestDto HoneypotSubmission() => new()
    {
        Name = "Bot",
        Email = "bot@example.com",
        Subject = "Hello",
        QueueId = Guid.NewGuid(),
        Body = "Hello",
        Website = "http://spam.example.com"
    };

    private static Queue NewQueue(string slug, bool isActive) => new()
    {
        Name = slug,
        Slug = slug,
        IsActive = isActive,
        CreatedOn = DateTimeOffset.UtcNow,
        CreatedById = Guid.Empty
    };

    /// <summary>Stores a guest ticket with a token that expires when asked to, and returns the token.</summary>
    private async Task<string> SeedTicketAsync(DateTimeOffset expiresOn)
    {
        var token = GuestAccessTokenGenerator.New();
        var now = DateTimeOffset.UtcNow;

        await factory.WithDatabaseAsync(async db =>
        {
            var queue = NewQueue($"q-{Guid.NewGuid():N}", isActive: true);
            var status = new TicketStatus
            {
                Name = "Submitted",
                Slug = $"s-{Guid.NewGuid():N}",
                CreatedOn = now,
                CreatedById = Guid.Empty
            };

            db.Set<Queue>().Add(queue);
            db.Set<TicketStatus>().Add(status);

            db.Set<Ticket>().Add(new Ticket
            {
                TenantId = null,
                SubmitterEmail = "guest@example.com",
                SubmitterName = "Guest",
                Queue = queue,
                QueueId = queue.Id,
                Status = status,
                StatusId = status.Id,
                Subject = "Help",
                SubmittedOn = now,
                GuestAccessToken = token,
                GuestAccessTokenExpiresOn = expiresOn,
                CreatedOn = now,
                CreatedById = Guid.Empty
            });

            await db.SaveChangesAsync();
        });

        return token;
    }
}
