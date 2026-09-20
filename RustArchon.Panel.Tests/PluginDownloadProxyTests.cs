// Copyright ©2026 Scott Blomfield

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using RustArchon.Panel.Services;

namespace RustArchon.Panel.Tests;

/// <summary>
/// The Panel's public plugin download door. It decides nothing - Api does - so what matters here is that it passes the
/// bytes through untouched, that every failure is the same bare 404, and that nothing is ever cacheable.
/// </summary>
public class PluginDownloadProxyTests
{
    private static readonly Guid ServerId = Guid.NewGuid();

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Request = request;
            return Task.FromResult(respond(request));
        }
    }

    private static HttpClient Client(StubHandler handler) => new(handler) { BaseAddress = new Uri("http://api.internal") };

    private static async Task<(IResult Result, HttpContext Http)> RunAsync(StubHandler handler, string token, Guid? serverId = null)
    {
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        var result = await PluginDownloadProxy.HandleAsync(serverId ?? ServerId, token, Client(handler), http.Response, CancellationToken.None);
        return (result, http);
    }

    private static HttpResponseMessage Ok(byte[] bytes, string? version = "0.2.1")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        if (version is not null) { response.Headers.Add("X-RustArchon-Plugin-Version", version); }
        return response;
    }

    [Fact]
    public async Task AGoodTokenReturnsTheExactBytesFromApi()
    {
        // BOM, CRLF, lone LF, non-ASCII: the script is signed, so nothing may be normalized on the way through.
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, 0x0D, 0x0A, 0x0A, 0xC3, 0xA9, 0x00, 0xFF };
        var (result, _) = await RunAsync(new StubHandler(_ => Ok(bytes)), "good");

        var file = Assert.IsType<FileContentHttpResult>(result);
        Assert.Equal(bytes, file.FileContents.ToArray());
        Assert.Equal("RustArchon.cs", file.FileDownloadName);
    }

    [Fact]
    public async Task TheRequestGoesToApisInternalEndpointForThisServerAndToken()
    {
        var handler = new StubHandler(_ => Ok([1]));

        await RunAsync(handler, "tok-123_ABC");

        Assert.Equal($"/internal/plugin/download/{ServerId:D}/tok-123_ABC", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Get, handler.Request.Method);
    }

    [Fact]
    public async Task ATokenWithSpecialCharactersCannotChangeTheUpstreamPath()
    {
        var handler = new StubHandler(_ => Ok([1]));

        await RunAsync(handler, "a/../../admin?x=1#y");

        var uri = handler.Request!.RequestUri!;
        Assert.StartsWith($"/internal/plugin/download/{ServerId:D}/", uri.AbsolutePath);
        Assert.Equal("", uri.Query);
        // The whole token stays one escaped path segment: no raw slash for a traversal to use.
        Assert.DoesNotContain('/', uri.AbsolutePath[$"/internal/plugin/download/{ServerId:D}/".Length..]);
    }

    [Fact]
    public async Task TheVersionHeaderIsPassedOn()
    {
        var (_, http) = await RunAsync(new StubHandler(_ => Ok([1], version: "0.2.1")), "good");

        Assert.Equal("0.2.1", http.Response.Headers["X-RustArchon-Plugin-Version"]);
    }

    [Fact]
    public async Task EverythingIsMarkedNoStoreSuccessOrNot()
    {
        var (_, good) = await RunAsync(new StubHandler(_ => Ok([1])), "good");
        var (_, bad) = await RunAsync(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), "bad");

        Assert.Equal("no-store", good.Response.Headers.CacheControl.ToString());
        Assert.Equal("no-store", bad.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task EveryUpstreamRefusalIsTheSameBareNotFound(HttpStatusCode status)
    {
        var (result, _) = await RunAsync(new StubHandler(_ => new HttpResponseMessage(status)), "bad");

        Assert.IsType<NotFound>(result);
    }

    [Fact]
    public async Task ApiBeingUnreachableIsANotFoundNotAServerErrorThatHintsAtInternals()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));

        var (result, _) = await RunAsync(handler, "good");

        Assert.IsType<NotFound>(result);
    }

    [Theory]
    [InlineData("")]
    public async Task AnEmptyTokenIsRefusedWithoutCallingApi(string token)
    {
        var handler = new StubHandler(_ => Ok([1]));

        var (result, _) = await RunAsync(handler, token);

        Assert.IsType<NotFound>(result);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AnOversizedTokenIsRefusedWithoutCallingApi()
    {
        var handler = new StubHandler(_ => Ok([1]));

        var (result, _) = await RunAsync(handler, new string('a', PluginDownloadProxy.MaxTokenLength + 1));

        Assert.IsType<NotFound>(result);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ATokenAtTheLimitIsStillForwarded()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await RunAsync(handler, new string('a', PluginDownloadProxy.MaxTokenLength));

        Assert.Equal(1, handler.Calls);
    }

    // ---- the header form (Updater 0.3.0 and later) ---------------------------------------------------------

    private static async Task<(IResult Result, HttpContext Http)> RunHeaderAsync(StubHandler handler, string? token)
    {
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        if (token is not null) { http.Request.Headers["X-RustArchon-Update-Token"] = token; }
        var result = await PluginDownloadProxy.HandleHeaderTokenAsync(http.Request, Client(handler), http.Response, CancellationToken.None);
        return (result, http);
    }

    [Fact]
    public async Task TheHeaderFormPassesTheExactBytesThroughAndForwardsTheTokenInTheSameHeaderNotTheAddress()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, 0x0D, 0x0A, 0x0A, 0xC3, 0xA9, 0x00, 0xFF };
        var handler = new StubHandler(_ => Ok(bytes));

        var (result, http) = await RunHeaderAsync(handler, "good-token_123");

        var file = Assert.IsType<FileContentHttpResult>(result);
        Assert.Equal(bytes, file.FileContents.ToArray());
        Assert.Equal("/internal/plugin/download", handler.Request!.RequestUri!.AbsolutePath);
        Assert.DoesNotContain("good-token_123", handler.Request.RequestUri.ToString());
        Assert.Equal("good-token_123", Assert.Single(handler.Request.Headers.GetValues("X-RustArchon-Update-Token")));
        Assert.Equal("0.2.1", http.Response.Headers["X-RustArchon-Plugin-Version"]);
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ANullOrEmptyHeaderIsRefusedWithoutCallingApi(string? token)
    {
        var handler = new StubHandler(_ => Ok([1]));

        var (result, http) = await RunHeaderAsync(handler, token);

        Assert.IsType<NotFound>(result);
        Assert.Equal(0, handler.Calls);
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task AnOversizedHeaderIsRefusedWithoutCallingApi()
    {
        var handler = new StubHandler(_ => Ok([1]));

        var (result, _) = await RunHeaderAsync(handler, new string('a', PluginDownloadProxy.MaxTokenLength + 1));

        Assert.IsType<NotFound>(result);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task EveryRefusalFromApiIsTheSameBareNotFound(HttpStatusCode status)
    {
        var (result, _) = await RunHeaderAsync(new StubHandler(_ => new HttpResponseMessage(status)), "some-token");

        Assert.IsType<NotFound>(result);
    }

    [Fact]
    public async Task AnUnreachableApiIsABareNotFound()
    {
        var (result, _) = await RunHeaderAsync(new StubHandler(_ => throw new HttpRequestException("down")), "some-token");

        Assert.IsType<NotFound>(result);
    }
}
