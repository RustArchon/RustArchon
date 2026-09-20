// Copyright ©2026 Scott Blomfield

// RustArchon companion plugin for Carbon and Oxide.
//
// Written for the C# 7.3 subset on purpose: it is compiled by the game server's own compiler on a Mono
// runtime, and the ordinary .NET APIs it can rely on are narrower than a modern project's (for example
// ECDSA and RSA-PSS are not implemented there). RustArchon.Plugin.CompileCheck builds this file at 7.3.
//
// Design rules (see docs/adr/0004-companion-plugin-rcon-pull-dormant-hooks-signed-updates.md):
//   * Every command is RCON-only: a call that arrives with a connection (an in-game F1 console) is ignored.
//   * The plugin does nothing between commands. Recording and combat-log hooks (later phases) are
//     subscribed only while their setting is on, and switched off the moment it is turned off.
//   * Replies are one JSON envelope: {"v":1,"ok":true,"data":{...}} or {"v":1,"ok":false,"err":"..."}.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("RustArchon", "RustArchon", "0.7.0")]
    [Description("RustArchon companion plugin. Dormant until the RustArchon panel asks; every command is RCON-only.")]
    public class RustArchon : RustPlugin
    {
        internal const int ProtocolVersion = 1;
        internal const string SettingsDirectoryName = "RustArchon";
        internal const string SettingsFileName = "settings.txt";

        // Written on every load so the Updater can tell that a freshly swapped-in version actually came up: a version
        // that fails to compile never runs Init, so its marker never appears, and the Updater rolls back.
        internal const string LoadedMarkerFileName = "loaded.txt";

        // What this build can actually do. A capability is listed only once its code exists, so the panel
        // never enables a feature the installed plugin cannot perform. "recording" and "combat" arrive with
        // their hooks in a later phase; until then only the settings channel itself is offered.
        private static readonly string[] Capabilities = { "config", "combat", "tcs", "positions", "map" };

        // Set by Init unless something (a test) supplied a path first.
        internal string SettingsFilePath;
        internal string ScriptFilePath;
        internal ArchonSettings Settings = new ArchonSettings();
        internal ArchonIntegrity.Result Integrity = ArchonIntegrity.Result.Unlocated;
        private bool _settingsPersisted;

        private void Init()
        {
            if (SettingsFilePath == null)
            {
                SettingsFilePath = LocateSettingsFilePath();
            }

            if (SettingsFilePath != null)
            {
                Settings = ArchonSettings.Load(SettingsFilePath);
                _settingsPersisted = true;
            }

            // Once, at load: read our own file and check the signature the Panel put on it. Cheap (milliseconds),
            // and never allowed to stop the plugin loading - a failure is reported, not thrown.
            if (ScriptFilePath == null)
            {
                ScriptFilePath = LocateOwnScriptPath();
            }
            Integrity = ArchonIntegrity.CheckFile(ScriptFilePath, ArchonIntegrity.TrustedModulus, ArchonIntegrity.TrustedExponent);

            WriteLoadedMarker();

            // Dormant by default: the damage hooks fire for every hit on every entity, so they are only subscribed
            // while the Combat log switch is on (see ApplyCombatSubscription).
            ApplyCombatSubscription(Settings.Combat);
            ApplyTcIndex(Settings.Recording);
            ApplyPositionRecording(Settings.Recording);

            Puts("Init v" + Version + " protocol=" + ProtocolVersion + " settingsPersisted=" + _settingsPersisted
                + " recording=" + Settings.Recording + " combat=" + Settings.Combat
                + " signature=" + Integrity.State + (Integrity.KeyFingerprint.Length > 0 ? " key=" + Integrity.KeyFingerprint : ""));
        }

        // archon.hello - what the panel calls to learn this plugin's version, protocol, capabilities and settings.
        [ConsoleCommand("archon.hello")]
        internal void CmdHello(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) { return; }

            arg.ReplyWith(ArchonJson.Ok(HelloData()));
        }

        // archon.config get
        // archon.config set <recording|combat|map> <true|false>
        [ConsoleCommand("archon.config")]
        internal void CmdConfig(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) { return; }

            // HasArgs/GetString rather than arg.Args: on the game server Args is a StringView[], not a string[],
            // and code that treats it as strings does not compile there (found on the first live load).
            var action = arg.HasArgs(1) ? arg.GetString(0, "get").ToLowerInvariant() : "get";

            if (action == "get")
            {
                arg.ReplyWith(ArchonJson.Ok(Settings.ToJson(_settingsPersisted)));
                return;
            }

            if (action != "set" || !arg.HasArgs(3) || arg.HasArgs(4))
            {
                arg.ReplyWith(ArchonJson.Err("usage", "archon.config get | archon.config set <recording|combat|map> <true|false>"));
                return;
            }

            string code;
            string message;
            if (!Settings.TrySet(arg.GetString(1, ""), arg.GetString(2, ""), out code, out message))
            {
                arg.ReplyWith(ArchonJson.Err(code, message));
                return;
            }

            ApplyCombatSubscription(Settings.Combat);
            ApplyTcIndex(Settings.Recording);
            ApplyPositionRecording(Settings.Recording);
            _settingsPersisted = SettingsFilePath != null && Settings.TrySave(SettingsFilePath);
            arg.ReplyWith(ArchonJson.Ok(Settings.ToJson(_settingsPersisted)));
        }

        // ---- combat log --------------------------------------------------------------------------------------
        //
        // A bounded in-memory record of damage that involves a real player, drained by the Worker over RCON. The two
        // hooks below fire for EVERY hit on every entity (NpcSpawn alone made ~40 million fires on the test server), so
        // each body exits before doing anything unless a real player is on either side, and the hooks are only
        // subscribed at all while the Combat log switch is on.

        internal readonly ArchonCombat.Buffer CombatEvents = new ArchonCombat.Buffer(ArchonCombat.DefaultCapacity);
        internal long CombatHookFires;
        internal long CombatHookRecorded;
        internal long CombatHookZeroDamage;
        internal long CombatHookThrottled;

        // Where the time comes from. A field only so a test can drive it; nothing at runtime assigns it.
        internal Func<long> NowMs = ArchonCombat.NowUnixMs;

        // Damage with no attacker (bleeding, cold, radiation, hunger and the like) arrives as a steady tick - found
        // live, a row every two seconds for as long as a player bled. One row per victim per damage type per this many
        // milliseconds is enough to show it happened. Damage WITH an attacker is never throttled.
        internal const long EnvironmentalRepeatMs = 10000;
        private const int MaxEnvironmentalKeys = 2000;
        private readonly Dictionary<string, long> _environmentalLast = new Dictionary<string, long>();
        internal bool CombatSubscribed;

        internal void ApplyCombatSubscription(bool enabled)
        {
            if (enabled == CombatSubscribed) { return; }

            CombatSubscribed = enabled;
            if (enabled)
            {
                Subscribe("OnEntityTakeDamage");
                Subscribe("OnEntityDeath");
            }
            else
            {
                Unsubscribe("OnEntityTakeDamage");
                Unsubscribe("OnEntityDeath");
            }
        }

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            CombatHookFires++;
            RecordCombat("hit", entity, info);
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            CombatHookFires++;
            RecordCombat("death", entity, info);
        }

        private void RecordCombat(string kind, BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null) { return; }

            // The cheap part: who is a real player? Anything else exits here without touching the rest of HitInfo.
            var victimPlayer = entity as BasePlayer;
            var attackerPlayer = info == null ? null : info.InitiatorPlayer;
            var victimReal = victimPlayer != null && ArchonCombat.IsRealPlayer(victimPlayer.userID);
            var attackerReal = attackerPlayer != null && ArchonCombat.IsRealPlayer(attackerPlayer.userID);
            if (!victimReal && !attackerReal) { return; }

            var e = new ArchonCombat.Event();
            e.Kind = kind;
            e.UnixMs = NowMs();

            e.VictimIsPlayer = victimReal;
            e.Victim = victimReal ? victimPlayer.userID.ToString() : entity.ShortPrefabName;
            e.VictimName = victimReal ? victimPlayer.displayName : "";
            var victimAt = entity.transform.position;
            e.VictimX = victimAt.x; e.VictimY = victimAt.y; e.VictimZ = victimAt.z;

            if (info != null)
            {
                e.AttackerIsPlayer = attackerReal;
                var initiator = info.Initiator;
                e.Attacker = attackerReal ? attackerPlayer.userID.ToString() : (initiator == null ? "" : initiator.ShortPrefabName);
                e.AttackerName = attackerReal ? attackerPlayer.displayName : "";

                if (initiator != null)
                {
                    var attackerAt = initiator.transform.position;
                    e.AttackerX = attackerAt.x; e.AttackerY = attackerAt.y; e.AttackerZ = attackerAt.z;
                    e.HasAttackerPosition = true;
                    e.Distance = Vector3.Distance(attackerAt, victimAt);
                }

                var weapon = info.WeaponPrefab;
                e.Weapon = weapon == null ? "" : weapon.ShortPrefabName;
                e.Headshot = info.isHeadshot;

                var damage = info.damageTypes;
                if (damage != null)
                {
                    e.Damage = damage.Total();
                    e.DamageType = damage.GetMajorityDamageType().ToString();

                    // The game fires this hook for hits that do nothing (cold or radiation ticks at a comfortable
                    // level, for instance: found live, one row every two seconds). A hit that did no damage is not
                    // combat. A death is always kept, and so is a hit whose damage is unknown (no damage list).
                    if (kind == "hit" && e.Damage <= 0f)
                    {
                        CombatHookZeroDamage++;
                        return;
                    }

                    if (kind == "hit" && initiator == null && ThrottleEnvironmental(e))
                    {
                        CombatHookThrottled++;
                        return;
                    }
                }
            }

            CombatEvents.Add(e);
            CombatHookRecorded++;
        }

        // True when this attacker-less hit repeats one recorded for the same victim and damage type within the last
        // EnvironmentalRepeatMs, and so should be skipped. The first of each burst is always recorded.
        private bool ThrottleEnvironmental(ArchonCombat.Event e)
        {
            var key = e.Victim + "|" + e.DamageType;
            long last;
            if (_environmentalLast.TryGetValue(key, out last) && e.UnixMs - last < EnvironmentalRepeatMs && e.UnixMs >= last)
            {
                return true;
            }

            // Bounded: victims come and go, so the map is simply started over if it ever grows large (worst case, one
            // extra row per victim and type right after).
            if (_environmentalLast.Count >= MaxEnvironmentalKeys) { _environmentalLast.Clear(); }
            _environmentalLast[key] = e.UnixMs;
            return false;
        }

        // ---- base (tool cupboard) index ------------------------------------------------------------------------
        //
        // Which tool cupboards exist, where, whose they are and who is authorized on them. Kept incrementally: a
        // cupboard is added when it spawns and dropped when it is killed, and when the index is switched on the world's
        // existing cupboards are found by a scan spread over many frames so it never stalls the server. Authorized
        // players are read live from the cupboard when asked, so a change to the list needs no hook. Follows the
        // Recording switch: off means the hooks are unsubscribed and the index emptied.

        internal readonly ArchonTcs.Index TcIndex = new ArchonTcs.Index();
        internal bool TcSubscribed;
        internal long TcScanned;
        private Timer _tcScanTimer;
        private IEnumerator<BaseNetworkable> _tcScan;
        private int _tcScanRestarts;

        // How much of the world one scan step may look at: a count, and a time, whichever comes first.
        internal const int TcScanBatch = 2000;
        internal const double TcScanBudgetMs = 2.0;
        private const int TcScanMaxRestarts = 3;

        internal bool TcScanRunning { get { return _tcScan != null; } }

        internal void ApplyTcIndex(bool enabled)
        {
            if (enabled == TcSubscribed) { return; }

            TcSubscribed = enabled;
            if (enabled)
            {
                Subscribe("OnEntitySpawned");
                Subscribe("OnEntityKill");
                BeginTcScan();
            }
            else
            {
                Unsubscribe("OnEntitySpawned");
                Unsubscribe("OnEntityKill");
                StopTcScan();
                TcIndex.Clear();
            }
        }

        private void OnEntitySpawned(BaseNetworkable entity)
        {
            var privilege = entity as BuildingPrivlidge;
            if (privilege != null) { TcIndex.Add(privilege); }
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            var privilege = entity as BuildingPrivlidge;
            if (privilege != null) { TcIndex.Remove(privilege); }
        }

        internal void BeginTcScan()
        {
            StopTcScan();
            _tcScan = BaseNetworkable.serverEntities.GetEnumerator();
            _tcScanTimer = timer.Every(0.05f, ScanSlice);
        }

        private void StopTcScan()
        {
            if (_tcScanTimer != null) { _tcScanTimer.Destroy(); _tcScanTimer = null; }
            if (_tcScan != null) { _tcScan.Dispose(); _tcScan = null; }
        }

        // One step of the initial scan. Public to the tests, which drive it themselves.
        internal void ScanSlice()
        {
            if (_tcScan == null) { return; }

            var clock = Stopwatch.StartNew();
            var looked = 0;
            try
            {
                while (looked < TcScanBatch && _tcScan.MoveNext())
                {
                    looked++;
                    TcScanned++;
                    var privilege = _tcScan.Current as BuildingPrivlidge;
                    if (privilege != null) { TcIndex.Add(privilege); }

                    if ((looked & 63) == 0 && clock.Elapsed.TotalMilliseconds > TcScanBudgetMs) { return; }
                }

                if (looked < TcScanBatch)
                {
                    // MoveNext returned false: the whole world has been seen.
                    TcIndex.Ready = true;
                    StopTcScan();
                }
            }
            catch (InvalidOperationException)
            {
                // The world's entity list changed under the enumerator (something spawned or was killed between two
                // steps). Start over rather than trust a half-finished pass; anything already found is kept, and adding
                // a cupboard twice is harmless.
                if (++_tcScanRestarts > TcScanMaxRestarts)
                {
                    StopTcScan();
                    return;
                }

                _tcScan.Dispose();
                _tcScan = BaseNetworkable.serverEntities.GetEnumerator();
            }
        }

        // archon.tcs [offset] [max] - the indexed cupboards, a page at a time.
        [ConsoleCommand("archon.tcs")]
        internal void CmdTcs(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) { return; }

            var offset = 0;
            var max = ArchonTcs.DefaultPageSize;
            int parsed;
            if (arg.HasArgs(1))
            {
                if (!int.TryParse(arg.GetString(0, ""), out parsed) || parsed < 0)
                {
                    arg.ReplyWith(ArchonJson.Err("usage", "archon.tcs [offset] [max]"));
                    return;
                }
                offset = parsed;
            }
            if (arg.HasArgs(2) && int.TryParse(arg.GetString(1, ""), out parsed) && parsed > 0) { max = parsed; }

            arg.ReplyWith(ArchonJson.Ok(TcIndex.PageJson(offset, max)));
        }

        // ---- positions ---------------------------------------------------------------------------------------
        //
        // While the Recording switch is on, a timer looks at the connected players every few seconds and records where
        // they are into a bounded ring the Worker drains. Only a player who moved (or a heartbeat once a minute) makes a
        // sample, so an idle server or an idle player costs a loop over a short list and nothing else. Off means no
        // timer and no hook: the plugin is back to doing nothing between commands.

        internal readonly ArchonPositions.Buffer Positions = new ArchonPositions.Buffer(ArchonPositions.DefaultCapacity);
        internal bool PositionsRecording;
        internal long PositionSweeps;
        internal long PositionSweepTicks;
        internal long PositionSamples;
        private Timer _positionTimer;
        private readonly Dictionary<ulong, ArchonPositions.Last> _lastPosition = new Dictionary<ulong, ArchonPositions.Last>();

        internal void ApplyPositionRecording(bool enabled)
        {
            if (enabled == PositionsRecording) { return; }

            PositionsRecording = enabled;
            if (enabled)
            {
                Subscribe("OnPlayerDisconnected");
                _positionTimer = timer.Every(ArchonPositions.SampleSeconds, SamplePositions);
            }
            else
            {
                Unsubscribe("OnPlayerDisconnected");
                if (_positionTimer != null) { _positionTimer.Destroy(); _positionTimer = null; }
                _lastPosition.Clear();
            }
        }

        // One look at everyone connected. Real players only (an NPC has no SteamID); sleepers are not in the list.
        internal void SamplePositions()
        {
            if (!PositionsRecording) { return; }

            var started = Stopwatch.GetTimestamp();
            var now = NowMs();
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !ArchonCombat.IsRealPlayer(player.userID)) { continue; }
                RecordPosition(player, now, false);
            }

            PositionSweeps++;
            PositionSweepTicks += Stopwatch.GetTimestamp() - started;
        }

        // Where a player was when they left is where their body (the sleeper) stays, so it is always recorded.
        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (!PositionsRecording || player == null || !ArchonCombat.IsRealPlayer(player.userID)) { return; }

            RecordPosition(player, NowMs(), true);
            _lastPosition.Remove(player.userID);
        }

        private void RecordPosition(BasePlayer player, long now, bool leaving)
        {
            var at = player.transform.position;
            ArchonPositions.Last last;
            var known = _lastPosition.TryGetValue(player.userID, out last);
            var heartbeat = known && now - last.UnixMs >= ArchonPositions.HeartbeatMs;

            if (!leaving && known && !ArchonPositions.ShouldRecord(last, at.x, at.y, at.z, now)) { return; }

            var sample = new ArchonPositions.Sample();
            sample.UnixMs = now;
            sample.PlayerId = player.userID.ToString();
            sample.X = at.x; sample.Y = at.y; sample.Z = at.z;
            // The look direction. (The body transform does not follow it - found live: yaw read 0 on every sample.)
            var eyes = player.eyes;
            sample.Yaw = eyes != null ? eyes.rotation.eulerAngles.y : 0f;
            if (!known || heartbeat || leaving) { sample.Name = player.displayName ?? ""; }
            sample.Marker = leaving ? "off" : (known ? "" : "on");
            Positions.Add(sample);
            PositionSamples++;

            if (!leaving)
            {
                if (!known && _lastPosition.Count >= ArchonPositions.MaxTracked) { _lastPosition.Clear(); }
                last.UnixMs = now; last.X = at.x; last.Y = at.y; last.Z = at.z;
                _lastPosition[player.userID] = last;
            }
        }

        // archon.positions.drain <bootId> <cursor> [max] - the samples after <cursor>, oldest first; same contract as
        // archon.events.drain.
        [ConsoleCommand("archon.positions.drain")]
        internal void CmdPositionsDrain(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) { return; }

            long bootId;
            long cursor;
            if (!arg.HasArgs(2)
                || !long.TryParse(arg.GetString(0, ""), out bootId)
                || !long.TryParse(arg.GetString(1, ""), out cursor)
                || cursor < 0)
            {
                arg.ReplyWith(ArchonJson.Err("usage", "archon.positions.drain <bootId> <cursor> [max]"));
                return;
            }

            var max = ArchonCombat.DefaultDrainMax;
            if (arg.HasArgs(3))
            {
                int parsed;
                if (int.TryParse(arg.GetString(2, ""), out parsed) && parsed > 0) { max = parsed; }
            }

            arg.ReplyWith(ArchonJson.Ok(Positions.DrainJson(bootId, cursor, max)));
        }

        // ---- map ---------------------------------------------------------------------------------------------
        //
        // The game can draw its own map (world.rendermap), the only server-side way to get an image of it. It BLOCKS the
        // game for about a minute, so it is only ever started when nobody is online (unless the caller says otherwise),
        // and it is started a moment after the command replies so the Worker gets its answer before the freeze.

        internal string MapDirectory = null;
        internal Func<uint> WorldSize = delegate { return World.Size; };
        internal Func<uint> WorldSeed = delegate { return World.Seed; };
        // The named places on the map. The game's own list (TerrainMeta.Path.Monuments) is not reachable from a plugin, so the
        // scene is searched - a one-off cost of a fraction of a second, only when the list is asked for (once per wipe).
        internal Func<IEnumerable<MonumentInfo>> MonumentSource = delegate { return UnityEngine.Object.FindObjectsOfType<MonumentInfo>(); };

        internal Action<string> RunServerCommand = delegate (string command) { ConsoleSystem.Run(ConsoleSystem.Option.Server, command, new object[0]); };

        private bool _mapRenderScheduled;
        internal bool MapRenderScheduled { get { return _mapRenderScheduled; } }

        // The outcome of the last render this run of the plugin performed, as JSON ("null" before any).
        internal string MapLastRenderJson = "null";

        internal string MapFileName()
        {
            return "map_" + WorldSize() + "_" + WorldSeed() + ".png";
        }

        internal string MapFilePath()
        {
            return Path.Combine(MapDirectory ?? Directory.GetCurrentDirectory(), MapFileName());
        }

        // The world is only known once it has loaded; before that its size is 0 and there is no file name to speak of.
        internal bool MapWorldKnown()
        {
            return WorldSize() > 0;
        }

        internal long MapFileBytes()
        {
            try
            {
                var info = new FileInfo(MapFilePath());
                return info.Exists ? info.Length : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        internal static int RealPlayersOnline()
        {
            var count = 0;
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player != null && ArchonCombat.IsRealPlayer(player.userID)) { count++; }
            }
            return count;
        }

        // Decides and, if it is allowed, schedules a render. code is null when scheduled.
        internal bool TryScheduleMapRender(bool overwrite, bool ignorePlayers, float delaySeconds, out string code, out string message)
        {
            code = null;
            message = null;

            if (!MapWorldKnown())
            {
                code = "no_world";
                message = "the world has not finished loading";
                return false;
            }

            if (_mapRenderScheduled)
            {
                code = "render_pending";
                message = "a render is already about to start";
                return false;
            }

            if (MapUploadState() == "uploading")
            {
                code = "upload_running";
                message = "the picture is being uploaded; try again when that finishes";
                return false;
            }

            if (MapFileBytes() >= 0 && !overwrite)
            {
                code = "map_exists";
                message = MapFileName() + " already exists; ask to overwrite it";
                return false;
            }

            var online = RealPlayersOnline();
            if (online > 0 && !ignorePlayers)
            {
                code = "players_online";
                message = online + " player(s) online; the render freezes the game for about a minute";
                return false;
            }

            _mapRenderScheduled = true;
            timer.Once(delaySeconds, DoMapRender);
            return true;
        }

        // The render itself: runs the game's own command, which returns only when the picture is written.
        internal void DoMapRender()
        {
            _mapRenderScheduled = false;

            var started = Stopwatch.GetTimestamp();
            string error = null;
            try
            {
                RunServerCommand("world.rendermap");
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            var millis = (long)((Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);
            var bytes = MapFileBytes();
            var ok = error == null && bytes > 0;
            MapLastRenderJson = "{\"ok\":" + (ok ? "true" : "false") + ",\"ms\":" + millis + ",\"bytes\":" + (bytes < 0 ? 0 : bytes)
                + ",\"atMs\":" + NowMs() + (error == null ? "" : ",\"error\":" + ArchonJson.Quote(error)) + "}";
            Puts("Map render " + (ok ? "finished" : "did not produce a file") + " in " + millis + " ms (" + MapFileName() + ")");
        }

        // When the plugin loads (which the game also does for a plugin loaded or updated while running) and the switch is
        // on: a world with no map yet, and nobody to freeze, gets one. A few seconds' delay lets the other plugins finish
        // loading first.
        private void OnServerInitialized()
        {
            ConsiderAutoRender();
        }

        internal void ConsiderAutoRender()
        {
            if (!Settings.Map || !MapWorldKnown() || MapFileBytes() >= 0) { return; }

            string code;
            string message;
            if (TryScheduleMapRender(false, false, 10f, out code, out message))
            {
                Puts("No map for this world yet and nobody is online: rendering it shortly.");
            }
        }

        // archon.map.render [overwrite] [override] - "overwrite" replaces an existing picture, "override" goes ahead even
        // with players online. Replies at once; the render starts a second later.
        [ConsoleCommand("archon.map.render")]
        internal void CmdMapRender(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) { return; }

            var overwrite = false;
            var ignorePlayers = false;
            for (var i = 0; arg.HasArgs(i + 1); i++)
            {
                var word = arg.GetString(i, "").ToLowerInvariant();
                if (word == "overwrite") { overwrite = true; }
                else if (word == "override") { ignorePlayers = true; }
                else
                {
                    arg.ReplyWith(ArchonJson.Err("usage", "archon.map.render [overwrite] [override]"));
                    return;
                }
            }

            string code;
            string message;
            if (!TryScheduleMapRender(overwrite, ignorePlayers, 1f, out code, out message))
            {
                arg.ReplyWith(ArchonJson.Err(code, message));
                return;
            }

            arg.ReplyWith(ArchonJson.Ok("{\"started\":true,\"file\":" + ArchonJson.Quote(MapFileName()) + "}"));
        }

        // archon.map.status - what the Worker needs to know about the map: which world, which file, whether it exists.
        [ConsoleCommand("archon.map.status")]
        internal void CmdMapStatus(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) { return; }

            var known = MapWorldKnown();
            var bytes = known ? MapFileBytes() : -1;
            arg.ReplyWith(ArchonJson.Ok(
                "{\"world\":{\"known\":" + (known ? "true" : "false") + ",\"size\":" + WorldSize() + ",\"seed\":" + WorldSeed() + "}"
                + ",\"file\":" + ArchonJson.Quote(known ? MapFileName() : "")
                + ",\"exists\":" + (bytes >= 0 ? "true" : "false")
                + ",\"bytes\":" + (bytes < 0 ? 0 : bytes)
                + ",\"rendering\":" + (_mapRenderScheduled ? "true" : "false")
                + ",\"auto\":" + (Settings.Map ? "true" : "false")
                + ",\"lastRender\":" + MapLastRenderJson
                + ",\"upload\":" + MapUploadJson() + "}"));
        }

        // ---- map upload ----------------------------------------------------------------------------------------
        //
        // The plugin holds no standing credential and does not decide when to upload: the Panel's Api does, and hands the
        // Worker a one-time address to pass on here. The picture is streamed from a background thread (it is tens of
        // megabytes; the game must not wait for it). Only the file for the current world can be sent, and only to an
        // http(s) address.

        private readonly object _uploadLock = new object();
        private string _uploadState = "idle";      // idle | uploading | done | failed
        private string _uploadFile = "";
        private long _uploadBytes;
        private long _uploadAtMs;
        private string _uploadError = "";

        // Where the upload runs. A field only so a test can run it inline; nothing at runtime assigns it.
        internal Action<Action> RunInBackground = delegate (Action work) { System.Threading.ThreadPool.QueueUserWorkItem(delegate { work(); }); };

        internal string MapUploadState()
        {
            lock (_uploadLock) { return _uploadState; }
        }

        internal string MapUploadJson()
        {
            lock (_uploadLock)
            {
                if (_uploadState == "idle") { return "null"; }

                return "{\"state\":" + ArchonJson.Quote(_uploadState) + ",\"file\":" + ArchonJson.Quote(_uploadFile)
                    + ",\"bytes\":" + _uploadBytes + ",\"atMs\":" + _uploadAtMs
                    + (_uploadError.Length == 0 ? "" : ",\"error\":" + ArchonJson.Quote(_uploadError)) + "}";
            }
        }

        // archon.map.upload <url> <token> - sends the current world's picture to <url> (POST, raw PNG), with the one-time
        // <token> in a header (not in the address, so it is not recorded wherever addresses are). Replies at once; the outcome
        // shows in archon.map.status.
        [ConsoleCommand("archon.map.upload")]
        internal void CmdMapUpload(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) { return; }

            if (!arg.HasArgs(2) || arg.HasArgs(3))
            {
                arg.ReplyWith(ArchonJson.Err("usage", "archon.map.upload <url> <token>"));
                return;
            }

            Uri uri;
            if (!ArchonUpload.TryParseUrl(arg.GetString(0, ""), out uri))
            {
                arg.ReplyWith(ArchonJson.Err("bad_url", "the address must be an absolute http or https URL"));
                return;
            }

            var token = arg.GetString(1, "");
            if (!ArchonUpload.IsValidToken(token))
            {
                arg.ReplyWith(ArchonJson.Err("bad_token", "the token must be 1 to 128 letters, digits, - or _"));
                return;
            }

            if (!MapWorldKnown())
            {
                arg.ReplyWith(ArchonJson.Err("no_world", "the world has not finished loading"));
                return;
            }

            var bytes = MapFileBytes();
            if (bytes <= 0)
            {
                arg.ReplyWith(ArchonJson.Err("no_map", "there is no picture for this world yet"));
                return;
            }

            if (_mapRenderScheduled)
            {
                arg.ReplyWith(ArchonJson.Err("render_pending", "a render is about to start; upload after it"));
                return;
            }

            var file = MapFileName();
            var path = MapFilePath();
            lock (_uploadLock)
            {
                if (_uploadState == "uploading")
                {
                    arg.ReplyWith(ArchonJson.Err("upload_running", "an upload is already in progress"));
                    return;
                }

                _uploadState = "uploading";
                _uploadFile = file;
                _uploadBytes = bytes;
                _uploadAtMs = NowMs();
                _uploadError = "";
            }

            RunInBackground(delegate
            {
                // On a worker thread: nothing here may touch the game (no Puts, no entities), only the file and the network.
                var error = ArchonUpload.Post(uri, token, path, bytes);
                lock (_uploadLock)
                {
                    _uploadState = error == null ? "done" : "failed";
                    _uploadError = error ?? "";
                    _uploadAtMs = NowMs();
                }
            });

            arg.ReplyWith(ArchonJson.Ok("{\"started\":true,\"file\":" + ArchonJson.Quote(file) + ",\"bytes\":" + bytes + "}"));
        }

        // archon.map.monuments - the named places on the map (only those the game itself shows), for drawing over the image.
        [ConsoleCommand("archon.map.monuments")]
        internal void CmdMapMonuments(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) { return; }

            var sb = new StringBuilder(2048);
            var first = true;
            IEnumerable<MonumentInfo> monuments;
            try
            {
                monuments = MonumentSource();
            }
            catch (Exception ex)
            {
                arg.ReplyWith(ArchonJson.Err("unavailable", ex.Message));
                return;
            }

            if (monuments != null)
            {
                foreach (var monument in monuments)
                {
                    if (monument == null || !monument.shouldDisplayOnMap) { continue; }

                    var at = monument.transform.position;
                    var name = monument.displayPhrase != null ? monument.displayPhrase.english : "";
                    if (string.IsNullOrEmpty(name)) { continue; }

                    if (!first) { sb.Append(','); }
                    first = false;
                    sb.Append("{\"n\":").Append(ArchonJson.Quote(name));
                    sb.Append(",\"x\":").Append(Num1(at.x)).Append(",\"y\":").Append(Num1(at.y)).Append(",\"z\":").Append(Num1(at.z)).Append('}');
                }
            }

            arg.ReplyWith(ArchonJson.Ok("{\"format\":1,\"size\":" + WorldSize() + ",\"monuments\":[" + sb + "]}"));
        }

        private static string Num1(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) { return "0"; }
            return Math.Round(value, 1).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }

        // archon.events.drain <bootId> <cursor> [max] - the events after <cursor>, oldest first. A bootId that is not
        // this run's (the plugin reloaded since the caller last asked) restarts from the beginning and says so.
        [ConsoleCommand("archon.events.drain")]
        internal void CmdEventsDrain(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) { return; }

            long bootId;
            long cursor;
            if (!arg.HasArgs(2)
                || !long.TryParse(arg.GetString(0, ""), out bootId)
                || !long.TryParse(arg.GetString(1, ""), out cursor)
                || cursor < 0)
            {
                arg.ReplyWith(ArchonJson.Err("usage", "archon.events.drain <bootId> <cursor> [max]"));
                return;
            }

            var max = ArchonCombat.DefaultDrainMax;
            if (arg.HasArgs(3))
            {
                int parsed;
                if (int.TryParse(arg.GetString(2, ""), out parsed) && parsed > 0) { max = parsed; }
            }

            arg.ReplyWith(ArchonJson.Ok(CombatEvents.DrainJson(bootId, cursor, max)));
        }

        // archon.stats - the plugin's own cost counters: how often the damage hooks fired, how many of those involved a
        // real player (and so cost more than a type check), and what is buffered. For measuring, not for the Panel.
        [ConsoleCommand("archon.stats")]
        internal void CmdStats(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) { return; }

            arg.ReplyWith(ArchonJson.Ok(
                "{\"combat\":{\"subscribed\":" + (CombatSubscribed ? "true" : "false")
                + ",\"hookFires\":" + CombatHookFires
                + ",\"recorded\":" + CombatHookRecorded
                + ",\"zeroDamage\":" + CombatHookZeroDamage
                + ",\"throttled\":" + CombatHookThrottled
                + ",\"buffered\":" + CombatEvents.Count
                + ",\"head\":" + CombatEvents.Head
                + ",\"bootId\":" + CombatEvents.BootId + "}"
                + ",\"tcs\":{\"subscribed\":" + (TcSubscribed ? "true" : "false")
                + ",\"ready\":" + (TcIndex.Ready ? "true" : "false")
                + ",\"count\":" + TcIndex.Count
                + ",\"scanned\":" + TcScanned + "}"
                + ",\"positions\":{\"recording\":" + (PositionsRecording ? "true" : "false")
                + ",\"sweeps\":" + PositionSweeps
                + ",\"sampled\":" + PositionSamples
                + ",\"avgSweepMicros\":" + (PositionSweeps == 0 ? 0 : (long)(PositionSweepTicks * 1000000.0 / Stopwatch.Frequency / PositionSweeps))
                + ",\"tracked\":" + _lastPosition.Count
                + ",\"buffered\":" + Positions.Count
                + ",\"head\":" + Positions.Head
                + ",\"bootId\":" + Positions.BootId + "}}"));
        }

        private string HelloData()
        {
            var sb = new StringBuilder();
            sb.Append("{\"plugin\":\"RustArchon\",\"version\":").Append(ArchonJson.Quote(Version.ToString()));
            sb.Append(",\"protocolVersion\":").Append(ProtocolVersion);
            sb.Append(",\"runtime\":").Append(ArchonJson.Quote(Environment.Version.ToString()));
            sb.Append(",\"capabilities\":[");
            for (var i = 0; i < Capabilities.Length; i++)
            {
                if (i > 0) { sb.Append(','); }
                sb.Append(ArchonJson.Quote(Capabilities[i]));
            }
            sb.Append("],\"signing\":").Append(Integrity.ToJson());
            sb.Append(",\"settings\":").Append(Settings.ToJson(_settingsPersisted));
            sb.Append(",\"utc\":").Append(ArchonJson.Quote(DateTime.UtcNow.ToString("o")));
            sb.Append('}');
            return sb.ToString();
        }

        // "version=<x.y.z>" and "utc=<round-trip time>", next to the settings file. Best effort: a failure to write it
        // must never stop the plugin loading (the Updater would then wrongly roll a good update back, so it is worth
        // getting right, but a read-only data folder is the only realistic cause and settings would be failing too).
        internal void WriteLoadedMarker()
        {
            if (SettingsFilePath == null) { return; }

            try
            {
                var directory = Path.GetDirectoryName(SettingsFilePath);
                Directory.CreateDirectory(directory);
                File.WriteAllText(
                    Path.Combine(directory, LoadedMarkerFileName),
                    "version=" + Version + "\nutc=" + DateTime.UtcNow.ToString("o") + "\n");
            }
            catch (Exception)
            {
            }
        }

        // Where our own script lives: the plugins folder of whichever framework is running. Probed, and left null
        // (the integrity state then reads "unlocated") if it is loaded from somewhere else.
        private static string LocateOwnScriptPath()
        {
            var candidates = new[] { "carbon/plugins", "oxide/plugins" };
            foreach (var candidate in candidates)
            {
                var path = Path.Combine(Path.Combine(Environment.CurrentDirectory, candidate), "RustArchon.cs");
                if (File.Exists(path))
                {
                    return path;
                }
            }
            return null;
        }

        // The data folder of whichever framework is running. Probed rather than assumed, and left null (settings
        // then live in memory only and reset on reload) if neither exists.
        private static string LocateSettingsFilePath()
        {
            var candidates = new[] { "carbon/data", "oxide/data" };
            foreach (var candidate in candidates)
            {
                var dir = Path.Combine(Environment.CurrentDirectory, candidate);
                if (Directory.Exists(dir))
                {
                    return Path.Combine(Path.Combine(dir, SettingsDirectoryName), SettingsFileName);
                }
            }
            return null;
        }
    }

    // The two per-server switches the panel controls. Both default to ON: recording is on by default with an
    // opt-out (Scott, 2026-09-19), and the combat log is a must-have. They are switches, not detail levels.
    internal sealed class ArchonSettings
    {
        public bool Recording = true;
        public bool Combat = true;

        // Render the world map by itself when the plugin loads and there is none for this world (a fresh wipe) and nobody
        // is online. On by default (Scott, 2026-09-19); local to the server, not (yet) a Panel setting.
        public bool Map = true;

        // A missing file, or a line we do not understand, leaves the default in place rather than failing:
        // a damaged settings file must not stop the plugin from loading.
        public static ArchonSettings Load(string path)
        {
            var settings = new ArchonSettings();
            try
            {
                if (!File.Exists(path)) { return settings; }

                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    var equals = line.IndexOf('=');
                    if (equals <= 0) { continue; }

                    string ignoredCode;
                    string ignoredMessage;
                    settings.TrySet(line.Substring(0, equals), line.Substring(equals + 1), out ignoredCode, out ignoredMessage);
                }
            }
            catch (Exception)
            {
                return new ArchonSettings();
            }
            return settings;
        }

        public bool TrySave(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "recording=" + Bool(Recording) + "\ncombat=" + Bool(Combat) + "\nmap=" + Bool(Map) + "\n");
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool TrySet(string key, string value, out string code, out string message)
        {
            bool parsed;
            if (!TryParseBool(value, out parsed))
            {
                code = "bad_value";
                message = "value must be true/false (also on/off, 1/0), got '" + value + "'";
                return false;
            }

            switch ((key ?? "").Trim().ToLowerInvariant())
            {
                case "recording":
                    Recording = parsed;
                    break;
                case "combat":
                    Combat = parsed;
                    break;
                case "map":
                    Map = parsed;
                    break;
                default:
                    code = "unknown_key";
                    message = "unknown setting '" + key + "' (expected recording, combat or map)";
                    return false;
            }

            code = null;
            message = null;
            return true;
        }

        public static bool TryParseBool(string value, out bool result)
        {
            result = false;
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "true":
                case "on":
                case "1":
                    result = true;
                    return true;
                case "false":
                case "off":
                case "0":
                    return true;
                default:
                    return false;
            }
        }

        public string ToJson(bool persisted)
        {
            return "{\"recording\":" + Bool(Recording) + ",\"combat\":" + Bool(Combat) + ",\"map\":" + Bool(Map) + ",\"persisted\":" + Bool(persisted) + "}";
        }

        private static string Bool(bool value)
        {
            return value ? "true" : "false";
        }
    }

    // Checks that this very file is the one a Panel signed. The Panel, when it serves the script, puts its own
    // public key into the two constants below and appends one signature line; this reads the file back and verifies
    // that line. It is how a plugin (and later the Updater) knows which Panel it trusts.
    //
    // Signed content: every byte of the file before the final "// RUSTARCHON-SIG-V1: <base64>" line, which must
    // start a line. Scheme: RSA, SHA-256, PKCS#1 v1.5 - the only signature scheme the game server's Mono runtime
    // implements (ECDSA and RSA-PSS throw there). The plugin only ever verifies; it never generates a key (Mono's
    // RSA.Create() ignores the requested key size).
    //
    // States: "valid" (signed by the stamped key), "invalid" (a signature is there and does not verify: the file was
    // altered, re-encoded, or signed by another key), "unsigned" (no key stamped, or no signature line: a developer
    // copy), "unlocated" (the file could not be found to read), "error" (the runtime could not perform the check).
    internal static class ArchonIntegrity
    {
        // Stamped by the Panel when it serves the script. An unstamped copy keeps these placeholder values.
        internal const string TrustedModulus = "@@RUSTARCHON_TRUSTED_MODULUS@@";
        internal const string TrustedExponent = "@@RUSTARCHON_TRUSTED_EXPONENT@@";
        internal const string SignatureMarker = "// RUSTARCHON-SIG-V1: ";

        internal sealed class Result
        {
            public static readonly Result Unlocated = new Result("unlocated", "");

            public readonly string State;
            public readonly string KeyFingerprint;

            public Result(string state, string keyFingerprint)
            {
                State = state;
                KeyFingerprint = keyFingerprint ?? "";
            }

            public string ToJson()
            {
                return "{\"state\":" + ArchonJson.Quote(State) + ",\"keyFingerprint\":" + ArchonJson.Quote(KeyFingerprint) + "}";
            }
        }

        internal static bool IsStamped(string modulusBase64, string exponentBase64)
        {
            return !string.IsNullOrEmpty(modulusBase64) && !string.IsNullOrEmpty(exponentBase64)
                && modulusBase64.IndexOf('@') < 0 && exponentBase64.IndexOf('@') < 0;
        }

        internal static Result CheckFile(string path, string modulusBase64, string exponentBase64)
        {
            if (path == null)
            {
                return Result.Unlocated;
            }

            try
            {
                return Check(File.ReadAllBytes(path), modulusBase64, exponentBase64);
            }
            catch (Exception)
            {
                return new Result("error", IsStamped(modulusBase64, exponentBase64) ? Fingerprint(modulusBase64) : "");
            }
        }

        // Is this file validly signed for the key it embeds? Two file shapes count:
        //   * ordinary: the last line is the signature of everything before it, made with the embedded key;
        //   * bridged: the last line is the PREVIOUS key's signature (what an older Updater checks), and the line
        //     just above it is the embedded key's own signature of everything before THAT. A file bridged to a new
        //     key is signed by the old key for the Updater and co-signed by the new key so it can vouch for itself.
        internal static Result Check(byte[] file, string modulusBase64, string exponentBase64)
        {
            var last = CheckLastLine(file, modulusBase64, exponentBase64);
            if (last.State == "valid" || last.State == "unsigned")
            {
                return last;
            }

            var cosigned = CheckCosignature(file, modulusBase64, exponentBase64);
            return cosigned.State == "valid" ? cosigned : last;
        }

        // Only the very last line counts: is it a signature, over everything before it, by this key? This is the
        // check an Updater applies to a download, with ITS trusted key.
        internal static Result CheckLastLine(byte[] file, string modulusBase64, string exponentBase64)
        {
            if (!IsStamped(modulusBase64, exponentBase64))
            {
                return new Result("unsigned", "");
            }

            var fingerprint = Fingerprint(modulusBase64);
            var markerAt = LastLineStartOf(file, Encoding.ASCII.GetBytes(SignatureMarker));
            return markerAt < 0 ? new Result("unsigned", fingerprint) : VerifyLineAt(file, markerAt, modulusBase64, exponentBase64);
        }

        // Only the signature line directly above the last one: is it a signature, over everything before IT, by this
        // key? "unsigned" when the file has no such second signature line.
        internal static Result CheckCosignature(byte[] file, string modulusBase64, string exponentBase64)
        {
            if (!IsStamped(modulusBase64, exponentBase64))
            {
                return new Result("unsigned", "");
            }

            var fingerprint = Fingerprint(modulusBase64);
            var marker = Encoding.ASCII.GetBytes(SignatureMarker);
            var lastAt = LastLineStartOf(file, marker);
            var aboveAt = lastAt <= 0 ? -1 : LineAbove(file, lastAt);
            if (aboveAt < 0 || !StartsWithAt(file, aboveAt, marker))
            {
                return new Result("unsigned", fingerprint);
            }

            return VerifyLineAt(file, aboveAt, modulusBase64, exponentBase64);
        }

        // The signature on the line starting at markerAt, checked against everything before that line.
        private static Result VerifyLineAt(byte[] file, int markerAt, string modulusBase64, string exponentBase64)
        {
            var fingerprint = Fingerprint(modulusBase64);
            byte[] signature;
            try
            {
                var start = markerAt + SignatureMarker.Length;
                var end = start;
                while (end < file.Length && file[end] != (byte)'\n' && file[end] != (byte)'\r') { end++; }
                signature = Convert.FromBase64String(Encoding.ASCII.GetString(file, start, end - start).Trim());
            }
            catch (FormatException)
            {
                return new Result("invalid", fingerprint); // a signature line is present but is not base64
            }

            try
            {
                var payload = new byte[markerAt];
                Buffer.BlockCopy(file, 0, payload, 0, markerAt);

                using (var rsa = RSA.Create())
                {
                    rsa.ImportParameters(new RSAParameters
                    {
                        Modulus = Convert.FromBase64String(modulusBase64),
                        Exponent = Convert.FromBase64String(exponentBase64)
                    });
                    var ok = rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    return new Result(ok ? "valid" : "invalid", fingerprint);
                }
            }
            catch (Exception)
            {
                return new Result("error", fingerprint);
            }
        }

        // Where the line above the one starting at lineStart begins, or -1 if there is none.
        private static int LineAbove(byte[] file, int lineStart)
        {
            if (lineStart <= 0 || file[lineStart - 1] != (byte)'\n') { return -1; }

            var i = lineStart - 2;
            while (i >= 0 && file[i] != (byte)'\n') { i--; }
            return i + 1;
        }

        private static bool StartsWithAt(byte[] file, int at, byte[] text)
        {
            if (at < 0 || at + text.Length > file.Length) { return false; }
            for (var j = 0; j < text.Length; j++)
            {
                if (file[at + j] != text[j]) { return false; }
            }
            return true;
        }

        // First 16 hex characters of SHA-256 over the public modulus: enough to tell two keys apart at a glance,
        // and safe to show and log (it is derived from the public half only).
        internal static string Fingerprint(string modulusBase64)
        {
            try
            {
                using (var sha = SHA256.Create())
                {
                    var hash = sha.ComputeHash(Convert.FromBase64String(modulusBase64));
                    var sb = new StringBuilder(16);
                    for (var i = 0; i < 8; i++) { sb.Append(hash[i].ToString("x2")); }
                    return sb.ToString();
                }
            }
            catch (Exception)
            {
                return "";
            }
        }

        // The last place the marker begins a line - so the marker text appearing inside the source (in the constant
        // above, mid-line) is never mistaken for the signature line.
        private static int LastLineStartOf(byte[] file, byte[] marker)
        {
            for (var i = file.Length - marker.Length; i >= 0; i--)
            {
                if (i != 0 && file[i - 1] != (byte)'\n') { continue; }

                var match = true;
                for (var j = 0; j < marker.Length; j++)
                {
                    if (file[i + j] != marker[j]) { match = false; break; }
                }
                if (match) { return i; }
            }
            return -1;
        }
    }

    // A tiny JSON writer: the game server's runtime cannot be assumed to give a plugin a JSON library, and the
    // envelope is small and fixed.
    // The damage record and its buffer. Pure C# (no game types) so it is fully unit tested; the hook glue in the plugin
    // class converts game objects into Events.
    internal static class ArchonCombat
    {
        // About 20,000 events is a few MB: minutes to hours of even a busy fight, and the Worker drains every ~30 s.
        internal const int DefaultCapacity = 20000;
        internal const int DefaultDrainMax = 200;
        internal const int HardDrainMax = 500;

        // Stops a reply growing past what one RCON frame comfortably carries, whatever the events contain.
        internal const int MaxReplyChars = 60000;

        // The first SteamID64 of the individual account range. Below it is an NPC or an internal entity id; an
        // NPC "player" (scientist, bandit guard) never has one, so this tells a person from an NPC without needing
        // any other game API.
        internal const ulong FirstSteamId = 76561197960265728UL;

        internal static bool IsRealPlayer(ulong userId)
        {
            return userId >= FirstSteamId;
        }

        internal static long NowUnixMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        internal sealed class Event : IArchonRingItem
        {
            public long Seq { get; set; }
            public long UnixMs;
            public string Kind = "hit";
            public string Attacker = "";
            public string AttackerName = "";
            public bool AttackerIsPlayer;
            public string Victim = "";
            public string VictimName = "";
            public bool VictimIsPlayer;
            public string Weapon = "";
            public float Damage;
            public string DamageType = "";
            public bool Headshot;
            public float Distance;
            public bool HasAttackerPosition;
            public float AttackerX, AttackerY, AttackerZ;
            public float VictimX, VictimY, VictimZ;

            // One compact JSON object. Keys are short because every event is stored: t time, k kind, a/an attacker id
            // and name, ap attacker is a player, v/vn/vp victim, w weapon, d damage, dt damage type, hs headshot,
            // dist distance, apos/vpos positions.
            public string ToJson()
            {
                var sb = new StringBuilder(220);
                sb.Append("{\"s\":").Append(Seq);
                sb.Append(",\"t\":").Append(UnixMs);
                sb.Append(",\"k\":").Append(ArchonJson.Quote(Kind));
                sb.Append(",\"a\":").Append(ArchonJson.Quote(Attacker));
                if (AttackerName.Length > 0) { sb.Append(",\"an\":").Append(ArchonJson.Quote(AttackerName)); }
                sb.Append(",\"ap\":").Append(AttackerIsPlayer ? "true" : "false");
                sb.Append(",\"v\":").Append(ArchonJson.Quote(Victim));
                if (VictimName.Length > 0) { sb.Append(",\"vn\":").Append(ArchonJson.Quote(VictimName)); }
                sb.Append(",\"vp\":").Append(VictimIsPlayer ? "true" : "false");
                if (Weapon.Length > 0) { sb.Append(",\"w\":").Append(ArchonJson.Quote(Weapon)); }
                sb.Append(",\"d\":").Append(Num(Damage));
                if (DamageType.Length > 0) { sb.Append(",\"dt\":").Append(ArchonJson.Quote(DamageType)); }
                if (Headshot) { sb.Append(",\"hs\":true"); }
                if (HasAttackerPosition)
                {
                    sb.Append(",\"dist\":").Append(Num(Distance));
                    sb.Append(",\"apos\":[").Append(Num(AttackerX)).Append(',').Append(Num(AttackerY)).Append(',').Append(Num(AttackerZ)).Append(']');
                }
                sb.Append(",\"vpos\":[").Append(Num(VictimX)).Append(',').Append(Num(VictimY)).Append(',').Append(Num(VictimZ)).Append(']');
                sb.Append('}');
                return sb.ToString();
            }

            // One decimal place, invariant, never scientific notation: valid JSON whatever the server's culture.
            private static string Num(float value)
            {
                if (float.IsNaN(value) || float.IsInfinity(value)) { return "0"; }
                return Math.Round(value, 1).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        // The combat log's ring: its items are "events".
        internal sealed class Buffer : ArchonRing<Event>
        {
            public Buffer(int capacity) : base(capacity, "events") { }
        }
    }

    internal interface IArchonRingItem
    {
        long Seq { get; set; }
        string ToJson();
    }

    // A fixed-size ring of items with a sequence number. Once full, the oldest item is overwritten - bounded memory beats
    // keeping everything - and a consumer that fell behind is told (lost) instead of being given a silent gap. Single
    // writer (the game thread) and single reader (a console command on the game thread), so no locking is needed. Shared
    // by the combat log ("events") and the position recorder ("samples").
    internal class ArchonRing<T> where T : class, IArchonRingItem
    {
        private readonly T[] _ring;
        private readonly string _itemsName;
        private long _next = 1;

        public ArchonRing(int capacity, string itemsName)
        {
            _ring = new T[capacity < 1 ? 1 : capacity];
            _itemsName = itemsName;
            BootId = NewBootId();
        }

        // Identifies this run of the plugin: sequence numbers restart at 1 every time it loads, so a consumer's
        // cursor is only meaningful together with the bootId it came from.
        public long BootId;

        public long Head { get { return _next - 1; } }

        public int Count { get { return (int)Math.Min(_next - 1, _ring.Length); } }

        public long Add(T item)
        {
            item.Seq = _next;
            _ring[(int)((_next - 1) % _ring.Length)] = item;
            _next++;
            return item.Seq;
        }

        // {"format":1,"bootId":..,"head":..,"cursor":..,"lost":..,"reset":..,"<items>":[..]}. cursor is the last
        // sequence number in this reply (or the one asked for, if there was nothing new): send it back next time.
        public string DrainJson(long bootId, long cursor, int max)
        {
            var reset = bootId != BootId;
            if (reset) { cursor = 0; }
            if (max > ArchonCombat.HardDrainMax) { max = ArchonCombat.HardDrainMax; }
            if (max < 1) { max = 1; }

            var oldest = _next - Count;                 // the lowest sequence still in the ring
            var lost = oldest > cursor + 1;
            var first = lost ? oldest : cursor + 1;

            var sb = new StringBuilder(1024);
            var last = cursor;
            var taken = 0;
            for (var seq = first; seq < _next && taken < max; seq++)
            {
                var json = _ring[(int)((seq - 1) % _ring.Length)].ToJson();
                if (taken > 0 && sb.Length + json.Length > ArchonCombat.MaxReplyChars) { break; }

                if (taken > 0) { sb.Append(','); }
                sb.Append(json);
                last = seq;
                taken++;
            }

            return "{\"format\":1,\"bootId\":" + BootId + ",\"head\":" + Head + ",\"cursor\":" + last
                + ",\"lost\":" + (lost ? "true" : "false") + ",\"reset\":" + (reset ? "true" : "false")
                + ",\"" + _itemsName + "\":[" + sb + "]}";
        }

        private static long NewBootId()
        {
            // Not secret and not security relevant: only has to differ between two runs of the plugin.
            var bytes = Guid.NewGuid().ToByteArray();
            return Math.Abs(BitConverter.ToInt64(bytes, 0) % 9007199254740000L) + 1; // fits a JSON number exactly
        }
    }

    // Player positions. The sample and its buffer are pure C# (no game types) so they are fully unit tested; the timer
    // and hook glue in the plugin class turns players into Samples.
    internal static class ArchonPositions
    {
        // 30,000 samples is a few MB: a busy server (100 players, one sample each ~5 s) holds about 25 minutes, and the
        // Worker drains every ~30 s.
        internal const int DefaultCapacity = 30000;

        // How often the players are looked at, in seconds.
        internal const float SampleSeconds = 5f;

        // A player who has not moved this far (metres) since their last sample is not sampled again - a moving player
        // produces samples, an idle one does not - but is still sampled once a minute so a replay knows they are there.
        internal const float MinMoveMetres = 0.5f;
        internal const long HeartbeatMs = 60000;

        // Bounds the last-sample table if a logout hook was ever missed.
        internal const int MaxTracked = 5000;

        internal struct Last
        {
            public long UnixMs;
            public float X, Y, Z;
        }

        internal static bool ShouldRecord(Last last, float x, float y, float z, long nowMs)
        {
            if (nowMs - last.UnixMs >= HeartbeatMs) { return true; }

            var dx = x - last.X; var dy = y - last.Y; var dz = z - last.Z;
            return dx * dx + dy * dy + dz * dz >= MinMoveMetres * MinMoveMetres;
        }

        internal sealed class Sample : IArchonRingItem
        {
            public long Seq { get; set; }
            public long UnixMs;
            public string PlayerId = "";
            public string Name = "";
            public float X, Y, Z;
            public float Yaw;

            // "on" the first sample after they appeared, "off" the last one as they left; empty otherwise.
            public string Marker = "";

            // s sequence, t time, p player id, x/y/z position, r yaw in whole degrees, n name (only when it is worth
            // repeating: first sample and heartbeats), e marker.
            public string ToJson()
            {
                var sb = new StringBuilder(110);
                sb.Append("{\"s\":").Append(Seq);
                sb.Append(",\"t\":").Append(UnixMs);
                sb.Append(",\"p\":").Append(ArchonJson.Quote(PlayerId));
                sb.Append(",\"x\":").Append(Num(X)).Append(",\"y\":").Append(Num(Y)).Append(",\"z\":").Append(Num(Z));
                sb.Append(",\"r\":").Append(Degrees(Yaw));
                if (Name.Length > 0) { sb.Append(",\"n\":").Append(ArchonJson.Quote(Name)); }
                if (Marker.Length > 0) { sb.Append(",\"e\":").Append(ArchonJson.Quote(Marker)); }
                sb.Append('}');
                return sb.ToString();
            }

            private static string Num(float value)
            {
                if (float.IsNaN(value) || float.IsInfinity(value)) { return "0"; }
                return Math.Round(value, 1).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            }

            private static string Degrees(float value)
            {
                if (float.IsNaN(value) || float.IsInfinity(value)) { return "0"; }
                var wrapped = value % 360f;
                if (wrapped < 0) { wrapped += 360f; }
                return ((int)Math.Round(wrapped)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        internal sealed class Buffer : ArchonRing<Sample>
        {
            public Buffer(int capacity) : base(capacity, "samples") { }
        }
    }

    // Sends a file to an address. HttpWebRequest rather than HttpClient: the long-standing choice on the game server's
    // Mono runtime (the Updater downloads the same way). Streams from disk, so the picture is never held in memory.
    internal static class ArchonUpload
    {
        internal const int MaxUrlLength = 2048;
        internal const int MaxTokenLength = 128;
        internal const string TokenHeader = "X-RustArchon-Upload-Token";
        internal const int TimeoutMs = 30000;
        internal const int ReadWriteTimeoutMs = 120000;

        internal static bool TryParseUrl(string text, out Uri uri)
        {
            uri = null;
            if (string.IsNullOrEmpty(text) || text.Length > MaxUrlLength) { return false; }

            Uri parsed;
            if (!Uri.TryCreate(text, UriKind.Absolute, out parsed)) { return false; }
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) { return false; }
            if (parsed.UserInfo.Length > 0) { return false; }

            uri = parsed;
            return true;
        }

        // A token is base64url text. Anything else (in particular a line break) is refused, so it can never be a way to inject
        // another header.
        internal static bool IsValidToken(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength) { return false; }

            for (var i = 0; i < token.Length; i++)
            {
                var c = token[i];
                var ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok) { return false; }
            }

            return true;
        }

        // Returns null on success, else a short reason. Never throws.
        internal static string Post(Uri uri, string token, string path, long length)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(uri);
                request.Method = "POST";
                request.ContentType = "image/png";
                request.Headers[TokenHeader] = token;
                request.ContentLength = length;
                request.AllowWriteStreamBuffering = false;
                request.AllowAutoRedirect = false;
                request.KeepAlive = false;
                request.Timeout = TimeoutMs;
                request.ReadWriteTimeout = ReadWriteTimeoutMs;

                using (var input = File.OpenRead(path))
                using (var output = request.GetRequestStream())
                {
                    var buffer = new byte[65536];
                    var remaining = length;
                    while (remaining > 0)
                    {
                        var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (read <= 0) { return "the file ended early"; }
                        output.Write(buffer, 0, read);
                        remaining -= read;
                    }
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    var code = (int)response.StatusCode;
                    return code >= 200 && code < 300 ? null : "http " + code;
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                return response != null ? "http " + (int)response.StatusCode : ex.Status.ToString();
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }
    }

    // The tool cupboard index. Holds the cupboards themselves (not copies) so the authorized list is always current,
    // and turns them into JSON on request. Only cupboards owned by a real player are kept: an NPC or a monument's
    // cupboard is not somebody's base.
    internal static class ArchonTcs
    {
        internal const int DefaultPageSize = 200;
        internal const int HardPageSize = 500;

        internal sealed class Index
        {
            private readonly Dictionary<int, BuildingPrivlidge> _byId = new Dictionary<int, BuildingPrivlidge>();

            // True once the initial scan of the world has finished. Until then the list may be incomplete, and says so.
            public bool Ready;

            public int Count { get { return _byId.Count; } }

            public void Clear()
            {
                _byId.Clear();
                Ready = false;
            }

            public void Add(BuildingPrivlidge privilege)
            {
                if (privilege == null || !ArchonCombat.IsRealPlayer(privilege.OwnerID)) { return; }
                _byId[privilege.GetInstanceID()] = privilege;
            }

            public void Remove(BuildingPrivlidge privilege)
            {
                if (privilege != null) { _byId.Remove(privilege.GetInstanceID()); }
            }

            // {"format":1,"ready":..,"total":..,"offset":..,"next":..,"tcs":[..]}. next is where to continue; the caller is
            // done when next reaches total. The order is by id so a page boundary is stable while nothing changes; cupboards
            // built or destroyed between two pages can shift it, and a caller that cares asks again from the start.
            public string PageJson(int offset, int max)
            {
                if (max > HardPageSize) { max = HardPageSize; }
                if (max < 1) { max = 1; }

                var ids = new List<int>(_byId.Keys);
                ids.Sort();

                var sb = new StringBuilder(1024);
                var taken = 0;
                var index = offset;
                while (index < ids.Count && taken < max)
                {
                    BuildingPrivlidge privilege;
                    if (!_byId.TryGetValue(ids[index], out privilege)) { index++; continue; }

                    var json = privilege.IsDestroyed ? null : TcJson(ids[index], privilege);
                    if (json != null)
                    {
                        if (taken > 0 && sb.Length + json.Length > ArchonCombat.MaxReplyChars) { break; }
                        if (taken > 0) { sb.Append(','); }
                        sb.Append(json);
                        taken++;
                    }

                    index++;
                }

                return "{\"format\":1,\"ready\":" + (Ready ? "true" : "false") + ",\"total\":" + ids.Count
                    + ",\"offset\":" + offset + ",\"next\":" + index + ",\"tcs\":[" + sb + "]}";
            }

            private static string TcJson(int id, BuildingPrivlidge privilege)
            {
                var at = privilege.transform.position;
                var sb = new StringBuilder(160);
                sb.Append("{\"i\":").Append(id);
                sb.Append(",\"x\":").Append(Num(at.x)).Append(",\"y\":").Append(Num(at.y)).Append(",\"z\":").Append(Num(at.z));
                sb.Append(",\"o\":").Append(ArchonJson.Quote(privilege.OwnerID.ToString()));
                sb.Append(",\"a\":[");

                var first = true;
                // The game keeps only the ids (a HashSet<ulong>). A name is added when that player is in the world right
                // now, awake or asleep; anyone else is listed by id alone and the Panel shows the id.
                foreach (var authorizedId in privilege.authorizedPlayers)
                {
                    if (!first) { sb.Append(','); }
                    first = false;
                    sb.Append("{\"i\":").Append(ArchonJson.Quote(authorizedId.ToString()));
                    var known = BasePlayer.FindAwakeOrSleepingByID(authorizedId);
                    if (known != null && !string.IsNullOrEmpty(known.displayName)) { sb.Append(",\"n\":").Append(ArchonJson.Quote(known.displayName)); }
                    sb.Append('}');
                }

                sb.Append("]}");
                return sb.ToString();
            }

            private static string Num(float value)
            {
                if (float.IsNaN(value) || float.IsInfinity(value)) { return "0"; }
                return Math.Round(value, 1).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }

    internal static class ArchonJson
    {
        public static string Ok(string dataJson)
        {
            return "{\"v\":1,\"ok\":true,\"data\":" + dataJson + "}";
        }

        public static string Err(string code, string message)
        {
            return "{\"v\":1,\"ok\":false,\"err\":" + Quote(code) + ",\"message\":" + Quote(message) + "}";
        }

        public static string Quote(string value)
        {
            var sb = new StringBuilder(value == null ? 2 : value.Length + 2);
            sb.Append('"');
            if (value != null)
            {
                foreach (var c in value)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < ' ')
                            {
                                sb.Append("\\u").Append(((int)c).ToString("x4"));
                            }
                            else
                            {
                                sb.Append(c);
                            }
                            break;
                    }
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
