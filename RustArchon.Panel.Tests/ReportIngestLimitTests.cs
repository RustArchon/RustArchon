// Copyright ©2026 Scott Blomfield

using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Panel.Services;

namespace RustArchon.Panel.Tests;

/// <summary>
/// The per-address report limit the Panel's public door applies, which is a Platform Setting only the Api can read: the default until the
/// Api has answered, its answer after, and the last good value (never no limit) when the Api cannot be reached.
/// </summary>
public class ReportIngestLimitTests
{
    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Path { get; private set; }

        /// <summary>When set, the answer waits for this, so a test can look at what a read does while the Api has not answered yet.</summary>
        public Task? Gate { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Path = request.RequestUri!.AbsolutePath;
            if (Gate is not null)
            {
                await Gate;
            }

            return respond(request);
        }
    }

    private readonly MutableClock _clock = new(DateTimeOffset.UnixEpoch.AddDays(1));

    private ReportIngestLimit Create(Handler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(ReportIngestProxy.ClientName))
            .Returns(() => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://rustarchon-api:8080") });
        return new ReportIngestLimit(factory.Object, _clock, NullLogger<ReportIngestLimit>.Instance);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ItAsksTheApiAndUsesItsAnswer()
    {
        var handler = new Handler(_ => Json("{\"perAddressPerMinute\":45}"));
        var limit = Create(handler);

        await limit.RefreshAsync();

        Assert.Equal(45, limit.PerAddressPerMinute);
        Assert.Equal("/internal/reports/limits", handler.Path);
    }

    [Fact]
    public async Task UntilTheApiHasAnsweredTheApisOwnDefaultApplies()
    {
        var release = new TaskCompletionSource();
        var handler = new Handler(_ => Json("{\"perAddressPerMinute\":45}")) { Gate = release.Task };
        var limit = Create(handler);

        Assert.Equal(ReportIngestLimit.Default, limit.PerAddressPerMinute);      // asks, but does not wait for the answer
        Assert.Equal(120, ReportIngestLimit.Default);

        release.SetResult();
        for (var i = 0; i < 100 && limit.PerAddressPerMinute != 45; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(45, limit.PerAddressPerMinute);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.Unauthorized, "")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, "{\"perAddressPerMinute\":0}")]
    [InlineData(HttpStatusCode.OK, "{\"perAddressPerMinute\":-4}")]
    [InlineData(HttpStatusCode.OK, "{}")]
    public async Task AnAnswerThatIsNotAUsableLimitChangesNothing(HttpStatusCode status, string body)
    {
        var good = true;
        var limit = Create(new Handler(_ => good
            ? Json("{\"perAddressPerMinute\":45}")
            : new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        await limit.RefreshAsync();
        Assert.Equal(45, limit.PerAddressPerMinute);

        good = false;
        await limit.RefreshAsync();

        Assert.Equal(45, limit.PerAddressPerMinute);
    }

    [Fact]
    public async Task AnApiThatCannotBeReachedIsNotAskedAgainOnEveryRequest()
    {
        var handler = new Handler(_ => throw new HttpRequestException("connection refused"));
        var limit = Create(handler);
        await limit.RefreshAsync();
        Assert.Equal(1, handler.Calls);

        for (var i = 0; i < 50; i++)
        {
            _ = limit.PerAddressPerMinute;
        }

        Assert.Equal(1, handler.Calls);
        Assert.Equal(ReportIngestLimit.Default, limit.PerAddressPerMinute);
    }

    [Fact]
    public async Task AChangeIsPickedUpOnceTheValueIsOldEnough()
    {
        var current = 45;
        var handler = new Handler(_ => Json($"{{\"perAddressPerMinute\":{current}}}"));
        var limit = Create(handler);
        await limit.RefreshAsync();

        current = 90;
        _clock.Now += ReportIngestLimit.MaxAge - TimeSpan.FromSeconds(1);
        Assert.Equal(45, limit.PerAddressPerMinute);
        Assert.Equal(1, handler.Calls);

        _clock.Now += TimeSpan.FromSeconds(2);
        _ = limit.PerAddressPerMinute;              // starts the refresh
        for (var i = 0; i < 100 && limit.PerAddressPerMinute != 90; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(90, limit.PerAddressPerMinute);
    }
}
