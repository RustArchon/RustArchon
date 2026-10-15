// Copyright ©2026 Scott Blomfield

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Oxide.Plugins;
using ArchonPlugin = Oxide.Plugins.RustArchon;

namespace RustArchon.Plugin.Tests;

/// <summary>
/// The plugin's own log, read on demand by the panel (ADR-0006): a bounded buffer with the same cursor contract as the combat and position drains, a
/// redactor that keeps secrets from ever leaving the game server, and a small journal of update attempts that survives the reload an update causes.
/// The plugin does nothing between requests beyond writing into the buffer.
/// </summary>
public sealed class RustArchonLogTests : IDisposable
{
    private const string SigMarker = "// RUSTARCHON-SIG-V1: ";

    private readonly RSA _key = RSA.Create(2048);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rustarchon-log-" + Guid.NewGuid().ToString("N"));
    private readonly string _plugins;
    private readonly string _data;
    private DateTime _now = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    public RustArchonLogTests()
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

    private string MainPath => Path.Combine(_plugins, "RustArchon.cs");

    private void InstallMain()
    {
        var payload = Encoding.UTF8.GetBytes("[Info(\"RustArchon\", \"RustArchon\", \"0.9.0\")]\nclass M {}\n");
        var signature = _key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        File.WriteAllBytes(MainPath, payload.Concat(Encoding.ASCII.GetBytes(SigMarker + Convert.ToBase64String(signature) + "\n")).ToArray());
    }

    private ArchonPlugin Loaded(Func<string, long, byte[]>? download = null)
    {
        InstallMain();
        var plugin = new ArchonPlugin
        {
            SettingsFilePath = Path.Combine(_data, "settings.txt"),
            ScriptFilePath = MainPath,
            TrustedModulus = Convert.ToBase64String(_key.ExportParameters(false).Modulus!),
            TrustedExponent = Convert.ToBase64String(_key.ExportParameters(false).Exponent!),
            UtcNow = () => _now,
            ThirdPartyDownload = download ?? ((_, _) => throw new InvalidOperationException("no download expected")),
            RunInBackground = work => work()
        };
        typeof(ArchonPlugin).GetMethod("Init", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(plugin, null);
        return plugin;
    }

    private static JsonElement Data(ConsoleSystem.Arg arg)
    {
        var reply = JsonDocument.Parse(Assert.Single(arg.Replies)).RootElement;
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        return reply.GetProperty("data");
    }

    private static ConsoleSystem.Arg Drain(ArchonPlugin plugin, string bootId, string cursor, string? max = null)
    {
        var arg = max is null ? ConsoleSystem.Arg.WithArgs(bootId, cursor) : ConsoleSystem.Arg.WithArgs(bootId, cursor, max);
        plugin.CmdLogsDrain(arg);
        return arg;
    }

    private sealed record Entry(long Seq, DateTime TimeUtc, int Level, string Category, string Message, Dictionary<string, string> Props);

    /// <summary>Everything the plugin would hand the panel, paged the way the Worker pages it.</summary>
    private static List<Entry> AllEntries(ArchonPlugin plugin)
    {
        var entries = new List<Entry>();
        long cursor = 0;
        for (var guard = 0; guard < 100; guard++)
        {
            var data = Data(Drain(plugin, plugin.Logs.BootId.ToString(), cursor.ToString(), "500"));
            foreach (var item in data.GetProperty("logs").EnumerateArray())
            {
                entries.Add(new Entry(
                    item.GetProperty("s").GetInt64(), item.GetProperty("t").GetDateTime().ToUniversalTime(), item.GetProperty("l").GetInt32(),
                    item.GetProperty("c").GetString()!, item.GetProperty("m").GetString()!,
                    item.GetProperty("p").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!)));
            }

            var next = data.GetProperty("cursor").GetInt64();
            if (next >= data.GetProperty("head").GetInt64() || next == cursor) { break; }
            cursor = next;
        }

        return entries;
    }

    private static List<Entry> UpdateEntries(ArchonPlugin plugin) => AllEntries(plugin).Where(e => e.Category == "update").ToList();

    // ---- the buffer and the command --------------------------------------------------------------------------------

    [Fact]
    public void AFreshPluginHasLoggedItsOwnStartAndTheDrainSaysSo()
    {
        var plugin = Loaded();

        var data = Data(Drain(plugin, plugin.Logs.BootId.ToString(), "0"));

        Assert.Equal(1, data.GetProperty("format").GetInt32());
        Assert.Equal(plugin.Logs.BootId, data.GetProperty("bootId").GetInt64());
        Assert.False(data.GetProperty("reset").GetBoolean());
        Assert.True(data.GetProperty("head").GetInt64() >= 1);
        Assert.Equal(1, data.GetProperty("oldest").GetInt64());
        Assert.Contains(data.GetProperty("logs").EnumerateArray(), item => item.GetProperty("m").GetString()!.StartsWith("Init v"));
    }

    [Fact]
    public void EntriesCarryTheirSequenceTimeLevelCategoryMessageAndProperties()
    {
        var plugin = Loaded();
        var before = plugin.Logs.Head;

        plugin.Say(3, "update", "Something happened", "Plugin", "Foo");

        var data = Data(Drain(plugin, plugin.Logs.BootId.ToString(), before.ToString()));
        var item = Assert.Single(data.GetProperty("logs").EnumerateArray());
        Assert.Equal(before + 1, item.GetProperty("s").GetInt64());
        Assert.Equal(_now, item.GetProperty("t").GetDateTime().ToUniversalTime());
        Assert.Equal(3, item.GetProperty("l").GetInt32());
        Assert.Equal("update", item.GetProperty("c").GetString());
        Assert.Equal("Something happened", item.GetProperty("m").GetString());
        Assert.Equal("Foo", item.GetProperty("p").GetProperty("Plugin").GetString());
        Assert.Equal(before + 1, data.GetProperty("cursor").GetInt64());
    }

    [Fact]
    public void DrainingFromTheHeadReturnsNothingAndKeepsTheCursor()
    {
        var plugin = Loaded();
        var head = plugin.Logs.Head;

        var data = Data(Drain(plugin, plugin.Logs.BootId.ToString(), head.ToString()));

        Assert.Empty(data.GetProperty("logs").EnumerateArray());
        Assert.Equal(head, data.GetProperty("cursor").GetInt64());
        Assert.False(data.GetProperty("lost").GetBoolean());
    }

    [Fact]
    public void ABootIdThatIsNotThisRunsIsAResetAndStartsFromTheOldest()
    {
        var plugin = Loaded();
        plugin.Say(2, "update", "one");

        var data = Data(Drain(plugin, "12345", "99"));

        Assert.True(data.GetProperty("reset").GetBoolean());
        Assert.True(data.GetProperty("logs").GetArrayLength() >= 2);
        Assert.Equal(1, data.GetProperty("logs")[0].GetProperty("s").GetInt64());
    }

    [Fact]
    public void TheBufferIsBoundedAndACursorBehindItIsToldItLostEntries()
    {
        var plugin = Loaded();

        for (var i = 0; i < ArchonPlugin.LogCapacity + 50; i++) { plugin.Say(2, "test", "m" + i); }
        var data = Data(Drain(plugin, plugin.Logs.BootId.ToString(), "0", "10"));

        Assert.True(data.GetProperty("lost").GetBoolean());
        Assert.Equal(plugin.Logs.Head - ArchonPlugin.LogCapacity + 1, data.GetProperty("oldest").GetInt64());
        Assert.True(data.GetProperty("logs")[0].GetProperty("s").GetInt64() > 1);
    }

    [Fact]
    public void TheNumberOfEntriesPerReplyIsCapped()
    {
        var plugin = Loaded();
        for (var i = 0; i < 600; i++) { plugin.Say(2, "test", "m" + i); }

        var data = Data(Drain(plugin, plugin.Logs.BootId.ToString(), "0", "100000"));

        Assert.True(data.GetProperty("logs").GetArrayLength() <= 500);
    }

    [Fact]
    public void ACallFromAnInGameConsoleIsIgnored()
    {
        var plugin = Loaded();
        var arg = ConsoleSystem.Arg.WithArgs("1", "0");
        arg.Connection = new object();

        plugin.CmdLogsDrain(arg);

        Assert.Empty(arg.Replies);
    }

    [Theory]
    [InlineData]
    [InlineData("1")]
    [InlineData("x", "0")]
    [InlineData("1", "-1")]
    public void BadArgumentsGetAUsageError(params string[] args)
    {
        var plugin = Loaded();
        var arg = ConsoleSystem.Arg.WithArgs(args);

        plugin.CmdLogsDrain(arg);

        var reply = JsonDocument.Parse(Assert.Single(arg.Replies)).RootElement;
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("usage", reply.GetProperty("err").GetString());
    }

    [Fact]
    public void TheLogsCapabilityIsAdvertisedSoThePanelKnowsLogsCanBeRead()
    {
        var plugin = Loaded();
        var arg = ConsoleSystem.Arg.WithArgs();

        plugin.CmdHello(arg);

        var capabilities = Data(arg).GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Contains("logs", capabilities);
        Assert.Contains("thirdparty-update", capabilities);
    }

    [Fact]
    public void SayStillWritesToTheServerConsoleAsPutsAlwaysDid()
    {
        var plugin = Loaded();

        plugin.Say(2, "test", "visible on the console");

        Assert.Contains("visible on the console", plugin.Log);
    }

    [Fact]
    public void AnEntryNeverCostsATimerAHookOrBackgroundWork()
    {
        var backgroundCalls = 0;
        InstallMain();
        var plugin = new ArchonPlugin
        {
            SettingsFilePath = Path.Combine(_data, "settings.txt"), ScriptFilePath = MainPath,
            TrustedModulus = Convert.ToBase64String(_key.ExportParameters(false).Modulus!),
            TrustedExponent = Convert.ToBase64String(_key.ExportParameters(false).Exponent!),
            RunInBackground = work => { backgroundCalls++; work(); }
        };
        typeof(ArchonPlugin).GetMethod("Init", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(plugin, null);
        var before = backgroundCalls;
        var subscriptions = plugin.Subscriptions.Count;

        for (var i = 0; i < 100; i++) { plugin.Say(2, "test", "m"); }

        Assert.Equal(before, backgroundCalls);
        Assert.Equal(subscriptions, plugin.Subscriptions.Count);
    }

    // ---- redaction ---------------------------------------------------------------------------------------------------

    public static TheoryData<string, string> Vectors()
    {
        var data = new TheoryData<string, string>();
        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Vectors", "redaction.tsv")))
        {
            if (line.Length == 0 || line[0] == '#') { continue; }
            var tab = line.IndexOf('\t');
            data.Add(line[..tab], line[(tab + 1)..].Replace("\\n", "\n").Replace("\\t", "\t"));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void EverySharedVectorLosesItsSecretInThePluginsOwnRedactor(string secret, string text)
    {
        var result = ArchonRedact.Clean(text);

        Assert.DoesNotContain(secret, result);
        Assert.Contains("[REDACTED]", result);
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void ThePluginAndTheServerSideRedactorAgreeThatTheSecretMustGo(string secret, string text)
    {
        var plugin = ArchonRedact.Clean(text);
        var server = RustArchon.Diagnostics.LogRedactor.Redact(text);

        Assert.DoesNotContain(secret, plugin);
        Assert.DoesNotContain(secret, server);
    }

    [Theory]
    [InlineData("Started the update of Foo on server 3f2504e0-4f89-11d3-9a0c-0305e82c3301.")]
    [InlineData("Update of Foo.cs to 2.0.0 failed: apply_failed: IOException")]
    [InlineData("sha256 e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855 matched")]
    [InlineData("")]
    public void OrdinaryTextIsLeftAlone(string text)
    {
        Assert.Equal(text, ArchonRedact.Clean(text));
    }

    [Fact]
    public void RedactionOfAVeryLongUnbrokenRunStillFinishes()
    {
        var result = ArchonRedact.Clean(new string('y', 60_000));

        Assert.NotEqual("[REDACTION FAILED]", result);
    }

    [Fact]
    public void AnEntryHoldingASecretIsRedactedBeforeItEntersTheBuffer()
    {
        var plugin = Loaded();

        plugin.Say(2, "update", "Fetching with token=abc123SECRET from the panel", "Detail", "Password=hunter2");

        var json = Data(Drain(plugin, plugin.Logs.BootId.ToString(), "0", "500")).GetProperty("logs").ToString();
        Assert.DoesNotContain("abc123SECRET", json);
        Assert.DoesNotContain("hunter2", json);
        Assert.DoesNotContain("abc123SECRET", string.Join('\n', plugin.Log));
    }

    [Fact]
    public void LongMessagesAreCutDown()
    {
        var plugin = Loaded();

        plugin.Say(2, "test", new string('a', 50_000));

        var item = AllEntries(plugin).Last();
        Assert.True(item.Message.Length <= ArchonPlugin.MaxLogMessageLength + 20);
    }

    // ---- update requests and outcomes -------------------------------------------------------------------------------------

    [Fact]
    public void ARefusedThirdPartyUpdateRequestIsLoggedWithTheReasonItWasGiven()
    {
        var plugin = Loaded();
        var arg = ConsoleSystem.Arg.WithArgs("Marker", "bad version!", new string('a', 64), "100", "https://umod.org/plugins/Marker.cs");

        plugin.CmdThirdPartyUpdate(arg);

        var entry = UpdateEntries(plugin).Single(e => e.Message.Contains("refused"));
        Assert.Equal(3, entry.Level);
        Assert.Contains("bad_version", entry.Message);
        Assert.Contains("Marker", entry.Message);
    }

    [Fact]
    public void AnUpdateRequestWithTheWrongNumberOfArgumentsIsLoggedAsRefused()
    {
        var plugin = Loaded();

        plugin.CmdThirdPartyUpdate(ConsoleSystem.Arg.WithArgs("Marker"));

        Assert.Contains(UpdateEntries(plugin), e => e.Message.Contains("refused") && e.Message.Contains("usage"));
    }

    [Fact]
    public void AnAcceptedUpdateRequestIsLoggedAsAccepted()
    {
        var plugin = Loaded(download: (_, _) => Array.Empty<byte>());
        Directory.CreateDirectory(_plugins);
        File.WriteAllText(Path.Combine(_plugins, "Marker.cs"), "[Info(\"Marker\", \"Someone\", \"1.0.0\")]\npublic class Marker : RustPlugin {}\n");
        var bytes = Encoding.UTF8.GetBytes("[Info(\"Marker\", \"Someone\", \"1.1.0\")]\npublic class Marker : RustPlugin {}\n");
        plugin.FindLoaded = name => name == "Marker" ? new ArchonThirdParty.LoadedPlugin { Version = "1.0.0", Instance = new object() } : null;
        plugin.ThirdPartyDownload = (_, _) => bytes;

        plugin.CmdThirdPartyUpdate(ConsoleSystem.Arg.WithArgs(
            "Marker", "1.1.0", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length.ToString(), "https://umod.org/plugins/Marker.cs"));

        Assert.Contains(UpdateEntries(plugin), e => e.Level == 2 && e.Message.Contains("Marker") && e.Message.Contains("1.1.0") && e.Message.Contains("accepted"));
    }

    [Fact]
    public void AFileThatIsNotTheOneThePanelCheckedIsLoggedWithWhatWasWrong()
    {
        var plugin = Loaded();
        File.WriteAllText(Path.Combine(_plugins, "Marker.cs"), "[Info(\"Marker\", \"Someone\", \"1.0.0\")]\npublic class Marker : RustPlugin {}\n");
        var served = Encoding.UTF8.GetBytes("[Info(\"Marker\", \"Someone\", \"1.1.0\")]\npublic class Marker : RustPlugin {}\n");
        plugin.FindLoaded = name => name == "Marker" ? new ArchonThirdParty.LoadedPlugin { Version = "1.0.0", Instance = new object() } : null;
        plugin.ThirdPartyDownload = (_, _) => served;

        var reply = plugin.BeginThirdPartyUpdate("Marker", "1.1.0", new string('0', 64), served.Length.ToString(), "https://umod.org/plugins/Marker.cs");
        Assert.Contains("\"ok\":true", reply);
        plugin.ThirdPartyTick();                                    // verifies the download against what the panel checked

        var entry = UpdateEntries(plugin).Single(e => e.Message.Contains("not applied"));
        Assert.Equal(3, entry.Level);
        Assert.Contains("changed", entry.Message);
    }

    // ---- the journal --------------------------------------------------------------------------------------------------------

    [Fact]
    public void UpdateEntriesAreStillThereAfterThePluginReloads()
    {
        var first = Loaded();
        first.Say(3, "update", "Update of Foo.cs to 2.0.0 failed: apply_failed: IOException", "Plugin", "Foo");
        _now = _now.AddMinutes(5);

        var second = Loaded();

        var entry = UpdateEntries(second).Single(e => e.Message.Contains("apply_failed"));
        Assert.Equal(3, entry.Level);
        Assert.Equal("Foo", entry.Props["Plugin"]);
        Assert.Equal("true", entry.Props["persisted"]);
        Assert.Equal(new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc), entry.TimeUtc);
    }

    [Fact]
    public void OnlyUpdateEntriesAreJournaled()
    {
        var first = Loaded();
        first.Say(2, "other", "not an update");

        var second = Loaded();

        Assert.DoesNotContain(AllEntries(second), e => e.Message == "not an update");
    }

    [Fact]
    public void TheJournalKeepsOnlyTheMostRecentEntries()
    {
        var first = Loaded();
        for (var i = 0; i < ArchonPlugin.JournalKeep * 3; i++) { first.Say(2, "update", "attempt " + i); }

        var second = Loaded();

        var persisted = AllEntries(second).Where(e => e.Props.ContainsKey("persisted")).ToList();
        Assert.Equal(ArchonPlugin.JournalKeep, persisted.Count);
        Assert.Equal("attempt " + (ArchonPlugin.JournalKeep * 3 - 1), persisted.Last().Message);
        Assert.True(File.ReadAllLines(Path.Combine(_data, ArchonPlugin.UpdateJournalFileName)).Length <= ArchonPlugin.JournalKeep * 2);
    }

    [Fact]
    public void ACorruptJournalLineIsSkippedAndTheRestStillLoad()
    {
        var first = Loaded();
        first.Say(2, "update", "good one");
        File.AppendAllText(Path.Combine(_data, ArchonPlugin.UpdateJournalFileName), "this is not a journal line\n\t\t\n");
        first.Say(2, "update", "good two");

        var second = Loaded();

        var messages = UpdateEntries(second).Select(e => e.Message).ToList();
        Assert.Contains("good one", messages);
        Assert.Contains("good two", messages);
    }

    [Fact]
    public void AnEntryWithTabsNewlinesAndBackslashesSurvivesTheJournal()
    {
        var first = Loaded();
        first.Say(2, "update", "line one\nline\ttwo \\ three", "Detail", "a\tb");

        var second = Loaded();

        var entry = UpdateEntries(second).Single(e => e.Message.StartsWith("line one"));
        Assert.Equal("line one\nline\ttwo \\ three", entry.Message);
        Assert.Equal("a\tb", entry.Props["Detail"]);
    }

    [Fact]
    public void AJournalThatCannotBeWrittenNeverStopsThePluginLoadingOrSaying()
    {
        var plugin = Loaded();
        plugin.DataDirectory = Path.Combine(_data, "no", "such", "folder", new string('x', 5));
        File.WriteAllText(Path.Combine(_data, "no"), "a file where a folder is needed");

        plugin.Say(2, "update", "still works");

        Assert.Contains(UpdateEntries(plugin), e => e.Message == "still works");
    }
}
