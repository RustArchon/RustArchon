// Copyright ©2026 Scott Blomfield

using System.Text.Json;
using Oxide.Plugins;
using UnityEngine;
using ArchonPlugin = Oxide.Plugins.RustArchon;

namespace RustArchon.Plugin.Tests;

/// <summary>
/// The position recorder: who is sampled (real, connected players only), when (only when they moved, plus a heartbeat),
/// the on/off markers at the edges of a session, the bounded ring and its drain command, and that everything follows the
/// Recording switch. The connected players are the stub <see cref="BasePlayer.activePlayerList"/>, which is shared, so
/// these tests run one at a time.
/// </summary>
[Collection("World")]
public sealed class RustArchonPositionTests : IDisposable
{
    private const ulong Alice = 76561198000000001UL;
    private const ulong Bob = 76561198000000002UL;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rustarchon-position-tests-" + Guid.NewGuid().ToString("N"));
    private long _now = 1_800_000_000_000L;

    public RustArchonPositionTests() => BasePlayer.activePlayerList.Clear();

    public void Dispose()
    {
        BasePlayer.activePlayerList.Clear();
        if (Directory.Exists(_directory)) { Directory.Delete(_directory, recursive: true); }
    }

    private ArchonPlugin Loaded(bool recording = true)
    {
        var path = Path.Combine(_directory, "RustArchon", "settings.txt");
        if (!recording)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "recording=false\ncombat=true\n");
        }

        var plugin = new ArchonPlugin { SettingsFilePath = path, NowMs = () => _now };
        typeof(ArchonPlugin).GetMethod("Init", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(plugin, null);
        return plugin;
    }

    private static BasePlayer Join(ulong id, string name, float x = 0, float y = 0, float z = 0, float yaw = 0)
    {
        var player = new BasePlayer { userID = id, displayName = name, ShortPrefabName = "player" };
        player.transform.position = new Vector3(x, y, z);
        player.eyes.rotation = new Quaternion { eulerAngles = new Vector3(0, yaw, 0) };
        BasePlayer.activePlayerList.Add(player);
        return player;
    }

    private static void Leave(ArchonPlugin plugin, BasePlayer player)
    {
        BasePlayer.activePlayerList.Remove(player);
        typeof(ArchonPlugin).GetMethod("OnPlayerDisconnected", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(plugin, [player, "Disconnected"]);
    }

    private static void Move(BasePlayer player, float x, float y, float z) => player.transform.position = new Vector3(x, y, z);

    private static JsonElement Drain(ArchonPlugin plugin, long bootId, long cursor, int? max = null)
    {
        var arg = max is null
            ? ConsoleSystem.Arg.WithArgs(bootId.ToString(), cursor.ToString())
            : ConsoleSystem.Arg.WithArgs(bootId.ToString(), cursor.ToString(), max.ToString()!);
        plugin.CmdPositionsDrain(arg);
        return JsonDocument.Parse(Assert.Single(arg.Replies)).RootElement.GetProperty("data");
    }

    private static List<JsonElement> Samples(ArchonPlugin plugin) =>
        Drain(plugin, plugin.Positions.BootId, 0).GetProperty("samples").EnumerateArray().Select(e => e.Clone()).ToList();

    // ---- what is sampled -----------------------------------------------------------------------------------------

    [Fact]
    public void ARealConnectedPlayerIsSampledWithPositionYawAndNameOnTheFirstLook()
    {
        var plugin = Loaded();
        Join(Alice, "Alice", 10.24f, 30.5f, -40.96f, yaw: 90f);

        plugin.SamplePositions();

        var s = Assert.Single(Samples(plugin));
        Assert.Equal(Alice.ToString(), s.GetProperty("p").GetString());
        Assert.Equal(10.2, s.GetProperty("x").GetDouble(), 1);
        Assert.Equal(30.5, s.GetProperty("y").GetDouble(), 1);
        Assert.Equal(-41.0, s.GetProperty("z").GetDouble(), 1);
        Assert.Equal(90, s.GetProperty("r").GetInt32());
        Assert.Equal("Alice", s.GetProperty("n").GetString());
        Assert.Equal("on", s.GetProperty("e").GetString());
        Assert.Equal(_now, s.GetProperty("t").GetInt64());
    }

    [Fact]
    public void ANpcWithoutARealSteamIdIsNeverSampled()
    {
        var plugin = Loaded();
        Join(12345UL, "Scientist");
        Join(Alice, "Alice");

        plugin.SamplePositions();

        Assert.Equal(Alice.ToString(), Assert.Single(Samples(plugin)).GetProperty("p").GetString());
    }

    [Fact]
    public void ANullEntryInTheListIsSkippedNotFatal()
    {
        var plugin = Loaded();
        BasePlayer.activePlayerList.Add(null!);
        Join(Alice, "Alice");

        plugin.SamplePositions();

        Assert.Single(Samples(plugin));
    }

    [Fact]
    public void AnIdlePlayerIsNotSampledAgainUntilTheHeartbeat()
    {
        var plugin = Loaded();
        Join(Alice, "Alice", 5, 5, 5);
        plugin.SamplePositions();

        _now += 5000; plugin.SamplePositions();
        _now += 5000; plugin.SamplePositions();
        Assert.Single(Samples(plugin));                       // nothing new: they have not moved

        _now += 50000;                                       // a minute since the first sample
        plugin.SamplePositions();

        var all = Samples(plugin);
        Assert.Equal(2, all.Count);
        Assert.Equal("Alice", all[1].GetProperty("n").GetString());     // a heartbeat repeats the name
        Assert.False(all[1].TryGetProperty("e", out _));                // and is not another "on"
    }

    [Fact]
    public void AMovingPlayerIsSampledEachTimeTheyHaveMovedFarEnough()
    {
        var plugin = Loaded();
        var alice = Join(Alice, "Alice", 0, 0, 0);
        plugin.SamplePositions();

        _now += 5000; Move(alice, 0.2f, 0, 0); plugin.SamplePositions();   // a shuffle: under the threshold
        _now += 5000; Move(alice, 3f, 0, 0); plugin.SamplePositions();     // a real step
        _now += 5000; Move(alice, 3f, 4f, 0); plugin.SamplePositions();    // straight up is movement too

        var all = Samples(plugin);
        Assert.Equal(3, all.Count);
        Assert.Equal(3.0, all[1].GetProperty("x").GetDouble(), 1);
        Assert.False(all[1].TryGetProperty("n", out _));                // a plain movement sample carries no name
    }

    [Fact]
    public void EachPlayerIsTrackedOnTheirOwn()
    {
        var plugin = Loaded();
        var alice = Join(Alice, "Alice", 0, 0, 0);
        Join(Bob, "Bob", 100, 0, 0);
        plugin.SamplePositions();

        _now += 5000; Move(alice, 10, 0, 0); plugin.SamplePositions();     // only Alice moved

        var all = Samples(plugin);
        Assert.Equal(3, all.Count);
        Assert.Equal(Alice.ToString(), all[2].GetProperty("p").GetString());
    }

    [Fact]
    public void LeavingRecordsTheFinalPositionEvenIfTheyDidNotMoveAndStartsThemOverOnReturn()
    {
        var plugin = Loaded();
        var alice = Join(Alice, "Alice", 7, 7, 7);
        plugin.SamplePositions();

        Leave(plugin, alice);
        _now += 5000;
        BasePlayer.activePlayerList.Add(alice);                              // back again, same spot
        plugin.SamplePositions();

        var all = Samples(plugin);
        Assert.Equal(["on", "off", "on"], all.Select(s => s.GetProperty("e").GetString()).ToArray());
        Assert.Equal("Alice", all[1].GetProperty("n").GetString());
    }

    [Fact]
    public void ANpcLeavingIsNotRecorded()
    {
        var plugin = Loaded();
        var npc = Join(12345UL, "Scientist");

        Leave(plugin, npc);

        Assert.Empty(Samples(plugin));
    }

    [Fact]
    public void APlayerWithNoEyesYetIsRecordedFacingZeroNotFatal()
    {
        var plugin = Loaded();
        var alice = Join(Alice, "Alice", yaw: 123f);
        alice.eyes = null!;

        plugin.SamplePositions();

        Assert.Equal(0, Assert.Single(Samples(plugin)).GetProperty("r").GetInt32());
    }

    [Fact]
    public void AMissingNameIsWrittenAsNothingNotAsNull()
    {
        var plugin = Loaded();
        var alice = Join(Alice, null!);

        plugin.SamplePositions();

        Assert.False(Assert.Single(Samples(plugin)).TryGetProperty("n", out _));
    }

    [Fact]
    public void YawIsWrappedIntoZeroTo359AndBadNumbersBecomeZero()
    {
        var plugin = Loaded();
        var a = Join(Alice, "A", 0, 0, 0, yaw: -90f);
        var b = Join(Bob, "B", 0, 0, 0, yaw: 725f);
        a.transform.position = new Vector3(float.NaN, float.PositiveInfinity, 0);

        plugin.SamplePositions();

        var all = Samples(plugin);
        Assert.Equal(270, all[0].GetProperty("r").GetInt32());
        Assert.Equal(5, all[1].GetProperty("r").GetInt32());
        Assert.Equal(0, all[0].GetProperty("x").GetDouble());
        Assert.Equal(0, all[0].GetProperty("y").GetDouble());
    }

    [Fact]
    public void HostileNamesStillProduceValidJson()
    {
        var plugin = Loaded();
        var hostile = "\"}]}" + (char)0 + "\n\\ " + (char)0x2028 + " x";
        Join(Alice, hostile);

        plugin.SamplePositions();

        Assert.Equal(hostile, Assert.Single(Samples(plugin)).GetProperty("n").GetString());
    }

    // ---- the Recording switch ------------------------------------------------------------------------------------

    [Fact]
    public void WithRecordingOnTheDisconnectHookIsSubscribedAndRecordingRuns()
    {
        var plugin = Loaded(recording: true);

        Assert.Contains("+OnPlayerDisconnected", plugin.Subscriptions);
        Assert.True(plugin.PositionsRecording);
    }

    [Fact]
    public void WithRecordingOffNothingIsSubscribedAndALookRecordsNothing()
    {
        var plugin = Loaded(recording: false);
        Join(Alice, "Alice");

        plugin.SamplePositions();

        Assert.DoesNotContain(plugin.Subscriptions, s => s.Contains("OnPlayerDisconnected"));
        Assert.False(plugin.PositionsRecording);
        Assert.Empty(Samples(plugin));
        Assert.Equal(0, plugin.PositionSweeps);
    }

    [Fact]
    public void WithRecordingOffNoTimerIsLeftRunningAndTurningItOffStopsTheSampler()
    {
        var off = Loaded(recording: false);
        Assert.Empty(Timers(off).Started);

        var on = Loaded(recording: true);
        on.CmdConfig(ConsoleSystem.Arg.WithArgs("set", "recording", "false"));

        Assert.All(Timers(on).Started, t => Assert.True(t.Destroyed));
    }

    private static PluginTimers Timers(ArchonPlugin plugin) =>
        (PluginTimers)typeof(RustPlugin).GetField("timer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(plugin)!;

    [Fact]
    public void TurningRecordingOffStopsItAndOnStartsItAgainFromScratch()
    {
        var plugin = Loaded();
        Join(Alice, "Alice");
        plugin.SamplePositions();
        plugin.Subscriptions.Clear();

        plugin.CmdConfig(ConsoleSystem.Arg.WithArgs("set", "recording", "false"));
        _now += 5000;
        plugin.SamplePositions();

        Assert.Contains("-OnPlayerDisconnected", plugin.Subscriptions);
        Assert.Single(Samples(plugin));                                       // nothing added while off

        plugin.CmdConfig(ConsoleSystem.Arg.WithArgs("set", "recording", "true"));
        _now += 5000;
        plugin.SamplePositions();

        var all = Samples(plugin);
        Assert.Equal(2, all.Count);
        Assert.Equal("on", all[1].GetProperty("e").GetString());               // forgotten while off, so "on" again
    }

    [Fact]
    public void SettingRecordingToItsCurrentValueDoesNotResubscribe()
    {
        var plugin = Loaded();
        plugin.Subscriptions.Clear();

        plugin.CmdConfig(ConsoleSystem.Arg.WithArgs("set", "recording", "true"));

        Assert.DoesNotContain(plugin.Subscriptions, s => s.Contains("OnPlayerDisconnected"));
    }

    // ---- the ring and the drain command --------------------------------------------------------------------------

    [Fact]
    public void DrainingReturnsSamplesAfterTheCursorAndTheCursorAdvances()
    {
        var plugin = Loaded();
        var alice = Join(Alice, "Alice", 0, 0, 0);
        plugin.SamplePositions();
        _now += 5000; Move(alice, 5, 0, 0); plugin.SamplePositions();
        _now += 5000; Move(alice, 10, 0, 0); plugin.SamplePositions();
        var boot = plugin.Positions.BootId;

        var first = Drain(plugin, boot, 0, max: 2);
        var second = Drain(plugin, boot, first.GetProperty("cursor").GetInt64());

        Assert.Equal(2, first.GetProperty("samples").GetArrayLength());
        Assert.Equal(2, first.GetProperty("cursor").GetInt64());
        Assert.Equal(3, first.GetProperty("head").GetInt64());
        Assert.Equal(1, second.GetProperty("samples").GetArrayLength());
        Assert.Equal(3, second.GetProperty("cursor").GetInt64());
        Assert.False(second.GetProperty("lost").GetBoolean());
        Assert.False(second.GetProperty("reset").GetBoolean());
    }

    [Fact]
    public void ADifferentBootIdMeansTheCallerMustStartOver()
    {
        var plugin = Loaded();
        Join(Alice, "Alice");
        plugin.SamplePositions();

        var data = Drain(plugin, plugin.Positions.BootId + 1, 99);

        Assert.True(data.GetProperty("reset").GetBoolean());
        Assert.Equal(1, data.GetProperty("samples").GetArrayLength());
    }

    [Fact]
    public void ARingThatWrappedTellsTheCallerItLostSamples()
    {
        var ring = new ArchonPositions.Buffer(3);
        for (var i = 0; i < 5; i++) { ring.Add(new ArchonPositions.Sample { UnixMs = i, PlayerId = "1" }); }

        var data = JsonDocument.Parse(ring.DrainJson(ring.BootId, 0, 10)).RootElement;

        Assert.True(data.GetProperty("lost").GetBoolean());
        Assert.Equal([3L, 4L, 5L], data.GetProperty("samples").EnumerateArray().Select(e => e.GetProperty("s").GetInt64()).ToArray());
        Assert.Equal(3, ring.Count);
    }

    [Fact]
    public void ABadDrainCommandGetsAUsageErrorNotAnException()
    {
        var plugin = Loaded();
        var arg = ConsoleSystem.Arg.WithArgs("x");

        plugin.CmdPositionsDrain(arg);

        Assert.Contains("usage", Assert.Single(arg.Replies));
    }

    [Fact]
    public void TheDrainCommandIsRconOnly()
    {
        var plugin = Loaded();
        var arg = ConsoleSystem.Arg.WithArgs("1", "0");
        arg.Connection = new object();

        plugin.CmdPositionsDrain(arg);

        Assert.Empty(arg.Replies);
    }

    [Fact]
    public void OneReplyNeverGrowsPastTheFrameLimitWhateverTheNames()
    {
        var ring = new ArchonPositions.Buffer(2000);
        for (var i = 0; i < 1000; i++) { ring.Add(new ArchonPositions.Sample { UnixMs = i, PlayerId = "76561198000000001", Name = new string('n', 90) }); }

        var reply = ring.DrainJson(ring.BootId, 0, 500);

        Assert.True(reply.Length < ArchonCombat.MaxReplyChars + 2000);
        Assert.True(JsonDocument.Parse(reply).RootElement.GetProperty("samples").GetArrayLength() < 500);
    }

    // ---- reporting -----------------------------------------------------------------------------------------------

    [Fact]
    public void ThePositionsCapabilityIsReportedAndStatsCountTheSweeps()
    {
        var plugin = Loaded();
        Join(Alice, "Alice");
        plugin.SamplePositions();
        var hello = ConsoleSystem.Arg.WithArgs();
        plugin.CmdHello(hello);
        var stats = ConsoleSystem.Arg.WithArgs();
        plugin.CmdStats(stats);

        var capabilities = JsonDocument.Parse(hello.Replies.Single()).RootElement.GetProperty("data").GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).ToArray();
        var positions = JsonDocument.Parse(stats.Replies.Single()).RootElement.GetProperty("data").GetProperty("positions");

        Assert.Contains("positions", capabilities);
        Assert.True(positions.GetProperty("recording").GetBoolean());
        Assert.Equal(1, positions.GetProperty("sweeps").GetInt64());
        Assert.Equal(1, positions.GetProperty("sampled").GetInt64());
        Assert.Equal(1, positions.GetProperty("tracked").GetInt32());
    }

    [Fact]
    public void ThePlayerTableStaysBoundedIfLogoutsAreMissed()
    {
        var plugin = Loaded();
        for (var i = 0; i < ArchonPositions.MaxTracked + 10; i++) { Join(Alice + (ulong)i, "P" + i); }

        plugin.SamplePositions();

        var stats = ConsoleSystem.Arg.WithArgs();
        plugin.CmdStats(stats);
        var tracked = JsonDocument.Parse(stats.Replies.Single()).RootElement.GetProperty("data").GetProperty("positions").GetProperty("tracked").GetInt32();
        Assert.True(tracked <= ArchonPositions.MaxTracked);
    }
}
