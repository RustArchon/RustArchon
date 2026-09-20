// Copyright ©2026 Scott Blomfield

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Oxide.Plugins;
using ArchonPlugin = Oxide.Plugins.RustArchon;

namespace RustArchon.Plugin.Tests;

/// <summary>
/// Sending the world's picture to the Panel: only the current world's file, only to an http(s) address, never twice at
/// once, never while a render is about to overwrite it, streamed exactly and without following a redirect (the address
/// carries a one-time token), with every outcome (including a failure) reported and never thrown into the game.
/// </summary>
[Collection("World")]
public sealed class RustArchonMapUploadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rustarchon-mapupload-tests-" + Guid.NewGuid().ToString("N"));

    public RustArchonMapUploadTests()
    {
        Directory.CreateDirectory(_directory);
        BasePlayer.activePlayerList.Clear();
        World.Size = 4500;
        World.Seed = 1234;
    }

    public void Dispose()
    {
        World.Size = 0;
        World.Seed = 0;
        if (Directory.Exists(_directory)) { Directory.Delete(_directory, recursive: true); }
    }

    private string MapFile => Path.Combine(_directory, "map_4500_1234.png");

    private const string Token = "tok_ABC-123_xyz";

    private static byte[] Picture(int length)
    {
        var bytes = new byte[length];
        new Random(7).NextBytes(bytes);
        bytes[0] = 0x89; bytes[1] = 0x50; bytes[2] = 0x4E; bytes[3] = 0x47;
        return bytes;
    }

    private ArchonPlugin Loaded(bool inline = true)
    {
        var plugin = new ArchonPlugin
        {
            SettingsFilePath = Path.Combine(_directory, "RustArchon", "settings.txt"),
            MapDirectory = _directory
        };
        if (inline) { plugin.RunInBackground = work => work(); }
        typeof(ArchonPlugin).GetMethod("Init", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(plugin, null);
        return plugin;
    }

    private static JsonElement Upload(ArchonPlugin plugin, params string[] args)
    {
        var arg = ConsoleSystem.Arg.WithArgs(args);
        plugin.CmdMapUpload(arg);
        return JsonDocument.Parse(Assert.Single(arg.Replies)).RootElement;
    }

    private static JsonElement Status(ArchonPlugin plugin)
    {
        var arg = ConsoleSystem.Arg.WithArgs();
        plugin.CmdMapStatus(arg);
        return JsonDocument.Parse(Assert.Single(arg.Replies)).RootElement.GetProperty("data");
    }

    // ---- a one-shot capture server -------------------------------------------------------------------------------

    private sealed record Captured(string RequestLine, Dictionary<string, string> Headers, byte[] Body);

    /// <summary>Answers the first request with <paramref name="status"/> after reading its whole body.</summary>
    private static (string Url, Task<Captured> Done) Capture(string status = "200 OK")
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var done = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var head = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(one) == 0) { break; }
                head.Add(one[0]);
                var n = head.Count;
                if (n >= 4 && head[n - 4] == '\r' && head[n - 3] == '\n' && head[n - 2] == '\r' && head[n - 1] == '\n') { break; }
            }

            var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var headers = lines.Skip(1).Select(l => l.Split(':', 2)).ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim());
            var length = headers.TryGetValue("content-length", out var value) ? int.Parse(value) : 0;
            var body = new byte[length];
            var got = 0;
            while (got < length)
            {
                var read = await stream.ReadAsync(body.AsMemory(got, length - got));
                if (read == 0) { break; }
                got += read;
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            await stream.FlushAsync();
            listener.Stop();
            return new Captured(lines[0], headers, body);
        });
        return ($"http://127.0.0.1:{port}/ingest/plugin-map", done);
    }

    // ---- the happy path ------------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePictureIsPostedExactlyAsRawPngBytesWithItsLength()
    {
        var picture = Picture(300_000);
        File.WriteAllBytes(MapFile, picture);
        var plugin = Loaded();
        var (url, done) = Capture();

        var reply = await Task.Run(() => Upload(plugin, url, Token));
        var captured = await done;

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("map_4500_1234.png", reply.GetProperty("data").GetProperty("file").GetString());
        Assert.Equal(300_000, reply.GetProperty("data").GetProperty("bytes").GetInt64());
        Assert.StartsWith("POST /ingest/plugin-map ", captured.RequestLine);
        Assert.Equal("image/png", captured.Headers["content-type"]);
        Assert.Equal("300000", captured.Headers["content-length"]);
        Assert.Equal(picture, captured.Body);
    }

    [Fact]
    public async Task ASuccessfulUploadIsReportedDoneWithItsFileAndSize()
    {
        File.WriteAllBytes(MapFile, Picture(5000));
        var plugin = Loaded();
        var (url, done) = Capture();

        await Task.Run(() => Upload(plugin, url, Token));
        await done;

        var upload = Status(plugin).GetProperty("upload");
        Assert.Equal("done", upload.GetProperty("state").GetString());
        Assert.Equal("map_4500_1234.png", upload.GetProperty("file").GetString());
        Assert.Equal(5000, upload.GetProperty("bytes").GetInt64());
        Assert.False(upload.TryGetProperty("error", out _));
        Assert.True(upload.GetProperty("atMs").GetInt64() > 0);
    }

    [Fact]
    public void BeforeAnyUploadTheStatusHasNoUploadBlock()
    {
        var plugin = Loaded();

        Assert.Equal(JsonValueKind.Null, Status(plugin).GetProperty("upload").ValueKind);
    }

    // ---- failures ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("404 Not Found", "http 404")]
    [InlineData("403 Forbidden", "http 403")]
    [InlineData("500 Internal Server Error", "http 500")]
    public async Task ARefusalFromThePanelIsReportedAsFailedWithItsStatus(string status, string expected)
    {
        File.WriteAllBytes(MapFile, Picture(2000));
        var plugin = Loaded();
        var (url, done) = Capture(status);

        await Task.Run(() => Upload(plugin, url, Token));
        await done;

        var upload = Status(plugin).GetProperty("upload");
        Assert.Equal("failed", upload.GetProperty("state").GetString());
        Assert.Equal(expected, upload.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ARedirectIsNotFollowedBecauseTheAddressCarriesAOneTimeToken()
    {
        File.WriteAllBytes(MapFile, Picture(2000));
        var plugin = Loaded();
        var (url, done) = Capture("302 Found");

        await Task.Run(() => Upload(plugin, url, Token));
        await done;

        Assert.Equal("failed", Status(plugin).GetProperty("upload").GetProperty("state").GetString());
    }

    [Fact]
    public void ANothingListeningAddressFailsCleanlyNotWithAnException()
    {
        File.WriteAllBytes(MapFile, Picture(2000));
        var plugin = Loaded();
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();

        var reply = Upload(plugin, $"http://127.0.0.1:{port}/x", Token);

        Assert.True(reply.GetProperty("ok").GetBoolean());                        // started; the failure is in the status
        Assert.Equal("failed", Status(plugin).GetProperty("upload").GetProperty("state").GetString());
    }

    [Fact]
    public async Task AFailedUploadCanBeRetried()
    {
        File.WriteAllBytes(MapFile, Picture(2000));
        var plugin = Loaded();
        var (badUrl, badDone) = Capture("500 Internal Server Error");
        await Task.Run(() => Upload(plugin, badUrl, Token));
        await badDone;
        Assert.Equal("failed", Status(plugin).GetProperty("upload").GetProperty("state").GetString());

        var (url, done) = Capture();
        await Task.Run(() => Upload(plugin, url, Token));
        await done;

        Assert.Equal("done", Status(plugin).GetProperty("upload").GetProperty("state").GetString());
    }

    // ---- refusals ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("//example.com/x")]
    [InlineData("http://user:pass@example.com/x")]
    public void OnlyAnAbsoluteHttpOrHttpsAddressWithoutCredentialsIsAccepted(string url)
    {
        File.WriteAllBytes(MapFile, Picture(100));
        var plugin = Loaded();

        var reply = Upload(plugin, url, Token);

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("bad_url", reply.GetProperty("err").GetString());
        Assert.Equal(JsonValueKind.Null, Status(plugin).GetProperty("upload").ValueKind);   // nothing was started
    }

    [Fact]
    public void AnAddressLongerThanTheLimitIsRefused()
    {
        File.WriteAllBytes(MapFile, Picture(100));
        var plugin = Loaded();

        var reply = Upload(plugin, "http://example.com/" + new string('a', ArchonUpload.MaxUrlLength), Token);

        Assert.Equal("bad_url", reply.GetProperty("err").GetString());
    }

    [Fact]
    public void AnythingButExactlyAnAddressAndATokenGetsAUsageError()
    {
        var plugin = Loaded();

        var none = ConsoleSystem.Arg.WithArgs();
        plugin.CmdMapUpload(none);
        var addressOnly = ConsoleSystem.Arg.WithArgs("http://a/");
        plugin.CmdMapUpload(addressOnly);
        var three = ConsoleSystem.Arg.WithArgs("http://a/", Token, "extra");
        plugin.CmdMapUpload(three);

        Assert.Contains("usage", Assert.Single(none.Replies));
        Assert.Contains("usage", Assert.Single(addressOnly.Replies));
        Assert.Contains("usage", Assert.Single(three.Replies));
    }

    // ---- the token -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheTokenTravelsInAHeaderAndNeverInTheAddressOrTheBody()
    {
        File.WriteAllBytes(MapFile, Picture(3000));
        var plugin = Loaded();
        var (url, done) = Capture();

        await Task.Run(() => Upload(plugin, url, Token));
        var captured = await done;

        Assert.Equal(Token, captured.Headers["x-rustarchon-upload-token"]);
        Assert.DoesNotContain(Token, captured.RequestLine);
        Assert.False(Encoding.ASCII.GetString(captured.Body).Contains(Token));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("line\nbreak")]
    [InlineData("cr\rlf")]
    [InlineData("colon:here")]
    [InlineData("semi;colon")]
    [InlineData("slash/here")]
    [InlineData("plus+equals=")]
    [InlineData("ünïcode")]
    public void ATokenWithAnythingButBase64UrlCharactersIsRefusedSoItCanNeverInjectAHeader(string token)
    {
        File.WriteAllBytes(MapFile, Picture(100));
        var plugin = Loaded();

        var reply = Upload(plugin, "http://127.0.0.1:1/x", token);

        Assert.Equal("bad_token", reply.GetProperty("err").GetString());
        Assert.Equal(JsonValueKind.Null, Status(plugin).GetProperty("upload").ValueKind);   // nothing was started
    }

    [Fact]
    public void ATokenLongerThanTheLimitIsRefused()
    {
        File.WriteAllBytes(MapFile, Picture(100));
        var plugin = Loaded();

        var reply = Upload(plugin, "http://127.0.0.1:1/x", new string('a', ArchonUpload.MaxTokenLength + 1));

        Assert.Equal("bad_token", reply.GetProperty("err").GetString());
    }

    [Fact]
    public void ARealLengthTokenIsAccepted()
    {
        Assert.True(ArchonUpload.IsValidToken("Kq3j9-_zV0aH5oXr1mYb7uT2wNc8dLfE4gPiS6kQ0A"));
        Assert.False(ArchonUpload.IsValidToken(""));
        Assert.False(ArchonUpload.IsValidToken(null!));
    }

    [Fact]
    public void WithNoPictureForThisWorldThereIsNothingToSend()
    {
        var plugin = Loaded();

        var reply = Upload(plugin, "http://127.0.0.1:1/x", Token);

        Assert.Equal("no_map", reply.GetProperty("err").GetString());
    }

    [Fact]
    public void AnEmptyPictureFileIsNotSent()
    {
        File.WriteAllBytes(MapFile, []);
        var plugin = Loaded();

        Assert.Equal("no_map", Upload(plugin, "http://127.0.0.1:1/x", Token).GetProperty("err").GetString());
    }

    [Fact]
    public void BeforeTheWorldHasLoadedThereIsNothingToSend()
    {
        World.Size = 0;
        var plugin = Loaded();

        Assert.Equal("no_world", Upload(plugin, "http://127.0.0.1:1/x", Token).GetProperty("err").GetString());
    }

    [Fact]
    public void AnotherWorldsPictureIsNeverSent()
    {
        File.WriteAllBytes(MapFile, Picture(100));
        var plugin = Loaded();
        World.Seed = 999;                       // a wipe: the file on disk belongs to the old world

        Assert.Equal("no_map", Upload(plugin, "http://127.0.0.1:1/x", Token).GetProperty("err").GetString());
    }

    [Fact]
    public void ASecondUploadWhileOneIsRunningIsRefused()
    {
        File.WriteAllBytes(MapFile, Picture(100));
        Action? pending = null;
        var plugin = Loaded(inline: false);
        plugin.RunInBackground = work => pending = work;      // the first upload "runs" until the test lets it

        Assert.True(Upload(plugin, "http://127.0.0.1:1/x", Token).GetProperty("ok").GetBoolean());
        var second = Upload(plugin, "http://127.0.0.1:1/y", Token);

        Assert.Equal("upload_running", second.GetProperty("err").GetString());
        Assert.Equal("uploading", Status(plugin).GetProperty("upload").GetProperty("state").GetString());
        Assert.NotNull(pending);
    }

    [Fact]
    public void ARenderIsRefusedWhileAnUploadIsReadingTheFile()
    {
        File.WriteAllBytes(MapFile, Picture(100));
        var plugin = Loaded(inline: false);
        plugin.RunInBackground = _ => { };
        Upload(plugin, "http://127.0.0.1:1/x", Token);

        var arg = ConsoleSystem.Arg.WithArgs("overwrite");
        plugin.CmdMapRender(arg);

        Assert.Contains("upload_running", Assert.Single(arg.Replies));
    }

    [Fact]
    public void AnUploadIsRefusedWhileARenderIsAboutToStart()
    {
        var plugin = Loaded();
        var render = ConsoleSystem.Arg.WithArgs();
        plugin.CmdMapRender(render);
        File.WriteAllBytes(MapFile, Picture(100));      // a picture appears (or exists) but a render will overwrite it

        var reply = Upload(plugin, "http://127.0.0.1:1/x", Token);

        Assert.Equal("render_pending", reply.GetProperty("err").GetString());
    }

    [Fact]
    public void TheUploadCommandIsRconOnly()
    {
        File.WriteAllBytes(MapFile, Picture(100));
        var plugin = Loaded();
        var arg = ConsoleSystem.Arg.WithArgs("http://127.0.0.1:1/x", Token);
        arg.Connection = new object();

        plugin.CmdMapUpload(arg);

        Assert.Empty(arg.Replies);
        Assert.Equal(JsonValueKind.Null, Status(plugin).GetProperty("upload").ValueKind);
    }

    [Fact]
    public async Task ThePictureShrinkingMidUploadIsAFailureNotACrash()
    {
        // The length was read first; if the file is shorter by the time it is streamed, the upload fails and says so.
        File.WriteAllBytes(MapFile, Picture(10_000));
        var (url, done) = Capture();

        var error = await Task.Run(() => ArchonUpload.Post(new Uri(url), Token, MapFile, 20_000));

        Assert.False(string.IsNullOrEmpty(error));
        await Task.WhenAny(done, Task.Delay(2000));
    }

    [Fact]
    public void AMissingFileIsAFailureNotACrash()
    {
        var error = ArchonUpload.Post(new Uri("http://127.0.0.1:1/x"), Token, Path.Combine(_directory, "gone.png"), 100);

        Assert.False(string.IsNullOrEmpty(error));
    }
}
