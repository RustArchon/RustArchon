// Copyright ©2026 Scott Blomfield

using System.Text.Json;
using Oxide.Plugins;
using ArchonPlugin = Oxide.Plugins.RustArchon;

namespace RustArchon.Plugin.Tests;

/// <summary>
/// Behavior tests for the RustArchon companion plugin's command surface (<c>archon.hello</c>,
/// <c>archon.config</c>), run against the stubs in Stubs/. They prove our logic; whether Carbon and Oxide
/// actually find and run these commands is proven by the live smoke test on a real server.
/// </summary>
public sealed class RustArchonPluginTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rustarchon-plugin-tests-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "RustArchon", "settings.txt");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private ArchonPlugin LoadedPlugin(string settingsPath = null)
    {
        var plugin = new ArchonPlugin { SettingsFilePath = settingsPath ?? SettingsPath };
        InvokeInit(plugin);
        return plugin;
    }

    // Init is private on purpose (the framework finds it by reflection), so the tests do the same.
    private static void InvokeInit(ArchonPlugin plugin) =>
        typeof(ArchonPlugin).GetMethod("Init", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(plugin, null);

    private static ConsoleSystem.Arg Rcon(params string[] args) => ConsoleSystem.Arg.WithArgs(args);

    private static JsonElement ParseReply(ConsoleSystem.Arg arg)
    {
        var reply = Assert.Single(arg.Replies);
        return JsonDocument.Parse(reply).RootElement; // throws if the reply is not valid JSON
    }

    [Fact]
    public void TheArgStubKeepsTheRealTypeOfArgs()
    {
        // Found on the first live load: on the game server ConsoleSystem.Arg.Args is a StringView[], not a
        // string[], and code that treated it as strings did not compile there. The stub must stay faithful so
        // that kind of mistake fails to compile here too - if this fails, someone "simplified" the stub.
        Assert.Equal(typeof(StringView[]), typeof(ConsoleSystem.Arg).GetField(nameof(ConsoleSystem.Arg.Args))!.FieldType);
    }

    // ---- archon.hello -------------------------------------------------------------------------------------

    [Fact]
    public void Hello_ReturnsAnOkEnvelopeWithVersionProtocolAndSettings()
    {
        var plugin = LoadedPlugin();
        var arg = Rcon();

        plugin.CmdHello(arg);

        var root = ParseReply(arg);
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.True(root.GetProperty("ok").GetBoolean());
        var data = root.GetProperty("data");
        Assert.Equal("RustArchon", data.GetProperty("plugin").GetString());
        Assert.Equal("0.1.0", data.GetProperty("version").GetString());
        Assert.Equal(ArchonPlugin.ProtocolVersion, data.GetProperty("protocolVersion").GetInt32());
        Assert.True(data.GetProperty("settings").GetProperty("recording").GetBoolean());
        Assert.True(data.GetProperty("settings").GetProperty("combat").GetBoolean());
    }

    [Fact]
    public void Hello_OnlyAdvertisesCapabilitiesThatExist()
    {
        // Fail closed: the panel enables a feature only when a capability is positively reported, so a build
        // without the recording/combat code must not claim it.
        var plugin = LoadedPlugin();
        var arg = Rcon();

        plugin.CmdHello(arg);

        var capabilities = ParseReply(arg).GetProperty("data").GetProperty("capabilities")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "config", "combat", "tcs", "positions", "map", "updates" }, capabilities); // "combat": the damage hooks and the drain command exist
    }

    [Fact]
    public void Commands_AreIgnoredWhenCalledFromAnInGameConsole()
    {
        var plugin = LoadedPlugin();
        var hello = new ConsoleSystem.Arg { Connection = new object() };
        var config = ConsoleSystem.Arg.WithArgs("set", "combat", "false");
        config.Connection = new object();

        plugin.CmdHello(hello);
        plugin.CmdConfig(config);

        Assert.Empty(hello.Replies);
        Assert.Empty(config.Replies);
        Assert.True(plugin.Settings.Combat); // and the attempted change did not apply
    }

    // ---- archon.config ------------------------------------------------------------------------------------

    [Fact]
    public void Config_DefaultsBothSwitchesOn()
    {
        var plugin = LoadedPlugin();
        var arg = Rcon("get");

        plugin.CmdConfig(arg);

        var data = ParseReply(arg).GetProperty("data");
        Assert.True(data.GetProperty("recording").GetBoolean());
        Assert.True(data.GetProperty("combat").GetBoolean());
    }

    [Fact]
    public void Config_GetIsTheDefaultAction()
    {
        var plugin = LoadedPlugin();
        var arg = Rcon();

        plugin.CmdConfig(arg);

        Assert.True(ParseReply(arg).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Config_SetTurnsOneSwitchOffAndLeavesTheOtherAlone()
    {
        var plugin = LoadedPlugin();
        var arg = Rcon("set", "combat", "false");

        plugin.CmdConfig(arg);

        var data = ParseReply(arg).GetProperty("data");
        Assert.False(data.GetProperty("combat").GetBoolean());
        Assert.True(data.GetProperty("recording").GetBoolean());
        Assert.False(plugin.Settings.Combat);
        Assert.True(plugin.Settings.Recording);
    }

    [Fact]
    public void Config_SettingsSurviveAReloadBecauseTheyArePersisted()
    {
        var first = LoadedPlugin();
        first.CmdConfig(Rcon("set", "recording", "off"));

        var reloaded = LoadedPlugin(); // a fresh instance reading the same file, as after a plugin reload

        Assert.False(reloaded.Settings.Recording);
        Assert.True(reloaded.Settings.Combat);
    }

    [Fact]
    public void Config_ReportsPersistedTrueWhenTheFileWasWritten()
    {
        var plugin = LoadedPlugin();
        var arg = Rcon("set", "combat", "false");

        plugin.CmdConfig(arg);

        Assert.True(ParseReply(arg).GetProperty("data").GetProperty("persisted").GetBoolean());
        Assert.True(File.Exists(SettingsPath));
    }

    [Fact]
    public void Config_StillAppliesInMemoryButReportsNotPersistedWhenTheFileCannotBeWritten()
    {
        // A directory can't be created underneath a regular file.
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "not a directory");
        var plugin = LoadedPlugin(Path.Combine(blocker, "RustArchon", "settings.txt"));
        var arg = Rcon("set", "combat", "false");

        plugin.CmdConfig(arg);

        var root = ParseReply(arg);
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.False(root.GetProperty("data").GetProperty("persisted").GetBoolean());
        Assert.False(plugin.Settings.Combat);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("on", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("Off", false)]
    [InlineData("0", false)]
    public void Config_AcceptsTheDocumentedBooleanSpellings(string value, bool expected)
    {
        var plugin = LoadedPlugin();
        var arg = Rcon("set", "recording", value);

        plugin.CmdConfig(arg);

        Assert.True(ParseReply(arg).GetProperty("ok").GetBoolean());
        Assert.Equal(expected, plugin.Settings.Recording);
    }

    [Fact]
    public void Config_RejectsAnUnknownKeyWithoutChangingAnything()
    {
        var plugin = LoadedPlugin();
        var arg = Rcon("set", "damage", "false");

        plugin.CmdConfig(arg);

        var root = ParseReply(arg);
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("unknown_key", root.GetProperty("err").GetString());
        Assert.True(plugin.Settings.Combat);
        Assert.True(plugin.Settings.Recording);
    }

    [Fact]
    public void Config_RejectsABadValueWithoutChangingAnything()
    {
        var plugin = LoadedPlugin();
        var arg = Rcon("set", "combat", "maybe");

        plugin.CmdConfig(arg);

        var root = ParseReply(arg);
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("bad_value", root.GetProperty("err").GetString());
        Assert.True(plugin.Settings.Combat);
    }

    [Theory]
    [InlineData("set")]
    [InlineData("set", "combat")]
    [InlineData("set", "combat", "false", "extra")]
    [InlineData("frobnicate")]
    public void Config_MalformedCallsGetAUsageError(params string[] args)
    {
        var plugin = LoadedPlugin();
        var arg = Rcon(args);

        plugin.CmdConfig(arg);

        var root = ParseReply(arg);
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("usage", root.GetProperty("err").GetString());
    }

    // ---- settings file ------------------------------------------------------------------------------------

    [Fact]
    public void Settings_ADamagedFileFallsBackToDefaultsRatherThanFailing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, "\0\0garbage\nno equals sign\n=novalue\nrecording=maybe\ncombat");

        var plugin = LoadedPlugin();

        Assert.True(plugin.Settings.Recording);
        Assert.True(plugin.Settings.Combat);
    }

    [Fact]
    public void Settings_UnknownLinesAreIgnoredAndKnownOnesStillApply()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, "future_setting=on\ncombat=false\n");

        var plugin = LoadedPlugin();

        Assert.False(plugin.Settings.Combat);
        Assert.True(plugin.Settings.Recording);
    }

    // ---- JSON writer --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("back\\slash", "\"back\\\\slash\"")]
    [InlineData("line\nbreak", "\"line\\nbreak\"")]
    [InlineData("tab\there", "\"tab\\there\"")]
    public void Json_QuoteEscapesWhatJsonRequires(string input, string expected)
    {
        Assert.Equal(expected, ArchonJson.Quote(input));
    }

    [Fact]
    public void Json_QuoteEscapesControlCharactersAsUnicodeAndKeepsRoundTripsValid()
    {
        var quoted = ArchonJson.Quote("abc");

        Assert.Equal("\"a\\u0001b\\u001fc\"", quoted);
        Assert.Equal("abc", JsonDocument.Parse(quoted).RootElement.GetString());
    }

    [Fact]
    public void Json_QuoteOfNullIsAnEmptyString()
    {
        Assert.Equal("\"\"", ArchonJson.Quote(null));
    }

    [Fact]
    public void Json_ErrorEnvelopeIsValidJsonWithTheCodeAndMessage()
    {
        var root = JsonDocument.Parse(ArchonJson.Err("bad_value", "no \"good\"")).RootElement;

        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("bad_value", root.GetProperty("err").GetString());
        Assert.Equal("no \"good\"", root.GetProperty("message").GetString());
    }
}
