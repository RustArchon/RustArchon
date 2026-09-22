// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Api.Services;

namespace RustArchon.Api.Tests;

/// <summary>
/// Fetching a plugin file: redirects followed by hand (a few, https only), every failure a quiet failed result, a host's "too often" honoured but
/// capped - and the part that keeps this from ever being a way to reach the network's own machines: connections are only made to public internet addresses.
/// </summary>
public class PluginFileDownloaderTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw exception;
    }

    private static PluginFileDownloader Downloader(HttpMessageHandler handler) => new(new HttpClient(handler), NullLogger<PluginFileDownloader>.Instance);

    private static readonly Uri Start = new("https://umod.org/plugins/BlueprintShare.cs");

    private static HttpResponseMessage Ok(string body = "the file") => new(HttpStatusCode.OK) { Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(body)) };

    private static HttpResponseMessage Redirect(string to, HttpStatusCode status = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        return response;
    }

    // ---- fetching --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASuccessfulDownloadReturnsTheBytes()
    {
        var download = await Downloader(new StubHandler(_ => Ok("hello"))).DownloadAsync(Start, CancellationToken.None);

        Assert.True(download.Ok);
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(download.Content!));
    }

    [Fact]
    public async Task ARedirectIsFollowedByHandToWhereItLeads()
    {
        var handler = new StubHandler(r => r.RequestUri!.Host == "umod.org" ? Redirect("https://cdn.example.net/files/x.cs") : Ok("from the cdn"));

        var download = await Downloader(handler).DownloadAsync(Start, CancellationToken.None);

        Assert.True(download.Ok);
        Assert.Equal("from the cdn", System.Text.Encoding.UTF8.GetString(download.Content!));
        Assert.Equal([Start, new Uri("https://cdn.example.net/files/x.cs")], handler.Requested);
    }

    [Fact]
    public async Task ARelativeRedirectIsResolvedAgainstTheAddressItCameFrom()
    {
        var handler = new StubHandler(r => r.RequestUri!.AbsolutePath == "/plugins/BlueprintShare.cs" ? Redirect("/files/x.cs", HttpStatusCode.MovedPermanently) : Ok());

        var download = await Downloader(handler).DownloadAsync(Start, CancellationToken.None);

        Assert.True(download.Ok);
        Assert.Equal(new Uri("https://umod.org/files/x.cs"), handler.Requested[1]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Moved)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.RedirectMethod)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task EveryKindOfRedirectIsFollowed(HttpStatusCode status)
    {
        var handler = new StubHandler(r => r.RequestUri!.Host == "umod.org" ? Redirect("https://cdn.example.net/x.cs", status) : Ok());

        Assert.True((await Downloader(handler).DownloadAsync(Start, CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task ARedirectAwayFromHttpsIsNotFollowed()
    {
        var handler = new StubHandler(_ => Redirect("http://umod.org/plugins/x.cs"));

        var download = await Downloader(handler).DownloadAsync(Start, CancellationToken.None);

        Assert.False(download.Ok);
        Assert.Contains("https", download.Reason);
        Assert.Single(handler.Requested);
    }

    [Fact]
    public async Task AStartAddressThatIsNotHttpsIsNotFetchedAtAll()
    {
        var handler = new StubHandler(_ => Ok());

        var download = await Downloader(handler).DownloadAsync(new Uri("http://umod.org/plugins/x.cs"), CancellationToken.None);

        Assert.False(download.Ok);
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task ARedirectLoopGivesUpAfterAFewHops()
    {
        var handler = new StubHandler(_ => Redirect("https://umod.org/again"));

        var download = await Downloader(handler).DownloadAsync(Start, CancellationToken.None);

        Assert.False(download.Ok);
        Assert.Contains("too many redirects", download.Reason);
        Assert.Equal(PluginFileDownloader.MaxRedirects + 1, handler.Requested.Count);
    }

    [Fact]
    public async Task ARedirectThatSaysNothingAboutWhereIsAFailure()
    {
        var download = await Downloader(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Found))).DownloadAsync(Start, CancellationToken.None);

        Assert.False(download.Ok);
        Assert.Contains("without saying where", download.Reason);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AnyOtherAnswerIsAFailureThatNamesIt(HttpStatusCode status)
    {
        var download = await Downloader(new StubHandler(_ => new HttpResponseMessage(status))).DownloadAsync(Start, CancellationToken.None);

        Assert.False(download.Ok);
        Assert.Null(download.Content);
        Assert.Contains(((int)status).ToString(), download.Reason);
    }

    [Fact]
    public async Task ATooManyRequestsAnswerCarriesHowLongToLeaveTheHostAlone()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));

        var download = await Downloader(new StubHandler(_ => response)).DownloadAsync(Start, CancellationToken.None);

        Assert.False(download.Ok);
        Assert.Equal(TimeSpan.FromMinutes(2), download.RetryAfter);
    }

    [Fact]
    public async Task AnAbsurdRetryAfterIsCapped()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromDays(30));

        var download = await Downloader(new StubHandler(_ => response)).DownloadAsync(Start, CancellationToken.None);

        Assert.Equal(PluginFileDownloader.MaxRetryAfter, download.RetryAfter);
    }

    [Fact]
    public async Task ANetworkErrorIsAQuietFailure()
    {
        var download = await Downloader(new ThrowingHandler(new HttpRequestException("no route"))).DownloadAsync(Start, CancellationToken.None);

        Assert.False(download.Ok);
        Assert.Contains("could not be reached", download.Reason);
    }

    [Fact]
    public async Task ATimeoutIsAQuietFailureButTheCallersOwnCancellationIsNot()
    {
        var timedOut = await Downloader(new ThrowingHandler(new TaskCanceledException("timed out"))).DownloadAsync(Start, CancellationToken.None);
        Assert.False(timedOut.Ok);
        Assert.Contains("in time", timedOut.Reason);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Downloader(new WaitsForCancellationHandler()).DownloadAsync(Start, cancelled.Token));
    }

    private sealed class WaitsForCancellationHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task ThereIsNoCapOnHowBigADownloadMayBe()
    {
        var big = new byte[3 * 1024 * 1024];
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(big) });

        var download = await Downloader(handler).DownloadAsync(Start, CancellationToken.None);

        Assert.True(download.Ok);
        Assert.Equal(big.Length, download.Content!.Length);
    }

    // ---- only ever the public internet ---------------------------------------------------------------------------

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("172.32.0.1")]                    // just outside 172.16.0.0/12
    [InlineData("172.15.255.255")]                // just below it
    [InlineData("100.63.255.255")]                // just below the shared range
    [InlineData("100.128.0.1")]                   // just above it
    [InlineData("2606:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]                // IPv4 written as IPv6, judged as IPv4
    public void APublicAddressIsAllowed(string address)
    {
        Assert.True(PluginFileDownloader.IsPublicAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.1.100")]
    [InlineData("169.254.169.254")]               // cloud metadata
    [InlineData("100.64.0.1")]                    // carrier-grade NAT
    [InlineData("100.127.255.255")]
    [InlineData("192.0.0.8")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fec0::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]              // a private IPv4 hidden inside an IPv6 address
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    public void ALoopbackPrivateSharedLinkLocalMulticastOrReservedAddressIsRefused(string address)
    {
        Assert.False(PluginFileDownloader.IsPublicAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task TheRealHandlerNeverConnectsToThisMachineEvenWhenSomethingIsListeningThere()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var http = new HttpClient(PluginFileDownloader.CreateHandler());

            var error = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync($"http://127.0.0.1:{port}/plugin.cs"));

            Assert.Contains("public internet address", ExceptionText(error));
            Assert.False(listener.Pending(), "a connection reached the local listener");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TheRealHandlerRefusesAHostNameThatResolvesToThisMachineToo()
    {
        using var http = new HttpClient(PluginFileDownloader.CreateHandler());

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("http://localhost:9/plugin.cs"));

        Assert.Contains("public internet address", ExceptionText(error));
    }

    [Fact]
    public async Task TheRealHandlerDoesNotFollowRedirectsItself()
    {
        using var handler = PluginFileDownloader.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        await Task.CompletedTask;
    }

    private static string ExceptionText(Exception exception)
    {
        var text = new System.Text.StringBuilder();
        for (var e = exception; e is not null; e = e.InnerException)
        {
            text.AppendLine(e.Message);
        }

        return text.ToString();
    }

    // ---- the size bound: a host's word about size is never trusted ----------------------------------------------------------

    private const long Mb = 1024 * 1024;

    /// <summary>A body that produces zeros for ever (or up to <paramref name="total"/> bytes), counting what was actually read from it.</summary>
    private sealed class EndlessStream(long total = long.MaxValue) : Stream
    {
        public long Produced;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = (int)Math.Min(buffer.Length, total - Produced);
            buffer[..n].Clear();
            Produced += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static PluginFileDownloader DownloaderWithLimit(HttpMessageHandler handler, string? megabytes)
    {
        var settings = new Moq.Mock<RustArchon.Api.Infrastructure.IPlatformSettingsCache>();
        settings.Setup(s => s.GetStringAsync(RustArchon.Api.Infrastructure.PlatformSettingsRegistry.PluginFileMaxMegabytes)).Returns(Task.FromResult(megabytes));
        return new PluginFileDownloader(new HttpClient(handler), NullLogger<PluginFileDownloader>.Instance, settings.Object);
    }

    [Fact]
    public async Task AHostThatSaysItIsSendingMoreThanTheLimitIsRefusedWithoutReadingAnything()
    {
        var body = new EndlessStream();
        var content = new StreamContent(body);
        content.Headers.ContentLength = 500 * Mb;

        var download = await DownloaderWithLimit(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content }), null).DownloadAsync(Start, CancellationToken.None);

        Assert.False(download.Ok);
        Assert.True(download.TooLarge);
        Assert.Contains("100 MB", download.Reason);
        Assert.Equal(0, body.Produced);            // not one byte was read: the header alone was enough
    }

    [Fact]
    public async Task AHostThatSaysNothingAboutSizeAndNeverStopsIsCutOffAtTheLimit()
    {
        // Chunked, no Content-Length: the case a header check cannot see.
        var body = new EndlessStream();

        var download = await DownloaderWithLimit(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }), "5").DownloadAsync(Start, CancellationToken.None);

        Assert.True(download.TooLarge);
        Assert.InRange(body.Produced, 5 * Mb, 5 * Mb + 200_000);        // read up to the bound and one buffer past it, and no further
    }

    [Fact]
    public async Task AHostThatClaimsALittleAndSendsALotIsCutOffAtTheLimitToo()
    {
        var body = new EndlessStream();
        var content = new StreamContent(body);
        content.Headers.ContentLength = 10;                                // a lie

        var download = await DownloaderWithLimit(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content }), "5").DownloadAsync(Start, CancellationToken.None);

        Assert.True(download.TooLarge);
        Assert.InRange(body.Produced, 5 * Mb, 5 * Mb + 200_000);
    }

    [Fact]
    public async Task AFileExactlyAtTheLimitIsTaken()
    {
        var body = new EndlessStream(total: 2 * Mb);

        var download = await DownloaderWithLimit(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }), "2").DownloadAsync(Start, CancellationToken.None);

        Assert.True(download.Ok);
        Assert.Equal(2 * Mb, download.Content!.Length);
    }

    [Fact]
    public async Task AFileOneByteOverTheLimitIsNotTaken()
    {
        var body = new EndlessStream(total: 2 * Mb + 1);

        var download = await DownloaderWithLimit(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }), "2").DownloadAsync(Start, CancellationToken.None);

        Assert.True(download.TooLarge);
    }

    [Theory]
    [InlineData(null, 100)]
    [InlineData("", 100)]
    [InlineData("junk", 100)]
    [InlineData("0", 100)]
    [InlineData("-5", 100)]
    [InlineData("40", 40)]
    [InlineData("128", 128)]
    [InlineData("1000", 128)]                    // never more than a game server's plugin will fetch, whatever the setting says
    [InlineData("99999999999", 100)]             // does not parse as a number of megabytes at all
    public async Task TheLimitIsThePlatformSettingWithASaneDefaultAndAHardCeiling(string? setting, int expectedMegabytes)
    {
        var body = new EndlessStream();
        var declared = new StreamContent(body);
        declared.Headers.ContentLength = (expectedMegabytes + 1) * Mb;

        var over = await DownloaderWithLimit(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = declared }), setting).DownloadAsync(Start, CancellationToken.None);

        Assert.True(over.TooLarge);
        Assert.Contains($"{expectedMegabytes} MB", over.Reason);       // the limit in force, said in words
        Assert.Equal(0, body.Produced);                                 // refused by the header alone, so nothing was read
    }

    [Fact]
    public async Task WithNoSettingsAvailableTheDefaultApplies()
    {
        var content = new StreamContent(new EndlessStream());
        content.Headers.ContentLength = 101 * Mb;

        var download = await Downloader(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content })).DownloadAsync(Start, CancellationToken.None);

        Assert.True(download.TooLarge);
    }
}
