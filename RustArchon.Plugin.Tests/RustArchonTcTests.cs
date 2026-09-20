// Copyright ©2026 Scott Blomfield

using System.Text.Json;
using Oxide.Plugins;
using UnityEngine;
using ArchonPlugin = Oxide.Plugins.RustArchon;

namespace RustArchon.Plugin.Tests;

/// <summary>
/// The tool cupboard index: who is indexed (only a real player's cupboard), how it stays current (spawn and kill hooks,
/// and a scan of the existing world that is spread over many steps and survives the world changing under it), what
/// <c>archon.tcs</c> reports, and that everything follows the Recording switch. The world is the stub
/// <see cref="BaseNetworkable.serverEntities"/>, which is shared, so these tests run one at a time.
/// </summary>
[Collection("World")]
public sealed class RustArchonTcTests : IDisposable
{
    private const ulong Alice = 76561198000000001UL;
    private const ulong Bob = 76561198000000002UL;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rustarchon-tc-tests-" + Guid.NewGuid().ToString("N"));

    public RustArchonTcTests()
    {
        BaseNetworkable.serverEntities.All.Clear();
        BasePlayer.InWorld.Clear();
    }

    public void Dispose()
    {
        BaseNetworkable.serverEntities.All.Clear();
        BasePlayer.InWorld.Clear();
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

        var plugin = new ArchonPlugin { SettingsFilePath = path };
        typeof(ArchonPlugin).GetMethod("Init", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(plugin, null);
        return plugin;
    }

    /// <summary>A cupboard. Each authorized player with a non-empty name is also in the world under that name (the game keeps only ids).</summary>
    private static BuildingPrivlidge Tc(ulong owner, float x = 0, float y = 0, float z = 0, params (ulong Id, string Name)[] authorized)
    {
        var tc = new BuildingPrivlidge { OwnerID = owner, ShortPrefabName = "cupboard.tool.deployed" };
        tc.transform.position = new Vector3(x, y, z);
        foreach (var (id, name) in authorized)
        {
            tc.authorizedPlayers.Add(id);
            if (!string.IsNullOrEmpty(name)) { BasePlayer.InWorld[id] = new BasePlayer { userID = id, displayName = name }; }
        }
        return tc;
    }

    private static void Spawn(ArchonPlugin plugin, BaseNetworkable entity) =>
        typeof(ArchonPlugin).GetMethod("OnEntitySpawned", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(plugin, [entity]);

    private static void Kill(ArchonPlugin plugin, BaseNetworkable entity) =>
        typeof(ArchonPlugin).GetMethod("OnEntityKill", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(plugin, [entity]);

    /// <summary>Runs the initial scan to its end.</summary>
    private static void FinishScan(ArchonPlugin plugin)
    {
        for (var i = 0; i < 1000 && plugin.TcScanRunning; i++) { plugin.ScanSlice(); }
    }

    private static JsonElement Page(ArchonPlugin plugin, params string[] args)
    {
        var arg = ConsoleSystem.Arg.WithArgs(args);
        plugin.CmdTcs(arg);
        return JsonDocument.Parse(Assert.Single(arg.Replies)).RootElement.GetProperty("data");
    }

    // ---- switching on and off with Recording --------------------------------------------------------------------

    [Fact]
    public void WithRecordingOnTheSpawnAndKillHooksAreSubscribedAndAScanBegins()
    {
        var plugin = Loaded(recording: true);

        Assert.Contains("+OnEntitySpawned", plugin.Subscriptions);
        Assert.Contains("+OnEntityKill", plugin.Subscriptions);
        Assert.True(plugin.TcScanRunning);
    }

    [Fact]
    public void WithRecordingOffNothingIsSubscribedAndNothingIsScanned()
    {
        var plugin = Loaded(recording: false);

        Assert.DoesNotContain(plugin.Subscriptions, s => s.Contains("OnEntitySpawned") || s.Contains("OnEntityKill"));
        Assert.False(plugin.TcScanRunning);
        Assert.False(plugin.TcSubscribed);
    }

    [Fact]
    public void TurningRecordingOffUnsubscribesStopsTheScanAndEmptiesTheIndex()
    {
        BaseNetworkable.serverEntities.All.Add(Tc(Alice));
        var plugin = Loaded();
        FinishScan(plugin);
        Assert.Equal(1, plugin.TcIndex.Count);
        plugin.Subscriptions.Clear();

        plugin.CmdConfig(ConsoleSystem.Arg.WithArgs("set", "recording", "false"));

        Assert.Contains("-OnEntitySpawned", plugin.Subscriptions);
        Assert.Contains("-OnEntityKill", plugin.Subscriptions);
        Assert.Equal(0, plugin.TcIndex.Count);
        Assert.False(plugin.TcIndex.Ready);
        Assert.False(plugin.TcScanRunning);
    }

    [Fact]
    public void TurningRecordingBackOnScansAgainFromScratch()
    {
        BaseNetworkable.serverEntities.All.Add(Tc(Alice));
        var plugin = Loaded();
        FinishScan(plugin);
        plugin.CmdConfig(ConsoleSystem.Arg.WithArgs("set", "recording", "false"));

        plugin.CmdConfig(ConsoleSystem.Arg.WithArgs("set", "recording", "true"));

        Assert.True(plugin.TcScanRunning);
        Assert.False(plugin.TcIndex.Ready);           // until it has finished again
        FinishScan(plugin);
        Assert.True(plugin.TcIndex.Ready);
        Assert.Equal(1, plugin.TcIndex.Count);
    }

    [Fact]
    public void SettingRecordingToWhatItAlreadyIsDoesNotRestartTheScan()
    {
        BaseNetworkable.serverEntities.All.Add(Tc(Alice));
        var plugin = Loaded();
        FinishScan(plugin);
        plugin.Subscriptions.Clear();

        plugin.CmdConfig(ConsoleSystem.Arg.WithArgs("set", "recording", "true"));

        Assert.Empty(plugin.Subscriptions);
        Assert.False(plugin.TcScanRunning);
        Assert.Equal(1, plugin.TcIndex.Count);
    }

    [Fact]
    public void TheCombatSwitchDoesNotTouchTheCupboardIndex()
    {
        BaseNetworkable.serverEntities.All.Add(Tc(Alice));
        var plugin = Loaded();
        FinishScan(plugin);

        plugin.CmdConfig(ConsoleSystem.Arg.WithArgs("set", "combat", "false"));

        Assert.Equal(1, plugin.TcIndex.Count);
        Assert.True(plugin.TcSubscribed);
    }

    // ---- the initial scan ---------------------------------------------------------------------------------------

    [Fact]
    public void TheScanFindsOnlyPlayerOwnedCupboardsAmongEverythingElseInTheWorld()
    {
        var world = BaseNetworkable.serverEntities.All;
        world.Add(new BaseEntity { ShortPrefabName = "tree" });
        world.Add(Tc(Alice));
        world.Add(Tc(0));                                  // a monument's cupboard: no owner
        world.Add(Tc(4242));                               // owned by an NPC id, below the SteamID range
        world.Add(new BasePlayer { userID = Bob });
        world.Add(Tc(Bob));
        var plugin = Loaded();

        FinishScan(plugin);

        Assert.Equal(2, plugin.TcIndex.Count);
        Assert.True(plugin.TcIndex.Ready);
        Assert.False(plugin.TcScanRunning);
        Assert.Equal(6, plugin.TcScanned);
    }

    [Fact]
    public void ABigWorldTakesSeveralStepsAndIsNotReadyUntilTheLastOne()
    {
        var world = BaseNetworkable.serverEntities.All;
        for (var i = 0; i < ArchonPlugin.TcScanBatch * 2 + 500; i++) { world.Add(new BaseEntity()); }
        world.Add(Tc(Alice));
        var plugin = Loaded();

        plugin.ScanSlice();
        Assert.False(plugin.TcIndex.Ready);
        Assert.True(plugin.TcScanRunning);
        plugin.ScanSlice();
        Assert.False(plugin.TcIndex.Ready);          // two full batches, and the world is not exhausted yet
        plugin.ScanSlice();                          // the last 501 entities: a short batch means the end was reached

        Assert.True(plugin.TcIndex.Ready);
        Assert.False(plugin.TcScanRunning);
        Assert.Equal(1, plugin.TcIndex.Count);
        Assert.Equal(ArchonPlugin.TcScanBatch * 2 + 501, plugin.TcScanned);
    }

    [Fact]
    public void AStepNeverLooksAtMoreThanItsBatch()
    {
        var world = BaseNetworkable.serverEntities.All;
        for (var i = 0; i < ArchonPlugin.TcScanBatch * 3; i++) { world.Add(new BaseEntity()); }
        var plugin = Loaded();

        plugin.ScanSlice();

        Assert.True(plugin.TcScanned <= ArchonPlugin.TcScanBatch);
    }

    [Fact]
    public void AnEmptyWorldIsReadyAtTheFirstStep()
    {
        var plugin = Loaded();

        plugin.ScanSlice();

        Assert.True(plugin.TcIndex.Ready);
        Assert.Equal(0, plugin.TcIndex.Count);
    }

    [Fact]
    public void WhenTheWorldChangesUnderTheScanItStartsOverAndStillFindsEverything()
    {
        var world = BaseNetworkable.serverEntities.All;
        for (var i = 0; i < ArchonPlugin.TcScanBatch + 100; i++) { world.Add(new BaseEntity()); }
        world.Add(Tc(Alice));
        var plugin = Loaded();
        plugin.ScanSlice();                 // half way through
        world.Add(Tc(Bob));                 // something spawned between two steps: the enumerator is now invalid

        FinishScan(plugin);

        Assert.True(plugin.TcIndex.Ready);
        Assert.Equal(2, plugin.TcIndex.Count);
    }

    [Fact]
    public void AWorldThatKeepsChangingIsGivenUpOnRatherThanScannedForever()
    {
        var world = BaseNetworkable.serverEntities.All;
        for (var i = 0; i < ArchonPlugin.TcScanBatch * 2; i++) { world.Add(new BaseEntity()); }
        var plugin = Loaded();

        for (var i = 0; i < 20 && plugin.TcScanRunning; i++)
        {
            plugin.ScanSlice();
            world.Add(new BaseEntity());    // changes again before every step
        }

        Assert.False(plugin.TcScanRunning);   // gave up
        Assert.False(plugin.TcIndex.Ready);   // and does not claim to be complete
    }

    [Fact]
    public void AScanThatFinishedDestroysItsTimerSoNothingRunsBetweenCommands()
    {
        var plugin = Loaded();

        FinishScan(plugin);

        // The scan's own timer is gone; the only one left running is the position sampler, which Recording keeps on by design.
        Assert.Single(GetTimers(plugin).Started, t => !t.Destroyed);
    }

    private static PluginTimers GetTimers(ArchonPlugin plugin) =>
        (PluginTimers)typeof(RustPlugin).GetField("timer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(plugin)!;

    // ---- keeping it current -------------------------------------------------------------------------------------

    [Fact]
    public void ANewPlayerCupboardIsIndexedTheMomentItSpawns()
    {
        var plugin = Loaded();
        FinishScan(plugin);

        Spawn(plugin, Tc(Alice, 10, 20, 30));

        Assert.Equal(1, plugin.TcIndex.Count);
    }

    [Fact]
    public void ANpcCupboardAndOtherEntitiesAreNeverIndexedWhenTheySpawn()
    {
        var plugin = Loaded();
        FinishScan(plugin);

        Spawn(plugin, Tc(0));
        Spawn(plugin, Tc(4242));
        Spawn(plugin, new BaseEntity { ShortPrefabName = "wall" });
        Spawn(plugin, new BasePlayer { userID = Alice });

        Assert.Equal(0, plugin.TcIndex.Count);
    }

    [Fact]
    public void ADestroyedCupboardIsDroppedWhenItIsKilled()
    {
        var plugin = Loaded();
        FinishScan(plugin);
        var tc = Tc(Alice);
        Spawn(plugin, tc);

        Kill(plugin, tc);

        Assert.Equal(0, plugin.TcIndex.Count);
    }

    [Fact]
    public void KillingSomethingNeverIndexedIsHarmless()
    {
        var plugin = Loaded();
        FinishScan(plugin);

        var ex = Record.Exception(() =>
        {
            Kill(plugin, Tc(Alice));
            Kill(plugin, new BaseEntity());
            Kill(plugin, null!);
        });

        Assert.Null(ex);
    }

    [Fact]
    public void ACupboardSeenByBothTheScanAndTheSpawnHookIsCountedOnce()
    {
        var tc = Tc(Alice);
        BaseNetworkable.serverEntities.All.Add(tc);
        var plugin = Loaded();

        Spawn(plugin, tc);
        FinishScan(plugin);
        Spawn(plugin, tc);

        Assert.Equal(1, plugin.TcIndex.Count);
    }

    [Fact]
    public void ANullSpawnIsIgnored()
    {
        var plugin = Loaded();

        Assert.Null(Record.Exception(() => Spawn(plugin, null!)));
    }

    // ---- archon.tcs ---------------------------------------------------------------------------------------------

    [Fact]
    public void EachCupboardIsReportedWithPositionOwnerAndWhoIsAuthorized()
    {
        var plugin = Loaded();
        FinishScan(plugin);
        Spawn(plugin, Tc(Alice, 12.34f, 5.6f, -7.89f, (Alice, "Alice"), (Bob, "Bob \"the\" Builder")));

        var data = Page(plugin);

        var tc = Assert.Single(data.GetProperty("tcs").EnumerateArray().ToList());
        Assert.Equal(12.3, tc.GetProperty("x").GetDouble(), 1);
        Assert.Equal(5.6, tc.GetProperty("y").GetDouble(), 1);
        Assert.Equal(-7.9, tc.GetProperty("z").GetDouble(), 1);
        Assert.Equal(Alice.ToString(), tc.GetProperty("o").GetString());
        var authorized = tc.GetProperty("a").EnumerateArray().ToList();
        Assert.Equal([Alice.ToString(), Bob.ToString()], authorized.Select(a => a.GetProperty("i").GetString()).ToArray());
        Assert.Equal("Bob \"the\" Builder", authorized[1].GetProperty("n").GetString());
        Assert.True(tc.GetProperty("i").GetInt32() > 0);
    }

    [Fact]
    public void TheAuthorizedListIsReadLiveSoAChangeNeedsNoHook()
    {
        var plugin = Loaded();
        FinishScan(plugin);
        var tc = Tc(Alice, 0, 0, 0, (Alice, "Alice"));
        Spawn(plugin, tc);

        tc.authorizedPlayers.Add(Bob);      // Bob was just authorized
        tc.authorizedPlayers.Remove(Alice); // and Alice was removed

        var authorized = Page(plugin).GetProperty("tcs")[0].GetProperty("a").EnumerateArray().Select(a => a.GetProperty("i").GetString()).ToArray();
        Assert.Equal([Bob.ToString()], authorized);
    }

    [Fact]
    public void AnAuthorizedPlayerWhoIsNotInTheWorldRightNowIsListedByIdWithNoName()
    {
        var plugin = Loaded();
        FinishScan(plugin);
        var tc = Tc(Alice, 0, 0, 0, (Alice, "Alice"));
        tc.authorizedPlayers.Add(Bob);   // Bob is authorized but offline and not asleep anywhere: the game has no name for him
        Spawn(plugin, tc);

        var authorized = Page(plugin).GetProperty("tcs")[0].GetProperty("a").EnumerateArray().ToList();

        var bob = Assert.Single(authorized, a => a.GetProperty("i").GetString() == Bob.ToString());
        Assert.False(bob.TryGetProperty("n", out _));
        Assert.Equal("Alice", Assert.Single(authorized, a => a.GetProperty("i").GetString() == Alice.ToString()).GetProperty("n").GetString());
    }

    [Fact]
    public void ACupboardWithNobodyAuthorizedHasAnEmptyListAndANamelessOneHasNoName()
    {
        var plugin = Loaded();
        FinishScan(plugin);
        Spawn(plugin, Tc(Alice));
        Spawn(plugin, Tc(Bob, 0, 0, 0, (Bob, "")));

        var tcs = Page(plugin).GetProperty("tcs").EnumerateArray().ToList();

        Assert.Equal(0, tcs[0].GetProperty("a").GetArrayLength());
        Assert.False(tcs[1].GetProperty("a")[0].TryGetProperty("n", out _));
    }

    [Fact]
    public void ADestroyedCupboardStillInTheIndexIsSkippedNotReported()
    {
        var plugin = Loaded();
        FinishScan(plugin);
        var gone = Tc(Alice);
        Spawn(plugin, gone);
        Spawn(plugin, Tc(Bob));
        gone.IsDestroyed = true;     // destroyed, and the kill hook has not run yet

        var tcs = Page(plugin).GetProperty("tcs").EnumerateArray().ToList();

        Assert.Equal([Bob.ToString()], tcs.Select(t => t.GetProperty("o").GetString()).ToArray());
    }

    [Fact]
    public void TheReplySaysWhetherTheScanHasFinishedSoAPartialListIsNeverTakenForTheWholeWorld()
    {
        BaseNetworkable.serverEntities.All.Add(Tc(Alice));
        var plugin = Loaded();

        Assert.False(Page(plugin).GetProperty("ready").GetBoolean());
        FinishScan(plugin);
        Assert.True(Page(plugin).GetProperty("ready").GetBoolean());
    }

    [Fact]
    public void PagingWalksEveryCupboardExactlyOnceUsingNext()
    {
        var plugin = Loaded();
        FinishScan(plugin);
        for (var i = 0; i < 7; i++) { Spawn(plugin, Tc(Alice, i)); }

        var seen = new List<double>();
        var next = 0;
        var pages = 0;
        while (true)
        {
            var data = Page(plugin, next.ToString(), "3");
            seen.AddRange(data.GetProperty("tcs").EnumerateArray().Select(t => t.GetProperty("x").GetDouble()));
            next = data.GetProperty("next").GetInt32();
            pages++;
            if (next >= data.GetProperty("total").GetInt32()) { break; }
        }

        Assert.Equal(3, pages);
        Assert.Equal(7, seen.Count);
        Assert.Equal(7, seen.Distinct().Count());
    }

    [Fact]
    public void AnOffsetPastTheEndIsAnEmptyPageNotAnError()
    {
        var plugin = Loaded();
        FinishScan(plugin);
        Spawn(plugin, Tc(Alice));

        var data = Page(plugin, "50");

        Assert.Equal(0, data.GetProperty("tcs").GetArrayLength());
        Assert.Equal(1, data.GetProperty("total").GetInt32());
    }

    [Fact]
    public void ThePageSizeIsCappedWhateverIsAsked()
    {
        var plugin = Loaded();
        FinishScan(plugin);
        for (var i = 0; i < ArchonTcs.HardPageSize + 50; i++) { Spawn(plugin, Tc(Alice, i)); }

        var data = Page(plugin, "0", "100000");

        Assert.Equal(ArchonTcs.HardPageSize, data.GetProperty("tcs").GetArrayLength());
        Assert.Equal(ArchonTcs.HardPageSize, data.GetProperty("next").GetInt32());
    }

    [Fact]
    public void AReplyNeverGrowsPastTheCharacterCapEvenWithHugeAuthorizedLists()
    {
        var plugin = Loaded();
        FinishScan(plugin);
        for (var i = 0; i < 40; i++)
        {
            var many = Enumerable.Range(0, 60).Select(n => (Alice + (ulong)n, new string('n', 60))).ToArray();
            Spawn(plugin, Tc(Alice, i, 0, 0, many));
        }

        var arg = ConsoleSystem.Arg.WithArgs("0", "500");
        plugin.CmdTcs(arg);
        var json = Assert.Single(arg.Replies);

        Assert.True(json.Length < ArchonCombat.MaxReplyChars + 12000, $"reply was {json.Length} chars");
        var data = JsonDocument.Parse(json).RootElement.GetProperty("data");
        Assert.True(data.GetProperty("next").GetInt32() < 40);   // the rest waits for the next page, none dropped
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1.5")]
    public void ABadOffsetIsAUsageError(string offset)
    {
        var plugin = Loaded();
        var arg = ConsoleSystem.Arg.WithArgs(offset);

        plugin.CmdTcs(arg);

        var reply = JsonDocument.Parse(Assert.Single(arg.Replies)).RootElement;
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("usage", reply.GetProperty("err").GetString());
    }

    [Fact]
    public void ANonsenseMaxFallsBackToTheDefaultRatherThanFailing()
    {
        var plugin = Loaded();
        FinishScan(plugin);
        Spawn(plugin, Tc(Alice));

        Assert.Equal(1, Page(plugin, "0", "zero").GetProperty("tcs").GetArrayLength());
        Assert.Equal(1, Page(plugin, "0", "-4").GetProperty("tcs").GetArrayLength());
    }

    [Fact]
    public void TcsIsIgnoredFromAnInGameConsole()
    {
        var plugin = Loaded();
        var arg = ConsoleSystem.Arg.WithArgs();
        arg.Connection = new object();

        plugin.CmdTcs(arg);

        Assert.Empty(arg.Replies);
    }

    [Fact]
    public void HostileNamesStillProduceValidJson()
    {
        var plugin = Loaded();
        FinishScan(plugin);
        var hostile = "\"}]}" + (char)0 + "\n\\ " + (char)0x2028 + " x";
        Spawn(plugin, Tc(Alice, 0, 0, 0, (Bob, hostile)));

        var name = Page(plugin).GetProperty("tcs")[0].GetProperty("a")[0].GetProperty("n").GetString();

        Assert.Equal(hostile, name);
    }

    [Fact]
    public void NumbersAreInvariantCultureInThePositions()
    {
        var oldCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var plugin = Loaded();
            FinishScan(plugin);
            Spawn(plugin, Tc(Alice, 1.5f, float.NaN, 1234567.89f));

            var arg = ConsoleSystem.Arg.WithArgs();
            plugin.CmdTcs(arg);

            using var parsed = JsonDocument.Parse(Assert.Single(arg.Replies)); // throws on a comma decimal or NaN
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = oldCulture;
        }
    }

    [Fact]
    public void StatsReportTheIndexForMeasuring()
    {
        BaseNetworkable.serverEntities.All.Add(Tc(Alice));
        var plugin = Loaded();
        FinishScan(plugin);
        var arg = ConsoleSystem.Arg.WithArgs();

        plugin.CmdStats(arg);

        var tcs = JsonDocument.Parse(Assert.Single(arg.Replies)).RootElement.GetProperty("data").GetProperty("tcs");
        Assert.True(tcs.GetProperty("subscribed").GetBoolean());
        Assert.True(tcs.GetProperty("ready").GetBoolean());
        Assert.Equal(1, tcs.GetProperty("count").GetInt32());
        Assert.Equal(1, tcs.GetProperty("scanned").GetInt64());
    }

    [Fact]
    public void HelloAdvertisesTheTcsCapability()
    {
        var plugin = Loaded();
        var arg = ConsoleSystem.Arg.WithArgs();

        plugin.CmdHello(arg);

        var capabilities = JsonDocument.Parse(Assert.Single(arg.Replies)).RootElement.GetProperty("data").GetProperty("capabilities")
            .EnumerateArray().Select(c => c.GetString()).ToArray();
        Assert.Contains("tcs", capabilities);
    }
}

/// <summary>Tests that share the one stub world must not run at the same time.</summary>
[CollectionDefinition("World", DisableParallelization = true)]
public sealed class WorldCollection { }
