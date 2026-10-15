// Copyright ©2026 Scott Blomfield

using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RustArchon.Diagnostics;
using RustArchon.Panel.Services;

namespace RustArchon.Panel.Tests;

/// <summary>
/// The Panel's public door for the diagnostic views (ADR-0007): the same token, throttle and rejection rules as the log door, a forward to the Api that
/// carries the token in a header only, the Api's answer passed through untouched (status and body), and no answer at all when the Api cannot be reached.
/// </summary>
public class DiagnosticViewsProxyTests
{
    private const string Token = "abcd1234.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => T0;
    }

    private sealed class FakeApi(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return await respond(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://rustarchon-api:8080") };
    }

    private sealed class CapturingLogger : ILogger<LogReadProxy>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class Fixture
    {
        private readonly FixedTime _time = new();

        public Fixture()
        {
            Verified = new VerifiedLogTokenCache(_time);
            Throttle = new LogReadThrottle(_time, () => FailureLimit);
        }

        public VerifiedLogTokenCache Verified { get; }

        public LogReadThrottle Throttle { get; }

        public CapturingLogger Logger { get; } = new();

        public int FailureLimit { get; set; } = 3;

        public FakeApi Api { get; private set; } = null!;

        public LogReadProxy Proxy(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        {
            Api = new FakeApi(respond);
            return new LogReadProxy(new Factory(Api), new LogRing("panel", "panel-boot", 100), Throttle, Verified, _time, Logger);
        }
    }

    private static DefaultHttpContext Context(string query = "", string? token = Token, string address = "9.9.9.9")
    {
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        http.Request.QueryString = new QueryString(query);
        if (token is not null)
        {
            http.Request.Headers[LogReadProxy.TokenHeader] = token;
        }

        http.Connection.RemoteIpAddress = IPAddress.Parse(address);
        return http;
    }

    private static string Body(HttpContext http)
    {
        http.Response.Body.Position = 0;
        return new StreamReader(http.Response.Body, Encoding.UTF8).ReadToEnd();
    }

    private static Task<HttpResponseMessage> Respond(HttpStatusCode status, string body = "", string contentType = "application/json") =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) });

    [Fact]
    public async Task TheCatalogRequestIsForwardedToTheApisCatalogWithTheTokenInTheHeaderOnly()
    {
        var f = new Fixture();
        var proxy = f.Proxy(_ => Respond(HttpStatusCode.OK, "{\"views\":[]}"));

        await proxy.HandleViewsAsync(Context(), null, CancellationToken.None);

        var sent = Assert.Single(f.Api.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("/internal/diagnostics/views", sent.RequestUri!.AbsolutePath);
        Assert.Equal(Token, sent.Headers.GetValues(LogReadProxy.TokenHeader).Single());
        Assert.DoesNotContain(Token, sent.RequestUri.ToString());
    }

    [Fact]
    public async Task AViewRequestIsForwardedWithItsNameAndTheSameQuery()
    {
        var f = new Fixture();
        var proxy = f.Proxy(_ => Respond(HttpStatusCode.OK, "{}"));

        await proxy.HandleViewsAsync(Context("?serverId=01a09e82-6ba3-746c-a984-c61fba87ec8d&count=50"), "server-rcon-recent", CancellationToken.None);

        var sent = Assert.Single(f.Api.Requests);
        Assert.Equal("/internal/diagnostics/views/server-rcon-recent", sent.RequestUri!.AbsolutePath);
        Assert.Equal("?serverId=01a09e82-6ba3-746c-a984-c61fba87ec8d&count=50", sent.RequestUri.Query);
    }

    [Fact]
    public async Task AViewNameIsEscapedSoItCannotChangeWhereTheRequestGoes()
    {
        var f = new Fixture();
        var proxy = f.Proxy(_ => Respond(HttpStatusCode.NotFound));

        await proxy.HandleViewsAsync(Context(), "../../logs?x=", CancellationToken.None);

        var sent = Assert.Single(f.Api.Requests);
        Assert.StartsWith("/internal/diagnostics/views/", sent.RequestUri!.AbsolutePath);
        Assert.DoesNotContain("/internal/logs", sent.RequestUri.AbsolutePath.Replace("/internal/diagnostics/views/", string.Empty));
    }

    [Fact]
    public async Task TheApisAnswerIsPassedThroughWithItsStatusAndBodyAndNeverCached()
    {
        var f = new Fixture();
        var json = "{\"view\":\"server\",\"version\":1,\"data\":{}}";
        var proxy = f.Proxy(_ => Respond(HttpStatusCode.OK, json));
        var http = Context();

        await proxy.HandleViewsAsync(http, "server", CancellationToken.None);

        Assert.Equal(200, http.Response.StatusCode);
        Assert.Equal(json, Body(http));
        Assert.StartsWith("application/json", http.Response.ContentType);
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(413)]
    [InlineData(500)]
    public async Task AnErrorTheApiExplainsIsPassedThroughWithItsBody(int status)
    {
        var f = new Fixture();
        var error = "{\"error\":{\"code\":\"x\",\"message\":\"y\"}}";
        var proxy = f.Proxy(_ => Respond((HttpStatusCode)status, error));
        var http = Context();

        await proxy.HandleViewsAsync(http, "server", CancellationToken.None);

        Assert.Equal(status, http.Response.StatusCode);
        Assert.Equal(error, Body(http));
    }

    [Fact]
    public async Task WhenNoTokenHasEverBeenCreatedTheApisBareNotFoundIsPassedOn()
    {
        var f = new Fixture();
        var proxy = f.Proxy(_ => Respond(HttpStatusCode.NotFound));
        var http = Context();

        await proxy.HandleViewsAsync(http, null, CancellationToken.None);

        Assert.Equal(404, http.Response.StatusCode);
        Assert.Empty(Body(http));
    }

    [Fact]
    public async Task ARefusedTokenIsTheSameBareUnauthorizedAndIsCountedAgainstTheAddress()
    {
        var f = new Fixture();
        var proxy = f.Proxy(_ => Respond(HttpStatusCode.Unauthorized));
        var http = Context();

        await proxy.HandleViewsAsync(http, "server", CancellationToken.None);

        Assert.Equal(401, http.Response.StatusCode);
        Assert.Empty(Body(http));
        Assert.DoesNotContain(f.Logger.Entries, e => e.Message.Contains(Token));
    }

    [Fact]
    public async Task AddressesThatKeepGettingRefusedAreThrottledBeforeTheApiIsAsked()
    {
        var f = new Fixture { FailureLimit = 2 };
        var proxy = f.Proxy(_ => Respond(HttpStatusCode.Unauthorized));

        for (var i = 0; i < 2; i++)
        {
            await proxy.HandleViewsAsync(Context(), "server", CancellationToken.None);
        }

        var blocked = Context();
        await proxy.HandleViewsAsync(blocked, "server", CancellationToken.None);

        Assert.Equal(429, blocked.Response.StatusCode);
        Assert.Equal(2, f.Api.Requests.Count);
    }

    [Fact]
    public async Task AnOverlongTokenIsNotForwardedAtAll()
    {
        var f = new Fixture();
        var proxy = f.Proxy(_ => Respond(HttpStatusCode.Unauthorized));

        await proxy.HandleViewsAsync(Context(token: new string('a', 5000)), "server", CancellationToken.None);

        var sent = Assert.Single(f.Api.Requests);
        Assert.Equal(string.Empty, sent.Headers.GetValues(LogReadProxy.TokenHeader).Single());
    }

    [Fact]
    public async Task WhenTheApiCannotBeReachedThereIsNoAnswerAndNoFallbackEvenForAVerifiedToken()
    {
        var f = new Fixture();
        f.Verified.Remember(Token);
        var proxy = f.Proxy(_ => throw new HttpRequestException("connection refused"));
        var http = Context();

        await proxy.HandleViewsAsync(http, "server", CancellationToken.None);

        Assert.Equal(503, http.Response.StatusCode);
        Assert.DoesNotContain("\"data\"", Body(http));
    }

    [Fact]
    public async Task AnApiServerErrorFromTheFrontOfTheApiIsA503NotAPassedThroughBody()
    {
        var f = new Fixture();
        var proxy = f.Proxy(_ => Respond(HttpStatusCode.BadGateway, "<html>proxy error</html>", "text/html"));
        var http = Context();

        await proxy.HandleViewsAsync(http, "server", CancellationToken.None);

        Assert.Equal(503, http.Response.StatusCode);
        Assert.DoesNotContain("proxy error", Body(http));
    }
}
