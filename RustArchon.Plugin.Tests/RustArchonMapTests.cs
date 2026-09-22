// Copyright ©2026 Scott Blomfield

using System.Text.Json;
using Oxide.Plugins;
using UnityEngine;
using ArchonPlugin = Oxide.Plugins.RustArchon;

namespace RustArchon.Plugin.Tests;

/// <summary>
/// The map: when a render is allowed (the manual command never with players online unless overridden, never over an existing
/// picture unless asked, never twice at once), that the command replies before the game freezes, what the outcome reports, the
/// automatic render on load (whenever there is no picture yet, whoever is online), the monument list, and the <c>map</c> switch. The world, its monuments and the connected players are
/// stub statics, so these tests run one at a time.
/// </summary>
[Collection("World")]
public sealed class RustArchonMapTests : IDisposable
{
    private const ulong Alice = 76561198000000001UL;

    private readonly List<MonumentInfo> _monuments = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rustarchon-map-tests-" + Guid.NewGuid().ToString("N"));

    public RustArchonMapTests()
    {
        Directory.CreateDirectory(_directory);
        BasePlayer.activePlayerList.Clear();
        World.Size = 4500;
        World.Seed = 1234;
    }

    public void Dispose()
    {
        BasePlayer.activePlayerList.Clear();
        World.Size = 0;
        World.Seed = 0;
        if (Directory.Exists(_directory)) { Directory.Delete(_directory, recursive: true); }
    }

    private string MapFile => Path.Combine(_directory, "map_4500_1234.png");

    /// <summary>A loaded plugin; the game's render command is replaced by <paramref name="render"/> (default: writes the picture).</summary>
    private ArchonPlugin Loaded(bool map = true, Action<string>? render = null)
    {
        var path = Path.Combine(_directory, "RustArchon", "settings.txt");
        if (!map)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "recording=true\ncombat=true\nmap=false\n");
        }

        var plugin = new ArchonPlugin
        {
            SettingsFilePath = path,
            MapDirectory = _directory,
            MonumentSource = () => _monuments,
            RunServerCommand = render ?? (_ => File.WriteAllBytes(MapFile, new byte[2048]))
        };
        typeof(ArchonPlugin).GetMethod("Init", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(plugin, null);
        return plugin;
    }

    private static PluginTimers Timers(ArchonPlugin plugin) =>
        (PluginTimers)typeof(RustPlugin).GetField("timer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(plugin)!;

    private static void Server(ArchonPlugin plugin) =>
        typeof(ArchonPlugin).GetMethod("OnServerInitialized", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(plugin, null);

    private static void Join(ulong id) => BasePlayer.activePlayerList.Add(new BasePlayer { userID = id, displayName = "P" });

    private static JsonElement Reply(Action<ConsoleSystem.Arg> command, params string[] args)
    {
        var arg = ConsoleSystem.Arg.WithArgs(args);
        command(arg);
        return JsonDocument.Parse(Assert.Single(arg.Replies)).RootElement;
    }

    private static JsonElement Render(ArchonPlugin plugin, params string[] args) => Reply(plugin.CmdMapRender, args);
    private static JsonElement Status(ArchonPlugin plugin) => Reply(plugin.CmdMapStatus).GetProperty("data");

    // ---- status --------------------------------------------------------------------------------------------------

    [Fact]
    public void BeforeTheWorldLoadsTheStatusSaysSoAndNamesNoFile()
    {
        World.Size = 0;
        var plugin = Loaded();

        var status = Status(plugin);

        Assert.False(status.GetProperty("world").GetProperty("known").GetBoolean());
        Assert.Equal("", status.GetProperty("file").GetString());
        Assert.False(status.GetProperty("exists").GetBoolean());
    }

    [Fact]
    public void WithAWorldButNoPictureTheStatusNamesTheFileAndSaysItIsMissing()
    {
        var plugin = Loaded();

        var status = Status(plugin);

        Assert.Equal(4500, status.GetProperty("world").GetProperty("size").GetInt32());
        Assert.Equal(1234, status.GetProperty("world").GetProperty("seed").GetInt64());
        Assert.Equal("map_4500_1234.png", status.GetProperty("file").GetString());
        Assert.False(status.GetProperty("exists").GetBoolean());
        Assert.Equal(0, status.GetProperty("bytes").GetInt64());
        Assert.False(status.GetProperty("rendering").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("lastRender").ValueKind);
        Assert.True(status.GetProperty("auto").GetBoolean());
    }

    [Fact]
    public void WithAPictureTheStatusReportsItsSize()
    {
        File.WriteAllBytes(MapFile, new byte[12345]);
        var plugin = Loaded();

        var status = Status(plugin);

        Assert.True(status.GetProperty("exists").GetBoolean());
        Assert.Equal(12345, status.GetProperty("bytes").GetInt64());
    }

    [Fact]
    public void ADifferentWorldMeansADifferentFile()
    {
        File.WriteAllBytes(MapFile, new byte[10]);
        var plugin = Loaded();
        World.Seed = 999;                                         // a wipe with a new seed

        var status = Status(plugin);

        Assert.Equal("map_4500_999.png", status.GetProperty("file").GetString());
        Assert.False(status.GetProperty("exists").GetBoolean());  // the old wipe's picture is not this world's
    }

    [Fact]
    public void TheStatusCommandIsRconOnly()
    {
        var plugin = Loaded();
        var arg = ConsoleSystem.Arg.WithArgs();
        arg.Connection = new object();

        plugin.CmdMapStatus(arg);

        Assert.Empty(arg.Replies);
    }

    // ---- the render command --------------------------------------------------------------------------------------

    [Fact]
    public void ARenderRepliesAtOnceAndOnlyStartsTheGameRenderLater()
    {
        var calls = new List<string>();
        var plugin = Loaded(render: c => { calls.Add(c); File.WriteAllBytes(MapFile, new byte[100]); });

        var reply = Render(plugin);

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.True(reply.GetProperty("data").GetProperty("started").GetBoolean());
        Assert.Equal("map_4500_1234.png", reply.GetProperty("data").GetProperty("file").GetString());
        Assert.Empty(calls);                                                  // nothing has frozen the game yet
        Assert.True(plugin.MapRenderScheduled);
        var once = Assert.Single(Timers(plugin).Once_);
        Assert.Equal(1f, once.Key);                                           // a second later, so the reply gets out first

        once.Value();

        Assert.Equal(["world.rendermap"], calls);
        Assert.False(plugin.MapRenderScheduled);
    }

    [Fact]
    public void ARenderWithPlayersOnlineIsRefusedUnlessOverridden()
    {
        Join(Alice);
        var plugin = Loaded();

        var refused = Render(plugin);
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Equal("players_online", refused.GetProperty("err").GetString());
        Assert.Empty(Timers(plugin).Once_);

        var allowed = Render(plugin, "override");
        Assert.True(allowed.GetProperty("ok").GetBoolean());
        Assert.Single(Timers(plugin).Once_);
    }

    [Fact]
    public void ANonPlayerIsNotCountedAsSomeoneOnline()
    {
        BasePlayer.activePlayerList.Add(new BasePlayer { userID = 12345UL, displayName = "Scientist" });
        var plugin = Loaded();

        Assert.True(Render(plugin).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void ARenderOverAnExistingPictureIsRefusedUnlessOverwriteIsAsked()
    {
        File.WriteAllBytes(MapFile, new byte[10]);
        var plugin = Loaded();

        var refused = Render(plugin);
        Assert.Equal("map_exists", refused.GetProperty("err").GetString());

        Assert.True(Render(plugin, "overwrite").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void BothFlagsMayBeGivenInEitherOrderAndAreCaseInsensitive()
    {
        Join(Alice);
        File.WriteAllBytes(MapFile, new byte[10]);
        var plugin = Loaded();

        Assert.True(Render(plugin, "OVERRIDE", "Overwrite").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void ASecondRenderWhileOneIsPendingIsRefused()
    {
        var plugin = Loaded();
        Assert.True(Render(plugin).GetProperty("ok").GetBoolean());

        var second = Render(plugin, "overwrite", "override");

        Assert.Equal("render_pending", second.GetProperty("err").GetString());
        Assert.Single(Timers(plugin).Once_);
    }

    [Fact]
    public void ARenderBeforeTheWorldHasLoadedIsRefused()
    {
        World.Size = 0;
        var plugin = Loaded();

        Assert.Equal("no_world", Render(plugin, "overwrite", "override").GetProperty("err").GetString());
    }

    [Fact]
    public void AnUnknownWordGetsAUsageErrorAndStartsNothing()
    {
        var plugin = Loaded();

        var reply = Render(plugin, "now");

        Assert.Equal("usage", reply.GetProperty("err").GetString());
        Assert.Empty(Timers(plugin).Once_);
    }

    [Fact]
    public void TheRenderCommandIsRconOnly()
    {
        var plugin = Loaded();
        var arg = ConsoleSystem.Arg.WithArgs();
        arg.Connection = new object();

        plugin.CmdMapRender(arg);

        Assert.Empty(arg.Replies);
        Assert.Empty(Timers(plugin).Once_);
    }

    // ---- the outcome ---------------------------------------------------------------------------------------------

    [Fact]
    public void AFinishedRenderReportsItsSizeAndTime()
    {
        var plugin = Loaded(render: _ => File.WriteAllBytes(MapFile, new byte[4096]));
        Render(plugin);
        Timers(plugin).Once_[0].Value();

        var last = Status(plugin).GetProperty("lastRender");

        Assert.True(last.GetProperty("ok").GetBoolean());
        Assert.Equal(4096, last.GetProperty("bytes").GetInt64());
        Assert.True(last.GetProperty("ms").GetInt64() >= 0);
        Assert.True(Status(plugin).GetProperty("exists").GetBoolean());
    }

    [Fact]
    public void ARenderThatWritesNothingIsReportedAsFailedNotAsSuccess()
    {
        var plugin = Loaded(render: _ => { });
        Render(plugin);
        Timers(plugin).Once_[0].Value();

        var last = Status(plugin).GetProperty("lastRender");

        Assert.False(last.GetProperty("ok").GetBoolean());
        Assert.False(Status(plugin).GetProperty("exists").GetBoolean());
    }

    [Fact]
    public void AnExceptionFromTheGameIsCaughtAndReportedNotThrownIntoTheGameLoop()
    {
        var plugin = Loaded(render: _ => throw new InvalidOperationException("no renderer here"));
        Render(plugin);

        plugin.DoMapRender();

        var last = Status(plugin).GetProperty("lastRender");
        Assert.False(last.GetProperty("ok").GetBoolean());
        Assert.Equal("no renderer here", last.GetProperty("error").GetString());
        Assert.False(plugin.MapRenderScheduled);                    // and a later render is possible again
    }

    // ---- the automatic render ------------------------------------------------------------------------------------

    [Fact]
    public void ANewWorldWithNoPictureIsRenderedAutomaticallyAfterAShortDelay()
    {
        var plugin = Loaded();

        Server(plugin);

        var once = Assert.Single(Timers(plugin).Once_);
        Assert.Equal(10f, once.Key);
        Assert.Contains(plugin.Log, l => l.Contains("rendering it shortly"));
    }

    [Fact]
    public void NothingIsRenderedAutomaticallyWhenAPictureAlreadyExists()
    {
        File.WriteAllBytes(MapFile, new byte[10]);
        var plugin = Loaded();

        Server(plugin);

        Assert.Empty(Timers(plugin).Once_);
    }

    /// <summary>
    /// The rule is "no picture yet, so make it": it does not wait for the server to be empty. (A plugin loaded onto a running server
    /// is the case this is for - that is how a new customer starts.) The guard is on the manual command only.
    /// </summary>
    [Fact]
    public void AMissingPictureIsRenderedAutomaticallyEvenWithPlayersOnline()
    {
        Join(Alice);
        var plugin = Loaded();

        Server(plugin);

        var once = Assert.Single(Timers(plugin).Once_);
        Assert.Equal(10f, once.Key);
    }

    [Fact]
    public void TheAutomaticRenderStillRunsTheGameCommandOnceItsTimerFiresWithPlayersOnline()
    {
        Join(Alice);
        var calls = new List<string>();
        var plugin = Loaded(render: c => { calls.Add(c); File.WriteAllBytes(MapFile, new byte[100]); });
        Server(plugin);

        Assert.Single(Timers(plugin).Once_).Value();

        Assert.Equal(["world.rendermap"], calls);
    }

    [Fact]
    public void NothingIsRenderedAutomaticallyWhenTheSwitchIsOff()
    {
        var plugin = Loaded(map: false);

        Server(plugin);

        Assert.Empty(Timers(plugin).Once_);
    }

    [Fact]
    public void NothingIsRenderedAutomaticallyBeforeTheWorldHasLoaded()
    {
        World.Size = 0;
        var plugin = Loaded();

        Server(plugin);

        Assert.Empty(Timers(plugin).Once_);
    }

    [Fact]
    public void AutomaticRenderingCannotDoubleUpWithAManualOne()
    {
        var plugin = Loaded();
        Render(plugin);

        Server(plugin);

        Assert.Single(Timers(plugin).Once_);
    }

    // ---- the switch ----------------------------------------------------------------------------------------------

    [Fact]
    public void TheMapSwitchIsOnByDefaultAndCanBeSetAndPersisted()
    {
        var plugin = Loaded();
        Assert.True(plugin.Settings.Map);

        var arg = ConsoleSystem.Arg.WithArgs("set", "map", "false");
        plugin.CmdConfig(arg);

        var data = JsonDocument.Parse(Assert.Single(arg.Replies)).RootElement.GetProperty("data");
        Assert.False(data.GetProperty("map").GetBoolean());
        Assert.False(ArchonSettings.Load(plugin.SettingsFilePath).Map);
        Assert.Contains("map=false", File.ReadAllText(plugin.SettingsFilePath));
    }

    [Fact]
    public void AFileWrittenByAnOlderVersionWithoutTheKeyLeavesTheMapSwitchOn()
    {
        var path = Path.Combine(_directory, "old.txt");
        File.WriteAllText(path, "recording=false\ncombat=true\n");

        var settings = ArchonSettings.Load(path);

        Assert.True(settings.Map);
        Assert.False(settings.Recording);
    }

    [Fact]
    public void TheUsageMessageAndUnknownKeyMessageNameTheMapKey()
    {
        var plugin = Loaded();
        var bad = ConsoleSystem.Arg.WithArgs("set", "maps", "true");

        plugin.CmdConfig(bad);

        Assert.Contains("recording, combat or map", Assert.Single(bad.Replies));
    }

    [Fact]
    public void TheMapCapabilityIsReported()
    {
        var plugin = Loaded();
        var hello = ConsoleSystem.Arg.WithArgs();
        plugin.CmdHello(hello);

        var capabilities = JsonDocument.Parse(hello.Replies.Single()).RootElement.GetProperty("data").GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).ToArray();

        Assert.Contains("map", capabilities);
    }

    // ---- monuments -----------------------------------------------------------------------------------------------

    private static MonumentInfo Monument(string? name, float x, float y, float z, bool onMap = true)
    {
        var monument = new MonumentInfo { shouldDisplayOnMap = onMap, displayPhrase = name is null ? null : new Phrase { english = name } };
        monument.transform.position = new Vector3(x, y, z);
        return monument;
    }

    private static JsonElement Monuments(ArchonPlugin plugin) => Reply(plugin.CmdMapMonuments).GetProperty("data");

    [Fact]
    public void MonumentsAreListedWithTheirNameAndPosition()
    {
        _monuments.Add(Monument("Launch Site", 1200.44f, 30f, -900.06f));
        var plugin = Loaded();

        var data = Monuments(plugin);

        Assert.Equal(1, data.GetProperty("format").GetInt32());
        Assert.Equal(4500, data.GetProperty("size").GetInt32());
        var m = Assert.Single(data.GetProperty("monuments").EnumerateArray().ToList());
        Assert.Equal("Launch Site", m.GetProperty("n").GetString());
        Assert.Equal(1200.4, m.GetProperty("x").GetDouble(), 1);
        Assert.Equal(30.0, m.GetProperty("y").GetDouble(), 1);
        Assert.Equal(-900.1, m.GetProperty("z").GetDouble(), 1);
    }

    [Fact]
    public void HiddenUnnamedAndNullEntriesAreLeftOut()
    {
        _monuments.Add(Monument("Airfield", 1, 2, 3));
        _monuments.Add(Monument("Tunnel entrance", 4, 5, 6, onMap: false));
        _monuments.Add(Monument("", 7, 8, 9));
        _monuments.Add(Monument(null, 7, 8, 9));
        _monuments.Add(null!);
        var plugin = Loaded();

        var names = Monuments(plugin).GetProperty("monuments").EnumerateArray().Select(m => m.GetProperty("n").GetString()).ToArray();

        Assert.Equal(["Airfield"], names);
    }

    [Fact]
    public void NoMonumentsYetGivesAnEmptyListNotAnError()
    {
        var plugin = Loaded();

        Assert.Equal(0, Monuments(plugin).GetProperty("monuments").GetArrayLength());
    }

    [Fact]
    public void AMonumentSearchThatFailsIsAnErrorReplyNotAnExceptionInTheGame()
    {
        var plugin = Loaded();
        plugin.MonumentSource = () => throw new InvalidOperationException("no scene");
        var arg = ConsoleSystem.Arg.WithArgs();

        plugin.CmdMapMonuments(arg);

        Assert.Contains("unavailable", Assert.Single(arg.Replies));
    }

    [Fact]
    public void HostileMonumentNamesStillProduceValidJson()
    {
        var hostile = "\"}]}" + (char)0 + "\n\\ " + (char)0x2028 + " x";
        _monuments.Add(Monument(hostile, 1, 2, 3));
        var plugin = Loaded();

        Assert.Equal(hostile, Monuments(plugin).GetProperty("monuments")[0].GetProperty("n").GetString());
    }

    [Fact]
    public void TheMonumentsCommandIsRconOnly()
    {
        var plugin = Loaded();
        var arg = ConsoleSystem.Arg.WithArgs();
        arg.Connection = new object();

        plugin.CmdMapMonuments(arg);

        Assert.Empty(arg.Replies);
    }
}
