// Copyright ©2026 Scott Blomfield

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Oxide.Plugins;
using RustArchon.Shared.PluginZips;
using ArchonPlugin = Oxide.Plugins.RustArchon;

namespace RustArchon.Plugin.Tests;

/// <summary>
/// Applying a plugin update that ships as a ZIP archive, following the person's folder rules. The plugin downloads its own copy, checks its SHA-256,
/// reads the archive itself (by reflection, so the plugin file does not depend on System.IO.Compression at compile time), works out the mapping with
/// its own copy of the algorithm, unpacks to a staging folder under strict bounds, backs up what it will replace with a manifest, writes the files
/// (the plugin's .cs last) and lets the framework's registry confirm the new version - or puts everything back. Runs against temp Carbon/Oxide-shaped
/// folders with the download, the clock and the framework's list of loaded plugins under the test's control.
/// </summary>
public sealed class RustArchonThirdPartyZipTests : IDisposable
{
    private const string SigMarker = "// RUSTARCHON-SIG-V1: ";
    private const string Url = "https://umod.org/plugins/Test.zip";

    private readonly RSA _key = RSA.Create(2048);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rustarchon-3pzip-" + Guid.NewGuid().ToString("N"));
    private string _framework;
    private DateTime _now = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    public RustArchonThirdPartyZipTests()
    {
        UseFramework("carbon");
    }

    public void Dispose()
    {
        _key.Dispose();
        foreach (var path in new[] { _root, _root + "-snapshot" })
        {
            if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
        }
    }

    // ---- the folders -------------------------------------------------------------------------------------------------

    private void UseFramework(string name)
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
        _framework = Path.Combine(_root, name);
        Directory.CreateDirectory(Plugins);
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(RustArchonData);
    }

    private string Plugins => Path.Combine(_framework, "plugins");
    private string Data => Path.Combine(_framework, "data");
    private string RustArchonData => Path.Combine(Data, "RustArchon");
    private string Configs => Path.Combine(_framework, "configs");
    private string Lang => Path.Combine(_framework, "lang");
    private string BackupDir => Path.Combine(RustArchonData, "thirdparty-backup", "Test");
    private string StagingDir => Path.Combine(RustArchonData, "thirdparty-staging", "Test");
    private string TestCs => Path.Combine(Plugins, "test.cs");
    private string ConfigJson => Path.Combine(Configs, "test.json");
    private string One => Path.Combine(Data, "test", "one.jpg");
    private string Two => Path.Combine(Data, "test", "two.jpg");

    private string Modulus => Convert.ToBase64String(_key.ExportParameters(false).Modulus!);
    private string Exponent => Convert.ToBase64String(_key.ExportParameters(false).Exponent!);

    // ---- the plugin and the archive -----------------------------------------------------------------------------------

    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] Script(string version, string className = "Test", string extra = "") =>
        Encoding.UTF8.GetBytes($"using Oxide.Core;\n[Info(\"{className}\", \"Someone\", \"{version}\")]\npublic class {className} : RustPlugin\n{{\n{extra}}}\n");

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] Bytes(int length, int seed) => Enumerable.Range(0, length).Select(i => (byte)(seed + i * 7)).ToArray();

    private static ZipMappingRule Folder(string source, string role, string sub = "", bool keep = false) =>
        new() { IsFolder = true, Source = source, Role = role, SubFolder = sub, KeepExisting = keep };

    private static ZipMappingRule FileRule(string source, string role, string sub = "", bool keep = false) =>
        new() { IsFolder = false, Source = source, Role = role, SubFolder = sub, KeepExisting = keep };

    /// <summary>The user's example archive: an English and a Russian copy of the plugin, its settings and two pictures.</summary>
    private static List<(string Name, byte[] Data)> UserArchive(string version = "1.1.0") =>
    [
        ("en/plugins/test.cs", Script(version)),
        ("en/configs/test.json", Text("{\"v\":2}")),
        ("en/images/test/one.jpg", Bytes(1000, 9)),
        ("en/images/test/two.jpg", Bytes(2000, 3)),
        ("ru/plugins/test.cs", Script(version, extra: "// russian\n")),
        ("ru/configs/test.json", Text("{\"v\":\"ru\"}")),
        ("ru/images/test/one.jpg", Bytes(1001, 1)),
        ("ru/images/test/two.jpg", Bytes(2001, 2))
    ];

    private static List<ZipMappingRule> UserRules(bool keepConfig = false) =>
    [
        Folder("en/plugins/", ZipRoles.Plugins), Folder("en/configs/", ZipRoles.Config, keep: keepConfig), Folder("en/images/", ZipRoles.Data), Folder("ru/", ZipRoles.Skip)
    ];

    private static byte[] MakeZip(IEnumerable<(string Name, byte[] Data)> entries, CompressionLevel level = CompressionLevel.Optimal, bool directoryEntries = false)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var directories = new HashSet<string>();
            foreach (var (name, data) in entries)
            {
                if (directoryEntries)
                {
                    var parts = name.Split('/');
                    for (var i = 1; i < parts.Length; i++)
                    {
                        var directory = string.Join("/", parts.Take(i)) + "/";
                        if (directories.Add(directory)) { archive.CreateEntry(directory); }
                    }
                }

                var entry = archive.CreateEntry(name, level);
                using var writer = entry.Open();
                writer.Write(data, 0, data.Length);
            }
        }
        return stream.ToArray();
    }

    /// <summary>Changes the uncompressed size a zip claims for one entry (in the central directory and in the local header): a lying archive.</summary>
    private static byte[] ClaimSize(byte[] zip, string name, uint size)
    {
        var copy = (byte[])zip.Clone();
        var wanted = Encoding.UTF8.GetBytes(name);
        var patched = 0;
        for (var i = 0; i + 46 <= copy.Length; i++)
        {
            if (copy[i] != 0x50 || copy[i + 1] != 0x4B || copy[i + 2] != 0x01 || copy[i + 3] != 0x02) { continue; }
            var nameLength = BitConverter.ToUInt16(copy, i + 28);
            if (nameLength != wanted.Length || !copy.AsSpan(i + 46, nameLength).SequenceEqual(wanted)) { continue; }

            BitConverter.GetBytes(size).CopyTo(copy, i + 24);
            var local = (int)BitConverter.ToUInt32(copy, i + 42);
            Assert.Equal(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, copy.AsSpan(local, 4).ToArray());
            BitConverter.GetBytes(size).CopyTo(copy, local + 22);
            patched++;
        }
        Assert.Equal(1, patched);
        return copy;
    }

    private void InstallMain()
    {
        var payload = Encoding.UTF8.GetBytes("[Info(\"RustArchon\", \"RustArchon\", \"0.9.0\")]\nclass M {}\n");
        var signature = _key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        File.WriteAllBytes(Path.Combine(Plugins, "RustArchon.cs"), payload.Concat(Encoding.ASCII.GetBytes(SigMarker + Convert.ToBase64String(signature) + "\n")).ToArray());
    }

    /// <summary>The plugin as installed before the update: the old test.cs.</summary>
    private byte[] InstallOld(string version = "1.0.0")
    {
        var bytes = Script(version);
        File.WriteAllBytes(TestCs, bytes);
        return bytes;
    }

    private ArchonPlugin Loaded(Func<string, long, byte[]> download = null, bool validMain = true, Action<Action> background = null)
    {
        if (validMain) { InstallMain(); }
        var plugin = new ArchonPlugin
        {
            SettingsFilePath = Path.Combine(RustArchonData, "settings.txt"),
            ScriptFilePath = Path.Combine(Plugins, "RustArchon.cs"),
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

    private static string ErrorOf(string json) => Reply(json).GetProperty("err").GetString();

    private static string Encode(IReadOnlyList<ZipMappingRule> rules) => ZipMapping.Encode(rules)!;

    private string BeginZip(ArchonPlugin plugin, byte[] zip, IReadOnlyList<ZipMappingRule> rules = null, string version = "1.1.0", string className = "Test",
        string installBytes = "1000000", string sha = null, string size = null, string url = null, string rulesArgument = null) =>
        plugin.BeginThirdPartyZip(className, version, sha ?? Hex(zip), size ?? zip.Length.ToString(), installBytes, rulesArgument ?? Encode(rules ?? UserRules()), url ?? Url);

    private static ArchonThirdParty.LoadedPlugin Running(object instance, string version) => new() { Instance = instance, Version = version };

    /// <summary>Every file and folder under the test root, by relative path, with a hash of the contents (folders: "dir"), except the plugin's own state file.</summary>
    private Dictionary<string, string> Snapshot()
    {
        var result = new Dictionary<string, string>();
        foreach (var path in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(path) == "thirdparty-swap.txt") { continue; }
            result[Path.GetRelativePath(_root, path)] = Directory.Exists(path) ? "dir" : Hex(File.ReadAllBytes(path));
        }
        return result;
    }

    private List<string> Changed(Dictionary<string, string> before, Dictionary<string, string> after) =>
        after.Where(a => !before.TryGetValue(a.Key, out var old) || old != a.Value).Select(a => a.Key)
            .Concat(before.Keys.Where(b => !after.ContainsKey(b))).Select(p => p.Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();

    private string SwapFile(string key)
    {
        var line = File.ReadAllLines(Path.Combine(RustArchonData, "thirdparty-swap.txt")).Single(l => l.StartsWith(key + "=", StringComparison.Ordinal));
        return line[(key.Length + 1)..];
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var directory in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) { Directory.CreateDirectory(directory.Replace(from, to)); }
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) { File.Copy(file, file.Replace(from, to), true); }
    }

    /// <summary>An installed old plugin, its old settings and one old picture, and a plugin loaded with the archive on offer, ready to be asked to update.</summary>
    private ArchonPlugin Ready(byte[] zip, Action<Action> background = null)
    {
        InstallOld();
        var plugin = Loaded((_, _) => zip, background: background);
        return plugin;
    }

    private void InstallOldSettingsAndPicture()
    {
        Directory.CreateDirectory(Configs);
        File.WriteAllText(ConfigJson, "{\"v\":1}");
        Directory.CreateDirectory(Path.GetDirectoryName(One)!);
        File.WriteAllBytes(One, Bytes(50, 1));
    }

    // ---- the happy path -----------------------------------------------------------------------------------------------

    [Fact]
    public void TheUsersExampleIsUnpackedByItsRulesBackedUpAndConfirmedByTheFramework()
    {
        var oldPlugin = InstallOld();
        InstallOldSettingsAndPicture();
        var zip = MakeZip(UserArchive());
        var current = Running(new object(), "1.0.0");
        string seenUrl = null;
        long seenSize = 0;
        var plugin = Loaded((url, size) => { seenUrl = url; seenSize = size; return zip; });
        plugin.FindLoaded = _ => current;
        var before = Snapshot();

        var reply = Reply(BeginZip(plugin, zip));
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        Assert.Equal("downloading", reply.GetProperty("data").GetProperty("phase").GetString());
        Assert.Equal("test.cs", reply.GetProperty("data").GetProperty("file").GetString());
        Assert.Equal("1.0.0", reply.GetProperty("data").GetProperty("installedVersion").GetString());
        Assert.Equal("1.1.0", reply.GetProperty("data").GetProperty("targetVersion").GetString());
        Assert.Equal("zip", reply.GetProperty("data").GetProperty("kind").GetString());
        Assert.Equal((Url, (long)zip.Length), (seenUrl, seenSize));

        plugin.ThirdPartyTick();                                    // verifies, unpacks and writes

        Assert.Equal("loading", plugin.ThirdParty.Phase);
        Assert.Equal("zip", plugin.ThirdParty.Kind);
        Assert.Equal(4, plugin.ThirdParty.Installed);
        Assert.Equal(Script("1.1.0"), File.ReadAllBytes(TestCs));
        Assert.Equal(Text("{\"v\":2}"), File.ReadAllBytes(ConfigJson));
        Assert.Equal(Bytes(1000, 9), File.ReadAllBytes(One));
        Assert.Equal(Bytes(2000, 3), File.ReadAllBytes(Two));                 // the structure below en/images/ is kept
        Assert.False(Directory.Exists(StagingDir));

        // The backup: what was there, mirrored by role, and a manifest that says what was replaced and what was created.
        Assert.Equal(oldPlugin, File.ReadAllBytes(Path.Combine(BackupDir, "plugins", "test.cs")));
        Assert.Equal(Text("{\"v\":1}"), File.ReadAllBytes(Path.Combine(BackupDir, "config", "test.json")));
        Assert.Equal(Bytes(50, 1), File.ReadAllBytes(Path.Combine(BackupDir, "data", "test", "one.jpg")));
        Assert.False(File.Exists(Path.Combine(BackupDir, "data", "test", "two.jpg")));
        var manifest = File.ReadAllLines(Path.Combine(BackupDir, "manifest.txt"));
        Assert.Equal(
        [
            $"E\t{ConfigJson}\tconfig/test.json", $"E\t{One}\tdata/test/one.jpg", $"N\t{Two}", $"E\t{TestCs}\tplugins/test.cs"
        ], manifest);

        // Nothing else on the server changed: no russian files, no temp files, nothing outside the folders the rules name.
        var changed = Changed(before, Snapshot());
        Assert.Equal(new[]
        {
            "carbon/configs/test.json", "carbon/data/RustArchon/thirdparty-backup", "carbon/data/RustArchon/thirdparty-backup/Test", "carbon/data/RustArchon/thirdparty-backup/Test/config",
            "carbon/data/RustArchon/thirdparty-backup/Test/config/test.json", "carbon/data/RustArchon/thirdparty-backup/Test/data", "carbon/data/RustArchon/thirdparty-backup/Test/data/test",
            "carbon/data/RustArchon/thirdparty-backup/Test/data/test/one.jpg", "carbon/data/RustArchon/thirdparty-backup/Test/manifest.txt", "carbon/data/RustArchon/thirdparty-backup/Test/plugins",
            "carbon/data/RustArchon/thirdparty-backup/Test/plugins/test.cs", "carbon/data/test/one.jpg", "carbon/data/test/two.jpg", "carbon/plugins/test.cs"
        }.Order(StringComparer.Ordinal), changed);

        _now = _now.AddSeconds(5);
        plugin.ThirdPartyTick();                                    // still the old instance: not confirmed
        Assert.Equal("loading", plugin.ThirdParty.Phase);

        current = Running(new object(), "1.1.0");                   // the framework has reloaded it
        plugin.ThirdPartyTick();

        Assert.Equal("succeeded", plugin.ThirdParty.Phase);
        Assert.Equal(Script("1.1.0"), File.ReadAllBytes(TestCs));    // the new files stay, and so does the backup
        Assert.True(File.Exists(Path.Combine(BackupDir, "manifest.txt")));
        Assert.Equal("succeeded", SwapFile("phase"));
        Assert.Equal("zip", SwapFile("kind"));
        Assert.Equal("4", SwapFile("installed"));
        Assert.Equal(BackupDir, SwapFile("backup"));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void FolderEntriesInTheArchiveAreNotFiles()
    {
        InstallOld();
        var zip = MakeZip(UserArchive(), directoryEntries: true);
        var plugin = Loaded((_, _) => zip);

        Assert.True(Reply(BeginZip(plugin, zip)).GetProperty("ok").GetBoolean());
        plugin.ThirdPartyTick();

        Assert.Equal("loading", plugin.ThirdParty.Phase);
        Assert.Equal(Script("1.1.0"), File.ReadAllBytes(TestCs));
    }

    [Fact]
    public void TheStatusCommandReportsTheKindOfUpdateAndHowManyFilesWereWritten()
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);
        plugin.FindLoaded = _ => null;
        BeginZip(plugin, zip);
        plugin.ThirdPartyTick();
        var arg = ConsoleSystem.Arg.WithArgs();

        plugin.CmdThirdPartyStatus(arg);

        var data = Reply(Assert.Single(arg.Replies)).GetProperty("data");
        Assert.Equal("loading", data.GetProperty("phase").GetString());
        Assert.Equal("zip", data.GetProperty("kind").GetString());
        Assert.Equal(4, data.GetProperty("installed").GetInt32());
        Assert.Equal("Test", data.GetProperty("class").GetString());
        Assert.Equal("test.cs", data.GetProperty("file").GetString());
        Assert.Equal(Hex(zip), data.GetProperty("sha256").GetString());
    }

    [Fact]
    public void ASingleFileUpdateIsStillOfKindCs()
    {
        InstallOld();
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served);
        plugin.FindLoaded = _ => null;
        plugin.BeginThirdPartyUpdate("Test", "1.1.0", Hex(served), served.Length.ToString(), Url);
        plugin.ThirdPartyTick();
        var arg = ConsoleSystem.Arg.WithArgs();

        plugin.CmdThirdPartyStatus(arg);

        Assert.Equal("cs", Reply(Assert.Single(arg.Replies)).GetProperty("data").GetProperty("kind").GetString());
        Assert.Equal("cs", SwapFile("kind"));
    }

    [Fact]
    public void TheFilesThatAreNotCodeAreWrittenFirstAndTheCodeLastSoTheReloadedPluginFindsItsSettings()
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);
        var order = new List<string>();
        var configWhenCodeWritten = Array.Empty<byte>();
        plugin.BeforeThirdPartyWrite = destination =>
        {
            order.Add(Path.GetFileName(destination));
            if (destination == TestCs) { configWhenCodeWritten = File.ReadAllBytes(ConfigJson); }
        };

        BeginZip(plugin, zip);
        plugin.ThirdPartyTick();

        Assert.Equal(new[] { "test.json", "one.jpg", "two.jpg", "test.cs" }, order);
        Assert.Equal(Text("{\"v\":2}"), configWhenCodeWritten);
    }

    [Fact]
    public void TheCodeIsLastEvenWhenTheArchiveListsItFirstOfSeveralPluginFiles()
    {
        InstallOld();
        var zip = MakeZip(
        [
            ("plugin/test.cs", Script("1.1.0")), ("plugin/Helper.cs", Text("// helper, no class of note\n")), ("data/a.json", Text("{}")), ("lang/en/test.json", Text("{}"))
        ]);
        var plugin = Loaded((_, _) => zip);
        var order = new List<string>();
        plugin.BeforeThirdPartyWrite = destination => order.Add(Path.GetFileName(destination));

        BeginZip(plugin, zip, [Folder("plugin/", ZipRoles.Plugins), Folder("data/", ZipRoles.Data), Folder("lang/", ZipRoles.Lang)]);
        plugin.ThirdPartyTick();

        Assert.Equal("loading", plugin.ThirdParty.Phase);
        Assert.Equal(new[] { "a.json", "test.json", "test.cs", "Helper.cs" }, order);
        Assert.True(File.Exists(Path.Combine(Lang, "en", "test.json")));                   // the lang folder did not exist and was made
    }

    // ---- what is written where: the role folders ----------------------------------------------------------------------

    [Fact]
    public void OnCarbonTheSettingsGoToConfigsAndOnOxideToConfig()
    {
        // Carbon: a folder named carbon, no config folder yet -> configs.
        var zip = MakeZip(UserArchive());
        InstallOld();
        var carbon = Loaded((_, _) => zip);
        BeginZip(carbon, zip);
        carbon.ThirdPartyTick();
        Assert.Equal(Text("{\"v\":2}"), File.ReadAllBytes(Path.Combine(_root, "carbon", "configs", "test.json")));
        Assert.False(Directory.Exists(Path.Combine(_root, "carbon", "config")));

        // Oxide: the same archive and rules, another framework.
        UseFramework("oxide");
        InstallOld();
        var oxide = Loaded((_, _) => zip);
        BeginZip(oxide, zip);
        oxide.ThirdPartyTick();
        Assert.Equal("loading", oxide.ThirdParty.Phase);
        Assert.Equal(Text("{\"v\":2}"), File.ReadAllBytes(Path.Combine(_root, "oxide", "config", "test.json")));
        Assert.False(Directory.Exists(Path.Combine(_root, "oxide", "configs")));
        Assert.Equal(Bytes(1000, 9), File.ReadAllBytes(Path.Combine(_root, "oxide", "data", "test", "one.jpg")));
    }

    [Theory]
    [InlineData("carbon", new string[0], "configs")]
    [InlineData("carbon", new[] { "configs" }, "configs")]
    [InlineData("carbon", new[] { "config" }, "config")]
    [InlineData("carbon", new[] { "config", "configs" }, "configs")]
    [InlineData("Carbon", new string[0], "configs")]
    [InlineData("CARBON", new[] { "config" }, "config")]
    [InlineData("oxide", new string[0], "config")]
    [InlineData("oxide", new[] { "config" }, "config")]
    [InlineData("oxide", new[] { "configs" }, "configs")]
    [InlineData("oxide", new[] { "config", "configs" }, "configs")]
    [InlineData("server", new string[0], "config")]
    public void TheConfigRoleGoesToWhicheverOfConfigsAndConfigTheServerHasElseByTheFrameworkName(string framework, string[] existing, string expected)
    {
        var root = Path.Combine(_root, "roles", Guid.NewGuid().ToString("N"), framework);
        Directory.CreateDirectory(Path.Combine(root, "plugins"));
        foreach (var folder in existing) { Directory.CreateDirectory(Path.Combine(root, folder)); }

        Assert.Equal(Path.Combine(root, expected), ArchonZipMapping.RoleFolder("config", Path.Combine(root, "plugins")));
        Assert.Equal(Path.Combine(root, "plugins"), ArchonZipMapping.RoleFolder("plugins", Path.Combine(root, "plugins")));
        Assert.Equal(Path.Combine(root, "data"), ArchonZipMapping.RoleFolder("data", Path.Combine(root, "plugins")));
        Assert.Equal(Path.Combine(root, "lang"), ArchonZipMapping.RoleFolder("lang", Path.Combine(root, "plugins")));
        Assert.Equal(Path.Combine(root, "data"), ArchonZipMapping.RoleFolder("data", Path.Combine(root, "plugins") + Path.DirectorySeparatorChar));
        Assert.Null(ArchonZipMapping.RoleFolder("skip", Path.Combine(root, "plugins")));
        Assert.Null(ArchonZipMapping.RoleFolder("etc", Path.Combine(root, "plugins")));
        Assert.Null(ArchonZipMapping.RoleFolder("data", null));
    }

    [Fact]
    public void ASubFolderPutsTheFilesInsideTheRolesFolder()
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);

        BeginZip(plugin, zip, [Folder("en/plugins/", ZipRoles.Plugins), Folder("en/configs/", ZipRoles.Config), Folder("en/images/", ZipRoles.Data, "pictures/en"), Folder("ru/", ZipRoles.Skip)]);
        plugin.ThirdPartyTick();

        Assert.Equal("loading", plugin.ThirdParty.Phase);
        Assert.Equal(Bytes(1000, 9), File.ReadAllBytes(Path.Combine(Data, "pictures", "en", "test", "one.jpg")));
    }

    [Fact]
    public void AFileRuleSendsOneFileElsewhereAndSkipsAnother()
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);

        BeginZip(plugin, zip, [.. UserRules(), FileRule("en/images/test/one.jpg", ZipRoles.Skip), FileRule("en/images/test/two.jpg", ZipRoles.Lang, "extra")]);
        plugin.ThirdPartyTick();

        Assert.Equal("loading", plugin.ThirdParty.Phase);
        Assert.False(File.Exists(One));
        Assert.Equal(Bytes(2000, 3), File.ReadAllBytes(Path.Combine(Lang, "extra", "two.jpg")));
        Assert.Equal(3, plugin.ThirdParty.Installed);
    }

    // ---- overwrite is the default; keep-existing leaves a file alone ---------------------------------------------------

    [Fact]
    public void AFileThatIsMappedOverwritesTheOneThatIsThereAndTheOldOneIsBackedUp()
    {
        InstallOld();
        InstallOldSettingsAndPicture();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);

        BeginZip(plugin, zip, UserRules(keepConfig: false));
        plugin.ThirdPartyTick();

        Assert.Equal(Text("{\"v\":2}"), File.ReadAllBytes(ConfigJson));
        Assert.Equal(Text("{\"v\":1}"), File.ReadAllBytes(Path.Combine(BackupDir, "config", "test.json")));
    }

    [Fact]
    public void KeepExistingLeavesAFileThatIsThereAloneAndDoesNotBackItUpOrCountIt()
    {
        InstallOld();
        InstallOldSettingsAndPicture();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);

        BeginZip(plugin, zip, UserRules(keepConfig: true));
        plugin.ThirdPartyTick();

        Assert.Equal("loading", plugin.ThirdParty.Phase);
        Assert.Equal(Text("{\"v\":1}"), File.ReadAllBytes(ConfigJson));                       // the customer's edited settings survive
        Assert.False(File.Exists(Path.Combine(BackupDir, "config", "test.json")));
        Assert.DoesNotContain(File.ReadAllLines(Path.Combine(BackupDir, "manifest.txt")), line => line.Contains("test.json"));
        Assert.Equal(3, plugin.ThirdParty.Installed);                                          // the code and the two pictures
        Assert.Equal(Script("1.1.0"), File.ReadAllBytes(TestCs));
    }

    [Fact]
    public void KeepExistingStillWritesTheFileWhenItIsNotThereYet()
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);

        BeginZip(plugin, zip, UserRules(keepConfig: true));
        plugin.ThirdPartyTick();

        Assert.Equal(Text("{\"v\":2}"), File.ReadAllBytes(ConfigJson));
        Assert.Equal(4, plugin.ThirdParty.Installed);
        Assert.Contains($"N\t{ConfigJson}", File.ReadAllLines(Path.Combine(BackupDir, "manifest.txt")));
    }

    // ---- rolling back ---------------------------------------------------------------------------------------------------

    [Fact]
    public void APluginThatDoesNotComeUpPutsEveryReplacedFileBackAndDeletesEveryCreatedOne()
    {
        var oldPlugin = InstallOld();
        InstallOldSettingsAndPicture();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);
        plugin.FindLoaded = _ => null;                              // a compile error: it never appears
        var before = Snapshot();
        BeginZip(plugin, zip);
        plugin.ThirdPartyTick();
        Assert.Equal("loading", plugin.ThirdParty.Phase);

        _now = _now.AddSeconds(ArchonPlugin.ThirdPartyLoadWaitSeconds + 1);
        plugin.ThirdPartyTick();

        Assert.Equal("rolled-back", plugin.ThirdParty.Phase);
        Assert.Contains("did not appear", plugin.ThirdParty.Reason);
        Assert.Equal(oldPlugin, File.ReadAllBytes(TestCs));
        Assert.Equal(Text("{\"v\":1}"), File.ReadAllBytes(ConfigJson));
        Assert.Equal(Bytes(50, 1), File.ReadAllBytes(One));
        Assert.False(File.Exists(Two));                                                        // created by the update: gone again
        Assert.Equal("rolled-back", SwapFile("phase"));

        // Everything is as it was, apart from the backup that is kept.
        var changed = Changed(before, Snapshot());
        Assert.DoesNotContain(changed, path => !path.Contains("thirdparty-backup"));
    }

    [Fact]
    public void ARollbackRemovesTheFoldersTheUpdateCreatedAndOnlyThose()
    {
        InstallOld();
        Directory.CreateDirectory(Path.Combine(Data, "test"));                                 // was there before, with something of the customer's in it
        File.WriteAllText(Path.Combine(Data, "test", "customer.txt"), "mine");
        var zip = MakeZip(
        [
            ("plugin/test.cs", Script("1.1.0")), ("img/one.jpg", Bytes(10, 1)), ("img/deep/er/two.jpg", Bytes(10, 2))
        ]);
        var plugin = Loaded((_, _) => zip);
        plugin.FindLoaded = _ => null;

        BeginZip(plugin, zip, [Folder("plugin/", ZipRoles.Plugins), Folder("img/", ZipRoles.Data, "test/pics")]);
        plugin.ThirdPartyTick();
        Assert.True(File.Exists(Path.Combine(Data, "test", "pics", "deep", "er", "two.jpg")));
        var manifest = File.ReadAllLines(Path.Combine(BackupDir, "manifest.txt"));
        Assert.Equal(3, manifest.Count(l => l.StartsWith("D\t")));                             // test/pics, test/pics/deep, test/pics/deep/er

        _now = _now.AddSeconds(ArchonPlugin.ThirdPartyLoadWaitSeconds + 1);
        plugin.ThirdPartyTick();

        Assert.Equal("rolled-back", plugin.ThirdParty.Phase);
        Assert.False(Directory.Exists(Path.Combine(Data, "test", "pics")));                   // created by this update: gone
        Assert.True(Directory.Exists(Path.Combine(Data, "test")));                            // not created by it: stays
        Assert.Equal("mine", File.ReadAllText(Path.Combine(Data, "test", "customer.txt")));
        Assert.True(Directory.Exists(Data));
    }

    [Fact]
    public void ARolledBackFolderThatSomethingElseHasMovedIntoIsLeftAlone()
    {
        InstallOld();
        var zip = MakeZip([("plugin/test.cs", Script("1.1.0")), ("img/one.jpg", Bytes(10, 1))]);
        var plugin = Loaded((_, _) => zip);
        plugin.FindLoaded = _ => null;
        BeginZip(plugin, zip, [Folder("plugin/", ZipRoles.Plugins), Folder("img/", ZipRoles.Data, "made")]);
        plugin.ThirdPartyTick();
        File.WriteAllText(Path.Combine(Data, "made", "someone-elses.txt"), "not ours");

        _now = _now.AddSeconds(ArchonPlugin.ThirdPartyLoadWaitSeconds + 1);
        plugin.ThirdPartyTick();

        Assert.Equal("rolled-back", plugin.ThirdParty.Phase);
        Assert.False(File.Exists(Path.Combine(Data, "made", "one.jpg")));
        Assert.Equal("not ours", File.ReadAllText(Path.Combine(Data, "made", "someone-elses.txt")));
    }

    [Fact]
    public void AKeptFileIsNotTouchedByTheRollback()
    {
        InstallOld();
        InstallOldSettingsAndPicture();
        File.WriteAllText(ConfigJson, "edited by the customer after the update");
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);
        plugin.FindLoaded = _ => null;
        BeginZip(plugin, zip, UserRules(keepConfig: true));
        plugin.ThirdPartyTick();

        _now = _now.AddSeconds(ArchonPlugin.ThirdPartyLoadWaitSeconds + 1);
        plugin.ThirdPartyTick();

        Assert.Equal("rolled-back", plugin.ThirdParty.Phase);
        Assert.Equal("edited by the customer after the update", File.ReadAllText(ConfigJson));
    }

    [Fact]
    public void ARollbackThatCannotRestoreAFileNamesItInTheReason()
    {
        InstallOld();
        InstallOldSettingsAndPicture();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);
        plugin.FindLoaded = _ => null;
        BeginZip(plugin, zip);
        plugin.ThirdPartyTick();
        File.Delete(Path.Combine(BackupDir, "data", "test", "one.jpg"));                       // the backup of one file is gone

        _now = _now.AddSeconds(ArchonPlugin.ThirdPartyLoadWaitSeconds + 1);
        plugin.ThirdPartyTick();

        Assert.Equal("failed", plugin.ThirdParty.Phase);
        Assert.Contains("rollback failed", plugin.ThirdParty.Reason);
        Assert.Contains(One, plugin.ThirdParty.Reason);
        Assert.DoesNotContain(ConfigJson, plugin.ThirdParty.Reason);
        Assert.Equal(Text("{\"v\":1}"), File.ReadAllBytes(ConfigJson));                        // everything else was put back all the same
        Assert.Equal(Script("1.0.0"), File.ReadAllBytes(TestCs));
    }

    [Fact]
    public void AMissingBackupMeansTheRollbackFailsAndSaysSo()
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);
        plugin.FindLoaded = _ => null;
        BeginZip(plugin, zip);
        plugin.ThirdPartyTick();
        Directory.Delete(BackupDir, recursive: true);

        _now = _now.AddSeconds(ArchonPlugin.ThirdPartyLoadWaitSeconds + 1);
        plugin.ThirdPartyTick();

        Assert.Equal("failed", plugin.ThirdParty.Phase);
        Assert.Contains("rollback impossible", plugin.ThirdParty.Reason);
    }

    [Fact]
    public void ARolledBackUpdateStillWatchedAfterThePluginReloadsIsPutBackFromTheManifest()
    {
        var oldPlugin = InstallOld();
        InstallOldSettingsAndPicture();
        var zip = MakeZip(UserArchive());
        var first = Loaded((_, _) => zip);
        first.FindLoaded = _ => null;
        BeginZip(first, zip);
        first.ThirdPartyTick();
        Assert.Equal("loading", first.ThirdParty.Phase);

        var second = Loaded();                                       // this plugin reloads meanwhile
        second.FindLoaded = _ => null;
        Assert.Equal("loading", second.ThirdParty.Phase);
        Assert.Equal("zip", second.ThirdParty.Kind);
        Assert.Equal(4, second.ThirdParty.Installed);
        _now = _now.AddSeconds(ArchonPlugin.ThirdPartyLoadWaitSeconds + 1);
        second.ThirdPartyTick();

        Assert.Equal("rolled-back", second.ThirdParty.Phase);
        Assert.Equal(oldPlugin, File.ReadAllBytes(TestCs));
        Assert.Equal(Text("{\"v\":1}"), File.ReadAllBytes(ConfigJson));
        Assert.False(File.Exists(Two));
    }

    [Fact]
    public void AnOldFileThatCameUpAtTheWrongVersionIsRolledBack()
    {
        var oldPlugin = InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);
        plugin.FindLoaded = _ => Running(new object(), "1.0.5");
        BeginZip(plugin, zip);
        plugin.ThirdPartyTick();

        _now = _now.AddSeconds(ArchonPlugin.ThirdPartyLoadWaitSeconds + 1);
        plugin.ThirdPartyTick();

        Assert.Equal("rolled-back", plugin.ThirdParty.Phase);
        Assert.Equal(oldPlugin, File.ReadAllBytes(TestCs));
    }

    // ---- dying part way ------------------------------------------------------------------------------------------------

    [Fact]
    public void AWriteThatFailsPartWayPutsEverythingBackAtOnce()
    {
        var oldPlugin = InstallOld();
        InstallOldSettingsAndPicture();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);
        var writes = 0;
        plugin.BeforeThirdPartyWrite = _ => { if (++writes == 3) { throw new IOException("the disk is full"); } };
        BeginZip(plugin, zip);

        plugin.ThirdPartyTick();

        Assert.Equal("rolled-back", plugin.ThirdParty.Phase);
        Assert.StartsWith("apply_failed", plugin.ThirdParty.Reason);
        Assert.Contains("the disk is full", plugin.ThirdParty.Reason);
        Assert.Equal(oldPlugin, File.ReadAllBytes(TestCs));
        Assert.Equal(Text("{\"v\":1}"), File.ReadAllBytes(ConfigJson));
        Assert.Equal(Bytes(50, 1), File.ReadAllBytes(One));
        Assert.False(File.Exists(Two));
        Assert.False(Directory.Exists(StagingDir));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void APluginThatDiedMidWriteRollsEverythingBackWhenItLoadsAgain()
    {
        var oldPlugin = InstallOld();
        InstallOldSettingsAndPicture();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);
        plugin.FindLoaded = _ => null;
        var writes = 0;
        // The moment the process "dies": after one file is written and before the second. The whole server folder is copied aside as it is then.
        plugin.BeforeThirdPartyWrite = _ => { if (++writes == 2) { CopyDirectory(_root, _root + "-snapshot"); } };
        BeginZip(plugin, zip);
        plugin.ThirdPartyTick();
        Assert.Equal("loading", plugin.ThirdParty.Phase);

        Directory.Delete(_root, recursive: true);
        CopyDirectory(_root + "-snapshot", _root);
        Assert.Equal("applying", File.ReadAllLines(Path.Combine(RustArchonData, "thirdparty-swap.txt")).Single(l => l.StartsWith("phase=")).Substring(6));
        Assert.Equal(Text("{\"v\":2}"), File.ReadAllBytes(ConfigJson));                        // the state on disk: half written
        Assert.Equal(oldPlugin, File.ReadAllBytes(TestCs));
        Assert.True(Directory.Exists(StagingDir));

        var reloaded = Loaded();

        Assert.Equal("rolled-back", reloaded.ThirdParty.Phase);
        Assert.Contains("interrupted", reloaded.ThirdParty.Reason);
        Assert.Equal(oldPlugin, File.ReadAllBytes(TestCs));
        Assert.Equal(Text("{\"v\":1}"), File.ReadAllBytes(ConfigJson));
        Assert.Equal(Bytes(50, 1), File.ReadAllBytes(One));
        Assert.False(File.Exists(Two));
        Assert.False(Directory.Exists(StagingDir));
        Assert.Equal("rolled-back", SwapFile("phase"));
    }

    [Fact]
    public void AnApplyingStateWithNoBackupToRollBackFromFailsAndSaysSo()
    {
        InstallOld();
        File.WriteAllText(Path.Combine(RustArchonData, "thirdparty-swap.txt"),
            "phase=applying\nclass=Test\ntarget=1.1.0\nprevious=1.0.0\nfile=test.cs\nsha256=" + new string('a', 64) + "\nactual=\nsize=10\nkind=zip\nbackup=" + BackupDir
            + "\ninstalled=0\nreason=\nstarted=2026-09-21T12:00:00.0000000Z\nswapped=0001-01-01T00:00:00.0000000Z\n");

        var plugin = Loaded();

        Assert.Equal("failed", plugin.ThirdParty.Phase);
        Assert.Contains("rollback impossible", plugin.ThirdParty.Reason);
    }

    [Fact]
    public void AManifestIsOnlyEverTrustedForPathsInsideTheFoldersAnUpdateCanWriteTo()
    {
        InstallOld();
        var outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(outside, "precious");
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);
        plugin.FindLoaded = _ => null;
        BeginZip(plugin, zip);
        plugin.ThirdPartyTick();
        var manifest = Path.Combine(BackupDir, "manifest.txt");
        File.AppendAllText(manifest, $"N\t{outside}\nN\t{Path.Combine(_framework, "..", "outside.txt")}\nE\t{outside}\t../../outside.txt\n");

        _now = _now.AddSeconds(ArchonPlugin.ThirdPartyLoadWaitSeconds + 1);
        plugin.ThirdPartyTick();

        Assert.Equal("failed", plugin.ThirdParty.Phase);                                       // the bad lines are named, not obeyed
        Assert.Contains("outside.txt", plugin.ThirdParty.Reason);
        Assert.Equal("precious", File.ReadAllText(outside));
        Assert.Equal(Script("1.0.0"), File.ReadAllBytes(TestCs));                              // and the good lines were still done
    }

    // ---- one backup per plugin ---------------------------------------------------------------------------------------

    [Fact]
    public void ANewUpdateReplacesThePreviousBackupOfThatPlugin()
    {
        InstallOld();
        InstallOldSettingsAndPicture();
        var first = MakeZip(UserArchive("1.1.0"));
        var plugin = Loaded((_, _) => first);
        var current = Running(new object(), "1.0.0");
        plugin.FindLoaded = _ => current;
        BeginZip(plugin, first, version: "1.1.0");
        plugin.ThirdPartyTick();
        current = Running(new object(), "1.1.0");
        plugin.ThirdPartyTick();
        Assert.Equal("succeeded", plugin.ThirdParty.Phase);
        Assert.True(File.Exists(Path.Combine(BackupDir, "data", "test", "one.jpg")));          // the first update's backup

        // The second update carries only the code and settings: no pictures, and a different old code (1.1.0) to back up.
        var second = MakeZip([("en/plugins/test.cs", Script("1.2.0")), ("en/configs/test.json", Text("{\"v\":3}"))]);
        var again = Loaded((_, _) => second);
        again.FindLoaded = _ => current;
        Assert.True(Reply(BeginZip(again, second, [Folder("en/plugins/", ZipRoles.Plugins), Folder("en/configs/", ZipRoles.Config)], version: "1.2.0")).GetProperty("ok").GetBoolean());
        again.ThirdPartyTick();

        Assert.Equal("loading", again.ThirdParty.Phase);
        Assert.Equal(Script("1.1.0"), File.ReadAllBytes(Path.Combine(BackupDir, "plugins", "test.cs")));          // the code as it was before THIS update
        Assert.Equal(Text("{\"v\":2}"), File.ReadAllBytes(Path.Combine(BackupDir, "config", "test.json")));
        Assert.False(Directory.Exists(Path.Combine(BackupDir, "data")));                                        // the first update's picture backups are gone
        Assert.Equal(2, File.ReadAllLines(Path.Combine(BackupDir, "manifest.txt")).Length);
        Assert.Single(Directory.GetDirectories(Path.Combine(RustArchonData, "thirdparty-backup")));
    }

    [Fact]
    public void EachPluginKeepsItsOwnBackup()
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);
        BeginZip(plugin, zip);
        plugin.ThirdPartyTick();
        var other = Path.Combine(RustArchonData, "thirdparty-backup", "Other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "manifest.txt"), "");
        plugin.FindLoaded = _ => Running(new object(), "1.1.0");
        plugin.ThirdPartyTick();
        Assert.Equal("succeeded", plugin.ThirdParty.Phase);

        var second = MakeZip(UserArchive("1.2.0"));
        var again = Loaded((_, _) => second);
        BeginZip(again, second, version: "1.2.0");
        again.ThirdPartyTick();

        Assert.True(File.Exists(Path.Combine(other, "manifest.txt")));                        // another plugin's backup is not this plugin's to delete
    }

    // ---- the archive and the rules are not trusted: refusals that write nothing ----------------------------------------

    private void AssertRefusedWritingNothing(byte[] zip, IReadOnlyList<ZipMappingRule> rules, string reason, string version = "1.1.0", string installBytes = "1000000", bool oldSettings = true)
    {
        InstallOld();
        if (oldSettings) { InstallOldSettingsAndPicture(); }
        var plugin = Loaded((_, _) => zip);
        var before = Snapshot();

        Assert.True(Reply(BeginZip(plugin, zip, rules, version: version, installBytes: installBytes)).GetProperty("ok").GetBoolean());
        plugin.ThirdPartyTick();

        Assert.Equal("failed", plugin.ThirdParty.Phase);
        Assert.Contains(reason, plugin.ThirdParty.Reason);
        Assert.Equal(before, Snapshot());                           // no file changed, none made, no backup, no staging left
        Assert.Equal("failed", SwapFile("phase"));
    }

    [Fact]
    public void AFileNoRuleCoversIsARefusalOfTheWholeUpdate()
    {
        AssertRefusedWritingNothing(MakeZip([.. UserArchive(), ("readme.txt", Text("hello"))]), UserRules(), "bad_mapping: unassigned readme.txt");
    }

    [Fact]
    public void TwoFilesLandingOnOnePathAreARefusalOfTheWholeUpdate()
    {
        AssertRefusedWritingNothing(MakeZip(UserArchive()),
            [Folder("en/plugins/", ZipRoles.Plugins), Folder("ru/plugins/", ZipRoles.Plugins), Folder("en/configs/", ZipRoles.Config), Folder("ru/configs/", ZipRoles.Config), Folder("en/images/", ZipRoles.Data), Folder("ru/images/", ZipRoles.Data)],
            "bad_mapping: collision ru/");
    }

    [Theory]
    [InlineData("thing.dll")]
    [InlineData("thing.DLL")]
    [InlineData("native.so")]
    [InlineData("run.exe")]
    [InlineData("run.sh")]
    [InlineData("run.ps1")]
    public void AProgramFileInAFolderThatIsInstalledIsARefusal(string name)
    {
        AssertRefusedWritingNothing(MakeZip([.. UserArchive(), ("en/plugins/" + name, Bytes(20, 1))]), UserRules(), "bad_mapping: executable en/plugins/" + name);
    }

    [Fact]
    public void AProgramFileThatIsSkippedIsNotAProblem()
    {
        InstallOld();
        var zip = MakeZip([.. UserArchive(), ("extension/Ext.dll", Bytes(20, 1))]);
        var plugin = Loaded((_, _) => zip);

        BeginZip(plugin, zip, [.. UserRules(), Folder("extension/", ZipRoles.Skip)]);
        plugin.ThirdPartyTick();

        Assert.Equal("loading", plugin.ThirdParty.Phase);
    }

    [Fact]
    public void ASourceFileSentAnywhereButThePluginsFolderIsARefusal()
    {
        AssertRefusedWritingNothing(MakeZip(UserArchive()), [Folder("en/plugins/", ZipRoles.Data), Folder("en/configs/", ZipRoles.Config), Folder("en/images/", ZipRoles.Data), Folder("ru/", ZipRoles.Skip)],
            "bad_mapping: cs_outside_plugins en/plugins/test.cs");
    }

    [Theory]
    [InlineData("../x.json")]
    [InlineData("en/../../x.json")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/x.json")]
    [InlineData("en//x.json")]
    [InlineData("en/./x.json")]
    [InlineData("en/x.json.")]
    public void AnEntryWhosePathCouldEscapeItsFolderIsARefusalNothingIsWrittenAnywhere(string path)
    {
        var zip = MakeZip([.. UserArchive(), (path, Text("{}"))]);

        AssertRefusedWritingNothing(zip, UserRules(), "bad_mapping: ");
    }

    [Fact]
    public void AFileNamedLikeThisPluginsOwnIsNeverWritten()
    {
        foreach (var name in new[] { "RustArchon.cs", "rustarchon.cs", "RustArchonUpdater.cs" })
        {
            UseFramework("carbon");
            AssertRefusedWritingNothing(MakeZip([.. UserArchive(), ("en/plugins/" + name, Script("9.9.9", "RustArchon"))]), UserRules(), "bad_mapping: protected en/plugins/" + name);
        }
    }

    [Fact]
    public void ThisPluginsOwnDataFolderIsNotSomewhereAnArchiveCanWrite()
    {
        AssertRefusedWritingNothing(MakeZip(UserArchive()), [Folder("en/plugins/", ZipRoles.Plugins), Folder("en/configs/", ZipRoles.Config), Folder("en/images/", ZipRoles.Data, "RustArchon"), Folder("ru/", ZipRoles.Skip)],
            "bad_mapping: protected en/images/test/one.jpg");
        Assert.False(File.Exists(Path.Combine(RustArchonData, "test", "one.jpg")));
    }

    [Fact]
    public void ADestinationThatIsAFolderIsARefusal()
    {
        Directory.CreateDirectory(Path.Combine(Data, "test", "one.jpg"));
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);

        BeginZip(plugin, zip);
        plugin.ThirdPartyTick();

        Assert.Equal("failed", plugin.ThirdParty.Phase);
        Assert.Contains("unsafe_destination", plugin.ThirdParty.Reason);
        Assert.Equal(Script("1.0.0"), File.ReadAllBytes(TestCs));
    }

    [Fact]
    public void AFolderThatIsALinkToElsewhereIsNotWrittenThrough()
    {
        var outside = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(outside);
        try { Directory.CreateSymbolicLink(Path.Combine(Data, "test"), outside); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }      // cannot make links here

        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);

        BeginZip(plugin, zip);
        plugin.ThirdPartyTick();

        Assert.Equal("failed", plugin.ThirdParty.Phase);
        Assert.Contains("unsafe_destination", plugin.ThirdParty.Reason);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
        Assert.Equal(Script("1.0.0"), File.ReadAllBytes(TestCs));
    }

    [Fact]
    public void AFileInTheWayOfAFolderIsARefusal()
    {
        File.WriteAllText(Path.Combine(Data, "test"), "a file where a folder is needed");
        AssertRefusedWritingNothing(MakeZip(UserArchive()), UserRules(), "bad_mapping: a file is in the way", oldSettings: false);
    }

    [Fact]
    public void NothingToInstallIsARefusal()
    {
        AssertRefusedWritingNothing(MakeZip(UserArchive()), [Folder("ru/", ZipRoles.Skip), Folder("en/", ZipRoles.Skip)], "bad_mapping: nothing_to_install");
    }

    [Fact]
    public void AnArchiveThatIsNotAZipIsRefused()
    {
        InstallOld();
        var junk = Encoding.UTF8.GetBytes(new string('x', 500));
        var plugin = Loaded((_, _) => junk);
        var before = Snapshot();

        BeginZip(plugin, junk);
        plugin.ThirdPartyTick();

        Assert.Equal("failed", plugin.ThirdParty.Phase);
        Assert.Contains("bad_zip", plugin.ThirdParty.Reason);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void ADownloadThatIsNotTheFileThePanelCheckedIsAMismatchAndNothingIsWritten()
    {
        InstallOld();
        var checkedByPanel = MakeZip(UserArchive());
        var actuallyServed = MakeZip([.. UserArchive(), ("en/plugins/backdoor.cs", Text("// slipstreamed\n"))]);
        var plugin = Loaded((_, _) => actuallyServed);
        var before = Snapshot();

        BeginZip(plugin, checkedByPanel);
        plugin.ThirdPartyTick();

        Assert.Equal("mismatch", plugin.ThirdParty.Phase);
        Assert.Equal(Hex(actuallyServed), plugin.ThirdParty.ActualSha256);
        Assert.Equal(before, Snapshot());
        Assert.Equal(Hex(actuallyServed), SwapFile("actual"));
    }

    [Fact]
    public void MoreBytesThanThePanelSawIsAChangedFileNotAFailedDownload()
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, size) => throw new ArchonThirdParty.ChangedException($"larger than {size}"));
        var before = Snapshot();

        BeginZip(plugin, zip);
        plugin.ThirdPartyTick();

        Assert.Equal("mismatch", plugin.ThirdParty.Phase);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void AnArchiveWhosePluginDeclaresADifferentClassIsNotThatPlugin()
    {
        AssertRefusedWritingNothing(MakeZip([("en/plugins/test.cs", Script("1.1.0", className: "Elsewhere")), ("en/configs/test.json", Text("{}"))]),
            [Folder("en/plugins/", ZipRoles.Plugins), Folder("en/configs/", ZipRoles.Config)], "not_that_plugin");
    }

    [Fact]
    public void AnArchiveWithNoPluginFileAtAllIsNotThatPlugin()
    {
        AssertRefusedWritingNothing(MakeZip([("en/configs/test.json", Text("{}"))]), [Folder("en/configs/", ZipRoles.Config)], "not_that_plugin");
    }

    [Fact]
    public void AnArchiveWithTwoPluginFilesDeclaringTheClassIsNotGuessedBetween()
    {
        AssertRefusedWritingNothing(MakeZip([("en/plugins/test.cs", Script("1.1.0")), ("en/plugins/Test2.cs", Script("1.1.0"))]), [Folder("en/plugins/", ZipRoles.Plugins)], "not_that_plugin");
    }

    [Fact]
    public void ThePluginFileInTheArchiveHasToBeTheOneThatIsInstalled()
    {
        // A differently named file would leave two files declaring one class.
        AssertRefusedWritingNothing(MakeZip([("en/plugins/TestRenamed.cs", Script("1.1.0"))]), [Folder("en/plugins/", ZipRoles.Plugins)], "not_that_plugin");
    }

    [Fact]
    public void ThePluginFileInASubfolderOfPluginsIsNotTheInstalledOne()
    {
        AssertRefusedWritingNothing(MakeZip([("en/plugins/test.cs", Script("1.1.0"))]), [Folder("en/plugins/", ZipRoles.Plugins, "sub")], "not_that_plugin");
    }

    [Fact]
    public void AnArchiveWhoseVersionIsNotTheOneAskedForIsRefused()
    {
        AssertRefusedWritingNothing(MakeZip(UserArchive("1.2.0")), UserRules(), "version_mismatch", version: "1.1.0");
    }

    [Fact]
    public void ThePluginFolderChangingWhileTheUpdateIsPreparedStopsIt()
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip, background: _ => { });
        BeginZip(plugin, zip);
        File.WriteAllBytes(Path.Combine(Plugins, "TestCopy.cs"), Script("0.5.0"));            // now two files declare it
        typeof(ArchonPlugin).GetField("_thirdPartyDownload", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(plugin, new ArchonThirdParty.Download { Bytes = zip, Done = true });

        plugin.ThirdPartyTick();

        Assert.Equal("failed", plugin.ThirdParty.Phase);
        Assert.Contains("changed", plugin.ThirdParty.Reason);
        Assert.Equal(Script("1.0.0"), File.ReadAllBytes(TestCs));
        Assert.False(Directory.Exists(BackupDir));
        Assert.False(Directory.Exists(StagingDir));
    }

    [Fact]
    public void AnUpdaterSwapStartingWhileTheUpdateIsPreparedStopsIt()
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip, background: _ => { });
        BeginZip(plugin, zip);
        File.WriteAllText(Path.Combine(RustArchonData, "update-status.txt"), "phase=downloading\n");
        typeof(ArchonPlugin).GetField("_thirdPartyDownload", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(plugin, new ArchonThirdParty.Download { Bytes = zip, Done = true });

        plugin.ThirdPartyTick();

        Assert.Equal("failed", plugin.ThirdParty.Phase);
        Assert.Contains("busy", plugin.ThirdParty.Reason);
        Assert.False(Directory.Exists(BackupDir));
    }

    // ---- zip bombs and lying archives -----------------------------------------------------------------------------------

    [Fact]
    public void FilesThatAddUpToMoreThanTheInstallBytesAreRefusedBeforeAnythingIsUnpacked()
    {
        // 2 MB of zeros compresses to a few KB: the archive is small and what it unpacks to is not.
        var zip = MakeZip([("en/plugins/test.cs", Script("1.1.0")), ("en/images/big.dat", new byte[2_000_000])]);
        Assert.True(zip.Length < 20_000);

        AssertRefusedWritingNothing(zip, [Folder("en/plugins/", ZipRoles.Plugins), Folder("en/images/", ZipRoles.Data)], "too_large_for_plan", installBytes: "1000000");
    }

    [Fact]
    public void FilesThatAddUpToExactlyTheInstallBytesAreAccepted()
    {
        InstallOld();
        var code = Script("1.1.0");
        var zip = MakeZip([("en/plugins/test.cs", code), ("en/images/big.dat", new byte[1000])]);
        var plugin = Loaded((_, _) => zip);

        BeginZip(plugin, zip, [Folder("en/plugins/", ZipRoles.Plugins), Folder("en/images/", ZipRoles.Data)], installBytes: (code.Length + 1000).ToString());
        plugin.ThirdPartyTick();

        Assert.Equal("loading", plugin.ThirdParty.Phase);
    }

    [Fact]
    public void SizesThatAreSkippedDoNotCountAgainstTheInstallBytes()
    {
        InstallOld();
        var zip = MakeZip([("en/plugins/test.cs", Script("1.1.0")), ("ru/big.dat", new byte[2_000_000])]);
        var plugin = Loaded((_, _) => zip);

        BeginZip(plugin, zip, [Folder("en/plugins/", ZipRoles.Plugins), Folder("ru/", ZipRoles.Skip)], installBytes: "1000");
        plugin.ThirdPartyTick();

        Assert.Equal("loading", plugin.ThirdParty.Phase);
    }

    [Fact]
    public void AnEntryThatYieldsMoreBytesThanTheArchiveDeclaredForItIsARefusalAndNothingIsApplied()
    {
        // The header claims 10 bytes for a file that really holds 1000 (stored, so the bytes are there to be read).
        var honest = MakeZip([("en/plugins/test.cs", Script("1.1.0")), ("en/configs/test.json", Bytes(1000, 4))], CompressionLevel.NoCompression);
        var lying = ClaimSize(honest, "en/configs/test.json", 10);

        AssertRefusedWritingNothing(lying, [Folder("en/plugins/", ZipRoles.Plugins), Folder("en/configs/", ZipRoles.Config)], "en/configs/test.json");
    }

    [Fact]
    public void AnEntryThatYieldsFewerBytesThanTheArchiveDeclaredForItIsARefusalAndNothingIsApplied()
    {
        var honest = MakeZip([("en/plugins/test.cs", Script("1.1.0")), ("en/configs/test.json", Bytes(100, 4))], CompressionLevel.NoCompression);
        var lying = ClaimSize(honest, "en/configs/test.json", 5000);

        AssertRefusedWritingNothing(lying, [Folder("en/plugins/", ZipRoles.Plugins), Folder("en/configs/", ZipRoles.Config)], "en/configs/test.json");
    }

    [Fact]
    public void ADeclaredSizeThatUnderstatesACompressedEntryNeverMakesTheServerWriteMoreThanWasDeclared()
    {
        // The framework's own reader stops a compressed entry at the size the header claims, so this is either a refusal or a file no longer than the claim.
        InstallOld();
        var honest = MakeZip([("en/plugins/test.cs", Script("1.1.0")), ("en/images/z.dat", new byte[100_000])]);
        var lying = ClaimSize(honest, "en/images/z.dat", 50);
        var plugin = Loaded((_, _) => lying);

        BeginZip(plugin, lying, [Folder("en/plugins/", ZipRoles.Plugins), Folder("en/images/", ZipRoles.Data)]);
        plugin.ThirdPartyTick();

        var written = Path.Combine(Data, "z.dat");
        if (File.Exists(written)) { Assert.True(new FileInfo(written).Length <= 50); }
        else { Assert.Equal("failed", plugin.ThirdParty.Phase); }
        Assert.False(Directory.Exists(StagingDir));
    }

    [Fact]
    public void AnArchiveWithMoreThanTwoThousandEntriesIsRefusedBeforeItIsResolved()
    {
        var entries = new List<(string, byte[])> { ("en/plugins/test.cs", Script("1.1.0")) };
        entries.AddRange(Enumerable.Range(0, 2000).Select(i => ($"ru/{i}.txt", Array.Empty<byte>())));                  // 2001 entries
        var zip = MakeZip(entries);

        AssertRefusedWritingNothing(zip, [Folder("en/plugins/", ZipRoles.Plugins), Folder("ru/", ZipRoles.Skip)], "too_many_files");
    }

    [Fact]
    public void AnArchiveWithExactlyTwoThousandEntriesIsAccepted()
    {
        InstallOld();
        var entries = new List<(string, byte[])> { ("en/plugins/test.cs", Script("1.1.0")) };
        entries.AddRange(Enumerable.Range(0, 1999).Select(i => ($"ru/{i}.txt", Array.Empty<byte>())));                  // 2000 entries
        var zip = MakeZip(entries);
        var plugin = Loaded((_, _) => zip);

        BeginZip(plugin, zip, [Folder("en/plugins/", ZipRoles.Plugins), Folder("ru/", ZipRoles.Skip)]);
        plugin.ThirdPartyTick();

        Assert.Equal("loading", plugin.ThirdParty.Phase);
    }

    private sealed class EndlessStream : Stream
    {
        public long Served;
        public int LargestRequest;
        public override int Read(byte[] buffer, int offset, int count)
        {
            LargestRequest = Math.Max(LargestRequest, count);
            Array.Fill(buffer, (byte)7, offset, count);
            Served += count;
            if (Served > 300_000_000) { throw new InvalidOperationException("the reader went on without any bound"); }      // a test failure, not a hang
            return count;
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

    private sealed class CountingSink : Stream
    {
        public long Written;
        public int LargestWrite;
        public override void Write(byte[] buffer, int offset, int count) { Written += count; LargestWrite = Math.Max(LargestWrite, count); }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public void AStreamThatNeverEndsIsCutOffAtWhatWasDeclaredWithAFixedSmallBuffer()
    {
        var input = new EndlessStream();
        var sink = new CountingSink();
        long staged = 0;

        var problem = ArchonPlugin.CopyBounded(input, sink, "en/x.bin", 100_000, 1_000_000, ref staged);

        Assert.Contains("more bytes than the archive declared", problem);
        Assert.InRange(input.Served, 100_001, 100_000 + 16384);                              // it never read on past the bound (plus the one byte that shows the excess)
        Assert.True(sink.Written <= 100_000, "wrote " + sink.Written);                     // and never wrote more than was declared
        Assert.True(input.LargestRequest <= 16384, "asked for " + input.LargestRequest);   // a 16 KB buffer, not the entry's length
        Assert.True(sink.LargestWrite <= 16384);
    }

    [Fact]
    public void AnEntryThatDeclaresAHugeSizeButKeepsGoingPastTheInstallLimitIsCutOffAtTheLimit()
    {
        var input = new EndlessStream();
        var sink = new CountingSink();
        long staged = 0;

        var problem = ArchonPlugin.CopyBounded(input, sink, "en/x.bin", long.MaxValue / 2, 50_000, ref staged);

        Assert.Contains("more than the 50000 bytes", problem);
        Assert.True(input.Served <= 50_000 + 16384, "read " + input.Served);
        Assert.True(sink.Written <= 50_000, "wrote " + sink.Written);
    }

    [Fact]
    public void TheInstallLimitIsCountedAcrossAllTheEntriesOfTheArchive()
    {
        var sink = new CountingSink();
        long staged = 0;

        Assert.Null(ArchonPlugin.CopyBounded(new MemoryStream(new byte[600]), sink, "a", 600, 1000, ref staged));
        var problem = ArchonPlugin.CopyBounded(new MemoryStream(new byte[600]), sink, "b", 600, 1000, ref staged);

        Assert.Contains("more than the 1000 bytes", problem);
        Assert.True(sink.Written <= 1000);
    }

    [Fact]
    public void AStreamThatEndsEarlyIsARefusalToo()
    {
        long staged = 0;

        var problem = ArchonPlugin.CopyBounded(new MemoryStream(new byte[10]), new CountingSink(), "en/x.bin", 20, 1000, ref staged);

        Assert.Contains("holds 10 bytes but the archive declared 20", problem);
    }

    [Fact]
    public void AnExactStreamIsCopiedWholeAndAnEmptyOneIsFine()
    {
        long staged = 0;
        var sink = new CountingSink();

        Assert.Null(ArchonPlugin.CopyBounded(new MemoryStream(Bytes(40_000, 1)), sink, "a", 40_000, 1_000_000, ref staged));
        Assert.Null(ArchonPlugin.CopyBounded(new MemoryStream(), sink, "b", 0, 1_000_000, ref staged));

        Assert.Equal(40_000, sink.Written);
        Assert.Equal(40_000, staged);
    }

    // ---- this plugin's own limits, whatever the panel says ---------------------------------------------------------------

    [Fact]
    public void TheLimitsAreTheProductOwnersFigures()
    {
        Assert.Equal(128L * 1024 * 1024, ArchonThirdParty.MaxFileBytes);
        Assert.Equal(512L * 1024 * 1024, ArchonThirdParty.MaxInstallBytes);
    }

    [Fact]
    public void ASizeOverTheLimitIsRefusedByBothCommandsBeforeAnythingIsDownloaded()
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var downloads = 0;
        var plugin = Loaded((_, _) => { downloads++; return zip; });
        var tooBig = (ArchonThirdParty.MaxFileBytes + 1).ToString();

        Assert.Equal("too_large", ErrorOf(plugin.BeginThirdPartyZip("Test", "1.1.0", Hex(zip), tooBig, "1000", Encode(UserRules()), Url)));
        Assert.Equal("too_large", ErrorOf(plugin.BeginThirdPartyUpdate("Test", "1.1.0", Hex(zip), tooBig, Url)));
        Assert.Equal("too_large", ErrorOf(plugin.BeginThirdPartyUpdate("Test", "1.1.0", Hex(zip), long.MaxValue.ToString(), Url)));

        Assert.Equal(0, downloads);
        Assert.Equal("idle", plugin.ThirdParty.Phase);
    }

    [Fact]
    public void ASizeExactlyAtTheLimitIsAccepted()
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        long seenSize = 0;
        var plugin = Loaded((_, size) => { seenSize = size; return zip; }, background: work => work());
        var exactly = ArchonThirdParty.MaxFileBytes.ToString();

        var reply = Reply(plugin.BeginThirdPartyZip("Test", "1.1.0", Hex(zip), exactly, "1000000", Encode(UserRules()), Url));

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(ArchonThirdParty.MaxFileBytes, seenSize);
    }

    [Theory]
    [InlineData("536870913")]
    [InlineData("9223372036854775807")]
    public void InstallBytesOverTheLimitAreRefusedBeforeAnythingIsDownloaded(string installBytes)
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => throw new InvalidOperationException("nothing may be downloaded"));

        Assert.Equal("too_large", ErrorOf(BeginZip(plugin, zip, installBytes: installBytes)));
        Assert.Equal("idle", plugin.ThirdParty.Phase);
    }

    [Fact]
    public void InstallBytesExactlyAtTheLimitAreAccepted()
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip, background: _ => { });

        Assert.True(Reply(BeginZip(plugin, zip, installBytes: ArchonThirdParty.MaxInstallBytes.ToString())).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void TheDownloadNeverHoldsMoreThanItWasToldTheFileIsAndNeverTrustsAnyHeader()
    {
        var endless = new EndlessStream();

        var error = Assert.Throws<ArchonThirdParty.ChangedException>(() => ArchonThirdParty.ReadBounded(endless, 50_000));

        Assert.Contains("larger than", error.Message);
        Assert.True(endless.Served <= 50_000 + 8192, "read " + endless.Served);
        Assert.True(endless.LargestRequest <= 8192);
    }

    [Fact]
    public void ADownloadIsNeverSizedFromTheLengthAStreamClaimsAndStopsAtThePluginsOwnLimitWhateverItWasTold()
    {
        // EndlessStream.Length throws: a reader that looked at it would fail. A size far above the plugin's own limit is clamped to it.
        var stream = new MemoryStream(Bytes(5000, 1));

        Assert.Equal(Bytes(5000, 1), ArchonThirdParty.ReadBounded(new NoLengthStream(stream), long.MaxValue));
        Assert.Throws<ArchonThirdParty.ChangedException>(() => ArchonThirdParty.ReadBounded(new NoLengthStream(new MemoryStream(new byte[100])), 99));
    }

    private sealed class NoLengthStream : Stream
    {
        private readonly Stream _inner;
        public NoLengthStream(Stream inner) { _inner = inner; }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
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

    // ---- what is refused before anything is downloaded -------------------------------------------------------------------

    [Fact]
    public void APluginThatCannotVouchForItsOwnFileAppliesNothing()
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip, validMain: false);

        Assert.Equal("not_verified", ErrorOf(BeginZip(plugin, zip)));
        Assert.Equal("idle", plugin.ThirdParty.Phase);
    }

    [Theory]
    [InlineData("RustArchon")]
    [InlineData("RustArchonUpdater")]
    [InlineData("")]
    [InlineData("../Test")]
    [InlineData("Test.cs")]
    [InlineData("1Test")]
    public void AClassNameThatIsNotAPlainOneOfTheirsIsRefused(string className)
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);

        Assert.Equal("bad_class", ErrorOf(BeginZip(plugin, zip, className: className)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2.3 ")]
    [InlineData("1.2/3")]
    [InlineData("1.2.3\n")]
    public void AVersionThatIsNotPlainTextIsRefused(string version)
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);

        Assert.Equal("bad_version", ErrorOf(BeginZip(plugin, zip, version: version)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void AHashThatIsNotASha256IsRefused(string hash)
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);

        Assert.Equal("bad_hash", ErrorOf(BeginZip(plugin, zip, sha: hash)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("")]
    public void ASizeThatIsNotAPositiveWholeNumberIsRefused(string size)
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);

        Assert.Equal("bad_size", ErrorOf(BeginZip(plugin, zip, size: size)));
    }

    [Theory]
    [InlineData("http://umod.org/x.zip")]
    [InlineData("ftp://umod.org/x.zip")]
    [InlineData("https://user:pass@umod.org/x.zip")]
    [InlineData("/x.zip")]
    [InlineData("")]
    public void OnlyAnAbsoluteHttpsAddressWithNoCredentialsIsFollowed(string url)
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);

        Assert.Equal("bad_url", ErrorOf(BeginZip(plugin, zip, url: url)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("1.5")]
    public void InstallBytesThatAreNotPositiveAreRefused(string installBytes)
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);

        Assert.Equal("bad_install_bytes", ErrorOf(BeginZip(plugin, zip, installBytes: installBytes)));
        Assert.Equal("idle", plugin.ThirdParty.Phase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 !!")]
    [InlineData("AAAA")]
    public void RulesThatCannotBeReadAreRefused(string rules)
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);

        Assert.Equal("bad_rules", ErrorOf(BeginZip(plugin, zip, rulesArgument: rules)));
        Assert.Equal("idle", plugin.ThirdParty.Phase);
    }

    [Fact]
    public void RulesThatAreReadableButUnusableAreRefusedBeforeAnythingIsDownloaded()
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => throw new InvalidOperationException("nothing may be downloaded"));

        foreach (var rules in new[]
                 {
                     new[] { Folder("en/", "root") }, new[] { Folder("../", ZipRoles.Data) }, new[] { Folder("en/", ZipRoles.Data, "..") }, new[] { Folder("ru/", ZipRoles.Skip, "x") },
                     new[] { Folder("", ZipRoles.Data) }, new[] { Folder("en/", "PLUGINS") }
                 })
        {
            Assert.Equal("bad_rules", ErrorOf(BeginZip(plugin, zip, rules)));
        }
    }

    [Fact]
    public void ItOnlyUpdatesAPluginThatIsInstalled()
    {
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);

        Assert.Equal("not_installed", ErrorOf(BeginZip(plugin, zip)));
        Assert.False(File.Exists(TestCs));
    }

    [Fact]
    public void TwoFilesDeclaringTheSameClassAreNotGuessedBetween()
    {
        InstallOld();
        File.WriteAllBytes(Path.Combine(Plugins, "TestCopy.cs"), Script("0.9.0"));
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);

        Assert.Equal("ambiguous", ErrorOf(BeginZip(plugin, zip)));
    }

    [Fact]
    public void ASecondUpdateWhileOneIsInProgressIsRefused()
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip, background: _ => { });
        Assert.True(Reply(BeginZip(plugin, zip)).GetProperty("ok").GetBoolean());

        Assert.Equal("busy", ErrorOf(BeginZip(plugin, zip)));
        Assert.Equal("busy", ErrorOf(plugin.BeginThirdPartyUpdate("Test", "1.1.0", Hex(zip), "10", Url)));
    }

    [Fact]
    public void ItWaitsWhileTheUpdaterIsReplacingThisPlugin()
    {
        InstallOld();
        File.WriteAllText(Path.Combine(RustArchonData, "update-status.txt"), "phase=loading\n");
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);

        Assert.Equal("busy", ErrorOf(BeginZip(plugin, zip)));
    }

    [Fact]
    public void ADownloadThatFailsChangesNothing()
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => throw new IOException("the server answered 404"));
        var before = Snapshot();

        BeginZip(plugin, zip);
        plugin.ThirdPartyTick();

        Assert.Equal("failed", plugin.ThirdParty.Phase);
        Assert.Contains("download_failed", plugin.ThirdParty.Reason);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void ADownloadThatDiedWithThePreviousInstanceIsRecordedAsInterruptedAndChangedNothing()
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var first = Loaded((_, _) => zip, background: _ => { });
        BeginZip(first, zip);

        var second = Loaded();

        Assert.Equal("failed", second.ThirdParty.Phase);
        Assert.Contains("interrupted", second.ThirdParty.Reason);
        Assert.Equal(Script("1.0.0"), File.ReadAllBytes(TestCs));
        Assert.False(Directory.Exists(BackupDir));
    }

    // ---- the command ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    public void TheZipCommandNeedsExactlySevenArguments(int count)
    {
        var plugin = Loaded();
        var arg = ConsoleSystem.Arg.WithArgs(Enumerable.Repeat("x", count).ToArray());

        plugin.CmdThirdPartyZip(arg);

        Assert.Equal("usage", ErrorOf(Assert.Single(arg.Replies)));
    }

    [Fact]
    public void TheZipCommandRunsTheUpdateFromItsArguments()
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => zip);
        var arg = ConsoleSystem.Arg.WithArgs("Test", "1.1.0", Hex(zip), zip.Length.ToString(), "1000000", Encode(UserRules()), Url);

        plugin.CmdThirdPartyZip(arg);

        var reply = Reply(Assert.Single(arg.Replies));
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        Assert.Equal("zip", reply.GetProperty("data").GetProperty("kind").GetString());
        Assert.Equal("downloading", plugin.ThirdParty.Phase);
    }

    [Fact]
    public void ACallFromAnInGameConsoleIsIgnored()
    {
        var plugin = Loaded();
        var arg = ConsoleSystem.Arg.WithArgs("Test", "1.1.0", new string('a', 64), "10", "10", "AAAA", Url);
        arg.Connection = new object();

        plugin.CmdThirdPartyZip(arg);

        Assert.Empty(arg.Replies);
    }

    // ---- the capability -------------------------------------------------------------------------------------------------

    private static List<string> CapabilitiesOf(ArchonPlugin plugin)
    {
        var arg = ConsoleSystem.Arg.WithArgs();
        plugin.CmdHello(arg);
        return Reply(Assert.Single(arg.Replies)).GetProperty("data").GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).ToList();
    }

    [Fact]
    public void TheZipCapabilityIsAdvertisedRightAfterTheSingleFileOneWhenTheReaderCanBeLoaded()
    {
        var capabilities = CapabilitiesOf(Loaded());

        Assert.Equal(new[] { "config", "combat", "tcs", "positions", "map", "updates", "updater-update", "thirdparty-update", "thirdparty-zip" }, capabilities);
        Assert.Equal(RustArchon.Messaging.Contracts.RustArchonPlugin.ThirdPartyZipCapability, ArchonPlugin.ThirdPartyZipCapability);
    }

    [Fact]
    public void TheZipCapabilityIsAbsentWhenTheLoaderFailsAndEveryOtherOneStays()
    {
        var plugin = Loaded();
        plugin.ZipTypeLoader = () => null;

        var capabilities = CapabilitiesOf(plugin);

        Assert.DoesNotContain("thirdparty-zip", capabilities);
        Assert.Equal(new[] { "config", "combat", "tcs", "positions", "map", "updates", "updater-update", "thirdparty-update" }, capabilities);
    }

    [Fact]
    public void ALoaderThatThrowsCountsAsUnavailable()
    {
        var plugin = Loaded();
        plugin.ZipTypeLoader = () => throw new FileNotFoundException("System.IO.Compression");

        Assert.DoesNotContain("thirdparty-zip", CapabilitiesOf(plugin));
        Assert.False(plugin.ZipSupported());
    }

    [Fact]
    public void ALoaderThatHandsBackATypeThatIsNotTheZipReaderCountsAsUnavailableToo()
    {
        var plugin = Loaded();
        plugin.ZipTypeLoader = () => typeof(string);

        Assert.DoesNotContain("thirdparty-zip", CapabilitiesOf(plugin));
    }

    [Fact]
    public void TheLoaderIsAskedOnceAndTheAnswerIsKept()
    {
        var plugin = Loaded();
        var asked = 0;
        plugin.ZipTypeLoader = () => { asked++; return typeof(ZipArchive); };

        CapabilitiesOf(plugin);
        CapabilitiesOf(plugin);
        plugin.ZipSupported();

        Assert.Equal(1, asked);
    }

    [Fact]
    public void WithoutTheReaderTheZipCommandSaysSoAndDoesNothing()
    {
        InstallOld();
        var zip = MakeZip(UserArchive());
        var plugin = Loaded((_, _) => throw new InvalidOperationException("nothing may be downloaded"));
        plugin.ZipTypeLoader = () => null;

        Assert.Equal("zip_unsupported", ErrorOf(BeginZip(plugin, zip)));
        Assert.Equal("idle", plugin.ThirdParty.Phase);
    }

    [Fact]
    public void TheSingleFileUpdateDoesNotNeedTheZipReader()
    {
        InstallOld();
        var served = Script("1.1.0");
        var plugin = Loaded((_, _) => served);
        plugin.ZipTypeLoader = () => null;

        var reply = Reply(plugin.BeginThirdPartyUpdate("Test", "1.1.0", Hex(served), served.Length.ToString(), Url));

        Assert.True(reply.GetProperty("ok").GetBoolean());
        plugin.ThirdPartyTick();
        Assert.Equal("loading", plugin.ThirdParty.Phase);
    }

    [Fact]
    public void TheDefaultLoaderFindsTheRealReaderByNameAndReadsAnArchiveThroughReflectionOnly()
    {
        var type = ArchonZipArchive.DefaultLoadType();

        Assert.Equal(typeof(ZipArchive), type);
        using var archive = ArchonZipArchive.Open(type, MakeZip([("a/b.txt", Text("hello")), ("a/", Array.Empty<byte>()), ("c.txt", Text("x"))], directoryEntries: false));
        Assert.Equal(new[] { "a/b.txt", "c.txt" }, archive.Files.Select(f => f.Name));
        Assert.Equal(new long[] { 5, 1 }, archive.Files.Select(f => f.Length));
        using var stream = archive.OpenEntry(archive.Files[0]);
        Assert.Equal("hello", new StreamReader(stream).ReadToEnd());
        Assert.Throws<IOException>(() => ArchonZipArchive.Open(type, Encoding.UTF8.GetBytes("not a zip at all, not even close")));
    }

    [Fact]
    public void ThePluginFileDoesNotDependOnSystemIoCompressionAtCompileTime()
    {
        // A "using" or a direct reference would stop the whole plugin compiling on a server whose compiler does not reference that assembly.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "RustArchon.Plugin", "src", "RustArchon.cs"))) { directory = directory.Parent; }
        Assert.NotNull(directory);

        var source = File.ReadAllText(Path.Combine(directory!.FullName, "RustArchon.Plugin", "src", "RustArchon.cs"));

        Assert.DoesNotContain("using System.IO.Compression", source);
        Assert.DoesNotMatch(@"(?<![A-Za-z])ZipArchive\(", source);
        Assert.DoesNotContain("new ZipArchive(", source);
        Assert.DoesNotContain("ZipFile.", source);
    }
}
