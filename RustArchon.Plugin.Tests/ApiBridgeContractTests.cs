// Copyright ©2026 Scott Blomfield

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Oxide.Plugins;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Repositories;

namespace RustArchon.Plugin.Tests;

/// <summary>
/// Key rotation end to end with nothing faked across the boundary: the Api's REAL signing service, script service and
/// embedded sources build a bridge for a server still on an old key, and the plugin repo's REAL Updater installs it
/// and the plugin's REAL integrity check accepts it under the new key. Two rotations behind is still one hop.
/// </summary>
public sealed class ApiBridgeContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rustarchon-bridgechain-" + Guid.NewGuid().ToString("N"));
    private readonly string _plugins;
    private readonly string _data;
    private DateTime _now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    private readonly Dictionary<string, PluginKeyHistory> _history = new();

    public ApiBridgeContractTests()
    {
        _plugins = Path.Combine(_root, "plugins");
        _data = Path.Combine(_root, "data", "RustArchon");
        Directory.CreateDirectory(_plugins);
        Directory.CreateDirectory(_data);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    private sealed class VersionedSource(string? mainVersion) : IPluginScriptSource
    {
        private readonly EmbeddedPluginScriptSource _real = new();

        public Task<string> ReadSourceAsync() => Task.FromResult(ReadMainSource());

        public Task<string> ReadUpdaterSourceAsync() => Task.FromResult(_real.ReadUpdaterSource());

        private string ReadMainSource() => mainVersion is null
            ? _real.ReadSource()
            : Regex.Replace(_real.ReadSource(), @"(\[Info\(""RustArchon""\s*,\s*""[^""]*""\s*,\s*"")[^""]+(""\)\])", "${1}" + mainVersion + "${2}");
    }

    /// <summary>One deployment's signing identity: a stored active key plus whatever history the test has recorded.</summary>
    private sealed class Deployment(ApiBridgeContractTests owner)
    {
        public readonly PlatformSetting Row = new() { Key = PlatformSettingsRegistry.PluginSigningKey, Value = "" };

        public PluginSigningService Service()
        {
            var protector = new Mock<IApiKeyProtector>();
            protector.Setup(p => p.Protect(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((_, plain) => "enc:" + plain);
            protector.Setup(p => p.Unprotect(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((_, stored) => stored["enc:".Length..]);

            var settings = new Mock<IPlatformSettingRepository>();
            settings.Setup(s => s.GetByKeyAsync(PlatformSettingsRegistry.PluginSigningKey)).ReturnsAsync(Row);
            settings.Setup(s => s.SetValueIfEmptyAsync(PlatformSettingsRegistry.PluginSigningKey, It.IsAny<string>()))
                .ReturnsAsync((string _, string value) =>
                {
                    if (!string.IsNullOrEmpty(Row.Value)) { return false; }
                    Row.Value = value;
                    return true;
                });

            var history = new Mock<IPluginKeyHistoryRepository>();
            history.Setup(h => h.GetByFingerprintAsync(It.IsAny<string>()))
                .ReturnsAsync((string fp) => owner._history.GetValueOrDefault(fp));

            return new PluginSigningService(settings.Object, protector.Object, NullLogger<PluginSigningService>.Instance, history.Object);
        }

        public PluginScriptService Scripts(string? mainVersion = null) => new(Service(), new VersionedSource(mainVersion));
    }

    /// <summary>Retires the deployment's current key into the shared history and makes a brand-new key active.</summary>
    private async Task<string> RotateAsync(Deployment deployment, PluginKeyState retiredAs = PluginKeyState.Retired)
    {
        var old = await deployment.Service().GetPublicKeyAsync();
        _history[old.Fingerprint] = new PluginKeyHistory
        {
            Fingerprint = old.Fingerprint,
            ModulusBase64 = old.ModulusBase64,
            ExponentBase64 = old.ExponentBase64,
            EncryptedPrivateKey = deployment.Row.Value,
            State = retiredAs
        };
        deployment.Row.Value = ""; // the next use of the service generates a fresh key
        await deployment.Service().GetPublicKeyAsync();
        return old.Fingerprint;
    }

    private static string Extract(string text, string constantName)
    {
        var match = Regex.Match(text, "const string " + constantName + " = \"([^\"]*)\";");
        Assert.True(match.Success, $"{constantName} not found");
        return match.Groups[1].Value;
    }

    private RustArchonUpdater NewUpdater(byte[] updaterFile, Func<string, byte[]> download)
    {
        var text = Encoding.UTF8.GetString(updaterFile);
        var updater = new RustArchonUpdater
        {
            PluginDirectory = _plugins,
            DataDirectory = _data,
            UtcNow = () => _now,
            TrustedModulus = Extract(text, "TrustedModulus"),
            TrustedExponent = Extract(text, "TrustedExponent"),
            Download = download
        };
        typeof(RustArchonUpdater).GetMethod("Init", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(updater, null);
        return updater;
    }

    private async Task RunUpdateAsync(RustArchonUpdater updater, string version)
    {
        var reply = JsonDocument.Parse(updater.Begin(version, "http://192.168.0.46:5200/ingest/plugin/x/y")).RootElement;
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        await updater.DownloadTask;
        updater.Tick();
    }

    private string MainPath => Path.Combine(_plugins, "RustArchon.cs");

    // ---- one rotation --------------------------------------------------------------------------------------

    [Fact]
    public async Task AServerOnTheOldKeyIsBridgedToTheNewOneByTheRealUpdater()
    {
        var deployment = new Deployment(this);
        var oldMain = (await deployment.Scripts("0.2.0").BuildAsync()).Bytes;
        var updaterFile = (await deployment.Scripts().BuildUpdaterAsync()).Bytes; // stamped with the OLD key, installed by hand
        File.WriteAllBytes(MainPath, oldMain);
        var oldFingerprint = await RotateAsync(deployment);

        var bridge = await deployment.Scripts().BuildBridgeAsync(oldFingerprint);
        var updater = NewUpdater(updaterFile, _ => bridge.Bytes);
        await RunUpdateAsync(updater, bridge.PluginVersion!);

        Assert.Equal("loading", updater.State.Phase);
        Assert.Equal(bridge.Bytes, File.ReadAllBytes(MainPath));
        var newKey = await deployment.Service().GetPublicKeyAsync();
        Assert.Equal("valid", ArchonIntegrity.Check(bridge.Bytes, newKey.ModulusBase64, newKey.ExponentBase64).State);
        Assert.NotEqual(oldFingerprint, newKey.Fingerprint);
    }

    // ---- two rotations behind ------------------------------------------------------------------------------

    [Fact]
    public async Task AServerTwoRotationsBehindGetsToTheLatestInOneHopAndKeepsUpdatingAfterwards()
    {
        var deployment = new Deployment(this);
        var oldMain = (await deployment.Scripts("0.2.0").BuildAsync()).Bytes;
        var updaterFile = (await deployment.Scripts().BuildUpdaterAsync()).Bytes;
        File.WriteAllBytes(MainPath, oldMain);
        var first = await RotateAsync(deployment);
        await RotateAsync(deployment); // and again: the active key is now the third one
        var activeKey = await deployment.Service().GetPublicKeyAsync();

        // The server still trusts the FIRST key. One bridge, signed with the first, embedding the third.
        var bridge = await deployment.Scripts().BuildBridgeAsync(first);
        var updater = NewUpdater(updaterFile, _ => bridge.Bytes);
        await RunUpdateAsync(updater, bridge.PluginVersion!);
        Assert.Equal("loading", updater.State.Phase);
        Assert.Equal("valid", ArchonIntegrity.Check(File.ReadAllBytes(MainPath), activeKey.ModulusBase64, activeKey.ExponentBase64).State);

        // Confirm it loaded; then an ordinary update signed by the third key must be accepted by the SAME Updater
        // file, which is still stamped with the first key - because it trusts the plugin's key, not its own stamp.
        File.WriteAllText(Path.Combine(_data, "loaded.txt"), $"version={bridge.PluginVersion}\nutc={_now:o}\n");
        updater.Tick();
        Assert.Equal("succeeded", updater.State.Phase);

        var next = await deployment.Scripts("9.0.0").BuildAsync();
        var second = NewUpdater(updaterFile, _ => next.Bytes);
        await RunUpdateAsync(second, "9.0.0");
        Assert.Equal("loading", second.State.Phase);
    }

    // ---- what must not work --------------------------------------------------------------------------------

    [Fact]
    public async Task ARevokedKeyGetsNoBridge()
    {
        var deployment = new Deployment(this);
        var oldFingerprint = await RotateAsync(deployment, PluginKeyState.Revoked);

        await Assert.ThrowsAsync<PluginKeyUnavailableException>(() => deployment.Scripts().BuildBridgeAsync(oldFingerprint));
    }

    [Fact]
    public async Task AServerOnAKeyThisPanelNeverHadGetsNoBridge()
    {
        var deployment = new Deployment(this);
        await deployment.Service().GetPublicKeyAsync();

        await Assert.ThrowsAsync<PluginKeyUnavailableException>(() => deployment.Scripts().BuildBridgeAsync("ffffffffffffffff"));
    }

    [Fact]
    public async Task ABridgeBuiltForAnotherServersKeyIsNotAcceptedByAnUpdaterOnADifferentKey()
    {
        // Two separate Panels. A bridge Panel B made for its own retired key means nothing to a server on Panel A's key.
        var panelA = new Deployment(this);
        var oldMain = (await panelA.Scripts("0.2.0").BuildAsync()).Bytes;
        var updaterFile = (await panelA.Scripts().BuildUpdaterAsync()).Bytes;
        File.WriteAllBytes(MainPath, oldMain);

        var panelB = new Deployment(this);
        var bFingerprint = await RotateAsync(panelB);
        var foreign = await panelB.Scripts().BuildBridgeAsync(bFingerprint);

        var updater = NewUpdater(updaterFile, _ => foreign.Bytes);
        await RunUpdateAsync(updater, foreign.PluginVersion!);

        Assert.Equal("failed", updater.State.Phase);
        Assert.StartsWith("signature_invalid", updater.State.Reason);
        Assert.Equal(oldMain, File.ReadAllBytes(MainPath));
    }
}
