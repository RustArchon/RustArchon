// Copyright ©2026 Scott Blomfield

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Oxide.Plugins;
using ArchonPlugin = Oxide.Plugins.RustArchon;

namespace RustArchon.Plugin.Tests;

/// <summary>
/// Applying a newer version of a plugin that is already installed, on the say-so of the panel: the plugin downloads its OWN copy, applies it only if it
/// is byte for byte the file the panel checked (SHA-256 and size), keeps the old file as .bak, and puts it back if the new one does not come up loaded at
/// the version asked for. Runs against a temp plugins folder with the download, the clock and the framework's list of loaded plugins under the test's
/// control. What a real game server does with the swapped file (compile, reload) is proven live.
/// </summary>
public sealed class RustArchonThirdPartyUpdateTests : IDisposable
{
    private const string SigMarker = "// RUSTARCHON-SIG-V1: ";
    private const string Url = "https://umod.org/plugins/Marker.cs";

    private readonly RSA _key = RSA.Create(2048);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rustarchon-3p-" + Guid.NewGuid().ToString("N"));
    private readonly string _plugins;
    private readonly string _data;
    private DateTime _now = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    public RustArchonThirdPartyUpdateTests()
    {
        _plugins = Path.Combine(_root, "plugins");
        _data = Path.Combine(_root, "data", "RustArchon");
        Directory.CreateDirectory(_plugins);
        Directory.CreateDirectory(_data);
    }

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    private string Modulus => Convert.ToBase64String(_key.ExportParameters(false).Modulus!);
    private string Exponent => Convert.ToBase64String(_key.ExportParameters(false).Exponent!);
    private string MainPath => Path.Combine(_plugins, "RustArchon.cs");
    private string MarkerPath => Path.Combine(_plugins, "Marker.cs");

    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] Script(string version, string className = "Marker", string extra = "") =>
        Encoding.UTF8.GetBytes($"using Oxide.Core;\n[Info(\"{className}\", \"Someone\", \"{version}\")]\npublic class {className} : RustPlugin\n{{\n{extra}}}\n");

    private void InstallMain()
    {
        var payload = Encoding.UTF8.GetBytes("[Info(\"RustArchon\", \"RustArchon\", \"0.9.0\")]\nclass M {}\n");
        var signature = _key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        File.WriteAllBytes(MainPath, payload.Concat(Encoding.ASCII.GetBytes(SigMarker + Convert.ToBase64String(signature) + "\n")).ToArray());
    }

    private byte[] InstallMarker(string version = "1.0.0")
    {
        var bytes = Script(version);
        File.WriteAllBytes(MarkerPath, bytes);
        return bytes;
    }

    /// <summary>A plugin as loaded on a server: signed by its panel, with the framework's list of loaded plugins under the test's control.</summary>
    private ArchonPlugin Loaded(Func<string, long, byte[]>? download = null, bool validMain = true, Action<Action>? background = null)
    {
        if (validMain) { InstallMain(); }
        var plugin = new ArchonPlugin
        {
            SettingsFilePath = Path.Combine(_data, "settings.txt"),
            ScriptFilePath = MainPath,
            TrustedModulus = Modulus,
            TrustedExponent = Exponent,
            UtcNow = () => _now,
            ThirdPartyDownload = download ?? ((_, _) => throw new InvalidOperationException("no download expected")),
            RunInBackground = background ?? (work => work())
        };
        typeof(ArchonPlugin).GetMethod("Init", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(plugin, null);
        return plugin;
    }

    private static JsonElement Reply(string json) => JsonDocument.Parse(json).RootElement;

    private static string ErrorOf(string json) => Reply(json).GetProperty("err").GetString()!;

    private string Begin(ArchonPlugin plugin, byte[] served, string version = "1.1.0", string className = "Marker", string? url = null, long? size = null, string? sha = null) =>
        plugin.BeginThirdPartyUpdate(className, version, sha ?? Hex(served), (size ?? served.Length).ToString(), url ?? Url);

    private string SwapFile(string key)
    {
        var line = File.ReadAllLines(Path.Combine(_data, "thirdparty-swap.txt")).Single(l => l.StartsWith(key + "=", StringComparison.Ordinal));
        return line[(key.Length + 1)..];
    }

    private static ArchonThirdParty.LoadedPlugin Running(object instance, string version) => new() { Instance = instance, Version = version };

    // ---- the happy path ------------------------------------------------------------------------------------

    [Fact]
    public void TheFileIsDownloadedFromTheAddressGivenAndSwappedInWithABackupAndConfirmedByTheFramework()
    {
        var old = InstallMarker("1.0.0");
        var served = Script("1.1.0");
        string? seenUrl = null;
        long seenSize = 0;
        var oldInstance = new object();
        var current = Running(oldInstance, "1.0.0");
        var plugin = Loaded((url, size) => { seenUrl = url; seenSize = size; return served; });
        plugin.FindLoaded = _ => current;

        var reply = Reply(Begin(plugin, served));
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        Assert.Equal("Marker.cs", reply.GetProperty("data").GetProperty("file").GetString());
        Assert.Equal("1.0.0", reply.GetProperty("data").GetProperty("installedVersion").GetString());
        Assert.Equal((Url, (long)served.Length), (seenUrl, seenSize));

        plugin.ThirdPartyTick();                                    // verifies and swaps

        Assert.Equal("loading", plugin.ThirdParty.Phase);
        Assert.Equal(served, File.ReadAllBytes(MarkerPath));
        Assert.Equal(old, File.ReadAllBytes(MarkerPath + ".bak"));
        Assert.False(File.Exists(MarkerPath + ".new"));

        _now = _now.AddSeconds(5);
        plugin.ThirdPartyTick();                                    // still the old instance: not confirmed
        Assert.Equal("loading", plugin.ThirdParty.Phase);

        current = Running(new object(), "1.1.0");                   // the framework has reloaded it
        plugin.ThirdPartyTick();

        Assert.Equal("succeeded", plugin.ThirdParty.Phase);
        Assert.Equal(served, File.ReadAllBytes(MarkerPath));        // the new file stays; the backup is kept
        Assert.Equal(old, File.ReadAllBytes(MarkerPath + ".bak"));
        Assert.Equal("succeeded", SwapFile("phase"));
    }

    [Fact]
    public void AMissingPluginNowLoadingCountsAsLoaded()
    {
        // A plugin that failed to compile before is not in the framework's list at all; the fix is what makes it appear.
        InstallMarker("1.0.0");
        var served = Script("1.1.0");
        ArchonThirdParty.LoadedPlugin? current = null;
        var plugin = Loaded((_, _) => served);
        plugin.FindLoaded = _ => current;
        Begin(plugin, served);
        plugin.ThirdPartyTick();

        current = Running(new object(), "1.1.0");
        plugin.ThirdPartyTick();

        Assert.Equal("succeeded", plugin.ThirdParty.Phase);
    }

    [Theory]
    [InlineData("1.1", "1.1.0")]
    [InlineData("v2.0.0", "2.0.0")]
    [InlineData("1.1.0", "1.1")]
    public void TheVersionTheFrameworkReportsIsComparedTheWayItKeepsVersions(string asked, string reported)
    {
        InstallMarker("0.9.0");
        var served = Script(asked);
        var plugin = Loaded((_, _) => served);
        plugin.FindLoaded = _ => Running(new object(), reported);
        Begin(plugin, served, version: asked);
        plugin.ThirdPartyTick();

        plugin.ThirdPartyTick();

        Assert.Equal("succeeded", plugin.ThirdParty.Phase);
    }

    // ---- it does not come up: the old file is put back -----------------------------------------------------

    [Fact]
    public void APluginThatNeverAppearsIsRolledBackAfterTheWaitAndTheOldFileIsPutBack()
    {
        var old = InstallMarker("1.0.0");
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served);
        plugin.FindLoaded = _ => null;                              // a compile error: it never appears
        Begin(plugin, served);
        plugin.ThirdPartyTick();
        Assert.Equal(served, File.ReadAllBytes(MarkerPath));

        _now = _now.AddSeconds(ArchonPlugin.ThirdPartyLoadWaitSeconds - 1);
        plugin.ThirdPartyTick();
        Assert.Equal("loading", plugin.ThirdParty.Phase);           // not yet

        _now = _now.AddSeconds(2);
        plugin.ThirdPartyTick();

        Assert.Equal("rolled-back", plugin.ThirdParty.Phase);
        Assert.Contains("did not appear", plugin.ThirdParty.Reason);
        Assert.Equal(old, File.ReadAllBytes(MarkerPath));
        Assert.Equal("rolled-back", SwapFile("phase"));
    }

    [Fact]
    public void APluginStillRunningTheOldInstanceIsRolledBack()
    {
        var old = InstallMarker("1.0.0");
        var served = Script("1.1.0");
        var oldInstance = new object();
        var plugin = Loaded((_, _) => served);
        plugin.FindLoaded = _ => Running(oldInstance, "1.0.0");
        Begin(plugin, served);
        plugin.ThirdPartyTick();

        _now = _now.AddSeconds(ArchonPlugin.ThirdPartyLoadWaitSeconds + 1);
        plugin.ThirdPartyTick();

        Assert.Equal("rolled-back", plugin.ThirdParty.Phase);
        Assert.Contains("1.0.0", plugin.ThirdParty.Reason);
        Assert.Equal(old, File.ReadAllBytes(MarkerPath));
    }

    [Fact]
    public void APluginThatCameUpAtTheWrongVersionIsRolledBack()
    {
        var old = InstallMarker("1.0.0");
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served);
        plugin.FindLoaded = _ => Running(new object(), "1.0.5");
        Begin(plugin, served);
        plugin.ThirdPartyTick();

        _now = _now.AddSeconds(ArchonPlugin.ThirdPartyLoadWaitSeconds + 1);
        plugin.ThirdPartyTick();

        Assert.Equal("rolled-back", plugin.ThirdParty.Phase);
        Assert.Equal(old, File.ReadAllBytes(MarkerPath));
    }

    [Fact]
    public void AMissingBackupMeansTheRollbackFailsAndSaysSo()
    {
        InstallMarker("1.0.0");
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served);
        plugin.FindLoaded = _ => null;
        Begin(plugin, served);
        plugin.ThirdPartyTick();
        File.Delete(MarkerPath + ".bak");

        _now = _now.AddSeconds(ArchonPlugin.ThirdPartyLoadWaitSeconds + 1);
        plugin.ThirdPartyTick();

        Assert.Equal("failed", plugin.ThirdParty.Phase);
        Assert.Contains("no backup", plugin.ThirdParty.Reason);
    }

    // ---- the file is not the one the panel checked ---------------------------------------------------------

    [Fact]
    public void AFileWithADifferentHashIsNotAppliedAndTheActualHashIsReported()
    {
        var old = InstallMarker("1.0.0");
        var checkedByPanel = Script("1.1.0");
        var actuallyServed = Script("1.1.0", extra: "// slipstreamed\n");
        var plugin = Loaded((_, _) => actuallyServed);
        Begin(plugin, checkedByPanel);

        plugin.ThirdPartyTick();

        Assert.Equal("mismatch", plugin.ThirdParty.Phase);
        Assert.Equal(Hex(actuallyServed), plugin.ThirdParty.ActualSha256);
        Assert.Equal(Hex(checkedByPanel), plugin.ThirdParty.Sha256);
        Assert.Equal(old, File.ReadAllBytes(MarkerPath));           // nothing written, nothing backed up
        Assert.False(File.Exists(MarkerPath + ".bak"));
        Assert.False(File.Exists(MarkerPath + ".new"));
        Assert.Equal(Hex(actuallyServed), SwapFile("actual"));
    }

    [Fact]
    public void MoreBytesThanThePanelSawIsAChangedFileNotAFailedDownload()
    {
        var old = InstallMarker("1.0.0");
        var served = Script("1.1.0");
        var plugin = Loaded((_, size) => throw new ArchonThirdParty.ChangedException($"larger than {size}"));
        Begin(plugin, served);

        plugin.ThirdPartyTick();

        Assert.Equal("mismatch", plugin.ThirdParty.Phase);
        Assert.Equal("", plugin.ThirdParty.ActualSha256);
        Assert.Equal(old, File.ReadAllBytes(MarkerPath));
    }

    [Fact]
    public void AHashInCapitalsIsAccepted()
    {
        InstallMarker("1.0.0");
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served);
        Begin(plugin, served, sha: Hex(served).ToUpperInvariant());

        plugin.ThirdPartyTick();

        Assert.Equal("loading", plugin.ThirdParty.Phase);
    }

    // ---- what is refused before anything is downloaded -----------------------------------------------------

    [Fact]
    public void APluginThatCannotVouchForItsOwnFileAppliesNothing()
    {
        InstallMarker("1.0.0");
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served, validMain: false);

        Assert.Equal("not_verified", ErrorOf(Begin(plugin, served)));
        Assert.Equal("idle", plugin.ThirdParty.Phase);
    }

    [Theory]
    [InlineData("RustArchon")]
    [InlineData("RustArchonUpdater")]
    [InlineData("")]
    [InlineData("../Marker")]
    [InlineData("Marker.cs")]
    [InlineData("Mark er")]
    [InlineData("1Marker")]
    [InlineData("Mark.*")]
    public void AClassNameThatIsNotAPlainOneOfTheirsIsRefused(string className)
    {
        InstallMarker("1.0.0");
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served);

        Assert.Equal("bad_class", ErrorOf(Begin(plugin, served, className: className)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2.3 ")]
    [InlineData("1.2/3")]
    [InlineData("1.2.3\n")]
    public void AVersionThatIsNotPlainTextIsRefused(string version)
    {
        InstallMarker("1.0.0");
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served);

        Assert.Equal("bad_version", ErrorOf(Begin(plugin, served, version: version)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]     // 63
    [InlineData("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]    // not hex
    public void AHashThatIsNotASha256IsRefused(string hash)
    {
        InstallMarker("1.0.0");
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served);

        Assert.Equal("bad_hash", ErrorOf(Begin(plugin, served, sha: hash)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("")]
    public void ASizeThatIsNotAPositiveWholeNumberIsRefused(string size)
    {
        InstallMarker("1.0.0");
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served);

        Assert.Equal("bad_size", ErrorOf(plugin.BeginThirdPartyUpdate("Marker", "1.1.0", Hex(served), size, Url)));
    }

    [Theory]
    [InlineData("http://umod.org/plugins/Marker.cs")]                    // not https
    [InlineData("ftp://umod.org/Marker.cs")]
    [InlineData("https://user:pass@umod.org/Marker.cs")]                 // credentials
    [InlineData("/plugins/Marker.cs")]                                    // not absolute
    [InlineData("")]
    public void OnlyAnAbsoluteHttpsAddressWithNoCredentialsIsFollowed(string url)
    {
        InstallMarker("1.0.0");
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served);

        Assert.Equal("bad_url", ErrorOf(Begin(plugin, served, url: url)));
    }

    [Fact]
    public void AnAddressLongerThanTheLimitIsRefused()
    {
        InstallMarker("1.0.0");
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served);

        Assert.Equal("bad_url", ErrorOf(Begin(plugin, served, url: "https://umod.org/" + new string('a', 500))));
    }

    [Fact]
    public void ItOnlyUpdatesAPluginThatIsInstalled()
    {
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served);

        Assert.Equal("not_installed", ErrorOf(Begin(plugin, served)));
        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public void TwoFilesDeclaringTheSameClassAreNotGuessedBetween()
    {
        InstallMarker("1.0.0");
        File.WriteAllBytes(Path.Combine(_plugins, "MarkerCopy.cs"), Script("0.9.0"));
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served);

        Assert.Equal("ambiguous", ErrorOf(Begin(plugin, served)));
    }

    [Fact]
    public void ThePluginIsFoundByItsClassNotByItsFileName()
    {
        File.WriteAllBytes(Path.Combine(_plugins, "SomethingElse.cs"), Script("1.0.0"));
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served);

        var reply = Reply(Begin(plugin, served));

        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        Assert.Equal("SomethingElse.cs", reply.GetProperty("data").GetProperty("file").GetString());
    }

    [Fact]
    public void AFileThatMentionsTheClassButDoesNotDeclareItIsNotThePlugin()
    {
        File.WriteAllText(Path.Combine(_plugins, "Other.cs"), "[Info(\"Other\", \"x\", \"1.0.0\")]\npublic class Other : RustPlugin { // Marker is another plugin\n }\n");
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served);

        Assert.Equal("not_installed", ErrorOf(Begin(plugin, served)));
    }

    [Fact]
    public void TheRustArchonFilesAreNeverCandidatesEvenIfTheyDeclareTheClass()
    {
        // A hostile request must not be able to aim the swap at this plugin's own file or the Updater's.
        File.WriteAllBytes(Path.Combine(_plugins, "RustArchonUpdater.cs"), Script("0.3.0", className: "Marker"));
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served);

        Assert.Equal("not_installed", ErrorOf(Begin(plugin, served)));
    }

    [Fact]
    public void AFileInASubfolderIsNotTheInstalledPlugin()
    {
        Directory.CreateDirectory(Path.Combine(_plugins, "sub"));
        File.WriteAllBytes(Path.Combine(_plugins, "sub", "Marker.cs"), Script("1.0.0"));
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served);

        Assert.Equal("not_installed", ErrorOf(Begin(plugin, served)));
    }

    [Fact]
    public void TheIdenticalFileAlreadyInstalledIsUpToDate()
    {
        var installed = InstallMarker("1.1.0");
        var plugin = Loaded((_, _) => installed);

        Assert.Equal("up_to_date", ErrorOf(Begin(plugin, installed, version: "1.1.0")));
    }

    [Fact]
    public void TheSameVersionWithDifferentContentIsAllowedBecauseTheAuthorMayHaveRepublishedIt()
    {
        // The panel only asks for this once a person has seen the changed file and said yes.
        InstallMarker("1.1.0");
        var republished = Script("1.1.0", extra: "// fixed\n");
        var oldInstance = new object();
        var current = Running(oldInstance, "1.1.0");
        var plugin = Loaded((_, _) => republished);
        plugin.FindLoaded = _ => current;

        Assert.True(Reply(Begin(plugin, republished, version: "1.1.0")).GetProperty("ok").GetBoolean());
        plugin.ThirdPartyTick();

        plugin.ThirdPartyTick();                                    // the same instance at the same version: it has NOT reloaded yet
        Assert.Equal("loading", plugin.ThirdParty.Phase);

        current = Running(new object(), "1.1.0");
        plugin.ThirdPartyTick();

        Assert.Equal("succeeded", plugin.ThirdParty.Phase);
    }

    // ---- one at a time, and never over the Updater ---------------------------------------------------------

    [Fact]
    public void ASecondUpdateWhileOneIsInProgressIsRefused()
    {
        InstallMarker("1.0.0");
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served, background: _ => { });     // the download never finishes
        Assert.True(Reply(Begin(plugin, served)).GetProperty("ok").GetBoolean());

        Assert.Equal("busy", ErrorOf(Begin(plugin, served)));
    }

    [Fact]
    public void ItWaitsWhileTheUpdaterIsReplacingThisPlugin()
    {
        InstallMarker("1.0.0");
        File.WriteAllText(Path.Combine(_data, "update-status.txt"), "phase=loading\n");
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served);

        Assert.Equal("busy", ErrorOf(Begin(plugin, served)));
    }

    [Fact]
    public void ItWaitsWhileThisPluginIsReplacingTheUpdater()
    {
        InstallMarker("1.0.0");
        File.WriteAllText(Path.Combine(_data, "updater-swap.txt"), "phase=loading\ntarget=0.3.0\nprevious=0.2.0\nreason=\nstarted=2026-09-21T12:00:00.0000000Z\nswapped=2026-09-21T12:00:00.0000000Z\n");
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served);

        Assert.Equal("busy", ErrorOf(Begin(plugin, served)));
    }

    // ---- things going wrong while it runs ------------------------------------------------------------------

    [Fact]
    public void ADownloadThatFailsChangesNothing()
    {
        var old = InstallMarker("1.0.0");
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => throw new IOException("the server answered 404"));
        Begin(plugin, served);

        plugin.ThirdPartyTick();

        Assert.Equal("failed", plugin.ThirdParty.Phase);
        Assert.Contains("download_failed", plugin.ThirdParty.Reason);
        Assert.Equal(old, File.ReadAllBytes(MarkerPath));
        Assert.False(File.Exists(MarkerPath + ".bak"));
    }

    [Fact]
    public void ADownloadThatNeverFinishesTimesOut()
    {
        InstallMarker("1.0.0");
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served, background: _ => { });
        Begin(plugin, served);

        _now = _now.AddSeconds(ArchonThirdParty.DownloadTimeoutSeconds + 16);
        plugin.ThirdPartyTick();

        Assert.Equal("failed", plugin.ThirdParty.Phase);
        Assert.Contains("download_timeout", plugin.ThirdParty.Reason);
    }

    [Fact]
    public void AFileThatHasTheRightHashButIsADifferentPluginIsNotApplied()
    {
        var old = InstallMarker("1.0.0");
        var wrong = Script("1.1.0", className: "Elsewhere");
        var plugin = Loaded((_, _) => wrong);
        Begin(plugin, wrong);                                        // hash and size are the file's own; the class is not Marker's

        plugin.ThirdPartyTick();

        Assert.Equal("failed", plugin.ThirdParty.Phase);
        Assert.Contains("not_that_plugin", plugin.ThirdParty.Reason);
        Assert.Equal(old, File.ReadAllBytes(MarkerPath));
    }

    [Fact]
    public void AFileWhoseVersionIsNotTheOneAskedForIsNotApplied()
    {
        var old = InstallMarker("1.0.0");
        var served = Script("1.2.0");
        var plugin = Loaded((_, _) => served);
        Begin(plugin, served, version: "1.1.0");

        plugin.ThirdPartyTick();

        Assert.Equal("failed", plugin.ThirdParty.Phase);
        Assert.Contains("version_mismatch", plugin.ThirdParty.Reason);
        Assert.Equal(old, File.ReadAllBytes(MarkerPath));
    }

    [Fact]
    public void ThePluginFileBeingReplacedDuringTheDownloadStopsTheUpdate()
    {
        InstallMarker("1.0.0");
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served, background: _ => { });
        Begin(plugin, served);
        File.WriteAllBytes(Path.Combine(_plugins, "MarkerCopy.cs"), Script("0.5.0"));        // now two files declare it
        typeof(ArchonPlugin).GetField("_thirdPartyDownload", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(plugin, new ArchonThirdParty.Download { Bytes = served, Done = true });

        plugin.ThirdPartyTick();

        Assert.Equal("failed", plugin.ThirdParty.Phase);
        Assert.Contains("changed", plugin.ThirdParty.Reason);
        Assert.False(File.Exists(MarkerPath + ".bak"));
    }

    [Fact]
    public void AnUpdaterSwapStartingDuringTheDownloadStopsTheUpdate()
    {
        InstallMarker("1.0.0");
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served, background: _ => { });
        Begin(plugin, served);
        File.WriteAllText(Path.Combine(_data, "update-status.txt"), "phase=downloading\n");
        typeof(ArchonPlugin).GetField("_thirdPartyDownload", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(plugin, new ArchonThirdParty.Download { Bytes = served, Done = true });

        plugin.ThirdPartyTick();

        Assert.Equal("failed", plugin.ThirdParty.Phase);
        Assert.Contains("busy", plugin.ThirdParty.Reason);
    }

    // ---- surviving this plugin reloading -------------------------------------------------------------------

    [Fact]
    public void ASwapNotYetConfirmedWhenThePluginReloadsIsStillWatchedAndRolledBack()
    {
        var old = InstallMarker("1.0.0");
        var served = Script("1.1.0");
        var first = Loaded((_, _) => served);
        first.FindLoaded = _ => null;
        Begin(first, served);
        first.ThirdPartyTick();
        Assert.Equal("loading", first.ThirdParty.Phase);

        // The RustArchon plugin itself reloads (a swap of a third-party plugin does not cause that, but a server restart or another reload can).
        var second = Loaded();
        second.FindLoaded = _ => null;
        Assert.Equal("loading", second.ThirdParty.Phase);
        Assert.Equal("Marker.cs", second.ThirdParty.File);

        _now = _now.AddSeconds(ArchonPlugin.ThirdPartyLoadWaitSeconds + 1);
        second.ThirdPartyTick();

        Assert.Equal("rolled-back", second.ThirdParty.Phase);
        Assert.Equal(old, File.ReadAllBytes(MarkerPath));
    }

    [Fact]
    public void ADownloadThatDiedWithThePreviousInstanceIsRecordedAsInterruptedAndChangedNothing()
    {
        var old = InstallMarker("1.0.0");
        var served = Script("1.1.0");
        var first = Loaded((_, _) => served, background: _ => { });
        Begin(first, served);

        var second = Loaded();

        Assert.Equal("failed", second.ThirdParty.Phase);
        Assert.Contains("interrupted", second.ThirdParty.Reason);
        Assert.Equal(old, File.ReadAllBytes(MarkerPath));
    }

    [Fact]
    public void AResumedSameVersionSwapWaitsLongEnoughForAReloadBeforeCountingAsLoaded()
    {
        InstallMarker("1.1.0");
        var republished = Script("1.1.0", extra: "// fixed\n");
        var first = Loaded((_, _) => republished);
        first.FindLoaded = _ => Running(new object(), "1.1.0");
        Begin(first, republished, version: "1.1.0");
        first.ThirdPartyTick();

        var second = Loaded();
        var instance = Running(new object(), "1.1.0");
        second.FindLoaded = _ => instance;
        _now = _now.AddSeconds(ArchonPlugin.ThirdPartyMinimumReloadSeconds - 3);
        second.ThirdPartyTick();
        Assert.Equal("loading", second.ThirdParty.Phase);           // cannot yet tell it apart from the one before

        _now = _now.AddSeconds(4);
        second.ThirdPartyTick();

        Assert.Equal("succeeded", second.ThirdParty.Phase);
    }

    // ---- the commands --------------------------------------------------------------------------------------

    [Fact]
    public void TheStatusCommandReportsTheLastUpdate()
    {
        InstallMarker("1.0.0");
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served);
        plugin.FindLoaded = _ => null;
        Begin(plugin, served);
        plugin.ThirdPartyTick();
        var arg = ConsoleSystem.Arg.WithArgs();

        plugin.CmdThirdPartyStatus(arg);

        var data = Reply(Assert.Single(arg.Replies)).GetProperty("data");
        Assert.Equal("loading", data.GetProperty("phase").GetString());
        Assert.Equal("Marker", data.GetProperty("class").GetString());
        Assert.Equal("1.1.0", data.GetProperty("targetVersion").GetString());
        Assert.Equal("1.0.0", data.GetProperty("previousVersion").GetString());
        Assert.Equal("Marker.cs", data.GetProperty("file").GetString());
        Assert.Equal(Hex(served), data.GetProperty("sha256").GetString());
    }

    [Fact]
    public void TheStatusOfAPluginThatHasDoneNothingIsIdle()
    {
        var plugin = Loaded();
        var arg = ConsoleSystem.Arg.WithArgs();

        plugin.CmdThirdPartyStatus(arg);

        Assert.Equal("idle", Reply(Assert.Single(arg.Replies)).GetProperty("data").GetProperty("phase").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    public void TheUpdateCommandNeedsExactlyFiveArguments(int count)
    {
        var plugin = Loaded();
        var arg = ConsoleSystem.Arg.WithArgs(Enumerable.Repeat("x", count).ToArray());

        plugin.CmdThirdPartyUpdate(arg);

        Assert.Equal("usage", ErrorOf(Assert.Single(arg.Replies)));
    }

    [Fact]
    public void TheUpdateCommandRunsTheUpdateFromItsArguments()
    {
        InstallMarker("1.0.0");
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served);
        var arg = ConsoleSystem.Arg.WithArgs("Marker", "1.1.0", Hex(served), served.Length.ToString(), Url);

        plugin.CmdThirdPartyUpdate(arg);

        Assert.True(Reply(Assert.Single(arg.Replies)).GetProperty("ok").GetBoolean());
        Assert.Equal("downloading", plugin.ThirdParty.Phase);
    }

    [Fact]
    public void ACallFromAnInGameConsoleIsIgnored()
    {
        var plugin = Loaded();
        var update = ConsoleSystem.Arg.WithArgs("Marker", "1.1.0", new string('a', 64), "10", Url);
        update.Connection = new object();
        var status = ConsoleSystem.Arg.WithArgs();
        status.Connection = new object();

        plugin.CmdThirdPartyUpdate(update);
        plugin.CmdThirdPartyStatus(status);

        Assert.Empty(update.Replies);
        Assert.Empty(status.Replies);
    }

    [Fact]
    public void TheCapabilityIsAdvertisedSoThePanelKnowsThisPluginCanDoIt()
    {
        var plugin = Loaded();
        var arg = ConsoleSystem.Arg.WithArgs();

        plugin.CmdHello(arg);

        var capabilities = Reply(Assert.Single(arg.Replies)).GetProperty("data").GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Contains("thirdparty-update", capabilities);
    }

    // ---- the pieces ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1.2", "1.2.0", true)]
    [InlineData("1.2.0", "1.2", true)]
    [InlineData("v1.2.3", "1.2.3", true)]
    [InlineData("V1.2.3", "1.2.3", true)]
    [InlineData(" 1.2.3 ", "1.2.3", true)]
    [InlineData("1.2.3", "1.2.4", false)]
    [InlineData("1.10.0", "1.1.0", false)]
    [InlineData("1.2.3.4", "1.2.3.4", true)]
    [InlineData("1.2.3.4", "1.2.3.5", false)]
    [InlineData("beta", "beta", true)]
    [InlineData("beta", "Beta", false)]
    [InlineData("1.2.3-rc1", "1.2.3", false)]
    public void VersionsAreComparedTheWayTheFrameworkKeepsThem(string a, string b, bool same)
    {
        Assert.Equal(same, ArchonThirdParty.SameVersion(a, b));
    }

    [Theory]
    [InlineData("[Info(\"Marker\", \"Someone\", \"1.2.3\")]", "1.2.3")]
    [InlineData("[Info( \"Marker\" , \"Someone\" , \"2.0\" )]", "2.0")]
    [InlineData("[Info(\"Marker\", \"A, B\", \"3.1.4\")]", "3.1.4")]
    [InlineData("no attribute here", null)]
    [InlineData("[Info(\"Marker\", \"Someone\")]", null)]
    public void ThePluginsVersionIsReadFromItsInfoAttribute(string text, string? expected)
    {
        Assert.Equal(expected, ArchonThirdParty.ReadInfoVersion(text));
    }

    [Theory]
    [InlineData("public class Marker : RustPlugin", "Marker", true)]
    [InlineData("class Marker:CovalencePlugin", "Marker", true)]
    [InlineData("public class MarkerTwo : RustPlugin", "Marker", false)]
    [InlineData("public class AMarker : RustPlugin", "Marker", false)]
    [InlineData("// class Marker was removed", "Marker", false)]
    [InlineData("public class Marker : RustPlugin", "Mark.*", false)]
    public void AClassIsDeclaredOnlyByAWholeWordClassDeclaration(string text, string className, bool declared)
    {
        Assert.Equal(declared, ArchonThirdParty.DeclaresClass(text, className));
    }

    [Fact]
    public void AnHttpsAddressIsRefusedForRedirectsToo()
    {
        // The download follows redirects by hand and checks every hop; a first hop that is not https never leaves the machine.
        var error = Assert.Throws<IOException>(() => ArchonThirdParty.DefaultDownload("http://umod.org/x.cs", 10));

        Assert.Contains("https", error.Message);
    }
}
