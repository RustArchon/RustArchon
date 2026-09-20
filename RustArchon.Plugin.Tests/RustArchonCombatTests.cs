// Copyright ©2026 Scott Blomfield

using System.Text.Json;
using Oxide.Plugins;
using UnityEngine;
using ArchonPlugin = Oxide.Plugins.RustArchon;

namespace RustArchon.Plugin.Tests;

/// <summary>
/// The combat log: what is recorded and what is deliberately not, the bounded buffer and its cursor, the drain
/// command, and that the damage hooks are only subscribed while the Combat log switch is on. The hooks fire for every
/// hit on every entity in the game, so "exits early unless a real player is involved" is the property that matters most.
/// </summary>
public sealed class RustArchonCombatTests : IDisposable
{
    private const ulong Alice = 76561198000000001UL;
    private const ulong Bob = 76561198000000002UL;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rustarchon-combat-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) { Directory.Delete(_directory, recursive: true); }
    }

    private ArchonPlugin Loaded(bool combat = true)
    {
        var path = Path.Combine(_directory, "RustArchon", "settings.txt");
        if (!combat)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "recording=true\ncombat=false\n");
        }

        var plugin = new ArchonPlugin { SettingsFilePath = path };
        typeof(ArchonPlugin).GetMethod("Init", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(plugin, null);
        return plugin;
    }

    // The damage hooks' subscription changes only; the cupboard index subscribes hooks of its own.
    private static string[] DamageHooks(ArchonPlugin plugin) =>
        plugin.Subscriptions.Where(s => s.EndsWith("OnEntityTakeDamage") || s.EndsWith("OnEntityDeath")).ToArray();

    private static void Hit(ArchonPlugin plugin, BaseCombatEntity victim, HitInfo? info) =>
        typeof(ArchonPlugin).GetMethod("OnEntityTakeDamage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(plugin, [victim, info]);

    private static void Death(ArchonPlugin plugin, BaseCombatEntity victim, HitInfo? info) =>
        typeof(ArchonPlugin).GetMethod("OnEntityDeath", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(plugin, [victim, info]);

    private static BasePlayer Player(ulong id, string name, float x = 0, float y = 0, float z = 0)
    {
        var player = new BasePlayer { userID = id, displayName = name, ShortPrefabName = "player" };
        player.transform.position = new Vector3(x, y, z);
        return player;
    }

    private static BaseCombatEntity Animal(string prefab, float x = 0, float y = 0, float z = 0)
    {
        var animal = new BaseCombatEntity { ShortPrefabName = prefab };
        animal.transform.position = new Vector3(x, y, z);
        return animal;
    }

    private static HitInfo From(BasePlayer attacker, string weapon = "rifle.ak", float damage = 40f, Rust.DamageType type = Rust.DamageType.Bullet, bool headshot = false) => new()
    {
        Initiator = attacker,
        InitiatorPlayer = attacker,
        WeaponPrefab = new BaseEntity { ShortPrefabName = weapon },
        isHeadshot = headshot,
        damageTypes = new DamageTypeList { TotalValue = damage, Majority = type }
    };

    private static JsonElement DrainAll(ArchonPlugin plugin, long bootId = 0, long cursor = 0, string? max = null)
    {
        var arg = max is null ? ConsoleSystem.Arg.WithArgs(bootId.ToString(), cursor.ToString()) : ConsoleSystem.Arg.WithArgs(bootId.ToString(), cursor.ToString(), max);
        plugin.CmdEventsDrain(arg);
        return JsonDocument.Parse(Assert.Single(arg.Replies)).RootElement.GetProperty("data");
    }

    // ---- subscription: dormant unless switched on ---------------------------------------------------------------

    [Fact]
    public void WithTheSwitchOnTheDamageHooksAreSubscribed()
    {
        var plugin = Loaded(combat: true);

        Assert.Equal(["+OnEntityTakeDamage", "+OnEntityDeath"], DamageHooks(plugin));
    }

    [Fact]
    public void WithTheSwitchOffNoDamageHookIsEverSubscribed()
    {
        var plugin = Loaded(combat: false);

        Assert.DoesNotContain(DamageHooks(plugin), s => s.StartsWith('+'));
        Assert.False(plugin.CombatSubscribed);
    }

    [Fact]
    public void TurningTheSwitchOffUnsubscribesAndOnSubscribesAgain()
    {
        var plugin = Loaded(combat: true);
        plugin.Subscriptions.Clear();

        plugin.CmdConfig(ConsoleSystem.Arg.WithArgs("set", "combat", "false"));
        Assert.Equal(["-OnEntityTakeDamage", "-OnEntityDeath"], DamageHooks(plugin));
        plugin.Subscriptions.Clear();

        plugin.CmdConfig(ConsoleSystem.Arg.WithArgs("set", "combat", "true"));
        Assert.Equal(["+OnEntityTakeDamage", "+OnEntityDeath"], DamageHooks(plugin));
    }

    [Fact]
    public void SettingTheSwitchToWhatItAlreadyIsDoesNotChurnSubscriptions()
    {
        var plugin = Loaded(combat: true);
        plugin.Subscriptions.Clear();

        plugin.CmdConfig(ConsoleSystem.Arg.WithArgs("set", "combat", "true"));

        Assert.Empty(DamageHooks(plugin));
    }

    [Fact]
    public void TheRecordingSwitchDoesNotTouchTheDamageHooks()
    {
        var plugin = Loaded(combat: true);
        plugin.Subscriptions.Clear();

        plugin.CmdConfig(ConsoleSystem.Arg.WithArgs("set", "recording", "false"));

        Assert.Empty(DamageHooks(plugin));
    }

    // ---- what is recorded ---------------------------------------------------------------------------------------

    [Fact]
    public void APlayerHittingAPlayerIsRecordedWithEveryField()
    {
        var plugin = Loaded();
        var alice = Player(Alice, "Alice", 0, 0, 0);
        var bob = Player(Bob, "Bob \"the\" Builder", 30, 1, 40);

        Hit(plugin, bob, From(alice, "rifle.ak", 42.55f, Rust.DamageType.Bullet, headshot: true));

        var e = Assert.Single(DrainAll(plugin).GetProperty("events").EnumerateArray().ToList());
        Assert.Equal("hit", e.GetProperty("k").GetString());
        Assert.Equal(Alice.ToString(), e.GetProperty("a").GetString());
        Assert.Equal("Alice", e.GetProperty("an").GetString());
        Assert.True(e.GetProperty("ap").GetBoolean());
        Assert.Equal(Bob.ToString(), e.GetProperty("v").GetString());
        Assert.Equal("Bob \"the\" Builder", e.GetProperty("vn").GetString());
        Assert.True(e.GetProperty("vp").GetBoolean());
        Assert.Equal("rifle.ak", e.GetProperty("w").GetString());
        Assert.Equal(42.5, e.GetProperty("d").GetDouble(), 1); // one decimal
        Assert.Equal("Bullet", e.GetProperty("dt").GetString());
        Assert.True(e.GetProperty("hs").GetBoolean());
        Assert.Equal(50.0, e.GetProperty("dist").GetDouble(), 1); // (30, 1, 40) is 50.01 away
        Assert.Equal(3, e.GetProperty("apos").GetArrayLength());
        Assert.Equal(30.0, e.GetProperty("vpos")[0].GetDouble(), 1);
        Assert.True(e.GetProperty("t").GetInt64() > 1_700_000_000_000);
    }

    [Fact]
    public void APlayerHittingAnAnimalIsRecordedWithThePrefabAsTheVictim()
    {
        // The case vanilla never prints: the plugin must see player-versus-animal.
        var plugin = Loaded();

        Hit(plugin, Animal("bear", 5, 0, 5), From(Player(Alice, "Alice")));

        var e = Assert.Single(DrainAll(plugin).GetProperty("events").EnumerateArray().ToList());
        Assert.Equal("bear", e.GetProperty("v").GetString());
        Assert.False(e.GetProperty("vp").GetBoolean());
        Assert.False(e.TryGetProperty("vn", out _));
    }

    [Fact]
    public void AnAnimalHittingAPlayerIsRecordedWithThePrefabAsTheAttacker()
    {
        var plugin = Loaded();
        var bear = new BaseEntity { ShortPrefabName = "bear" };
        bear.transform.position = new Vector3(2, 0, 0);
        var info = new HitInfo { Initiator = bear, damageTypes = new DamageTypeList { TotalValue = 30, Majority = Rust.DamageType.Slash } };

        Hit(plugin, Player(Alice, "Alice"), info);

        var e = Assert.Single(DrainAll(plugin).GetProperty("events").EnumerateArray().ToList());
        Assert.Equal("bear", e.GetProperty("a").GetString());
        Assert.False(e.GetProperty("ap").GetBoolean());
        Assert.Equal(Alice.ToString(), e.GetProperty("v").GetString());
    }

    [Fact]
    public void ADeathIsRecordedAsADeathEvent()
    {
        var plugin = Loaded();

        Death(plugin, Player(Bob, "Bob"), From(Player(Alice, "Alice")));

        var e = Assert.Single(DrainAll(plugin).GetProperty("events").EnumerateArray().ToList());
        Assert.Equal("death", e.GetProperty("k").GetString());
    }

    [Fact]
    public void AnEntityDeathWithNoHitInfoStillRecordsAPlayerVictim()
    {
        // Suicide, drowning, decay: there is no HitInfo at all. A player dying is still worth a row.
        var plugin = Loaded();

        Death(plugin, Player(Bob, "Bob"), null);

        var e = Assert.Single(DrainAll(plugin).GetProperty("events").EnumerateArray().ToList());
        Assert.Equal("death", e.GetProperty("k").GetString());
        Assert.Equal(Bob.ToString(), e.GetProperty("v").GetString());
        Assert.Equal("", e.GetProperty("a").GetString());
    }

    // ---- what must NOT be recorded (the fast exit) --------------------------------------------------------------

    [Fact]
    public void NpcAgainstAnimalDamageIsCountedAsAFireButNeverRecorded()
    {
        var plugin = Loaded();
        var npc = new BasePlayer { userID = 12345UL, displayName = "Scientist" }; // an NPC "player": below the SteamID range
        var info = new HitInfo { Initiator = npc, InitiatorPlayer = npc, damageTypes = new DamageTypeList { TotalValue = 10 } };

        Hit(plugin, Animal("boar"), info);

        Assert.Equal(1, plugin.CombatHookFires);
        Assert.Equal(0, plugin.CombatHookRecorded);
        Assert.Equal(0, plugin.CombatEvents.Count);
    }

    [Fact]
    public void EnvironmentalDamageToAnEntityIsNeverRecorded()
    {
        var plugin = Loaded();

        Hit(plugin, Animal("wall"), new HitInfo { damageTypes = new DamageTypeList { TotalValue = 5, Majority = Rust.DamageType.Generic } });
        Hit(plugin, Animal("wall"), null);

        Assert.Equal(2, plugin.CombatHookFires);
        Assert.Equal(0, plugin.CombatHookRecorded);
    }

    [Fact]
    public void ANpcVictimHitByAnNpcIsNotRecordedEvenThoughBothArePlayerTypes()
    {
        var plugin = Loaded();
        var npcA = new BasePlayer { userID = 1UL };
        var npcB = new BasePlayer { userID = 2UL };

        Hit(plugin, npcA, new HitInfo { Initiator = npcB, InitiatorPlayer = npcB });

        Assert.Equal(0, plugin.CombatHookRecorded);
    }

    [Fact]
    public void AHitThatDidNoDamageIsNotCombatAndIsCountedSoItsFrequencyCanBeSeen()
    {
        // Found live: cold ticks at a comfortable level fire the hook with a player victim, no attacker and 0 damage,
        // about one every two seconds. That is noise, not combat.
        var plugin = Loaded();
        var cold = new HitInfo { damageTypes = new DamageTypeList { TotalValue = 0f, Majority = Rust.DamageType.Generic } };

        Hit(plugin, Player(Alice, "Alice"), cold);
        Hit(plugin, Player(Alice, "Alice"), cold);

        Assert.Equal(0, plugin.CombatHookRecorded);
        Assert.Equal(0, plugin.CombatEvents.Count);
        Assert.Equal(2, plugin.CombatHookZeroDamage);
    }

    [Fact]
    public void ATinyButRealDamageStaysBecauseBleedingAndFallDamageMatterToAFight()
    {
        var plugin = Loaded();
        var bleeding = new HitInfo { damageTypes = new DamageTypeList { TotalValue = 0.4f, Majority = Rust.DamageType.Bleeding } };

        Hit(plugin, Player(Alice, "Alice"), bleeding);

        var e = Assert.Single(DrainAll(plugin).GetProperty("events").EnumerateArray().ToList());
        Assert.Equal("Bleeding", e.GetProperty("dt").GetString());
        Assert.Equal(0.4, e.GetProperty("d").GetDouble(), 1);
    }

    [Fact]
    public void ADeathIsKeptEvenWhenItsDamageListIsEmpty()
    {
        // The killing blow's own damage may already be zeroed by the time the death hook runs.
        var plugin = Loaded();
        var info = new HitInfo { damageTypes = new DamageTypeList { TotalValue = 0f } };

        Death(plugin, Player(Alice, "Alice"), info);

        Assert.Equal("death", Assert.Single(DrainAll(plugin).GetProperty("events").EnumerateArray().ToList()).GetProperty("k").GetString());
    }

    [Fact]
    public void ZeroDamageIsReportedInTheStats()
    {
        var plugin = Loaded();
        Hit(plugin, Player(Alice, "Alice"), new HitInfo { damageTypes = new DamageTypeList { TotalValue = 0f } });
        var arg = ConsoleSystem.Arg.WithArgs();

        plugin.CmdStats(arg);

        var combat = JsonDocument.Parse(Assert.Single(arg.Replies)).RootElement.GetProperty("data").GetProperty("combat");
        Assert.Equal(1, combat.GetProperty("zeroDamage").GetInt64());
    }

    // ---- attacker-less damage is throttled (bleeding, cold, radiation ...) ----------------------------------

    private static HitInfo Tick(Rust.DamageType type, float amount = 0.4f) =>
        new() { damageTypes = new DamageTypeList { TotalValue = amount, Majority = type } };

    [Fact]
    public void ASteadyBleedIsRecordedOnceThenOncePerTenSecondsNotEveryTick()
    {
        var plugin = Loaded();
        long now = 1_800_000_000_000;
        plugin.NowMs = () => now;
        var alice = Player(Alice, "Alice");

        for (var i = 0; i < 12; i++)                 // a tick every 2 s for 24 s
        {
            Hit(plugin, alice, Tick(Rust.DamageType.Bleeding));
            now += 2000;
        }

        Assert.Equal(3, plugin.CombatHookRecorded);   // at 0 s, 10 s and 20 s
        Assert.Equal(9, plugin.CombatHookThrottled);
    }

    [Fact]
    public void TheFirstTickOfABurstIsAlwaysRecordedAndEachTypeAndVictimHasItsOwnClock()
    {
        var plugin = Loaded();
        plugin.NowMs = () => 1_800_000_000_000;

        Hit(plugin, Player(Alice, "Alice"), Tick(Rust.DamageType.Bleeding));
        Hit(plugin, Player(Alice, "Alice"), Tick(Rust.DamageType.Bleeding));   // same victim, same type: throttled
        Hit(plugin, Player(Alice, "Alice"), Tick(Rust.DamageType.Fall, 12f));  // same victim, other type: kept
        Hit(plugin, Player(Bob, "Bob"), Tick(Rust.DamageType.Bleeding));       // other victim, same type: kept

        Assert.Equal(3, plugin.CombatHookRecorded);
        Assert.Equal(1, plugin.CombatHookThrottled);
    }

    [Fact]
    public void DamageWithAnAttackerIsNeverThrottledHoweverFast()
    {
        var plugin = Loaded();
        plugin.NowMs = () => 1_800_000_000_000;
        var bear = new BaseEntity { ShortPrefabName = "bear" };
        bear.transform.position = new Vector3(1, 0, 0);
        var bite = new HitInfo { Initiator = bear, damageTypes = new DamageTypeList { TotalValue = 30f, Majority = Rust.DamageType.Slash } };

        for (var i = 0; i < 20; i++) { Hit(plugin, Player(Alice, "Alice"), bite); }

        Assert.Equal(20, plugin.CombatHookRecorded);
        Assert.Equal(0, plugin.CombatHookThrottled);
    }

    [Fact]
    public void ADeathIsNeverThrottled()
    {
        var plugin = Loaded();
        plugin.NowMs = () => 1_800_000_000_000;

        Hit(plugin, Player(Alice, "Alice"), Tick(Rust.DamageType.Bleeding));
        Death(plugin, Player(Alice, "Alice"), Tick(Rust.DamageType.Bleeding));   // died of the bleed a moment later

        Assert.Equal(2, plugin.CombatHookRecorded);
    }

    [Fact]
    public void AClockThatMovesBackwardsNeverSwallowsEvents()
    {
        var plugin = Loaded();
        long now = 1_800_000_000_000;
        plugin.NowMs = () => now;

        Hit(plugin, Player(Alice, "Alice"), Tick(Rust.DamageType.Bleeding));
        now -= 60_000;                                                           // the server's clock was corrected
        Hit(plugin, Player(Alice, "Alice"), Tick(Rust.DamageType.Bleeding));

        Assert.Equal(2, plugin.CombatHookRecorded);
    }

    [Fact]
    public void TheThrottleMapIsBoundedSoManyDifferentVictimsCannotGrowItForever()
    {
        var plugin = Loaded();
        plugin.NowMs = () => 1_800_000_000_000;

        for (ulong i = 0; i < 5000; i++)
        {
            Hit(plugin, Player(ArchonCombat.FirstSteamId + i, "P" + i), Tick(Rust.DamageType.Cold, 1f));
        }

        Assert.Equal(5000, plugin.CombatHookRecorded); // each is a different victim's first tick, all recorded, none lost to the reset
    }

    [Fact]
    public void ThrottledCountIsReportedInTheStats()
    {
        var plugin = Loaded();
        plugin.NowMs = () => 1_800_000_000_000;
        Hit(plugin, Player(Alice, "Alice"), Tick(Rust.DamageType.Bleeding));
        Hit(plugin, Player(Alice, "Alice"), Tick(Rust.DamageType.Bleeding));
        var arg = ConsoleSystem.Arg.WithArgs();

        plugin.CmdStats(arg);

        var combat = JsonDocument.Parse(Assert.Single(arg.Replies)).RootElement.GetProperty("data").GetProperty("combat");
        Assert.Equal(1, combat.GetProperty("throttled").GetInt64());
    }

    [Fact]
    public void ANullEntityIsIgnoredNotACrash()
    {
        var plugin = Loaded();

        Hit(plugin, null!, new HitInfo());
        Death(plugin, null!, null);

        Assert.Equal(0, plugin.CombatHookRecorded);
    }

    [Theory]
    [InlineData(0UL, false)]
    [InlineData(1UL, false)]
    [InlineData(76561197960265727UL, false)]
    [InlineData(76561197960265728UL, true)]
    [InlineData(76561198000000001UL, true)]
    public void OnlyASteamIdRangeIdIsARealPlayer(ulong id, bool expected)
    {
        Assert.Equal(expected, ArchonCombat.IsRealPlayer(id));
    }

    [Fact]
    public void AMissingWeaponOrDamageListDoesNotBreakARecord()
    {
        var plugin = Loaded();
        var alice = Player(Alice, "Alice");
        var info = new HitInfo { Initiator = alice, InitiatorPlayer = alice, WeaponPrefab = null, damageTypes = null! };

        Hit(plugin, Player(Bob, "Bob"), info);

        var e = Assert.Single(DrainAll(plugin).GetProperty("events").EnumerateArray().ToList());
        Assert.False(e.TryGetProperty("w", out _));
        Assert.Equal(0.0, e.GetProperty("d").GetDouble());
    }

    // ---- the buffer and the drain -------------------------------------------------------------------------------

    private static ArchonCombat.Event Ev(string victim = "v") => new() { Kind = "hit", Attacker = "a", Victim = victim, UnixMs = 1_800_000_000_000 };

    [Fact]
    public void SequenceNumbersStartAtOneAndIncreaseByOne()
    {
        var buffer = new ArchonCombat.Buffer(10);

        Assert.Equal(1, buffer.Add(Ev()));
        Assert.Equal(2, buffer.Add(Ev()));
        Assert.Equal(2, buffer.Head);
        Assert.Equal(2, buffer.Count);
    }

    [Fact]
    public void DrainingReturnsEventsAfterTheCursorOldestFirstAndAdvancesTheCursor()
    {
        var buffer = new ArchonCombat.Buffer(10);
        for (var i = 0; i < 5; i++) { buffer.Add(Ev("v" + i)); }

        var first = JsonDocument.Parse(buffer.DrainJson(buffer.BootId, 0, 3)).RootElement;
        Assert.Equal(3, first.GetProperty("cursor").GetInt64());
        Assert.Equal(["v0", "v1", "v2"], first.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("v").GetString()).ToArray());

        var second = JsonDocument.Parse(buffer.DrainJson(buffer.BootId, 3, 10)).RootElement;
        Assert.Equal(5, second.GetProperty("cursor").GetInt64());
        Assert.Equal(["v3", "v4"], second.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("v").GetString()).ToArray());
        Assert.False(second.GetProperty("lost").GetBoolean());
    }

    [Fact]
    public void DrainingWhenNothingIsNewKeepsTheCursorAndReturnsNoEvents()
    {
        var buffer = new ArchonCombat.Buffer(10);
        buffer.Add(Ev());

        var reply = JsonDocument.Parse(buffer.DrainJson(buffer.BootId, 1, 10)).RootElement;

        Assert.Equal(1, reply.GetProperty("cursor").GetInt64());
        Assert.Equal(0, reply.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public void WhenTheRingWrapsTheOldestAreDroppedAndAConsumerWhoFellBehindIsToldSo()
    {
        var buffer = new ArchonCombat.Buffer(5);
        for (var i = 1; i <= 12; i++) { buffer.Add(Ev("v" + i)); }   // 1..12, the ring holds 8..12

        var reply = JsonDocument.Parse(buffer.DrainJson(buffer.BootId, 2, 100)).RootElement;   // last saw 2

        Assert.True(reply.GetProperty("lost").GetBoolean());
        Assert.Equal(["v8", "v9", "v10", "v11", "v12"], reply.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("v").GetString()).ToArray());
        Assert.Equal(12, reply.GetProperty("cursor").GetInt64());
        Assert.Equal(5, buffer.Count);
    }

    [Fact]
    public void AConsumerThatKeptUpAfterAWrapIsNotToldItLostAnything()
    {
        var buffer = new ArchonCombat.Buffer(5);
        for (var i = 1; i <= 12; i++) { buffer.Add(Ev()); }

        var reply = JsonDocument.Parse(buffer.DrainJson(buffer.BootId, 10, 100)).RootElement;   // saw up to 10; 11 and 12 remain

        Assert.False(reply.GetProperty("lost").GetBoolean());
        Assert.Equal(2, reply.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public void ADifferentBootIdMeansThePluginReloadedSoTheDrainRestartsAndSaysReset()
    {
        var buffer = new ArchonCombat.Buffer(10);
        buffer.Add(Ev());
        buffer.Add(Ev());

        var reply = JsonDocument.Parse(buffer.DrainJson(buffer.BootId + 1, 999, 10)).RootElement;

        Assert.True(reply.GetProperty("reset").GetBoolean());
        Assert.Equal(2, reply.GetProperty("events").GetArrayLength());        // the stale cursor is ignored
        Assert.Equal(buffer.BootId, reply.GetProperty("bootId").GetInt64());  // and the caller learns the new one
    }

    [Fact]
    public void TwoBuffersGetDifferentBootIds()
    {
        Assert.NotEqual(new ArchonCombat.Buffer(1).BootId, new ArchonCombat.Buffer(1).BootId);
    }

    [Fact]
    public void ABootIdIsAJsonSafeIntegerSoAnyParserReadsItExactly()
    {
        for (var i = 0; i < 200; i++)
        {
            var id = new ArchonCombat.Buffer(1).BootId;
            Assert.InRange(id, 1, 9007199254740991L);
        }
    }

    [Fact]
    public void TheMaxIsCappedSoOneReplyStaysSmall()
    {
        var buffer = new ArchonCombat.Buffer(5000);
        for (var i = 0; i < 2000; i++) { buffer.Add(Ev()); }

        var reply = JsonDocument.Parse(buffer.DrainJson(buffer.BootId, 0, 1_000_000)).RootElement;

        Assert.Equal(ArchonCombat.HardDrainMax, reply.GetProperty("events").GetArrayLength());
        Assert.Equal(ArchonCombat.HardDrainMax, reply.GetProperty("cursor").GetInt64());
    }

    [Fact]
    public void AReplyNeverGrowsPastTheCharacterCapWhateverTheEventsContain()
    {
        var buffer = new ArchonCombat.Buffer(1000);
        for (var i = 0; i < 400; i++) { buffer.Add(new ArchonCombat.Event { Attacker = new string('a', 500), AttackerName = new string('n', 500), Victim = "v", UnixMs = 1 }); }

        var json = buffer.DrainJson(buffer.BootId, 0, 500);

        Assert.True(json.Length < ArchonCombat.MaxReplyChars + 2000, $"reply was {json.Length} chars");
        var reply = JsonDocument.Parse(json).RootElement;
        Assert.True(reply.GetProperty("events").GetArrayLength() < 400);
        Assert.True(reply.GetProperty("cursor").GetInt64() < 400); // the rest is left for the next drain, not dropped
    }

    [Fact]
    public void EventsWithHostilePlayerNamesStillProduceValidJson()
    {
        // Quotes, braces, a NUL, a newline, a backslash and a Unicode line separator, built from code points so this
        // file itself stays plain text.
        var hostile = "\"}]}" + (char)0 + "\n\\ " + (char)0x2028 + " name";
        var plugin = Loaded();

        Hit(plugin, Player(Bob, hostile), From(Player(Alice, "A\tlice")));

        var e = Assert.Single(DrainAll(plugin).GetProperty("events").EnumerateArray().ToList());
        Assert.Equal(hostile, e.GetProperty("vn").GetString());
    }

    [Fact]
    public void NumbersAreInvariantCultureNeverACommaDecimalOrScientific()
    {
        var oldCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var e = new ArchonCombat.Event { Damage = 1234567.89f, Distance = 0.0000001f, HasAttackerPosition = true, VictimX = float.NaN, VictimY = float.PositiveInfinity };

            var json = e.ToJson();

            using var parsed = JsonDocument.Parse(json); // throws on a comma decimal or NaN
            Assert.DoesNotContain("E+", json);
            Assert.DoesNotContain("e-", json);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = oldCulture;
        }
    }

    // ---- the command --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData]
    [InlineData("1")]
    [InlineData("abc", "0")]
    [InlineData("1", "-5")]
    [InlineData("1", "x")]
    public void DrainWithBadArgumentsIsAUsageError(params string[] args)
    {
        var plugin = Loaded();
        var arg = ConsoleSystem.Arg.WithArgs(args);

        plugin.CmdEventsDrain(arg);

        var reply = JsonDocument.Parse(Assert.Single(arg.Replies)).RootElement;
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("usage", reply.GetProperty("err").GetString());
    }

    [Fact]
    public void DrainIsIgnoredFromAnInGameConsole()
    {
        var plugin = Loaded();
        var arg = ConsoleSystem.Arg.WithArgs("1", "0");
        arg.Connection = new object();

        plugin.CmdEventsDrain(arg);

        Assert.Empty(arg.Replies);
    }

    [Fact]
    public void AnInvalidMaxFallsBackToTheDefaultRatherThanFailing()
    {
        var plugin = Loaded();
        Hit(plugin, Player(Bob, "Bob"), From(Player(Alice, "Alice")));

        var data = DrainAll(plugin, plugin.CombatEvents.BootId, 0, "not-a-number");

        Assert.Equal(1, data.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public void DrainStillWorksAfterTheSwitchIsTurnedOffSoBufferedEventsAreNotStranded()
    {
        var plugin = Loaded();
        Hit(plugin, Player(Bob, "Bob"), From(Player(Alice, "Alice")));

        plugin.CmdConfig(ConsoleSystem.Arg.WithArgs("set", "combat", "false"));

        Assert.Equal(1, DrainAll(plugin, plugin.CombatEvents.BootId).GetProperty("events").GetArrayLength());
    }

    [Fact]
    public void StatsReportTheCountersForMeasuringHookCost()
    {
        var plugin = Loaded();
        Hit(plugin, Player(Bob, "Bob"), From(Player(Alice, "Alice")));
        Hit(plugin, Animal("boar"), new HitInfo());
        var arg = ConsoleSystem.Arg.WithArgs();

        plugin.CmdStats(arg);

        var combat = JsonDocument.Parse(Assert.Single(arg.Replies)).RootElement.GetProperty("data").GetProperty("combat");
        Assert.True(combat.GetProperty("subscribed").GetBoolean());
        Assert.Equal(2, combat.GetProperty("hookFires").GetInt64());
        Assert.Equal(1, combat.GetProperty("recorded").GetInt64());
        Assert.Equal(1, combat.GetProperty("buffered").GetInt32());
        Assert.Equal(1, combat.GetProperty("head").GetInt64());
    }

    [Fact]
    public void StatsIsIgnoredFromAnInGameConsole()
    {
        var plugin = Loaded();
        var arg = ConsoleSystem.Arg.WithArgs();
        arg.Connection = new object();

        plugin.CmdStats(arg);

        Assert.Empty(arg.Replies);
    }
}
