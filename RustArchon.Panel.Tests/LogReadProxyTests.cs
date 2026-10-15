// Copyright ©2026 Scott Blomfield

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Diagnostics;
using RustArchon.Panel.Services;

namespace RustArchon.Panel.Tests;

/// <summary>
/// The Panel's public diagnostic log door (ADR-0006): it checks nothing itself beyond throttling, hands the token to the Api to decide, adds the
/// Panel's own entries, and - when the Api cannot be reached - still answers from the Panel's own logs for a token that was verified a moment ago.
/// </summary>
public class LogReadProxyTests
{
    private const string Token = "abcd1234.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private sealed class MutableTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = T0;

        public override DateTimeOffset GetUtcNow() => Now;
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
        public MutableTime Time { get; } = new();

        public LogRing PanelRing { get; } = new("panel", "panel-boot", 1000);

        public VerifiedLogTokenCache Verified { get; }

        public LogReadThrottle Throttle { get; }

        public CapturingLogger Logger { get; } = new();

        public int FailureLimit { get; set; } = 3;

        public FakeApi Api { get; private set; } = null!;

        public Fixture()
        {
            Verified = new VerifiedLogTokenCache(Time);
            Throttle = new LogReadThrottle(Time, () => FailureLimit);
        }

        public LogReadProxy Proxy(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        {
            Api = new FakeApi(respond);
            return new LogReadProxy(new Factory(Api), PanelRing, Throttle, Verified, Time, Logger);
        }
    }

    /// <summary>
    /// A response that behaves like Kestrel's: once any body byte has been written, the status code can no longer be changed. DefaultHttpContext
    /// allows it, which is how a status set after the body was written got past these tests and failed on the first real request.
    /// </summary>
    private sealed class StrictResponseFeature : IHttpResponseFeature
    {
        private readonly HttpResponseFeature _inner = new();

        public bool Started { get; set; }

        public bool HasStarted => Started;

        public int StatusCode
        {
            get => _inner.StatusCode;
            set
            {
                if (Started)
                {
                    throw new InvalidOperationException("StatusCode cannot be set because the response has already started.");
                }

                _inner.StatusCode = value;
            }
        }

        public string? ReasonPhrase { get => _inner.ReasonPhrase; set => _inner.ReasonPhrase = value; }

        public IHeaderDictionary Headers { get => _inner.Headers; set => _inner.Headers = value; }

        public Stream Body { get => _inner.Body; set => _inner.Body = value; }

        public void OnStarting(Func<object, Task> callback, object state) => _inner.OnStarting(callback, state);

        public void OnCompleted(Func<object, Task> callback, object state) => _inner.OnCompleted(callback, state);
    }

    private sealed class StartingStream(Stream inner, StrictResponseFeature feature) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count > 0)
            {
                feature.Started = true;
            }

            inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length > 0)
            {
                feature.Started = true;
            }

            await inner.WriteAsync(buffer, cancellationToken);
        }
    }

    private static DefaultHttpContext Context(string query = "", string? token = Token, string address = "9.9.9.9", string? cfAddress = null)
    {
        var http = new DefaultHttpContext();
        var feature = new StrictResponseFeature();
        http.Features.Set<IHttpResponseFeature>(feature);
        http.Response.Body = new StartingStream(new MemoryStream(), feature);
        http.Request.QueryString = new QueryString(query);
        if (token is not null)
        {
            http.Request.Headers[LogReadProxy.TokenHeader] = token;
        }

        if (cfAddress is not null)
        {
            http.Request.Headers["CF-Connecting-IP"] = cfAddress;
        }

        http.Connection.RemoteIpAddress = IPAddress.Parse(address);
        return http;
    }

    private static string Body(HttpContext http)
    {
        http.Response.Body.Position = 0;
        return new StreamReader(http.Response.Body, Encoding.UTF8).ReadToEnd();
    }

    private static string[] Lines(HttpContext http) => Body(http).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>A real Api answer: NDJSON from the same writer the Api uses, for the named api messages.</summary>
    private static async Task<string> ApiNdjson(params string[] messages)
    {
        var ring = new LogRing("api", "api-boot", 100);
        for (var i = 0; i < messages.Length; i++)
        {
            ring.Append(T0.AddSeconds(i * 2), LogLevel.Information, "Api.Cat", messages[i], null, null);
        }

        var result = await new LogQueryEngine().ExecuteAsync(new LogQuery(), [new RingLogSource("api", ring)], CancellationToken.None);
        using var stream = new MemoryStream();
        await LogNdjsonWriter.WriteAsync(result, stream, CancellationToken.None);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static Task<HttpResponseMessage> Respond(HttpStatusCode status, string body = "", string contentType = "application/x-ndjson") =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) });

    // ---- the Api decides ----------------------------------------------------------------------------------

    [Fact]
    public async Task ApiAnswerWithNoPanelSourceRequestedIsPassedThroughByteForByte()
    {
        var f = new Fixture();
        var ndjson = await ApiNdjson("one", "two");
        var proxy = f.Proxy(_ => Respond(HttpStatusCode.OK, ndjson));
        var http = Context("?sources=api");

        await proxy.HandleLogsAsync(http, CancellationToken.None);

        Assert.Equal(200, http.Response.StatusCode);
        Assert.Equal(ndjson, Body(http));
        Assert.StartsWith("application/x-ndjson", http.Response.ContentType);
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task TheRequestIsForwardedWithTheSameQueryAndTheTokenInTheHeaderOnly()
    {
        var f = new Fixture();
        var proxy = f.Proxy(_ => Respond(HttpStatusCode.OK, "{\"_meta\":{\"returned\":0,\"sources\":[]}}\n"));

        await proxy.HandleLogsAsync(Context("?sources=api&level=debug&text=needle"), CancellationToken.None);

        var sent = Assert.Single(f.Api.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("/internal/logs", sent.RequestUri!.AbsolutePath);
        Assert.Equal("?sources=api&level=debug&text=needle", sent.RequestUri.Query);
        Assert.Equal(Token, sent.Headers.GetValues(LogReadProxy.TokenHeader).Single());
        Assert.DoesNotContain(Token, sent.RequestUri.ToString());
    }

    [Fact]
    public async Task AnOverlongTokenIsNotForwardedAtAll()
    {
        var f = new Fixture();
        var proxy = f.Proxy(_ => Respond(HttpStatusCode.Unauthorized));

        await proxy.HandleLogsAsync(Context(token: new string('a', 5000)), CancellationToken.None);

        var sent = Assert.Single(f.Api.Requests);
        Assert.True(sent.Headers.TryGetValues(LogReadProxy.TokenHeader, out var values));
        Assert.Equal(string.Empty, values.Single());
    }

    [Fact]
    public async Task WhenTheFeatureIsOffTheApiSays404AndSoDoesThePanelWithoutCountingItAsAFailure()
    {
        var f = new Fixture { FailureLimit = 1 };
        var proxy = f.Proxy(_ => Respond(HttpStatusCode.NotFound));

        for (var i = 0; i < 3; i++)
        {
            var http = Context();
            await proxy.HandleLogsAsync(http, CancellationToken.None);
            Assert.Equal(404, http.Response.StatusCode);
            Assert.Empty(Body(http));
        }

        Assert.Equal(3, f.Api.Requests.Count);
    }

    [Fact]
    public async Task ARefusalIsTheSameBare401AndCountsAgainstTheAddress()
    {
        var f = new Fixture { FailureLimit = 2 };
        var proxy = f.Proxy(_ => Respond(HttpStatusCode.Unauthorized));

        var first = Context();
        await proxy.HandleLogsAsync(first, CancellationToken.None);
        var second = Context();
        await proxy.HandleLogsAsync(second, CancellationToken.None);
        var third = Context();
        await proxy.HandleLogsAsync(third, CancellationToken.None);

        Assert.Equal(401, first.Response.StatusCode);
        Assert.Empty(Body(first));
        Assert.Equal(401, second.Response.StatusCode);
        Assert.Equal(429, third.Response.StatusCode);
        Assert.Equal("60", third.Response.Headers.RetryAfter.ToString());
        Assert.Equal(2, f.Api.Requests.Count);
        Assert.Empty(Body(third));
    }

    [Fact]
    public async Task AGoodTokenFromABlockedAddressIsNotServedUntilTheMinuteHasPassed()
    {
        var f = new Fixture { FailureLimit = 1 };
        f.Throttle.RecordFailure("9.9.9.9");
        var proxy = f.Proxy(_ => Respond(HttpStatusCode.OK, "{\"_meta\":{\"returned\":0,\"sources\":[]}}\n"));

        var blocked = Context();
        await proxy.HandleLogsAsync(blocked, CancellationToken.None);
        f.Time.Now = f.Time.Now.AddSeconds(61);
        var later = Context();
        await proxy.HandleLogsAsync(later, CancellationToken.None);

        Assert.Equal(429, blocked.Response.StatusCode);
        Assert.Equal(200, later.Response.StatusCode);
    }

    [Fact]
    public async Task TheCloudflareClientAddressIsUsedForThrottlingWhenPresent()
    {
        var f = new Fixture { FailureLimit = 1 };
        var proxy = f.Proxy(_ => Respond(HttpStatusCode.Unauthorized));

        await proxy.HandleLogsAsync(Context(address: "172.16.0.1", cfAddress: "203.0.113.7"), CancellationToken.None);
        var sameEdgeOtherClient = Context(address: "172.16.0.1", cfAddress: "203.0.113.8");
        await proxy.HandleLogsAsync(sameEdgeOtherClient, CancellationToken.None);
        var sameClient = Context(address: "172.16.0.2", cfAddress: "203.0.113.7");
        await proxy.HandleLogsAsync(sameClient, CancellationToken.None);

        Assert.Equal(401, sameEdgeOtherClient.Response.StatusCode);
        Assert.Equal(429, sameClient.Response.StatusCode);
    }

    [Fact]
    public async Task ABadQueryParameterIsPassedOnAs400WithTheApisMessage()
    {
        var f = new Fixture();
        var proxy = f.Proxy(_ => Respond(HttpStatusCode.BadRequest, "Unknown parameter 'levl'.", "text/plain"));
        var http = Context("?levl=debug");

        await proxy.HandleLogsAsync(http, CancellationToken.None);

        Assert.Equal(400, http.Response.StatusCode);
        Assert.Contains("levl", Body(http));
    }

    // ---- the Panel's own source ----------------------------------------------------------------------------

    [Fact]
    public async Task ThePanelsOwnEntriesAreMergedInWhenThePanelSourceIsAsked()
    {
        var f = new Fixture();
        f.PanelRing.Append(T0.AddSeconds(1), LogLevel.Information, "Panel.Cat", "from the panel", null, null);
        var proxy = f.Proxy(async _ => await Respond(HttpStatusCode.OK, await ApiNdjson("api first", "api second")));
        var http = Context("?sources=api,panel");

        await proxy.HandleLogsAsync(http, CancellationToken.None);

        var lines = Lines(http);
        var messages = lines.SkipLast(1).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("message").GetString()).ToArray();
        Assert.Equal(new[] { "api first", "from the panel", "api second" }, messages);
        using var meta = JsonDocument.Parse(lines[^1]);
        Assert.Equal(new[] { "api", "panel" }, meta.RootElement.GetProperty("_meta").GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task ThePanelsOwnCursorIsHonoured()
    {
        var f = new Fixture();
        for (var i = 1; i <= 3; i++)
        {
            f.PanelRing.Append(T0.AddSeconds(i), LogLevel.Information, "Panel.Cat", "panel " + i, null, null);
        }

        var proxy = f.Proxy(async _ => await Respond(HttpStatusCode.OK, await ApiNdjson()));
        var http = Context("?sources=panel&after=panel:panel-boot:2");

        await proxy.HandleLogsAsync(http, CancellationToken.None);

        var entries = Lines(http).SkipLast(1).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("message").GetString()).ToArray();
        Assert.Equal(new[] { "panel 3" }, entries);
    }

    [Fact]
    public async Task ThePanelsEntriesAreNotReadUntilTheApiHasAcceptedTheToken()
    {
        var f = new Fixture();
        f.PanelRing.Append(T0, LogLevel.Information, "Panel.Cat", "must not leak", null, null);
        var proxy = f.Proxy(_ => Respond(HttpStatusCode.Unauthorized));
        var http = Context("?sources=panel");

        await proxy.HandleLogsAsync(http, CancellationToken.None);

        Assert.DoesNotContain("must not leak", Body(http));
    }

    // ---- the Api cannot be reached ---------------------------------------------------------------------------

    [Fact]
    public async Task WithTheApiDownAnUnknownTokenGetsA503AndNoLogData()
    {
        var f = new Fixture();
        f.PanelRing.Append(T0, LogLevel.Information, "Panel.Cat", "must not leak", null, null);
        var proxy = f.Proxy(_ => throw new HttpRequestException("connection refused"));
        var http = Context();

        await proxy.HandleLogsAsync(http, CancellationToken.None);

        Assert.Equal(503, http.Response.StatusCode);
        Assert.DoesNotContain("must not leak", Body(http));
    }

    [Fact]
    public async Task WithTheApiDownATokenVerifiedMomentsAgoStillGetsThePanelsOwnLogsAndTheOthersAreNamedUnavailable()
    {
        var f = new Fixture();
        f.PanelRing.Append(T0, LogLevel.Error, "Panel.Cat", "Api connection refused", null, null);
        var up = true;
        var proxy = f.Proxy(async _ => up ? await Respond(HttpStatusCode.OK, await ApiNdjson("fine")) : throw new HttpRequestException("connection refused"));
        await proxy.HandleLogsAsync(Context("?sources=api"), CancellationToken.None);
        up = false;
        var serverId = Guid.NewGuid();
        var http = Context($"?sources=api,worker,panel,plugin:{serverId}&level=debug");

        await proxy.HandleLogsAsync(http, CancellationToken.None);

        Assert.Equal(200, http.Response.StatusCode);
        var lines = Lines(http);
        Assert.Equal("Api connection refused", JsonDocument.Parse(lines[0]).RootElement.GetProperty("message").GetString());
        using var meta = JsonDocument.Parse(lines[^1]);
        var sources = meta.RootElement.GetProperty("_meta").GetProperty("sources").EnumerateArray().ToList();
        foreach (var name in new[] { "api", "worker", $"plugin:{serverId}" })
        {
            var source = sources.Single(s => s.GetProperty("name").GetString() == name);
            Assert.False(source.GetProperty("available").GetBoolean());
            Assert.Contains("Api", source.GetProperty("reason").GetString());
        }

        Assert.True(sources.Single(s => s.GetProperty("name").GetString() == "panel").GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task AnApiServerErrorIsTreatedLikeAnUnreachableApi()
    {
        var f = new Fixture();
        var proxy = f.Proxy(_ => Respond(HttpStatusCode.InternalServerError));

        var unknown = Context();
        await proxy.HandleLogsAsync(unknown, CancellationToken.None);

        Assert.Equal(503, unknown.Response.StatusCode);
    }

    [Fact]
    public async Task ATokenTheApiThenRefusesIsForgottenSoItCannotBeUsedWhenTheApiLaterGoesDown()
    {
        var f = new Fixture();
        var status = HttpStatusCode.OK;
        var proxy = f.Proxy(async _ => status == HttpStatusCode.OK
            ? await Respond(status, await ApiNdjson("fine"))
            : status == HttpStatusCode.Unauthorized ? await Respond(status) : throw new HttpRequestException("down"));
        await proxy.HandleLogsAsync(Context("?sources=api"), CancellationToken.None);
        status = HttpStatusCode.Unauthorized;
        await proxy.HandleLogsAsync(Context("?sources=api"), CancellationToken.None);
        status = HttpStatusCode.ServiceUnavailable;
        var http = Context("?sources=panel");

        await proxy.HandleLogsAsync(http, CancellationToken.None);

        Assert.Equal(503, http.Response.StatusCode);
    }

    [Fact]
    public async Task ABadQueryWithTheApiDownIsStillA400ForAVerifiedToken()
    {
        var f = new Fixture();
        var up = true;
        var proxy = f.Proxy(async _ => up ? await Respond(HttpStatusCode.OK, await ApiNdjson()) : throw new HttpRequestException("down"));
        await proxy.HandleLogsAsync(Context("?sources=api"), CancellationToken.None);
        up = false;
        var http = Context("?levl=debug");

        await proxy.HandleLogsAsync(http, CancellationToken.None);

        Assert.Equal(400, http.Response.StatusCode);
        Assert.Contains("levl", Body(http));
    }

    // ---- help --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task HelpIsFetchedFromTheApiWithTheTokenAndPassedBack()
    {
        var f = new Fixture();
        var proxy = f.Proxy(_ => Respond(HttpStatusCode.OK, "the help text", "text/plain"));
        var http = Context();

        await proxy.HandleHelpAsync(http, CancellationToken.None);

        Assert.Equal(200, http.Response.StatusCode);
        Assert.Equal("the help text", Body(http));
        Assert.StartsWith("text/plain", http.Response.ContentType);
        var sent = Assert.Single(f.Api.Requests);
        Assert.Equal("/internal/logs/help", sent.RequestUri!.AbsolutePath);
        Assert.Equal(Token, sent.Headers.GetValues(LogReadProxy.TokenHeader).Single());
    }

    [Fact]
    public async Task HelpWithARefusedTokenIsTheSameBare401AndCounts()
    {
        var f = new Fixture { FailureLimit = 1 };
        var proxy = f.Proxy(_ => Respond(HttpStatusCode.Unauthorized));

        var http = Context();
        await proxy.HandleHelpAsync(http, CancellationToken.None);
        var again = Context();
        await proxy.HandleHelpAsync(again, CancellationToken.None);

        Assert.Equal(401, http.Response.StatusCode);
        Assert.Empty(Body(http));
        Assert.Equal(429, again.Response.StatusCode);
    }

    [Fact]
    public async Task HelpWithTheApiDownIs503()
    {
        var f = new Fixture();
        var proxy = f.Proxy(_ => throw new HttpRequestException("down"));
        var http = Context();

        await proxy.HandleHelpAsync(http, CancellationToken.None);

        Assert.Equal(503, http.Response.StatusCode);
    }

    // ---- what it records -----------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryRequestIsLoggedWithTheAddressAndOutcomeButNeverTheToken()
    {
        var f = new Fixture();
        var proxy = f.Proxy(_ => Respond(HttpStatusCode.Unauthorized));

        await proxy.HandleLogsAsync(Context(address: "198.51.100.9"), CancellationToken.None);

        var entry = Assert.Single(f.Logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("198.51.100.9", entry.Message);
        Assert.Contains("401", entry.Message);
        Assert.DoesNotContain("AAAAAAAA", entry.Message);
    }

    [Fact]
    public async Task ASuccessfulReadIsLoggedAtInformation()
    {
        var f = new Fixture();
        var proxy = f.Proxy(async _ => await Respond(HttpStatusCode.OK, await ApiNdjson()));

        await proxy.HandleLogsAsync(Context("?sources=api"), CancellationToken.None);

        var entry = Assert.Single(f.Logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("200", entry.Message);
    }

    [Fact]
    public async Task NoInternalCredentialOrApiHeaderEverReachesTheCaller()
    {
        var f = new Fixture();
        var proxy = f.Proxy(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"_meta\":{\"returned\":0,\"sources\":[]}}\n") };
            response.Headers.Add("X-Internal-Api-Key", "internal-secret");
            response.Headers.Add("Server", "Kestrel");
            return Task.FromResult(response);
        });
        var http = Context("?sources=api");

        await proxy.HandleLogsAsync(http, CancellationToken.None);

        Assert.False(http.Response.Headers.ContainsKey("X-Internal-Api-Key"));
        Assert.False(http.Response.Headers.ContainsKey("Server"));
    }

    [Fact]
    public async Task AnUnreadableApiAnswerWithThePanelSourceRequestedIsA502NotAHalfAnswer()
    {
        var f = new Fixture();
        var proxy = f.Proxy(_ => Respond(HttpStatusCode.OK, "this is not ndjson"));
        var http = Context("?sources=api,panel");

        await proxy.HandleLogsAsync(http, CancellationToken.None);

        Assert.Equal(502, http.Response.StatusCode);
    }
}
