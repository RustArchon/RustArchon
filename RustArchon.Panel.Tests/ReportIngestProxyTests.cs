// Copyright ©2026 Scott Blomfield

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using RustArchon.Panel.Services;

namespace RustArchon.Panel.Tests;

/// <summary>
/// The Panel's public report door (ADR-0001): what a game server posts is streamed on to the Api with the secret in a header (never the
/// address), and every refusal - whoever refused it, for whatever reason - is the same bare 404, so the response tells a guesser nothing.
/// </summary>
public class ReportIngestProxyTests
{
    private static readonly Guid ServerId = Guid.NewGuid();
    private const string Token = "a-good-secret";
    private const string FormBody = "userid=76561198000000002&data=%7B%22Subject%22%3A%22hi%22%7D";

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? SentBody { get; private set; }
        public string? SentContentType { get; private set; }
        public long? SentContentLength { get; private set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Request = request;
            SentBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            SentContentType = request.Content?.Headers.ContentType?.ToString();
            SentContentLength = request.Content?.Headers.ContentLength;
            return await respond(request);
        }
    }

    private static RecordingHandler Upstream(HttpStatusCode status) =>
        new(_ => Task.FromResult(new HttpResponseMessage(status)));

    private static HttpClient Client(RecordingHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://rustarchon-api:8080") };

    private static HttpRequest FormRequest(string body = FormBody, string contentType = "application/x-www-form-urlencoded", bool declareLength = true)
    {
        var http = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        http.Request.Body = new MemoryStream(bytes);
        http.Request.ContentType = contentType;
        if (declareLength)
        {
            http.Request.ContentLength = bytes.Length;
        }

        return http.Request;
    }

    private static int StatusOf(IResult result) => result switch
    {
        IStatusCodeHttpResult { StatusCode: { } code } => code,
        _ => throw new InvalidOperationException($"{result.GetType().Name} carries no status code.")
    };

    [Fact]
    public async Task AGameServersFormIsStreamedOnToTheApiWithTheSecretInAHeaderNotTheAddress()
    {
        var handler = Upstream(HttpStatusCode.NoContent);
        var request = FormRequest();

        var result = await ReportIngestProxy.HandleAsync(ServerId, Token, request, Client(handler), CancellationToken.None);

        Assert.Equal(StatusCodes.Status204NoContent, StatusOf(result));
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal($"/internal/reports/{ServerId}", handler.Request.RequestUri!.AbsolutePath);
        Assert.DoesNotContain(Token, handler.Request.RequestUri.ToString()); // the address is what gets logged; the secret must not be in it
        Assert.Equal(Token, Assert.Single(handler.Request.Headers.GetValues(ReportIngestProxy.TokenHeader)));
        Assert.Equal(FormBody, handler.SentBody); // byte for byte: nothing here re-reads or re-encodes the form
        Assert.Equal("application/x-www-form-urlencoded", handler.SentContentType);
        Assert.Equal("no-store", request.HttpContext.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task TheHeaderNameMatchesTheOneTheApiReads()
    {
        // The Api's InternalReportIngestController.TokenHeader; asserted by value because the Panel does not reference the Api.
        Assert.Equal("X-RustArchon-Report-Token", ReportIngestProxy.TokenHeader);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AMultipartFormIsForwardedToo()
    {
        var handler = Upstream(HttpStatusCode.NoContent);

        var result = await ReportIngestProxy.HandleAsync(
            ServerId, Token, FormRequest("--b\r\n\r\n--b--", "multipart/form-data; boundary=b"), Client(handler), CancellationToken.None);

        Assert.Equal(StatusCodes.Status204NoContent, StatusOf(result));
        Assert.StartsWith("multipart/form-data", handler.SentContentType);
    }

    [Fact]
    public async Task ABodyWithNoDeclaredLengthIsStillForwardedAndBoundedByTheServersCeiling()
    {
        var handler = Upstream(HttpStatusCode.NoContent);

        var result = await ReportIngestProxy.HandleAsync(ServerId, Token, FormRequest(declareLength: false), Client(handler), CancellationToken.None);

        Assert.Equal(StatusCodes.Status204NoContent, StatusOf(result));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(FormBody, handler.SentBody);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task AnAcceptedReportAnswersNoContent(HttpStatusCode upstream)
    {
        var result = await ReportIngestProxy.HandleAsync(ServerId, Token, FormRequest(), Client(Upstream(upstream)), CancellationToken.None);

        Assert.Equal(StatusCodes.Status204NoContent, StatusOf(result));
    }

    /// <summary>Wrong secret, unknown server, throttled, a bad body, the Api falling over: the game server learns none of it.</summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task EveryRefusalIsTheSameBare404(HttpStatusCode upstream)
    {
        var result = await ReportIngestProxy.HandleAsync(ServerId, Token, FormRequest(), Client(Upstream(upstream)), CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
    }

    [Fact]
    public async Task AnApiThatCannotBeReachedIsA404NotAnError()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("connection refused"));

        var result = await ReportIngestProxy.HandleAsync(ServerId, Token, FormRequest(), Client(handler), CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
    }

    [Fact]
    public async Task ABodyThatTheServerRefusesToReadIsA404()
    {
        var handler = new RecordingHandler(_ => throw new BadHttpRequestException("too large", StatusCodes.Status413PayloadTooLarge));

        var result = await ReportIngestProxy.HandleAsync(ServerId, Token, FormRequest(), Client(handler), CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
    }

    [Theory]
    [InlineData("")]
    public async Task ARequestWithNoSecretIsRefusedWithoutCallingTheApi(string token)
    {
        var handler = Upstream(HttpStatusCode.NoContent);

        var result = await ReportIngestProxy.HandleAsync(ServerId, token, FormRequest(), Client(handler), CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ASecretThatIsTooLongIsRefusedWithoutBeingForwarded()
    {
        var handler = Upstream(HttpStatusCode.NoContent);

        var result = await ReportIngestProxy.HandleAsync(
            ServerId, new string('a', ReportIngestProxy.MaxTokenLength + 1), FormRequest(), Client(handler), CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ABodyThatIsNotAFormIsRefusedWithoutBeingForwarded()
    {
        var handler = Upstream(HttpStatusCode.NoContent);

        var result = await ReportIngestProxy.HandleAsync(
            ServerId, Token, FormRequest("{\"Subject\":\"hi\"}", "application/json"), Client(handler), CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ABodyLargerThanTheCeilingIsRefusedBeforeAByteIsRead()
    {
        var handler = Upstream(HttpStatusCode.NoContent);
        var request = FormRequest();
        request.ContentLength = ReportIngestProxy.MaxBytes + 1;

        var result = await ReportIngestProxy.HandleAsync(ServerId, Token, request, Client(handler), CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AnEmptyBodyIsRefused()
    {
        var handler = Upstream(HttpStatusCode.NoContent);
        var request = FormRequest("");
        request.ContentLength = 0;

        var result = await ReportIngestProxy.HandleAsync(ServerId, Token, request, Client(handler), CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void TheCeilingIsAnAbuseGuardNotAScreenshotCap()
    {
        // A real screenshot is well under a few megabytes; the ceiling must sit far above it (ADR-0001: no cap on stored screenshots).
        Assert.True(ReportIngestProxy.MaxBytes >= 32L * 1024 * 1024);
    }
}
