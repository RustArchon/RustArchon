// Copyright ©2026 Scott Blomfield

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Oxide.Plugins;
using ArchonPlugin = Oxide.Plugins.RustArchon;

namespace RustArchon.Plugin.Tests;

/// <summary>
/// The Updater's whole life cycle, against a temp plugins folder with the download, the clock and the marker under the
/// test's control: it accepts only a correctly signed, same-key, newer, as-asked version; keeps a backup; confirms the
/// new version by its "loaded" marker; and puts the old one back if that never appears. What a real game server does
/// with the swapped file (reload, compile) is proven live; everything else is here.
/// </summary>
public sealed class RustArchonUpdaterTests : IDisposable
{
    private const string Marker = "// RUSTARCHON-SIG-V1: ";

    private readonly RSA _panelKey = RSA.Create(2048);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rustarchon-updater-" + Guid.NewGuid().ToString("N"));
    private readonly string _plugins;
    private readonly string _data;
    private DateTime _now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    public RustArchonUpdaterTests()
    {
        _plugins = Path.Combine(_root, "plugins");
        _data = Path.Combine(_root, "data", "RustArchon");
        Directory.CreateDirectory(_plugins);
        Directory.CreateDirectory(_data);
    }

    public void Dispose()
    {
        _panelKey.Dispose();
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    private string Modulus => Convert.ToBase64String(_panelKey.ExportParameters(false).Modulus!);
    private string Exponent => Convert.ToBase64String(_panelKey.ExportParameters(false).Exponent!);
    private string MainPath => Path.Combine(_plugins, "RustArchon.cs");

    // A main-plugin script the way the Panel serves it: [Info] version, the two key constants, signature line last.
    private byte[] Script(string version, RSA? signer = null, string? modulus = null, string? exponent = null, bool sign = true, string body = "")
    {
        var text = $"[Info(\"RustArchon\", \"RustArchon\", \"{version}\")]\nclass X {{\nconst string TrustedModulus = \"{modulus ?? Modulus}\";\n"
            + $"const string TrustedExponent = \"{exponent ?? Exponent}\";\n{body}}}\n";
        var payload = Encoding.UTF8.GetBytes(text);
        if (!sign) { return payload; }
        var signature = (signer ?? _panelKey).SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return payload.Concat(Encoding.ASCII.GetBytes(Marker + Convert.ToBase64String(signature) + "\n")).ToArray();
    }

    private byte[] InstallCurrent(string version = "0.2.0")
    {
        var bytes = Script(version);
        File.WriteAllBytes(MainPath, bytes);
        return bytes;
    }

    private RustArchonUpdater NewUpdater(Func<string, byte[]>? download = null, bool stamped = true)
    {
        var updater = new RustArchonUpdater
        {
            PluginDirectory = _plugins,
            DataDirectory = _data,
            UtcNow = () => _now,
            Download = download ?? (_ => throw new InvalidOperationException("no download expected"))
        };
        if (stamped)
        {
            updater.TrustedModulus = Modulus;
            updater.TrustedExponent = Exponent;
        }
        typeof(RustArchonUpdater).GetMethod("Init", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(updater, null);
        return updater;
    }

    private static ConsoleSystem.Arg Rcon(params string[] args) => ConsoleSystem.Arg.WithArgs(args);

    private static JsonElement Reply(ConsoleSystem.Arg arg) => JsonDocument.Parse(Assert.Single(arg.Replies)).RootElement;

    private async Task<RustArchonUpdater> StartUpdateAsync(byte[] served, string version = "0.2.1", bool stamped = true)
    {
        InstallCurrent();
        var updater = NewUpdater(_ => served, stamped);
        var reply = JsonDocument.Parse(updater.Begin(version, "http://192.0.2.1/ingest/plugin/x/y")).RootElement;
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        await updater.DownloadTask;
        return updater;
    }

    private void WriteMarker(string version, DateTime utc) =>
        File.WriteAllText(Path.Combine(_data, "loaded.txt"), $"version={version}\nutc={utc:o}\n");

    // ---- accepting a request -------------------------------------------------------------------------------

    [Fact]
    public void Commands_AreIgnoredFromAnInGameConsole()
    {
        InstallCurrent();
        var updater = NewUpdater();
        var arg = Rcon("0.2.1", "http://192.0.2.1/x");
        arg.Connection = new object();

        updater.CmdUpdate(arg);

        Assert.Empty(arg.Replies);
        Assert.Equal("idle", updater.State.Phase);
    }

    [Theory]
    [InlineData]
    [InlineData("0.2.1")]
    [InlineData("0.2.1", "http://a/b", "extra")]
    public void WrongArgumentCountGetsAUsageError(params string[] args)
    {
        InstallCurrent();
        var updater = NewUpdater();
        var arg = Rcon(args);

        updater.CmdUpdate(arg);

        Assert.Equal("usage", Reply(arg).GetProperty("err").GetString());
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3-beta")]
    [InlineData("")]
    [InlineData("1.2.x")]
    public void ARequestForAMalformedVersionIsRefused(string version)
    {
        InstallCurrent();
        var updater = NewUpdater();

        var reply = JsonDocument.Parse(updater.Begin(version, "http://192.0.2.1/x")).RootElement;

        Assert.Equal("bad_version", reply.GetProperty("err").GetString());
        Assert.Equal("idle", updater.State.Phase);
    }

    [Theory]
    [InlineData("ftp://192.0.2.1/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/relative/path")]
    [InlineData("not a url")]
    [InlineData("")]
    public void ARequestForAnythingButAnAbsoluteHttpUrlIsRefused(string url)
    {
        InstallCurrent();
        var updater = NewUpdater();

        var reply = JsonDocument.Parse(updater.Begin("0.2.1", url)).RootElement;

        Assert.Equal("bad_url", reply.GetProperty("err").GetString());
    }

    [Fact]
    public void WithNoMainPluginFileThereIsNothingToUpdate()
    {
        var updater = NewUpdater(); // nothing installed

        var reply = JsonDocument.Parse(updater.Begin("0.2.1", "http://192.0.2.1/x")).RootElement;

        Assert.Equal("main_missing", reply.GetProperty("err").GetString());
    }

    [Theory]
    [InlineData("0.2.0")] // same
    [InlineData("0.1.9")] // older
    [InlineData("0.1.99")]
    public void AnEqualOrOlderVersionIsRefusedNoSilentDowngrade(string offered)
    {
        InstallCurrent("0.2.0");
        var updater = NewUpdater();

        var reply = JsonDocument.Parse(updater.Begin(offered, "http://192.0.2.1/x")).RootElement;

        Assert.Equal("not_newer", reply.GetProperty("err").GetString());
        Assert.Equal("idle", updater.State.Phase);
    }

    [Fact]
    public void AnUpdateAlreadyInProgressRefusesASecond()
    {
        InstallCurrent();
        var gate = new ManualResetEventSlim();
        var updater = NewUpdater(_ => { gate.Wait(); return []; });
        Assert.True(JsonDocument.Parse(updater.Begin("0.2.1", "http://192.0.2.1/x")).RootElement.GetProperty("ok").GetBoolean());

        var second = JsonDocument.Parse(updater.Begin("0.2.2", "http://192.0.2.1/y")).RootElement;

        Assert.Equal("busy", second.GetProperty("err").GetString());
        gate.Set();
    }

    // ---- the happy path ------------------------------------------------------------------------------------

    [Fact]
    public async Task AGoodUpdateIsSwappedInWithABackupAndWaitsForTheMarker()
    {
        var old = InstallCurrent("0.2.0");
        var served = Script("0.2.1");
        var updater = await StartUpdateAsync(served);

        updater.Tick();

        Assert.Equal("loading", updater.State.Phase);
        Assert.Equal(served, File.ReadAllBytes(MainPath));
        Assert.Equal(old, File.ReadAllBytes(MainPath + ".bak"));
        Assert.False(File.Exists(MainPath + ".new")); // the temp file was consumed by the swap
    }

    [Fact]
    public async Task ItSucceedsOnceTheNewVersionWritesItsMarker()
    {
        var updater = await StartUpdateAsync(Script("0.2.1"));
        updater.Tick(); // swap
        _now = _now.AddSeconds(3);
        WriteMarker("0.2.1", _now);

        updater.Tick();

        Assert.Equal("succeeded", updater.State.Phase);
        Assert.Equal("", updater.State.Reason);
    }

    [Fact]
    public async Task AMarkerForTheWrongVersionDoesNotCount()
    {
        var updater = await StartUpdateAsync(Script("0.2.1"));
        updater.Tick();
        _now = _now.AddSeconds(3);
        WriteMarker("0.2.0", _now); // the OLD version starting up again

        updater.Tick();

        Assert.Equal("loading", updater.State.Phase);
    }

    [Fact]
    public async Task AMarkerLeftOverFromBeforeTheSwapDoesNotCount()
    {
        WriteMarker("0.2.1", _now.AddMinutes(-30)); // stale, from some earlier attempt
        var updater = await StartUpdateAsync(Script("0.2.1"));
        updater.Tick();

        updater.Tick();

        Assert.Equal("loading", updater.State.Phase);
    }

    // ---- rollback ------------------------------------------------------------------------------------------

    [Fact]
    public async Task IfTheNewVersionNeverLoadsTheBackupIsPutBack()
    {
        var old = InstallCurrent("0.2.0");
        var updater = NewUpdater(_ => Script("0.2.1"));
        updater.Begin("0.2.1", "http://192.0.2.1/x");
        await updater.DownloadTask;
        updater.Tick(); // swapped in; the new file is NOT going to write a marker (it failed to compile)

        _now = _now.AddSeconds(RustArchonUpdater.LoadWaitSeconds + 1);
        updater.Tick();

        Assert.Equal("rolled-back", updater.State.Phase);
        Assert.Equal(old, File.ReadAllBytes(MainPath));
        Assert.Contains("did not report loading", updater.State.Reason);
    }

    [Fact]
    public async Task ItDoesNotRollBackWhileThereIsStillTimeToLoad()
    {
        var updater = await StartUpdateAsync(Script("0.2.1"));
        updater.Tick();
        _now = _now.AddSeconds(RustArchonUpdater.LoadWaitSeconds - 5);

        updater.Tick();

        Assert.Equal("loading", updater.State.Phase);
    }

    [Fact]
    public async Task ARollbackWithNoBackupIsReportedAsFailedNotSuccess()
    {
        var updater = await StartUpdateAsync(Script("0.2.1"));
        updater.Tick();
        File.Delete(MainPath + ".bak");
        _now = _now.AddSeconds(RustArchonUpdater.LoadWaitSeconds + 1);

        updater.Tick();

        Assert.Equal("failed", updater.State.Phase);
        Assert.Contains("rollback impossible", updater.State.Reason);
    }

    [Fact]
    public async Task ATickAfterAResolvedUpdateDoesNothingMore()
    {
        var updater = await StartUpdateAsync(Script("0.2.1"));
        updater.Tick();
        _now = _now.AddSeconds(RustArchonUpdater.LoadWaitSeconds + 1);
        updater.Tick(); // rolled back
        var main = File.ReadAllBytes(MainPath);

        updater.Tick();
        updater.Tick();

        Assert.Equal("rolled-back", updater.State.Phase);
        Assert.Equal(main, File.ReadAllBytes(MainPath));
    }

    // ---- refusing a bad download: the main plugin must be left exactly as it was ---------------------------

    private async Task AssertRefusedAsync(byte[] served, string expectedReasonPrefix, string version = "0.2.1", bool stamped = true)
    {
        var old = InstallCurrent("0.2.0");
        var updater = NewUpdater(_ => served, stamped);
        Assert.True(JsonDocument.Parse(updater.Begin(version, "http://192.0.2.1/x")).RootElement.GetProperty("ok").GetBoolean());
        await updater.DownloadTask;

        updater.Tick();

        Assert.Equal("failed", updater.State.Phase);
        Assert.StartsWith(expectedReasonPrefix, updater.State.Reason);
        Assert.Equal(old, File.ReadAllBytes(MainPath)); // untouched
        Assert.False(File.Exists(MainPath + ".bak"));
        Assert.False(File.Exists(MainPath + ".new"));
    }

    [Fact]
    public async Task AFileSignedByAnotherKeyIsRefused()
    {
        using var attacker = RSA.Create(2048);

        await AssertRefusedAsync(Script("0.2.1", signer: attacker), "signature_invalid");
    }

    [Fact]
    public async Task AFileWithASingleAlteredByteIsRefused()
    {
        var served = Script("0.2.1");
        served[20] ^= 1;

        await AssertRefusedAsync(served, "signature_invalid");
    }

    [Fact]
    public async Task AnUnsignedFileIsRefused()
    {
        await AssertRefusedAsync(Script("0.2.1", sign: false), "signature_unsigned");
    }

    [Fact]
    public async Task AFileSignedByOurKeyButStampedWithAnotherThatNeverSignedItIsRefused()
    {
        // Validly signed by the key we trust, but the file's own constants name a different key that has not vouched
        // for it: that would move this server to a key nobody proved they hold. A real bridge carries that key's own
        // signature too (see RustArchonUpdaterBridgeTests).
        using var other = RSA.Create(2048);
        var otherModulus = Convert.ToBase64String(other.ExportParameters(false).Modulus!);

        await AssertRefusedAsync(Script("0.2.1", modulus: otherModulus), "bridge_not_cosigned");
    }

    [Fact]
    public async Task AFileWhoseVersionIsNotTheOneAskedForIsRefused()
    {
        await AssertRefusedAsync(Script("0.2.5"), "version_mismatch", version: "0.2.4");
    }

    [Fact]
    public async Task AValidlySignedFileWithNoVersionIsRefused()
    {
        var text = $"class X {{\nconst string TrustedModulus = \"{Modulus}\";\nconst string TrustedExponent = \"{Exponent}\";\n}}\n";
        var payload = Encoding.UTF8.GetBytes(text);
        var signature = _panelKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var served = payload.Concat(Encoding.ASCII.GetBytes(Marker + Convert.ToBase64String(signature) + "\n")).ToArray();

        await AssertRefusedAsync(served, "no_version");
    }

    [Fact]
    public async Task ADeveloperCopyOfTheUpdaterWithNoTrustedKeyInstallsNothing()
    {
        // Fail closed: an updater that trusts no key must not treat "no key" as "trust anything".
        await AssertRefusedAsync(Script("0.2.1"), "updater_not_stamped", stamped: false);
    }

    [Fact]
    public async Task ADownloadThatThrowsIsReportedAndChangesNothing()
    {
        var old = InstallCurrent("0.2.0");
        var updater = NewUpdater(_ => throw new IOException("connection refused"));
        updater.Begin("0.2.1", "http://192.0.2.1/x");
        await updater.DownloadTask;

        updater.Tick();

        Assert.Equal("failed", updater.State.Phase);
        Assert.StartsWith("download_failed", updater.State.Reason);
        Assert.Contains("connection refused", updater.State.Reason);
        Assert.Equal(old, File.ReadAllBytes(MainPath));
    }

    [Fact]
    public void ADownloadThatNeverAnswersTimesOutAndChangesNothing()
    {
        var old = InstallCurrent("0.2.0");
        var gate = new ManualResetEventSlim();
        var updater = NewUpdater(_ => { gate.Wait(); return []; });
        updater.Begin("0.2.1", "http://192.0.2.1/x");

        updater.Tick(); // not done, not yet timed out
        Assert.Equal("downloading", updater.State.Phase);
        _now = _now.AddSeconds(RustArchonUpdater.DownloadTimeoutSeconds + 16);
        updater.Tick();

        Assert.Equal("failed", updater.State.Phase);
        Assert.StartsWith("download_timeout", updater.State.Reason);
        Assert.Equal(old, File.ReadAllBytes(MainPath));
        gate.Set();
    }

    // ---- surviving a reload of the Updater itself ----------------------------------------------------------

    [Fact]
    public async Task AReloadedUpdaterKeepsWatchingAnUpdateThatWasSwappedInAndConfirmsIt()
    {
        var first = await StartUpdateAsync(Script("0.2.1"));
        first.Tick(); // swapped in, loading
        _now = _now.AddSeconds(4);
        WriteMarker("0.2.1", _now);

        var reloaded = NewUpdater(); // a fresh instance reading the same status file

        Assert.Equal("loading", reloaded.State.Phase);
        Assert.Equal("0.2.1", reloaded.State.TargetVersion);
        reloaded.Tick();
        Assert.Equal("succeeded", reloaded.State.Phase);
    }

    [Fact]
    public async Task AReloadedUpdaterStillRollsBackIfTheNewVersionNeverLoads()
    {
        var old = InstallCurrent("0.2.0");
        var first = NewUpdater(_ => Script("0.2.1"));
        first.Begin("0.2.1", "http://192.0.2.1/x");
        await first.DownloadTask;
        first.Tick();

        var reloaded = NewUpdater();
        _now = _now.AddSeconds(RustArchonUpdater.LoadWaitSeconds + 1);
        reloaded.Tick();

        Assert.Equal("rolled-back", reloaded.State.Phase);
        Assert.Equal(old, File.ReadAllBytes(MainPath));
    }

    [Fact]
    public void ADownloadInterruptedByAReloadIsMarkedFailedNotLeftHanging()
    {
        InstallCurrent();
        File.WriteAllText(Path.Combine(_data, "update-status.txt"),
            $"phase=downloading\ntarget=0.2.1\nreason=\nstarted={_now:o}\nswapped={DateTime.MinValue:o}\n");

        var updater = NewUpdater();

        Assert.Equal("failed", updater.State.Phase);
        Assert.Contains("interrupted", updater.State.Reason);
    }

    [Fact]
    public void ADamagedStatusFileMeansIdleNotACrash()
    {
        File.WriteAllText(Path.Combine(_data, "update-status.txt"), "\0\0garbage\nno equals\n=novalue\nphase\n");

        var updater = NewUpdater();

        Assert.Equal("idle", updater.State.Phase);
    }

    // ---- status --------------------------------------------------------------------------------------------

    [Fact]
    public async Task StatusReportsThePhaseTheVersionsAndTheUpdatersOwnSigning()
    {
        var updater = await StartUpdateAsync(Script("0.2.1"));
        updater.Tick();
        var arg = Rcon();

        updater.CmdStatus(arg);

        var data = Reply(arg).GetProperty("data");
        Assert.Equal("loading", data.GetProperty("phase").GetString());
        Assert.Equal("0.2.1", data.GetProperty("targetVersion").GetString());
        Assert.Equal("0.2.1", data.GetProperty("installedVersion").GetString());
        Assert.Equal("0.1.0", data.GetProperty("updaterVersion").GetString());
        Assert.True(data.TryGetProperty("signing", out _));
    }

    [Fact]
    public void StatusOfAnIdleUpdaterIsIdle()
    {
        InstallCurrent();
        var updater = NewUpdater();
        var arg = Rcon();

        updater.CmdStatus(arg);

        Assert.Equal("idle", Reply(arg).GetProperty("data").GetProperty("phase").GetString());
    }

    // ---- the main plugin's marker --------------------------------------------------------------------------

    [Fact]
    public void TheMainPluginWritesItsLoadedMarkerOnInit()
    {
        var settingsPath = Path.Combine(_data, "settings.txt");
        var plugin = new ArchonPlugin { SettingsFilePath = settingsPath };

        typeof(ArchonPlugin).GetMethod("Init", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(plugin, null);

        var lines = File.ReadAllLines(Path.Combine(_data, "loaded.txt"));
        Assert.Equal("version=0.1.0", lines[0]); // the stub's Version; at runtime this is the [Info] version
        Assert.StartsWith("utc=", lines[1]);
        Assert.True(DateTime.TryParse(lines[1][4..], null, System.Globalization.DateTimeStyles.RoundtripKind, out var utc));
        Assert.True((DateTime.UtcNow - utc.ToUniversalTime()).TotalSeconds < 30);
    }

    [Fact]
    public void TheMarkerTheMainPluginWritesIsTheOneTheUpdaterReads()
    {
        // Both sides hard-code the file name; if either changes, every update would wrongly roll back.
        Assert.Equal(ArchonPlugin.LoadedMarkerFileName, RustArchonUpdater.LoadedMarkerFileName);
    }

    [Fact]
    public void TheMainPluginWithNoDataFolderStillLoads()
    {
        var plugin = new ArchonPlugin { SettingsFilePath = null };

        var ex = Record.Exception(() => plugin.WriteLoadedMarker());

        Assert.Null(ex);
    }

    // ---- small pure helpers --------------------------------------------------------------------------------

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("0.0.0", true)]
    [InlineData("10.20.30", true)]
    [InlineData("1.2", false)]
    [InlineData("1.2.3.4", false)]
    [InlineData("1.2.3 ", false)]
    [InlineData("a.b.c", false)]
    [InlineData(null, false)]
    public void IsVersionAcceptsOnlyThreeNumericParts(string? text, bool expected)
    {
        Assert.Equal(expected, UpdaterLogic.IsVersion(text!));
    }

    [Theory]
    [InlineData("0.2.1", "0.2.0", 1)]
    [InlineData("0.2.0", "0.2.0", 0)]
    [InlineData("0.2.0", "0.2.1", -1)]
    [InlineData("0.10.0", "0.9.0", 1)] // a string comparison gets this wrong
    [InlineData("1.0.0", "0.99.99", 1)]
    [InlineData("0.2.10", "0.2.9", 1)]
    public void VersionsAreComparedNumericallyPartByPart(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(UpdaterLogic.CompareVersions(a, b)));
    }

    [Fact]
    public void ReadInfoVersionReadsTheMainPluginAndIgnoresTheUpdatersOwnAttribute()
    {
        var text = "[Info(\"RustArchonUpdater\", \"RustArchon\", \"9.9.9\")]\n[Info(\"RustArchon\", \"RustArchon\", \"0.2.1\")]\n";

        Assert.Equal("0.2.1", UpdaterLogic.ReadInfoVersion(text));
        Assert.Null(UpdaterLogic.ReadInfoVersion("[Info(\"RustArchonUpdater\", \"RustArchon\", \"9.9.9\")]"));
        Assert.Null(UpdaterLogic.ReadInfoVersion(null!));
    }

    // ---- the Updater's copy of the signature check must equal the main plugin's ---------------------------

    [Fact]
    public void TheUpdatersSignatureCheckAgreesWithTheMainPluginsOnEveryCase()
    {
        using var other = RSA.Create(2048);
        var tampered = Script("0.2.1");
        tampered[10] ^= 1;
        var cases = new Dictionary<string, byte[]>
        {
            ["valid"] = Script("0.2.1"),
            ["tampered"] = tampered,
            ["other key"] = Script("0.2.1", signer: other),
            ["unsigned"] = Script("0.2.1", sign: false),
            ["bad base64"] = Encoding.UTF8.GetBytes("class X { }\n" + Marker + "!!! not base64\n"),
            ["empty signature"] = Encoding.UTF8.GetBytes("class X { }\n" + Marker + "\n"),
            ["crlf"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Script("0.2.1")).Replace("\n", "\r\n")),
            ["marker mid-line only"] = Encoding.UTF8.GetBytes("class X { const string M = \"" + Marker + "AAAA\"; }\n"),
        };

        foreach (var (name, file) in cases)
        {
            var main = ArchonIntegrity.Check(file, Modulus, Exponent);
            var updater = UpdaterIntegrity.Check(file, Modulus, Exponent);
            Assert.True(main.State == updater.State && main.KeyFingerprint == updater.KeyFingerprint,
                $"{name}: main says {main.State}/{main.KeyFingerprint}, updater says {updater.State}/{updater.KeyFingerprint}");
        }
    }

    [Fact]
    public void BothPluginsAgreeOnTheMarkerAndThePlaceholders()
    {
        Assert.Equal(ArchonIntegrity.SignatureMarker, UpdaterIntegrity.SignatureMarker);
        Assert.Equal(ArchonIntegrity.TrustedModulus, UpdaterIntegrity.TrustedModulus);
        Assert.Equal(ArchonIntegrity.TrustedExponent, UpdaterIntegrity.TrustedExponent);
        Assert.Equal(ArchonIntegrity.Fingerprint(Modulus), UpdaterIntegrity.Fingerprint(Modulus));
    }

    // ---- the real download, against a local HTTP server -----------------------------------------------------

    // A minimal one-shot HTTP server: answers the first request with exactly these bytes. Raw sockets, not
    // HttpListener, so it needs no URL reservation and cannot be confused by a framework's own behavior.
    private static (string Url, Task Done) Serve(byte[] rawResponse)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var done = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer); // the request line and headers; content is irrelevant
            await stream.WriteAsync(rawResponse);
            await stream.FlushAsync();
            listener.Stop();
        });
        return ($"http://127.0.0.1:{port}/ingest/plugin/x/y", done);
    }

    private static byte[] Http(string status, byte[] body, string extraHeaders = "") =>
        Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n{extraHeaders}\r\n")
            .Concat(body).ToArray();

    [Fact]
    public async Task TheRealDownloadReturnsTheBodyExactly()
    {
        var body = Script("0.2.1");
        var (url, done) = Serve(Http("200 OK", body));

        var got = await Task.Run(() => RustArchonUpdater.DefaultDownload(url));

        Assert.Equal(body, got);
        await done;
    }

    [Theory]
    [InlineData("404 Not Found")]
    [InlineData("500 Internal Server Error")]
    [InlineData("403 Forbidden")]
    public async Task TheRealDownloadRefusesAnyStatusButOk(string status)
    {
        var (url, done) = Serve(Http(status, Encoding.ASCII.GetBytes("nope")));

        await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => RustArchonUpdater.DefaultDownload(url)));
        await done;
    }

    [Fact]
    public async Task TheRealDownloadDoesNotFollowARedirect()
    {
        // A token URL must answer directly: following a redirect could send the token somewhere else.
        var (url, done) = Serve(Http("302 Found", [], "Location: http://192.0.2.1/elsewhere\r\n"));

        await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => RustArchonUpdater.DefaultDownload(url)));
        await done;
    }

    [Fact]
    public async Task TheRealDownloadRefusesABodyOverTheSizeCap()
    {
        var (url, done) = Serve(Http("200 OK", new byte[RustArchonUpdater.MaxDownloadBytes + 1]));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => RustArchonUpdater.DefaultDownload(url)));

        Assert.Contains("larger than", ex.Message);
        await done;
    }

    [Fact]
    public async Task ARealEndToEndUpdateOverHttpSucceeds()
    {
        // The whole path with a real socket instead of the injected download.
        InstallCurrent("0.2.0");
        var served = Script("0.2.1");
        var (url, done) = Serve(Http("200 OK", served));
        var updater = new RustArchonUpdater
        {
            PluginDirectory = _plugins,
            DataDirectory = _data,
            UtcNow = () => _now,
            TrustedModulus = Modulus,
            TrustedExponent = Exponent
        };
        typeof(RustArchonUpdater).GetMethod("Init", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(updater, null);

        Assert.True(JsonDocument.Parse(updater.Begin("0.2.1", url)).RootElement.GetProperty("ok").GetBoolean());
        await updater.DownloadTask;
        updater.Tick();

        Assert.Equal("loading", updater.State.Phase);
        Assert.Equal(served, File.ReadAllBytes(MainPath));
        await done;
    }
}
