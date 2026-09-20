// Copyright ©2026 Scott Blomfield

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Oxide.Plugins;

namespace RustArchon.Plugin.Tests;

/// <summary>
/// Key changes ("bridges"). The Updater trusts the key the INSTALLED main plugin carries, so it follows every rotation
/// and is never stale, and it accepts a file that moves to a different key only when the key it trusts now signed the
/// file (last line) AND the key it moves to co-signed it (the line above). One hop, however many rotations behind the
/// server is - the Panel signs with whichever key the server reports.
/// </summary>
public sealed class RustArchonUpdaterBridgeTests : IDisposable
{
    private const string Marker = "// RUSTARCHON-SIG-V1: ";

    private readonly RSA _k1 = RSA.Create(2048);
    private readonly RSA _k2 = RSA.Create(2048);
    private readonly RSA _k3 = RSA.Create(2048);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rustarchon-bridge-" + Guid.NewGuid().ToString("N"));
    private readonly string _plugins;
    private readonly string _data;
    private DateTime _now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    public RustArchonUpdaterBridgeTests()
    {
        _plugins = Path.Combine(_root, "plugins");
        _data = Path.Combine(_root, "data", "RustArchon");
        Directory.CreateDirectory(_plugins);
        Directory.CreateDirectory(_data);
    }

    public void Dispose()
    {
        _k1.Dispose();
        _k2.Dispose();
        _k3.Dispose();
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    private static string Mod(RSA key) => Convert.ToBase64String(key.ExportParameters(false).Modulus!);
    private static string Exp(RSA key) => Convert.ToBase64String(key.ExportParameters(false).Exponent!);
    private static string Sign(RSA key, byte[] data) =>
        Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

    private string MainPath => Path.Combine(_plugins, "RustArchon.cs");

    private static byte[] Body(string version, RSA embeds) =>
        Encoding.UTF8.GetBytes($"[Info(\"RustArchon\", \"RustArchon\", \"{version}\")]\nclass X {{\n"
            + $"const string TrustedModulus = \"{Mod(embeds)}\";\nconst string TrustedExponent = \"{Exp(embeds)}\";\n}}\n");

    /// <summary>An ordinary Panel file: stamped with <paramref name="key"/> and signed by it.</summary>
    private static byte[] Plain(string version, RSA key, RSA? signer = null)
    {
        var body = Body(version, key);
        return body.Concat(Encoding.ASCII.GetBytes(Marker + Sign(signer ?? key, body) + "\n")).ToArray();
    }

    /// <summary>
    /// A bridge: stamped with <paramref name="newKey"/>, co-signed by <paramref name="cosigner"/> (the new key unless a
    /// test says otherwise), then signed as a whole by <paramref name="oldKey"/> - the key the server trusts today.
    /// </summary>
    private static byte[] Bridge(string version, RSA oldKey, RSA newKey, RSA? cosigner = null)
    {
        var body = Body(version, newKey);
        var withCosignature = body.Concat(Encoding.ASCII.GetBytes(Marker + Sign(cosigner ?? newKey, body) + "\n")).ToArray();
        return withCosignature.Concat(Encoding.ASCII.GetBytes(Marker + Sign(oldKey, withCosignature) + "\n")).ToArray();
    }

    private void Install(byte[] main) => File.WriteAllBytes(MainPath, main);

    private RustArchonUpdater NewUpdater(RSA ownStamp, byte[] served)
    {
        var updater = new RustArchonUpdater
        {
            PluginDirectory = _plugins,
            DataDirectory = _data,
            UtcNow = () => _now,
            TrustedModulus = Mod(ownStamp),
            TrustedExponent = Exp(ownStamp),
            Download = (_, _) => served
        };
        typeof(RustArchonUpdater).GetMethod("Init", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(updater, null);
        return updater;
    }

    private async Task<RustArchonUpdater> RunAsync(RSA ownStamp, byte[] served, string version)
    {
        var updater = NewUpdater(ownStamp, served);
        var reply = JsonDocument.Parse(updater.Begin(version, "http://192.0.2.1/x")).RootElement;
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        await updater.DownloadTask;
        updater.Tick();
        return updater;
    }

    private void WriteMarker(string version) =>
        File.WriteAllText(Path.Combine(_data, "loaded.txt"), $"version={version}\nutc={_now:o}\n");

    // ---- the bridge itself ---------------------------------------------------------------------------------

    [Fact]
    public async Task ABridgeSignedByTheTrustedKeyAndCosignedByTheNewOneIsInstalled()
    {
        Install(Plain("0.2.0", _k1));
        var bridge = Bridge("0.3.0", oldKey: _k1, newKey: _k2);

        var updater = await RunAsync(_k1, bridge, "0.3.0");

        Assert.Equal("loading", updater.State.Phase);
        Assert.Equal(bridge, File.ReadAllBytes(MainPath));
    }

    [Fact]
    public async Task TheBridgedPluginReportsItselfValidUnderItsNewKey()
    {
        // Otherwise the panel would show "does not match its signature" on every bridged server.
        Install(Plain("0.2.0", _k1));
        var bridge = Bridge("0.3.0", oldKey: _k1, newKey: _k2);
        await RunAsync(_k1, bridge, "0.3.0");

        var result = ArchonIntegrity.Check(File.ReadAllBytes(MainPath), Mod(_k2), Exp(_k2));

        Assert.Equal("valid", result.State);
    }

    [Fact]
    public async Task AnUpdateSignedByTheNewKeyIsAcceptedAfterABridgeEvenThoughTheUpdaterWasStampedWithTheOldOne()
    {
        // The whole point of anchoring trust to the installed plugin: the Updater file is only ever replaced by hand,
        // so its own stamp is K1 forever - but the server moved to K2 and must keep updating.
        Install(Plain("0.2.0", _k1));
        var first = await RunAsync(_k1, Bridge("0.3.0", _k1, _k2), "0.3.0");
        WriteMarker("0.3.0");
        first.Tick();
        Assert.Equal("succeeded", first.State.Phase);

        var second = await RunAsync(_k1, Plain("0.3.1", _k2), "0.3.1");

        Assert.Equal("loading", second.State.Phase);
    }

    [Fact]
    public async Task ServerTwoRotationsBehindReachesTheLatestInOneHop()
    {
        // The server still trusts K1; the Panel has rotated twice and its active key is K3. It signs with the key the
        // server reports (K1) and embeds the latest (K3): no chain through K2.
        Install(Plain("0.2.0", _k1));

        var updater = await RunAsync(_k1, Bridge("0.5.0", oldKey: _k1, newKey: _k3), "0.5.0");

        Assert.Equal("loading", updater.State.Phase);
        Assert.Equal("valid", ArchonIntegrity.Check(File.ReadAllBytes(MainPath), Mod(_k3), Exp(_k3)).State);
    }

    // ---- what must still be refused ------------------------------------------------------------------------

    private async Task AssertRefusedAsync(byte[] installed, byte[] served, string reason, string version = "0.3.0", RSA? ownStamp = null)
    {
        Install(installed);
        var updater = await RunAsync(ownStamp ?? _k1, served, version);

        Assert.Equal("failed", updater.State.Phase);
        Assert.StartsWith(reason, updater.State.Reason);
        Assert.Equal(installed, File.ReadAllBytes(MainPath)); // untouched
        Assert.False(File.Exists(MainPath + ".bak"));
    }

    [Fact]
    public async Task ABridgeNotSignedByTheTrustedKeyIsRefused()
    {
        // Signed by K3, an attacker's (or an unrelated Panel's) key, moving to K2.
        await AssertRefusedAsync(Plain("0.2.0", _k1), Bridge("0.3.0", oldKey: _k3, newKey: _k2), "signature_invalid");
    }

    [Fact]
    public async Task ABridgeTheNewKeyNeverSignedIsRefused()
    {
        // Trusted key signed it, but the key it moves to has not vouched: K3 co-signed a file that embeds K2.
        await AssertRefusedAsync(Plain("0.2.0", _k1), Bridge("0.3.0", oldKey: _k1, newKey: _k2, cosigner: _k3), "bridge_not_cosigned");
    }

    [Fact]
    public async Task ABridgeWithNoCosignatureAtAllIsRefused()
    {
        var body = Body("0.3.0", _k2);
        var singleSigned = body.Concat(Encoding.ASCII.GetBytes(Marker + Sign(_k1, body) + "\n")).ToArray();

        await AssertRefusedAsync(Plain("0.2.0", _k1), singleSigned, "bridge_not_cosigned");
    }

    [Fact]
    public async Task ABridgeThatEmbedsNoUsableKeyIsRefused()
    {
        var body = Encoding.UTF8.GetBytes(
            "[Info(\"RustArchon\", \"RustArchon\", \"0.3.0\")]\nclass X {\nconst string TrustedModulus = \"@@RUSTARCHON_TRUSTED_MODULUS@@\";\n"
            + "const string TrustedExponent = \"@@RUSTARCHON_TRUSTED_EXPONENT@@\";\n}\n");
        var served = body.Concat(Encoding.ASCII.GetBytes(Marker + Sign(_k1, body) + "\n")).ToArray();

        await AssertRefusedAsync(Plain("0.2.0", _k1), served, "new_key_invalid");
    }

    [Fact]
    public async Task TheUpdatersOwnStaleKeyIsNotTrustedOnceTheServerHasMovedOn()
    {
        // The server moved to K2. A file signed by K1 - retired, or later revoked - must no longer be accepted, even
        // though this Updater file was stamped with K1 when it was installed.
        await AssertRefusedAsync(Plain("0.3.0", _k2), Plain("0.3.1", _k1), "signature_invalid", version: "0.3.1", ownStamp: _k1);
    }

    [Fact]
    public async Task AnInstalledPluginThatDoesNotVerifyUnderItsOwnKeyMeansNothingIsTrusted()
    {
        // Hand-edited after signing, so it no longer verifies: there is no key to anchor to. Fail closed - a fresh
        // download from the Panel is the fix, not an Updater that trusts an unproven key.
        var tampered = Plain("0.2.0", _k1);
        tampered[Encoding.UTF8.GetString(tampered).IndexOf("class X", StringComparison.Ordinal) + 2] ^= 1; // inside the body, not the [Info] version

        await AssertRefusedAsync(tampered, Plain("0.3.0", _k1), "main_not_verified");
    }

    [Fact]
    public async Task AnUnsignedInstalledPluginMeansNothingIsTrusted()
    {
        await AssertRefusedAsync(Body("0.2.0", _k1), Plain("0.3.0", _k1), "main_not_verified");
    }

    [Fact]
    public async Task AnInstalledPluginWithNoKeyConstantsMeansNothingIsTrusted()
    {
        var bare = Encoding.UTF8.GetBytes("[Info(\"RustArchon\", \"RustArchon\", \"0.2.0\")]\nclass X { }\n");

        await AssertRefusedAsync(bare, Plain("0.3.0", _k1), "main_not_verified");
    }

    [Fact]
    public async Task ARolledBackBridgeLeavesTheServerOnTheOldKeyAndStillUpdatable()
    {
        Install(Plain("0.2.0", _k1));
        var original = File.ReadAllBytes(MainPath);
        var updater = await RunAsync(_k1, Bridge("0.3.0", _k1, _k2), "0.3.0");

        _now = _now.AddSeconds(60); // it never reported loading
        updater.Tick();

        Assert.Equal("rolled-back", updater.State.Phase);
        Assert.Equal(original, File.ReadAllBytes(MainPath));
        var reread = updater.ReadInstalledTrust(out _, out _);
        Assert.Equal("valid", reread.State);
        Assert.Equal(ArchonIntegrity.Fingerprint(Mod(_k1)), reread.KeyFingerprint);
    }

    // ---- reporting -----------------------------------------------------------------------------------------

    [Fact]
    public void StatusReportsWhichKeyTheUpdaterIsTrustingRightNow()
    {
        Install(Plain("0.2.0", _k2));
        var updater = NewUpdater(_k1, []);
        var arg = ConsoleSystem.Arg.WithArgs();

        updater.CmdStatus(arg);

        var data = JsonDocument.Parse(Assert.Single(arg.Replies)).RootElement.GetProperty("data");
        var trust = data.GetProperty("trust");
        Assert.Equal("valid", trust.GetProperty("state").GetString());
        Assert.Equal(ArchonIntegrity.Fingerprint(Mod(_k2)), trust.GetProperty("keyFingerprint").GetString());
        // and the Updater's own stamp is still reported separately, as its own file's signing state
        Assert.True(data.TryGetProperty("signing", out _));
    }

    [Fact]
    public void StatusSaysWhenThereIsNothingToTrust()
    {
        Install(Body("0.2.0", _k1)); // unsigned
        var updater = NewUpdater(_k1, []);
        var arg = ConsoleSystem.Arg.WithArgs();

        updater.CmdStatus(arg);

        var trust = JsonDocument.Parse(Assert.Single(arg.Replies)).RootElement.GetProperty("data").GetProperty("trust");
        Assert.Equal("unsigned", trust.GetProperty("state").GetString());
    }
}
