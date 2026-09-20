// Copyright ©2026 Scott Blomfield

using System.Security.Cryptography;
using System.Text;
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
/// The whole update chain with nothing faked on either side of the wire: the Api's REAL stamper, signer and embedded
/// sources produce an Updater and two versions of the main plugin; the plugin repo's REAL Updater, configured only
/// from the key stamped into its own served file, replaces the older main with the newer. If the Api changes what it
/// serves, or the Updater changes what it accepts, this fails before a server does.
/// </summary>
public sealed class ApiToUpdaterContractTests : IDisposable
{
    private readonly PlatformSetting _row = new() { Key = PlatformSettingsRegistry.PluginSigningKey, Value = "" };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rustarchon-chain-" + Guid.NewGuid().ToString("N"));
    private readonly string _plugins;
    private readonly string _data;
    private DateTime _now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    public ApiToUpdaterContractTests()
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

    /// <summary>The embedded sources, with the main plugin's version optionally rewritten to make an "older" one.</summary>
    private sealed class VersionedSource(string? mainVersion) : IPluginScriptSource
    {
        private readonly EmbeddedPluginScriptSource _real = new();

        public Task<string> ReadSourceAsync() => Task.FromResult(ReadMainSource());

        public Task<string> ReadUpdaterSourceAsync() => Task.FromResult(_real.ReadUpdaterSource());

        private string ReadMainSource() => mainVersion is null
            ? _real.ReadSource()
            : Regex.Replace(_real.ReadSource(), @"(\[Info\(""RustArchon""\s*,\s*""[^""]*""\s*,\s*"")[^""]+(""\)\])", "${1}" + mainVersion + "${2}");
    }

    private PluginSigningService NewSigningService()
    {
        var protector = new Mock<IApiKeyProtector>();
        protector.Setup(p => p.Protect(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((_, plain) => "enc:" + plain);
        protector.Setup(p => p.Unprotect(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((_, stored) => stored["enc:".Length..]);

        var settings = new Mock<IPlatformSettingRepository>();
        settings.Setup(s => s.GetByKeyAsync(PlatformSettingsRegistry.PluginSigningKey)).ReturnsAsync(_row);
        settings.Setup(s => s.SetValueIfEmptyAsync(PlatformSettingsRegistry.PluginSigningKey, It.IsAny<string>()))
            .ReturnsAsync((string _, string value) =>
            {
                if (!string.IsNullOrEmpty(_row.Value)) { return false; }
                _row.Value = value;
                return true;
            });

        return new PluginSigningService(settings.Object, protector.Object, NullLogger<PluginSigningService>.Instance);
    }

    private static string Extract(string text, string constantName)
    {
        var match = Regex.Match(text, "const string " + constantName + " = \"([^\"]*)\";");
        Assert.True(match.Success, $"{constantName} not found in the served file");
        return match.Groups[1].Value;
    }

    private async Task<(RustArchonUpdater Updater, byte[] Old, byte[] New)> ArrangeAsync(string oldVersion = "0.2.0")
    {
        var signing = NewSigningService();
        var oldMain = (await new PluginScriptService(signing, new VersionedSource(oldVersion)).BuildAsync()).Bytes;
        var newMain = (await new PluginScriptService(signing, new VersionedSource(null)).BuildAsync()).Bytes;
        var updaterFile = (await new PluginScriptService(signing, new VersionedSource(null)).BuildUpdaterAsync()).Bytes;

        File.WriteAllBytes(Path.Combine(_plugins, "RustArchon.cs"), oldMain);
        File.WriteAllBytes(Path.Combine(_plugins, "RustArchonUpdater.cs"), updaterFile);

        // The Updater trusts exactly what is stamped into its own served file - nothing handed in from outside.
        var updaterText = Encoding.UTF8.GetString(updaterFile);
        var updater = new RustArchonUpdater
        {
            PluginDirectory = _plugins,
            DataDirectory = _data,
            UtcNow = () => _now,
            TrustedModulus = Extract(updaterText, "TrustedModulus"),
            TrustedExponent = Extract(updaterText, "TrustedExponent"),
            Download = _ => newMain
        };
        typeof(RustArchonUpdater).GetMethod("Init", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(updater, null);
        return (updater, oldMain, newMain);
    }

    [Fact]
    public async Task TheRealUpdaterReplacesTheOlderRealMainWithTheNewerOne()
    {
        var (updater, oldMain, newMain) = await ArrangeAsync();
        var latest = await new PluginScriptService(NewSigningService(), new VersionedSource(null)).BuildAsync();

        var reply = System.Text.Json.JsonDocument.Parse(updater.Begin(latest.PluginVersion!, "http://192.168.0.46:5200/ingest/plugin/x/y")).RootElement;
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        await updater.DownloadTask;
        updater.Tick();

        Assert.Equal("loading", updater.State.Phase);
        Assert.Equal(newMain, File.ReadAllBytes(Path.Combine(_plugins, "RustArchon.cs")));
        Assert.Equal(oldMain, File.ReadAllBytes(Path.Combine(_plugins, "RustArchon.cs.bak")));
    }

    [Fact]
    public async Task TheNewMainSatisfiesTheOldMainsSelfCheckSoItWillAlsoReportValid()
    {
        var (_, _, newMain) = await ArrangeAsync();
        var text = Encoding.UTF8.GetString(newMain);

        var result = ArchonIntegrity.Check(newMain, Extract(text, "TrustedModulus"), Extract(text, "TrustedExponent"));

        Assert.Equal("valid", result.State);
    }

    [Fact]
    public async Task ARollbackIsWhatHappensIfTheNewVersionNeverReportsLoaded()
    {
        var (updater, oldMain, _) = await ArrangeAsync();
        var latest = await new PluginScriptService(NewSigningService(), new VersionedSource(null)).BuildAsync();
        updater.Begin(latest.PluginVersion!, "http://192.168.0.46:5200/ingest/plugin/x/y");
        await updater.DownloadTask;
        updater.Tick(); // swap

        _now = _now.AddSeconds(60); // longer than the load wait, and no marker appeared
        updater.Tick();

        Assert.Equal("rolled-back", updater.State.Phase);
        Assert.Equal(oldMain, File.ReadAllBytes(Path.Combine(_plugins, "RustArchon.cs")));
    }

    [Fact]
    public async Task ARealMainThatIsNotNewerIsRefusedByTheUpdater()
    {
        var (updater, _, _) = await ArrangeAsync(oldVersion: "9.0.0");
        var latest = await new PluginScriptService(NewSigningService(), new VersionedSource(null)).BuildAsync();

        var reply = System.Text.Json.JsonDocument.Parse(updater.Begin(latest.PluginVersion!, "http://192.168.0.46:5200/x")).RootElement;

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("not_newer", reply.GetProperty("err").GetString());
    }
}
