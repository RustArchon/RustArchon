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
/// The main plugin replacing the Updater, and putting the old one back if the new one does not come up (the Updater cannot do that for
/// itself). It accepts only a file signed by the key THIS plugin trusts, whose version is the one asked for and newer than the installed
/// one; keeps a backup; confirms the new Updater by the marker it writes on load; and restores the backup if that never appears. Runs against a
/// temp plugins folder with the download, the clock and the marker under the test's control. What a real game server does with the swapped
/// file (reload, compile) is proven live.
/// </summary>
public sealed class RustArchonUpdaterSwapTests : IDisposable
{
    private const string SigMarker = "// RUSTARCHON-SIG-V1: ";
    private const string Token = "kQ3x9-Zr_AbCdEfGhIjKlMnOpQrStUvWxYz0123456";
    private const string Url = "http://192.0.2.10/ingest/plugin";

    private readonly RSA _key = RSA.Create(2048);
    private readonly RSA _otherKey = RSA.Create(2048);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rustarchon-swap-" + Guid.NewGuid().ToString("N"));
    private readonly string _plugins;
    private readonly string _data;
    private DateTime _now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    public RustArchonUpdaterSwapTests()
    {
        _plugins = Path.Combine(_root, "plugins");
        _data = Path.Combine(_root, "data", "RustArchon");
        Directory.CreateDirectory(_plugins);
        Directory.CreateDirectory(_data);
    }

    public void Dispose()
    {
        _key.Dispose();
        _otherKey.Dispose();
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    private string Modulus => Convert.ToBase64String(_key.ExportParameters(false).Modulus!);
    private string Exponent => Convert.ToBase64String(_key.ExportParameters(false).Exponent!);
    private string MainPath => Path.Combine(_plugins, "RustArchon.cs");
    private string UpdaterPath => Path.Combine(_plugins, "RustArchonUpdater.cs");

    private static byte[] Signed(string text, RSA signer)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var signature = signer.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return payload.Concat(Encoding.ASCII.GetBytes(SigMarker + Convert.ToBase64String(signature) + "\n")).ToArray();
    }

    // An Updater the way the Panel serves it: [Info] title and version, signature last.
    private byte[] UpdaterScript(string version, RSA? signer = null, bool sign = true, string title = "RustArchonUpdater", string body = "")
    {
        var text = $"[Info(\"{title}\", \"RustArchon\", \"{version}\")]\nclass U {{\n{body}}}\n";
        return sign ? Signed(text, signer ?? _key) : Encoding.UTF8.GetBytes(text);
    }

    private void InstallMain() =>
        File.WriteAllBytes(MainPath, Signed("[Info(\"RustArchon\", \"RustArchon\", \"0.8.0\")]\nclass M {}\n", _key));

    private byte[] InstallUpdater(string version = "0.2.0")
    {
        var bytes = UpdaterScript(version);
        File.WriteAllBytes(UpdaterPath, bytes);
        return bytes;
    }

    private ArchonPlugin Loaded(Func<string, string?, byte[]>? download = null, bool validMain = true)
    {
        if (validMain) { InstallMain(); }
        var plugin = new ArchonPlugin
        {
            SettingsFilePath = Path.Combine(_data, "settings.txt"),
            ScriptFilePath = MainPath,
            TrustedModulus = Modulus,
            TrustedExponent = Exponent,
            UtcNow = () => _now,
            UpdaterDownload = download ?? ((_, _) => throw new InvalidOperationException("no download expected")),
            RunInBackground = work => work()
        };
        typeof(ArchonPlugin).GetMethod("Init", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(plugin, null);
        return plugin;
    }

    private static JsonElement Reply(string json) => JsonDocument.Parse(json).RootElement;

    private static string ErrorOf(string json) => Reply(json).GetProperty("err").GetString()!;

    private string SwapFile(string key)
    {
        var line = File.ReadAllLines(Path.Combine(_data, "updater-swap.txt")).Single(l => l.StartsWith(key + "=", StringComparison.Ordinal));
        return line[(key.Length + 1)..];
    }

    private void WriteMarker(string version, DateTime utc) =>
        File.WriteAllText(Path.Combine(_data, "updater-loaded.txt"), $"version={version}\nutc={utc:o}\n");

    // ---- the happy path ------------------------------------------------------------------------------------

    [Fact]
    public void ASignedNewerUpdaterIsSwappedInWithABackupAndConfirmedByItsMarker()
    {
        var old = InstallUpdater("0.2.0");
        var served = UpdaterScript("0.3.0");
        string? seenUrl = null, seenToken = null;
        var plugin = Loaded((url, token) => { seenUrl = url; seenToken = token; return served; });

        var reply = Reply(plugin.BeginUpdaterUpdate("0.3.0", Url, Token));
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        Assert.Equal((Url, Token), (seenUrl, seenToken));

        plugin.UpdaterTick();                                     // verifies and swaps

        Assert.Equal("loading", plugin.UpdaterSwap.Phase);
        Assert.Equal(served, File.ReadAllBytes(UpdaterPath));
        Assert.Equal(old, File.ReadAllBytes(UpdaterPath + ".bak"));

        WriteMarker("0.3.0", _now.AddSeconds(3));
        plugin.UpdaterTick();

        Assert.Equal("succeeded", plugin.UpdaterSwap.Phase);
        Assert.Equal(served, File.ReadAllBytes(UpdaterPath));
        Assert.Equal("succeeded", SwapFile("phase"));
    }

    [Fact]
    public void TheRealUpdatersMarkerIsWhatTheMainPluginWaitsFor()
    {
        // Both halves of the contract: the marker file the Updater actually writes on load is the one the main plugin reads.
        InstallUpdater("0.2.0");
        var plugin = Loaded((_, _) => UpdaterScript("0.3.0"));
        plugin.BeginUpdaterUpdate("0.3.0", Url, Token);
        plugin.UpdaterTick();

        var updater = new RustArchonUpdater { DataDirectory = _data, PluginDirectory = _plugins, UtcNow = () => _now.AddSeconds(2), Version = new Oxide.Core.VersionNumber("0.3.0") };
        updater.WriteLoadedMarker();
        plugin.UpdaterTick();

        Assert.Equal("succeeded", plugin.UpdaterSwap.Phase);
    }

    [Fact]
    public void AnUpdaterThatNeverReportsLoadingIsReplacedByTheBackup()
    {
        var old = InstallUpdater("0.2.0");
        var plugin = Loaded((_, _) => UpdaterScript("0.3.0"));
        plugin.BeginUpdaterUpdate("0.3.0", Url, Token);
        plugin.UpdaterTick();
        Assert.NotEqual(old, File.ReadAllBytes(UpdaterPath));

        _now = _now.AddSeconds(ArchonPlugin.UpdaterLoadWaitSeconds - 1);
        plugin.UpdaterTick();
        Assert.Equal("loading", plugin.UpdaterSwap.Phase);              // still inside the wait

        _now = _now.AddSeconds(5);
        plugin.UpdaterTick();

        Assert.Equal("rolled-back", plugin.UpdaterSwap.Phase);
        Assert.Equal(old, File.ReadAllBytes(UpdaterPath));
        Assert.Equal("rolled-back", SwapFile("phase"));
        Assert.Contains("did not report loading", plugin.UpdaterSwap.Reason);
    }

    [Fact]
    public void AMarkerForTheWrongVersionOrFromBeforeTheSwapDoesNotCount()
    {
        var old = InstallUpdater("0.2.0");
        var plugin = Loaded((_, _) => UpdaterScript("0.3.0"));
        plugin.BeginUpdaterUpdate("0.3.0", Url, Token);
        plugin.UpdaterTick();

        WriteMarker("0.2.0", _now.AddSeconds(1));                   // the old Updater's marker
        plugin.UpdaterTick();
        Assert.Equal("loading", plugin.UpdaterSwap.Phase);

        WriteMarker("0.3.0", _now.AddMinutes(-10));                 // right version, but written long before the swap
        plugin.UpdaterTick();
        Assert.Equal("loading", plugin.UpdaterSwap.Phase);

        _now = _now.AddSeconds(ArchonPlugin.UpdaterLoadWaitSeconds + 1);
        plugin.UpdaterTick();
        Assert.Equal("rolled-back", plugin.UpdaterSwap.Phase);
        Assert.Equal(old, File.ReadAllBytes(UpdaterPath));
    }

    [Fact]
    public void IfTheBackupHasGoneThereIsNothingToRestoreAndItSaysSo()
    {
        InstallUpdater("0.2.0");
        var plugin = Loaded((_, _) => UpdaterScript("0.3.0"));
        plugin.BeginUpdaterUpdate("0.3.0", Url, Token);
        plugin.UpdaterTick();
        File.Delete(UpdaterPath + ".bak");

        _now = _now.AddSeconds(ArchonPlugin.UpdaterLoadWaitSeconds + 1);
        plugin.UpdaterTick();

        Assert.Equal("failed", plugin.UpdaterSwap.Phase);
        Assert.Contains("no backup", plugin.UpdaterSwap.Reason);
    }

    // ---- refusing a request --------------------------------------------------------------------------------

    [Fact]
    public void APluginThatDoesNotVerifyUnderItsOwnKeyInstallsNothing()
    {
        InstallUpdater();
        File.WriteAllBytes(MainPath, Signed("[Info(\"RustArchon\", \"RustArchon\", \"0.8.0\")]\nclass M {}\n", _otherKey));   // signed by a key it does not trust
        var plugin = Loaded(validMain: false);

        Assert.Equal("not_verified", ErrorOf(plugin.BeginUpdaterUpdate("0.3.0", Url, Token)));
        Assert.Equal("idle", plugin.UpdaterSwap.Phase);
    }

    [Fact]
    public void APluginWithNoTrustedKeyStampedInstallsNothing()
    {
        InstallUpdater();
        var plugin = Loaded();
        plugin.TrustedModulus = "@@RUSTARCHON_TRUSTED_MODULUS@@";

        Assert.Equal("not_verified", ErrorOf(plugin.BeginUpdaterUpdate("0.3.0", Url, Token)));
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3-beta")]
    [InlineData("1.2.x")]
    [InlineData("")]
    [InlineData("1.2.3.4")]
    public void AMalformedVersionIsRefused(string version)
    {
        InstallUpdater();

        Assert.Equal("bad_version", ErrorOf(Loaded().BeginUpdaterUpdate(version, Url, Token)));
    }

    [Theory]
    [InlineData("ftp://192.0.2.1/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/relative/path")]
    [InlineData("not a url")]
    [InlineData("http://user:pass@192.0.2.1/x")]
    [InlineData("")]
    public void AnythingButAnAbsoluteHttpAddressWithNoCredentialsIsRefused(string url)
    {
        InstallUpdater();

        Assert.Equal("bad_url", ErrorOf(Loaded().BeginUpdaterUpdate("0.3.0", url, Token)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("line\r\nbreak: injected")]
    [InlineData("slash/inside")]
    [InlineData("plus+sign")]
    public void ATokenThatIsNotUrlSafeBase64IsRefused(string token)
    {
        InstallUpdater();

        Assert.Equal("bad_token", ErrorOf(Loaded().BeginUpdaterUpdate("0.3.0", Url, token)));
    }

    [Fact]
    public void ATokenLongerThanTheLimitIsRefused()
    {
        InstallUpdater();

        Assert.Equal("bad_token", ErrorOf(Loaded().BeginUpdaterUpdate("0.3.0", Url, new string('a', 129))));
    }

    [Fact]
    public void WhenThePluginsFolderCannotBeFoundNothingIsStarted()
    {
        var plugin = Loaded();
        plugin.PluginDirectory = null;

        Assert.Equal("plugins_folder_unknown", ErrorOf(plugin.BeginUpdaterUpdate("0.3.0", Url, Token)));
    }

    // ---- no Updater at all: a fresh install ----------------------------------------------------------------

    [Fact]
    public void WithNoUpdaterFileTheSameSignedDownloadIsInstalledFreshWithNoBackup()
    {
        var served = UpdaterScript("0.3.0");
        var plugin = Loaded((_, _) => served);

        var reply = Reply(plugin.BeginUpdaterUpdate("0.3.0", Url, Token));
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        Assert.Equal("", reply.GetProperty("data").GetProperty("installedVersion").GetString());
        plugin.UpdaterTick();

        Assert.Equal("loading", plugin.UpdaterSwap.Phase);
        Assert.Equal(served, File.ReadAllBytes(UpdaterPath));
        Assert.False(File.Exists(UpdaterPath + ".bak"));
        Assert.False(File.Exists(UpdaterPath + ".new"));

        WriteMarker("0.3.0", _now.AddSeconds(2));
        plugin.UpdaterTick();
        Assert.Equal("succeeded", plugin.UpdaterSwap.Phase);
    }

    [Fact]
    public void AFreshUpdaterThatNeverComesUpIsRemovedLeavingTheServerAsItWas()
    {
        var plugin = Loaded((_, _) => UpdaterScript("0.3.0"));
        plugin.BeginUpdaterUpdate("0.3.0", Url, Token);
        plugin.UpdaterTick();
        Assert.True(File.Exists(UpdaterPath));

        _now = _now.AddSeconds(ArchonPlugin.UpdaterLoadWaitSeconds + 1);
        plugin.UpdaterTick();

        Assert.Equal("rolled-back", plugin.UpdaterSwap.Phase);
        Assert.False(File.Exists(UpdaterPath));
    }

    [Fact]
    public void AFreshInstallStillNeedsAValidSignatureAndLeavesNoFileWhenRefused()
    {
        var plugin = Loaded((_, _) => UpdaterScript("0.3.0", _otherKey));
        plugin.BeginUpdaterUpdate("0.3.0", Url, Token);

        plugin.UpdaterTick();

        Assert.Equal("failed", plugin.UpdaterSwap.Phase);
        Assert.StartsWith("signature_invalid", plugin.UpdaterSwap.Reason);
        Assert.False(File.Exists(UpdaterPath));
        Assert.False(File.Exists(UpdaterPath + ".new"));
    }

    [Fact]
    public void AnUpdaterThatAppearsWhileAFreshDownloadRunsIsNotOverwrittenBlind()
    {
        var plugin = Loaded((_, _) =>
        {
            File.WriteAllBytes(UpdaterPath, UpdaterScript("0.2.0"));       // installed by hand meanwhile
            return UpdaterScript("0.3.0");
        });
        plugin.BeginUpdaterUpdate("0.3.0", Url, Token);

        plugin.UpdaterTick();

        Assert.Equal("failed", plugin.UpdaterSwap.Phase);
        Assert.StartsWith("changed", plugin.UpdaterSwap.Reason);
        Assert.Equal(UpdaterScript("0.2.0").Length, File.ReadAllBytes(UpdaterPath).Length);
    }

    [Fact]
    public void AnUpdaterThatDisappearsWhileAnUpdateDownloadsIsNotRecreatedBlind()
    {
        InstallUpdater("0.2.0");
        var plugin = Loaded((_, _) =>
        {
            File.Delete(UpdaterPath);
            return UpdaterScript("0.3.0");
        });
        plugin.BeginUpdaterUpdate("0.3.0", Url, Token);

        plugin.UpdaterTick();

        Assert.Equal("failed", plugin.UpdaterSwap.Phase);
        Assert.StartsWith("changed", plugin.UpdaterSwap.Reason);
        Assert.False(File.Exists(UpdaterPath));
    }

    [Theory]
    [InlineData("0.2.0", "0.2.0")]
    [InlineData("0.2.0", "0.1.9")]
    [InlineData("1.0.0", "0.9.9")]
    public void OnlyANewerVersionIsAccepted(string installed, string offered)
    {
        InstallUpdater(installed);

        Assert.Equal("not_newer", ErrorOf(Loaded().BeginUpdaterUpdate(offered, Url, Token)));
    }

    [Fact]
    public void AnInstalledUpdaterWithNoReadableVersionIsRefused()
    {
        File.WriteAllText(UpdaterPath, "class U {}");

        Assert.Equal("updater_unreadable", ErrorOf(Loaded().BeginUpdaterUpdate("0.3.0", Url, Token)));
    }

    [Theory]
    [InlineData("downloading")]
    [InlineData("loading")]
    public void WhileTheUpdaterIsReplacingThisPluginNothingIsStarted(string phase)
    {
        InstallUpdater();
        File.WriteAllText(Path.Combine(_data, "update-status.txt"), $"phase={phase}\ntarget=0.9.0\n");

        Assert.Equal("busy", ErrorOf(Loaded().BeginUpdaterUpdate("0.3.0", Url, Token)));
    }

    [Theory]
    [InlineData("idle")]
    [InlineData("succeeded")]
    [InlineData("failed")]
    [InlineData("rolled-back")]
    public void AnUpdaterThatIsAtRestDoesNotBlockIt(string phase)
    {
        InstallUpdater();
        File.WriteAllText(Path.Combine(_data, "update-status.txt"), $"phase={phase}\n");

        Assert.True(Reply(Loaded((_, _) => UpdaterScript("0.3.0")).BeginUpdaterUpdate("0.3.0", Url, Token)).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void ASecondRequestWhileOneIsInProgressIsRefused()
    {
        InstallUpdater();
        var plugin = Loaded((_, _) => UpdaterScript("0.3.0"));
        plugin.BeginUpdaterUpdate("0.3.0", Url, Token);

        Assert.Equal("busy", ErrorOf(plugin.BeginUpdaterUpdate("0.4.0", Url, Token)));
    }

    // ---- refusing what was downloaded: the Updater must be left exactly as it was ---------------------------

    private void AssertRefused(ArchonPlugin plugin, byte[] original, string reasonStartsWith)
    {
        plugin.UpdaterTick();

        Assert.Equal("failed", plugin.UpdaterSwap.Phase);
        Assert.StartsWith(reasonStartsWith, plugin.UpdaterSwap.Reason);
        Assert.Equal(original, File.ReadAllBytes(UpdaterPath));
        Assert.False(File.Exists(UpdaterPath + ".bak"));
        Assert.False(File.Exists(UpdaterPath + ".new"));
    }

    [Fact]
    public void AFileSignedByAnotherKeyIsRefused()
    {
        var old = InstallUpdater();
        var plugin = Loaded((_, _) => UpdaterScript("0.3.0", _otherKey));
        plugin.BeginUpdaterUpdate("0.3.0", Url, Token);

        AssertRefused(plugin, old, "signature_invalid");
    }

    [Fact]
    public void AnUnsignedFileIsRefused()
    {
        var old = InstallUpdater();
        var plugin = Loaded((_, _) => UpdaterScript("0.3.0", sign: false));
        plugin.BeginUpdaterUpdate("0.3.0", Url, Token);

        AssertRefused(plugin, old, "signature_unsigned");
    }

    [Fact]
    public void AFileAlteredAfterSigningIsRefused()
    {
        var old = InstallUpdater();
        var served = UpdaterScript("0.3.0");
        served[10] ^= 0x01;
        var plugin = Loaded((_, _) => served);
        plugin.BeginUpdaterUpdate("0.3.0", Url, Token);

        AssertRefused(plugin, old, "signature_invalid");
    }

    [Fact]
    public void AFileWhoseVersionIsNotTheOneAskedForIsRefused()
    {
        var old = InstallUpdater();
        var plugin = Loaded((_, _) => UpdaterScript("0.9.0"));
        plugin.BeginUpdaterUpdate("0.3.0", Url, Token);

        AssertRefused(plugin, old, "version_mismatch");
    }

    [Fact]
    public void AValidlySignedFileThatIsNotAnUpdaterIsRefused()
    {
        var old = InstallUpdater();
        var plugin = Loaded((_, _) => UpdaterScript("0.3.0", title: "RustArchon"));      // the main plugin's own [Info]
        plugin.BeginUpdaterUpdate("0.3.0", Url, Token);

        AssertRefused(plugin, old, "not_an_updater");
    }

    [Fact]
    public void ADownloadThatFailsLeavesEverythingAlone()
    {
        var old = InstallUpdater();
        var plugin = Loaded((_, _) => throw new IOException("connection refused"));
        plugin.BeginUpdaterUpdate("0.3.0", Url, Token);

        AssertRefused(plugin, old, "download_failed");
        Assert.Contains("connection refused", plugin.UpdaterSwap.Reason);
    }

    [Fact]
    public void ADownloadThatNeverFinishesTimesOut()
    {
        var old = InstallUpdater();
        var gate = new ManualResetEventSlim();
        var plugin = Loaded((_, _) => { gate.Wait(); return []; });
        plugin.RunInBackground = work => Task.Run(work);
        plugin.BeginUpdaterUpdate("0.3.0", Url, Token);

        plugin.UpdaterTick();
        Assert.Equal("downloading", plugin.UpdaterSwap.Phase);
        _now = _now.AddSeconds(ArchonUpdaterSwap.DownloadTimeoutSeconds + 16);

        AssertRefused(plugin, old, "download_timeout");
        gate.Set();
    }

    [Fact]
    public void AnUpdateOfThisPluginThatStartsDuringTheDownloadStopsTheSwap()
    {
        var old = InstallUpdater();
        var plugin = Loaded((_, _) =>
        {
            File.WriteAllText(Path.Combine(_data, "update-status.txt"), "phase=loading\n");     // the Updater started meanwhile
            return UpdaterScript("0.3.0");
        });
        plugin.BeginUpdaterUpdate("0.3.0", Url, Token);

        AssertRefused(plugin, old, "busy");
    }

    [Fact]
    public void AnUpdaterThatIsNoLongerNewerByTheTimeItArrivesIsRefused()
    {
        var old = InstallUpdater("0.2.0");
        var plugin = Loaded((_, _) =>
        {
            File.WriteAllBytes(UpdaterPath, UpdaterScript("0.3.0"));                             // someone installed it by hand meanwhile
            return UpdaterScript("0.3.0");
        });
        plugin.BeginUpdaterUpdate("0.3.0", Url, Token);
        plugin.UpdaterTick();

        Assert.Equal("failed", plugin.UpdaterSwap.Phase);
        Assert.StartsWith("not_newer", plugin.UpdaterSwap.Reason);
        Assert.NotEqual(old, File.ReadAllBytes(UpdaterPath));
        Assert.False(File.Exists(UpdaterPath + ".bak"));
    }

    // ---- surviving a reload of this plugin -----------------------------------------------------------------

    [Fact]
    public void ASwapNotYetConfirmedWhenThePluginReloadsIsStillWatchedAndStillRolledBack()
    {
        var old = InstallUpdater("0.2.0");
        var first = Loaded((_, _) => UpdaterScript("0.3.0"));
        first.BeginUpdaterUpdate("0.3.0", Url, Token);
        first.UpdaterTick();                                         // swapped; then this plugin reloads

        var second = Loaded();
        Assert.Equal("loading", second.UpdaterSwap.Phase);
        Assert.Equal("0.3.0", second.UpdaterSwap.Target);

        _now = _now.AddSeconds(ArchonPlugin.UpdaterLoadWaitSeconds + 1);
        second.UpdaterTick();

        Assert.Equal("rolled-back", second.UpdaterSwap.Phase);
        Assert.Equal(old, File.ReadAllBytes(UpdaterPath));
    }

    [Fact]
    public void AReloadedPluginStillConfirmsASwapWhoseMarkerAppearedMeanwhile()
    {
        InstallUpdater("0.2.0");
        var first = Loaded((_, _) => UpdaterScript("0.3.0"));
        first.BeginUpdaterUpdate("0.3.0", Url, Token);
        first.UpdaterTick();
        WriteMarker("0.3.0", _now.AddSeconds(2));

        var second = Loaded();
        second.UpdaterTick();

        Assert.Equal("succeeded", second.UpdaterSwap.Phase);
    }

    [Fact]
    public void ADownloadThatDiedWithAReloadIsRecordedAsInterruptedAndChangedNothing()
    {
        var old = InstallUpdater("0.2.0");
        var plugin = Loaded((_, _) => { throw new InvalidOperationException("never runs"); });
        plugin.RunInBackground = _ => { };                          // the download never gets going
        plugin.BeginUpdaterUpdate("0.3.0", Url, Token);
        Assert.Equal("downloading", SwapFile("phase"));

        var reloaded = Loaded();

        Assert.Equal("failed", reloaded.UpdaterSwap.Phase);
        Assert.Contains("interrupted", reloaded.UpdaterSwap.Reason);
        Assert.Equal(old, File.ReadAllBytes(UpdaterPath));
    }

    [Fact]
    public void AnUnreadableStateFileMeansIdleNotACrash()
    {
        File.WriteAllText(Path.Combine(_data, "updater-swap.txt"), "\0\0garbage without equals\n=\nphase\n");

        Assert.Equal("idle", Loaded().UpdaterSwap.Phase);
    }

    // ---- the commands --------------------------------------------------------------------------------------

    [Theory]
    [InlineData]
    [InlineData("0.3.0")]
    [InlineData("0.3.0", "http://a/b")]
    [InlineData("0.3.0", "http://a/b", "tok", "extra")]
    public void WrongArgumentCountGetsAUsageError(params string[] args)
    {
        var arg = ConsoleSystem.Arg.WithArgs(args);

        Loaded().CmdUpdaterUpdate(arg);

        Assert.Equal("usage", ErrorOf(Assert.Single(arg.Replies)));
    }

    [Fact]
    public void TheCommandsAreIgnoredFromAnInGameConsole()
    {
        InstallUpdater();
        var plugin = Loaded();
        var update = ConsoleSystem.Arg.WithArgs("0.3.0", Url, Token);
        update.Connection = new object();
        var status = ConsoleSystem.Arg.WithArgs();
        status.Connection = new object();

        plugin.CmdUpdaterUpdate(update);
        plugin.CmdUpdaterStatus(status);

        Assert.Empty(update.Replies);
        Assert.Empty(status.Replies);
        Assert.Equal("idle", plugin.UpdaterSwap.Phase);
    }

    [Fact]
    public void TheCommandStartsAnUpdateAndTheStatusSaysWhereItStands()
    {
        InstallUpdater("0.2.0");
        var plugin = Loaded((_, _) => UpdaterScript("0.3.0"));
        var update = ConsoleSystem.Arg.WithArgs("0.3.0", Url, Token);

        plugin.CmdUpdaterUpdate(update);
        plugin.UpdaterTick();
        var status = ConsoleSystem.Arg.WithArgs();
        plugin.CmdUpdaterStatus(status);

        Assert.True(Reply(Assert.Single(update.Replies)).GetProperty("ok").GetBoolean());
        var data = Reply(Assert.Single(status.Replies)).GetProperty("data");
        Assert.Equal("loading", data.GetProperty("phase").GetString());
        Assert.Equal("0.3.0", data.GetProperty("targetVersion").GetString());
        Assert.Equal("0.2.0", data.GetProperty("previousVersion").GetString());
        Assert.Equal("0.3.0", data.GetProperty("installedVersion").GetString());
    }

    [Fact]
    public void AnIdleStatusReportsTheInstalledVersion()
    {
        InstallUpdater("0.2.0");
        var plugin = Loaded();
        var status = ConsoleSystem.Arg.WithArgs();

        plugin.CmdUpdaterStatus(status);

        var data = Reply(Assert.Single(status.Replies)).GetProperty("data");
        Assert.Equal(("idle", "0.2.0"), (data.GetProperty("phase").GetString(), data.GetProperty("installedVersion").GetString()));
    }

    // ---- the pure helpers ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("0.3.0", "0.2.0", 1)]
    [InlineData("0.2.0", "0.3.0", -1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("0.10.0", "0.9.0", 1)]              // numbers, not text
    [InlineData("1.0.0", "0.99.99", 1)]
    public void VersionsAreComparedAsNumbers(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(ArchonUpdaterSwap.CompareVersions(a, b)));

    [Fact]
    public void OnlyTheUpdatersOwnInfoAttributeIsRead()
    {
        Assert.Equal("0.3.0", ArchonUpdaterSwap.ReadInfoVersion("[Info(\"RustArchonUpdater\", \"RustArchon\", \"0.3.0\")]"));
        Assert.Null(ArchonUpdaterSwap.ReadInfoVersion("[Info(\"RustArchon\", \"RustArchon\", \"0.8.0\")]"));
        Assert.Null(ArchonUpdaterSwap.ReadInfoVersion(null));
    }

    // ---- the real download ---------------------------------------------------------------------------------

    private static (string Url, Task Done, Task<string> Request) Serve(byte[] rawResponse)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var captured = new TaskCompletionSource<string>();
        var done = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[8192];
            var read = await stream.ReadAsync(buffer);
            captured.SetResult(Encoding.ASCII.GetString(buffer, 0, read));
            await stream.WriteAsync(rawResponse);
            await stream.FlushAsync();
            listener.Stop();
        });
        return ($"http://127.0.0.1:{port}/ingest/plugin", done, captured.Task);
    }

    private static byte[] Http(string status, byte[] body, string extraHeaders = "") =>
        Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n{extraHeaders}\r\n").Concat(body).ToArray();

    [Fact]
    public async Task TheRealDownloadSendsTheTokenInAHeaderNotTheAddressAndReturnsTheBodyExactly()
    {
        var body = UpdaterScript("0.3.0");
        var (url, done, request) = Serve(Http("200 OK", body));

        var got = await Task.Run(() => ArchonUpdaterSwap.DefaultDownload(url, Token));
        await done;

        Assert.Equal(body, got);
        var lines = (await request).Split("\r\n");
        Assert.DoesNotContain(Token, lines[0]);
        Assert.Contains(lines, l => l == "X-RustArchon-Update-Token: " + Token);
    }

    [Theory]
    [InlineData("404 Not Found")]
    [InlineData("500 Internal Server Error")]
    public async Task TheRealDownloadRefusesAnyStatusButOk(string status)
    {
        var (url, done, _) = Serve(Http(status, Encoding.ASCII.GetBytes("nope")));

        await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => ArchonUpdaterSwap.DefaultDownload(url, Token)));
        await done;
    }

    [Fact]
    public async Task TheRealDownloadDoesNotFollowARedirect()
    {
        var (url, done, _) = Serve(Http("302 Found", [], "Location: http://192.0.2.1/elsewhere\r\n"));

        await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => ArchonUpdaterSwap.DefaultDownload(url, Token)));
        await done;
    }

    [Fact]
    public async Task TheRealDownloadRefusesABodyOverTheSizeCap()
    {
        var (url, done, _) = Serve(Http("200 OK", new byte[ArchonUpdaterSwap.MaxDownloadBytes + 1]));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => ArchonUpdaterSwap.DefaultDownload(url, Token)));

        Assert.Contains("larger than", ex.Message);
        await done;
    }

    [Fact]
    public async Task ARealEndToEndSwapOverHttpSucceeds()
    {
        InstallUpdater("0.2.0");
        var served = UpdaterScript("0.3.0");
        var (url, done, request) = Serve(Http("200 OK", served));
        var plugin = Loaded();
        plugin.UpdaterDownload = ArchonUpdaterSwap.DefaultDownload;
        plugin.RunInBackground = work => Task.Run(work);

        Assert.True(Reply(plugin.BeginUpdaterUpdate("0.3.0", url, Token)).GetProperty("ok").GetBoolean());
        await done;
        for (var i = 0; i < 100 && plugin.UpdaterSwap.Phase == "downloading"; i++)
        {
            await Task.Delay(50);
            plugin.UpdaterTick();
        }

        Assert.Equal("loading", plugin.UpdaterSwap.Phase);
        Assert.Equal(served, File.ReadAllBytes(UpdaterPath));
        Assert.Contains("X-RustArchon-Update-Token: " + Token, await request);
    }

    // ---- what the Updater does on its side of the contract -------------------------------------------------

    [Fact]
    public void TheUpdaterWritesItsMarkerOnLoadWithItsVersionAndTime()
    {
        InstallUpdater("0.2.0");
        var updater = new RustArchonUpdater { DataDirectory = _data, PluginDirectory = _plugins, UtcNow = () => _now };
        typeof(RustArchonUpdater).GetMethod("Init", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(updater, null);

        var lines = File.ReadAllLines(Path.Combine(_data, "updater-loaded.txt"));

        Assert.StartsWith("version=", lines[0]);
        Assert.Equal($"utc={_now:o}", lines[1]);
    }

    [Theory]
    [InlineData("downloading", true)]
    [InlineData("loading", true)]
    [InlineData("idle", false)]
    [InlineData("succeeded", false)]
    [InlineData("failed", false)]
    [InlineData("rolled-back", false)]
    public void TheUpdaterWillNotStartAnUpdateWhileTheMainPluginIsReplacingIt(string phase, bool refused)
    {
        File.WriteAllBytes(Path.Combine(_plugins, "RustArchon.cs"), Signed($"[Info(\"RustArchon\", \"RustArchon\", \"0.7.0\")]\nconst string TrustedModulus = \"{Modulus}\";\nconst string TrustedExponent = \"{Exponent}\";\n", _key));
        File.WriteAllText(Path.Combine(_data, "updater-swap.txt"), $"phase={phase}\n");
        var updater = new RustArchonUpdater
        {
            DataDirectory = _data, PluginDirectory = _plugins, UtcNow = () => _now,
            Download = (_, _) => throw new InvalidOperationException("no download expected"), TrustedModulus = Modulus, TrustedExponent = Exponent
        };

        var reply = Reply(updater.Begin("0.8.0", "http://192.0.2.1/x", Token));

        if (refused)
        {
            Assert.Equal("busy", reply.GetProperty("err").GetString());
        }
        else
        {
            Assert.NotEqual("busy", reply.TryGetProperty("err", out var err) ? err.GetString() : null);
        }
    }
}
