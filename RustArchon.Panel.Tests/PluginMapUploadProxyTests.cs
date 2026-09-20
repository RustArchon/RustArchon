// Copyright ©2026 Scott Blomfield

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using RustArchon.Messaging.Contracts;
using RustArchon.Panel.Services;

namespace RustArchon.Panel.Tests;

/// <summary>
/// The Panel's public map upload door. It decides almost nothing - Api does - so what matters here is that it streams the
/// picture through untouched, that the token travels in a header (never in either address), that every token problem is the
/// same bare 404, and that nothing is cacheable.
/// </summary>
public class PluginMapUploadProxyTests
{
    private static readonly byte[] Picture = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 1, 2, 3, 0xFF];

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public byte[]? Body { get; private set; }
        public long? ContentLength { get; private set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Request = request;
            ContentLength = request.Content?.Headers.ContentLength;
            Body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return respond(request);
        }
    }

    private sealed class FakeSizeLimit : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; init; }
        public long? MaxRequestBodySize { get; set; } = 30_000_000;
    }

    private static HttpClient Client(StubHandler handler) => new(handler) { BaseAddress = new Uri("http://api.internal") };

    private static (HttpContext Http, FakeSizeLimit Limit) Request(byte[] body, string? token = "tok-123_ABC", long? contentLength = -1, bool readOnlyLimit = false)
    {
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(body);
        http.Request.ContentLength = contentLength == -1 ? body.Length : contentLength;
        if (token is not null) { http.Request.Headers[RustArchonPlugin.MapUploadTokenHeader] = token; }
        var limit = new FakeSizeLimit { IsReadOnly = readOnlyLimit };
        http.Features.Set<IHttpMaxRequestBodySizeFeature>(limit);
        return (http, limit);
    }

    private static Task<IResult> RunAsync(HttpContext http, StubHandler handler) =>
        PluginMapUploadProxy.HandleAsync(http.Request, Client(handler), CancellationToken.None);

    private static StubHandler Answers(HttpStatusCode status) => new(_ => new HttpResponseMessage(status));

    [Fact]
    public async Task AGoodUploadStreamsTheExactBytesToApiAndAnswersNoContent()
    {
        var handler = Answers(HttpStatusCode.NoContent);
        var (http, _) = Request(Picture);

        var result = await RunAsync(http, handler);

        Assert.IsType<NoContent>(result);
        Assert.Equal(Picture, handler.Body);
        Assert.Equal(Picture.Length, handler.ContentLength);
        Assert.Equal("image/png", handler.Request!.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task TheTokenIsForwardedInAHeaderAndTheUpstreamAddressCarriesNothing()
    {
        var handler = Answers(HttpStatusCode.NoContent);
        var (http, _) = Request(Picture, token: "tok-123_ABC");

        await RunAsync(http, handler);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/internal/plugin/map", handler.Request.RequestUri!.AbsolutePath);
        Assert.DoesNotContain("tok-123_ABC", handler.Request.RequestUri.ToString());
        Assert.Equal("tok-123_ABC", handler.Request.Content!.Headers.GetValues(RustArchonPlugin.MapUploadTokenHeader).Single());
    }

    [Fact]
    public async Task NothingIsCacheable()
    {
        var (http, _) = Request(Picture);

        await RunAsync(http, Answers(HttpStatusCode.NoContent));

        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task TheRequestSizeCeilingIsLiftedForThisRequestOnly()
    {
        var (http, limit) = Request(Picture);

        await RunAsync(http, Answers(HttpStatusCode.NoContent));

        Assert.Equal(PluginMapUploadProxy.MaxBytes, limit.MaxRequestBodySize);
    }

    [Fact]
    public async Task ACeilingTheServerWillNotLetUsChangeDoesNotBreakTheUpload()
    {
        var (http, limit) = Request(Picture, readOnlyLimit: true);

        var result = await RunAsync(http, Answers(HttpStatusCode.NoContent));

        Assert.IsType<NoContent>(result);
        Assert.Equal(30_000_000, limit.MaxRequestBodySize);      // left alone
    }

    // ---- refusals: one bare 404, and Api is not even asked when the request cannot be a real upload -----------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ARequestWithNoTokenIsANotFoundAndApiIsNeverAsked(string? token)
    {
        var handler = Answers(HttpStatusCode.NoContent);
        var (http, _) = Request(Picture, token: token);

        Assert.IsType<NotFound>(await RunAsync(http, handler));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ATokenLongerThanAnyRealOneIsANotFoundAndApiIsNeverAsked()
    {
        var handler = Answers(HttpStatusCode.NoContent);
        var (http, _) = Request(Picture, token: new string('a', PluginMapUploadProxy.MaxTokenLength + 1));

        Assert.IsType<NotFound>(await RunAsync(http, handler));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ARequestWithNoLengthOrAZeroLengthIsANotFoundAndApiIsNeverAsked()
    {
        var handler = Answers(HttpStatusCode.NoContent);

        var (noLength, _) = Request(Picture, contentLength: null);
        var (zero, _) = Request([], contentLength: 0);

        Assert.IsType<NotFound>(await RunAsync(noLength, handler));
        Assert.IsType<NotFound>(await RunAsync(zero, handler));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ALengthOverTheCeilingIsANotFoundBeforeAnyByteIsRead()
    {
        var handler = Answers(HttpStatusCode.NoContent);
        var (http, _) = Request(Picture, contentLength: PluginMapUploadProxy.MaxBytes + 1);

        Assert.IsType<NotFound>(await RunAsync(http, handler));
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, http.Request.Body.Position);                 // the body was not touched
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task EveryApiFailureThatIsAboutTheTokenIsTheSameBareNotFound(HttpStatusCode status)
    {
        var (http, _) = Request(Picture);

        Assert.IsType<NotFound>(await RunAsync(http, Answers(status)));
    }

    [Fact]
    public async Task ApiBeingUnreachableIsANotFoundNotAnException()
    {
        var (http, _) = Request(Picture);
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));

        Assert.IsType<NotFound>(await RunAsync(http, handler));
    }

    [Fact]
    public async Task ARefusalOfThePictureItselfIsABadRequestSoTheOwnerOfAGoodTokenCanTellWhy()
    {
        var (http, _) = Request(Picture);

        Assert.IsType<BadRequest>(await RunAsync(http, Answers(HttpStatusCode.BadRequest)));
    }
}
