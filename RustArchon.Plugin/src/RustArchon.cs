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
    [Info("RustArchon", "RustArchon", "0.10.0")]
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
        private static readonly string[] Capabilities = { "config", "combat", "tcs", "positions", "map", "updates", "updater-update", "thirdparty-update" };

        // Advertised right after "thirdparty-update", and only when the zip reader can actually be resolved at run time (see ArchonZipArchive):
        // the file is compiled by the game server's own compiler, which may not reference System.IO.Compression, so nothing here depends on it
        // at compile time. A server that cannot load it keeps every other capability and only loses this one.
        internal const string ThirdPartyZipCapability = "thirdparty-zip";

        // Set by Init unless something (a test) supplied a path first.
        internal string SettingsFilePath;
        internal string ScriptFilePath;
        // The key this plugin trusts: stamped in by the Panel that served it. Fields, not constants, only so a test can supply one;
        // nothing at runtime ever assigns them.
        internal string TrustedModulus = ArchonIntegrity.TrustedModulus;
        internal string TrustedExponent = ArchonIntegrity.TrustedExponent;

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
            Integrity = ArchonIntegrity.CheckFile(ScriptFilePath, TrustedModulus, TrustedExponent);

            WriteLoadedMarker();
            ResumeUpdaterSwap();
            ResumeThirdPartySwap();

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

        // ---- plugin update notices ---------------------------------------------------------------------------
        //
        // UpdateChecker (a third-party plugin, on servers that run it) calls OnUpdateCheckerUpdateFound once per outdated plugin
        // each time it scans. This keeps what it says - the plugin, the installed and newest versions, the marketplace and the
        // address of the plugin's page there (a page to visit, not a file to download) - and hands it to the Worker on request.
        // It is a table of the latest notice per plugin, not a history: UpdateChecker says it again at every scan, so a plugin that
        // reloads simply hears it again. Nothing here does any work unless UpdateChecker is loaded, so the hook stays subscribed.

        internal readonly ArchonUpdates.Store UpdateNotices = new ArchonUpdates.Store();

        private void OnUpdateCheckerUpdateFound(string name, string currentVersion, string latestVersion, string url, string marketplace)
        {
            var isNew = UpdateNotices.Record(name, currentVersion, latestVersion, url, marketplace, NowMs());
            if (isNew)
            {
                Puts("Update available (reported by UpdateChecker): " + ArchonUpdates.Clean(name, ArchonUpdates.MaxNameLength)
                    + " " + ArchonUpdates.Clean(currentVersion, ArchonUpdates.MaxVersionLength)
                    + " -> " + ArchonUpdates.Clean(latestVersion, ArchonUpdates.MaxVersionLength)
                    + " marketplace=" + ArchonUpdates.Clean(marketplace, ArchonUpdates.MaxMarketplaceLength)
                    + " url=" + ArchonUpdates.Clean(url, ArchonUpdates.MaxUrlLength));
            }
        }

        // archon.updates - the update notices UpdateChecker has reported since this plugin loaded.
        [ConsoleCommand("archon.updates")]
        internal void CmdUpdates(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) { return; }

            arg.ReplyWith(ArchonJson.Ok(UpdateNotices.ToJson()));
        }

        // ---- updating the Updater ----------------------------------------------------------------------------
        //
        // The Updater replaces this plugin; this plugin replaces the Updater. Each is the other's way back: an Updater that came up
        // broken (it failed to compile, or crashed in Init) cannot restore itself, so THIS plugin, which is running, puts the previous
        // Updater file back if the new one does not report loading in time. That is why the Updater is only replaced from here and never
        // by itself, and only while this plugin is running and verified.
        //
        // The new Updater is accepted only if its last-line signature verifies under the key THIS plugin trusts (the key stamped into
        // this file, which must itself verify), its [Info] version is the one asked for, and it is newer than the one installed. The
        // download is capped, refuses redirects and carries the one-time token in a header.

        internal const string UpdaterScriptFileName = "RustArchonUpdater.cs";
        internal const string UpdaterSwapFileName = "updater-swap.txt";
        internal const string UpdaterLoadedMarkerFileName = "updater-loaded.txt";
        internal const string UpdaterOwnStatusFileName = "update-status.txt";
        internal const int UpdaterLoadWaitSeconds = 45;

        // Where things are. Resolved by Init from where this file and its settings live, unless a test supplied them first.
        internal string PluginDirectory;
        internal string DataDirectory;
        internal Func<string, string, byte[]> UpdaterDownload = ArchonUpdaterSwap.DefaultDownload;
        internal Func<DateTime> UtcNow = delegate { return DateTime.UtcNow; };
        internal ArchonUpdaterSwap.State UpdaterSwap = new ArchonUpdaterSwap.State();
        private ArchonUpdaterSwap.Download _updaterDownload;
        private Timer _updaterTimer;

        // archon.updater.update <version> <url> <token>
        [ConsoleCommand("archon.updater.update")]
        internal void CmdUpdaterUpdate(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) { return; }

            if (!arg.HasArgs(3) || arg.HasArgs(4))
            {
                arg.ReplyWith(ArchonJson.Err("usage", "archon.updater.update <version> <url> <token>"));
                return;
            }

            arg.ReplyWith(BeginUpdaterUpdate(arg.GetString(0, ""), arg.GetString(1, ""), arg.GetString(2, "")));
        }

        // archon.updater.status
        [ConsoleCommand("archon.updater.status")]
        internal void CmdUpdaterStatus(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) { return; }

            var installed = ReadInstalledUpdaterVersion();
            arg.ReplyWith(ArchonJson.Ok(
                "{\"phase\":" + ArchonJson.Quote(UpdaterSwap.Phase)
                + ",\"targetVersion\":" + ArchonJson.Quote(UpdaterSwap.Target)
                + ",\"previousVersion\":" + ArchonJson.Quote(UpdaterSwap.Previous)
                + ",\"installedVersion\":" + ArchonJson.Quote(installed ?? "")
                + ",\"reason\":" + ArchonJson.Quote(UpdaterSwap.Reason) + "}"));
        }

        // Validates the request and starts the download. Returns the JSON reply.
        internal string BeginUpdaterUpdate(string version, string url, string token)
        {
            if (UpdaterSwap.Phase == ArchonUpdaterSwap.PhaseDownloading || UpdaterSwap.Phase == ArchonUpdaterSwap.PhaseLoading)
            {
                return ArchonJson.Err("busy", "an Updater update is already in progress (" + UpdaterSwap.Phase + ")");
            }

            // Fail closed: nothing is installed on the say-so of a plugin that cannot vouch for its own file.
            if (Integrity.State != "valid" || !ArchonIntegrity.IsStamped(TrustedModulus, TrustedExponent))
            {
                return ArchonJson.Err("not_verified", "this plugin does not verify under its own key (" + Integrity.State + "), so it will not install anything");
            }

            if (!ArchonUpdaterSwap.IsVersion(version))
            {
                return ArchonJson.Err("bad_version", "version must look like 1.2.3");
            }

            Uri uri;
            if (!ArchonUpload.TryParseUrl(url, out uri))
            {
                return ArchonJson.Err("bad_url", "url must be an absolute http or https address with no credentials in it");
            }

            if (!ArchonUpload.IsValidToken(token))
            {
                return ArchonJson.Err("bad_token", "the token must be 1 to " + ArchonUpload.MaxTokenLength + " letters, digits, dashes or underscores");
            }

            var path = UpdaterScriptPathOrNull();
            if (path == null || !Directory.Exists(PluginDirectory))
            {
                return ArchonJson.Err("plugins_folder_unknown", "the plugins folder this plugin lives in could not be found");
            }

            // Never swap while the Updater is in the middle of replacing this plugin: each would be watching the other move.
            if (MainUpdateInProgress())
            {
                return ArchonJson.Err("busy", "the Updater is updating this plugin right now");
            }

            // No Updater file at all is a fresh install (there is nothing to be newer than, and nothing to put back on failure - the new
            // file is simply removed). One that exists must be readable and older than what is offered.
            var present = File.Exists(path);
            var installed = present ? ReadInstalledUpdaterVersion() : "";
            if (present && installed == null)
            {
                return ArchonJson.Err("updater_unreadable", "the installed Updater has no readable version");
            }

            if (present && ArchonUpdaterSwap.CompareVersions(version, installed) <= 0)
            {
                return ArchonJson.Err("not_newer", "installed " + installed + ", offered " + version);
            }

            UpdaterSwap = new ArchonUpdaterSwap.State
            {
                Phase = ArchonUpdaterSwap.PhaseDownloading, Target = version, Previous = installed, StartedUtc = UtcNow()
            };
            SaveUpdaterSwap();

            var download = new ArchonUpdaterSwap.Download();
            _updaterDownload = download;
            var download_url = url;
            var download_token = token;
            RunInBackground(delegate
            {
                try
                {
                    download.Bytes = UpdaterDownload(download_url, download_token);
                }
                catch (Exception e)
                {
                    download.Error = e.GetType().Name + ": " + e.Message;
                }
                finally
                {
                    download.Done = true;
                }
            });

            StartUpdaterTicker();
            return ArchonJson.Ok("{\"phase\":\"downloading\",\"installedVersion\":" + ArchonJson.Quote(installed)
                + ",\"targetVersion\":" + ArchonJson.Quote(version) + "}");
        }

        // Runs once a second, on the game thread, only while an Updater update is in progress.
        internal void UpdaterTick()
        {
            if (UpdaterSwap.Phase == ArchonUpdaterSwap.PhaseDownloading)
            {
                TickUpdaterDownloading();
            }
            else if (UpdaterSwap.Phase == ArchonUpdaterSwap.PhaseLoading)
            {
                TickUpdaterLoading();
            }
            else
            {
                StopUpdaterTicker();
            }
        }

        private void TickUpdaterDownloading()
        {
            var download = _updaterDownload;
            if (download == null || !download.Done)
            {
                if ((UtcNow() - UpdaterSwap.StartedUtc).TotalSeconds > ArchonUpdaterSwap.DownloadTimeoutSeconds + 15)
                {
                    FailUpdaterSwap("download_timeout", "no answer within " + (ArchonUpdaterSwap.DownloadTimeoutSeconds + 15) + " seconds");
                }
                return;
            }

            if (download.Error != null || download.Bytes == null)
            {
                FailUpdaterSwap("download_failed", download.Error ?? "no data");
                return;
            }

            VerifyAndApplyUpdater(download.Bytes);
        }

        private void VerifyAndApplyUpdater(byte[] bytes)
        {
            var check = ArchonIntegrity.CheckLastLine(bytes, TrustedModulus, TrustedExponent);
            if (check.State != "valid")
            {
                FailUpdaterSwap("signature_" + check.State, "the downloaded file did not verify against this plugin's key " + check.KeyFingerprint);
                return;
            }

            var text = Encoding.UTF8.GetString(bytes);
            var offered = ArchonUpdaterSwap.ReadInfoVersion(text);
            if (offered == null)
            {
                FailUpdaterSwap("not_an_updater", "the downloaded file has no RustArchonUpdater [Info] version");
                return;
            }

            if (offered != UpdaterSwap.Target)
            {
                FailUpdaterSwap("version_mismatch", "asked for " + UpdaterSwap.Target + " but the file is " + offered);
                return;
            }

            var installedNow = ReadInstalledUpdaterVersion();
            var presentNow = File.Exists(UpdaterScriptPathOrNull());
            if (presentNow && (installedNow == null || ArchonUpdaterSwap.CompareVersions(offered, installedNow) <= 0))
            {
                FailUpdaterSwap("not_newer", "installed " + (installedNow ?? "unreadable") + ", offered " + offered);
                return;
            }

            if (!presentNow != (UpdaterSwap.Previous.Length == 0))
            {
                FailUpdaterSwap("changed", "the Updater file was added or removed while the download ran");
                return;
            }

            // The Updater may have started an update of this plugin while the download ran.
            if (MainUpdateInProgress())
            {
                FailUpdaterSwap("busy", "the Updater started updating this plugin");
                return;
            }

            ApplyUpdater(bytes);
        }

        private void ApplyUpdater(byte[] bytes)
        {
            var target = UpdaterScriptPathOrNull();
            var temp = target + ".new";
            var backup = target + ".bak";

            try
            {
                Directory.CreateDirectory(DataDirectory);
                File.WriteAllBytes(temp, bytes);
                var fresh = !File.Exists(target);
                if (!fresh) { File.Copy(target, backup, true); }
                UpdaterSwap.SwapUtc = UtcNow();
                if (fresh) { File.Move(temp, target); }
                else { ArchonUpdaterSwap.SwapInto(temp, target); }
            }
            catch (Exception e)
            {
                ArchonUpdaterSwap.TryDelete(temp);
                FailUpdaterSwap("apply_failed", e.GetType().Name + ": " + e.Message);
                return;
            }

            UpdaterSwap.Phase = ArchonUpdaterSwap.PhaseLoading;
            UpdaterSwap.Reason = "";
            SaveUpdaterSwap();
            Puts("Swapped in RustArchonUpdater " + UpdaterSwap.Target + "; waiting for it to load.");
        }

        private void TickUpdaterLoading()
        {
            var marker = ReadUpdaterMarker();
            if (marker != null && marker.Version == UpdaterSwap.Target && marker.Utc >= UpdaterSwap.SwapUtc.AddSeconds(-2))
            {
                UpdaterSwap.Phase = ArchonUpdaterSwap.PhaseSucceeded;
                UpdaterSwap.Reason = "";
                SaveUpdaterSwap();
                StopUpdaterTicker();
                Puts("RustArchonUpdater " + UpdaterSwap.Target + " loaded; Updater update succeeded.");
                return;
            }

            if ((UtcNow() - UpdaterSwap.SwapUtc).TotalSeconds > UpdaterLoadWaitSeconds)
            {
                RollBackUpdater("the new Updater did not report loading within " + UpdaterLoadWaitSeconds + " seconds");
            }
        }

        private void RollBackUpdater(string reason)
        {
            var target = UpdaterScriptPathOrNull();
            var backup = target == null ? null : target + ".bak";

            try
            {
                if (UpdaterSwap.Previous.Length == 0)
                {
                    // There was no Updater before: the one that did not come up is removed, leaving the server as it was.
                    if (target != null && File.Exists(target)) { File.Delete(target); }
                    UpdaterSwap.Phase = ArchonUpdaterSwap.PhaseRolledBack;
                    UpdaterSwap.Reason = reason;
                }
                else if (backup == null || !File.Exists(backup))
                {
                    UpdaterSwap.Phase = ArchonUpdaterSwap.PhaseFailed;
                    UpdaterSwap.Reason = "rollback impossible: no backup found (" + reason + ")";
                }
                else
                {
                    File.Copy(backup, target, true);
                    UpdaterSwap.Phase = ArchonUpdaterSwap.PhaseRolledBack;
                    UpdaterSwap.Reason = reason;
                }
            }
            catch (Exception e)
            {
                UpdaterSwap.Phase = ArchonUpdaterSwap.PhaseFailed;
                UpdaterSwap.Reason = "rollback failed: " + e.GetType().Name + ": " + e.Message + " (" + reason + ")";
            }

            SaveUpdaterSwap();
            StopUpdaterTicker();
            Puts("Updater update " + UpdaterSwap.Target + " " + UpdaterSwap.Phase + ": " + UpdaterSwap.Reason);
        }

        private void FailUpdaterSwap(string code, string detail)
        {
            UpdaterSwap.Phase = ArchonUpdaterSwap.PhaseFailed;
            UpdaterSwap.Reason = code + ": " + detail;
            SaveUpdaterSwap();
            StopUpdaterTicker();
            Puts("Updater update " + UpdaterSwap.Target + " failed: " + UpdaterSwap.Reason);
        }

        // At load: an Updater swap that was made but not yet confirmed when this plugin (re)loaded keeps being watched, so a broken
        // Updater is still put back; a download that died with the previous instance changed nothing on disk and is only recorded.
        internal void ResumeUpdaterSwap()
        {
            if (PluginDirectory == null && ScriptFilePath != null) { PluginDirectory = Path.GetDirectoryName(ScriptFilePath); }
            if (DataDirectory == null && SettingsFilePath != null) { DataDirectory = Path.GetDirectoryName(SettingsFilePath); }

            UpdaterSwap = LoadUpdaterSwap();
            if (UpdaterSwap.Phase == ArchonUpdaterSwap.PhaseLoading)
            {
                StartUpdaterTicker();
            }
            else if (UpdaterSwap.Phase == ArchonUpdaterSwap.PhaseDownloading)
            {
                UpdaterSwap.Phase = ArchonUpdaterSwap.PhaseFailed;
                UpdaterSwap.Reason = "interrupted: this plugin reloaded during the download";
                SaveUpdaterSwap();
            }
        }

        private void StartUpdaterTicker()
        {
            if (_updaterTimer == null) { _updaterTimer = timer.Every(1f, UpdaterTick); }
        }

        private void StopUpdaterTicker()
        {
            if (_updaterTimer != null) { _updaterTimer.Destroy(); _updaterTimer = null; }
        }

        internal string UpdaterScriptPathOrNull()
        {
            return PluginDirectory == null ? null : Path.Combine(PluginDirectory, UpdaterScriptFileName);
        }

        internal string ReadInstalledUpdaterVersion()
        {
            try
            {
                var path = UpdaterScriptPathOrNull();
                return path != null && File.Exists(path) ? ArchonUpdaterSwap.ReadInfoVersion(File.ReadAllText(path)) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // The Updater's own state file, read only to see whether it is in the middle of replacing this plugin.
        private bool MainUpdateInProgress()
        {
            try
            {
                if (DataDirectory == null) { return false; }
                var path = Path.Combine(DataDirectory, UpdaterOwnStatusFileName);
                if (!File.Exists(path)) { return false; }

                foreach (var line in File.ReadAllLines(path))
                {
                    if (line.StartsWith("phase=", StringComparison.Ordinal))
                    {
                        var phase = line.Substring(6).Trim();
                        return phase == "downloading" || phase == "loading";
                    }
                }
            }
            catch (Exception)
            {
                return true;    // cannot tell: do not start something that must not overlap
            }

            return false;
        }

        // What the new Updater writes when it comes up ("version=<x.y.z>" and "utc=<round-trip time>").
        private ArchonUpdaterSwap.Marker ReadUpdaterMarker()
        {
            try
            {
                if (DataDirectory == null) { return null; }
                var path = Path.Combine(DataDirectory, UpdaterLoadedMarkerFileName);
                if (!File.Exists(path)) { return null; }

                var marker = new ArchonUpdaterSwap.Marker();
                foreach (var line in File.ReadAllLines(path))
                {
                    if (line.StartsWith("version=", StringComparison.Ordinal)) { marker.Version = line.Substring(8).Trim(); }
                    else if (line.StartsWith("utc=", StringComparison.Ordinal))
                    {
                        DateTime utc;
                        if (DateTime.TryParse(line.Substring(4).Trim(), System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind, out utc))
                        {
                            marker.Utc = utc.ToUniversalTime();
                        }
                    }
                }
                return marker.Version == null ? null : marker;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private ArchonUpdaterSwap.State LoadUpdaterSwap()
        {
            var state = new ArchonUpdaterSwap.State();
            try
            {
                if (DataDirectory == null) { return state; }
                var path = Path.Combine(DataDirectory, UpdaterSwapFileName);
                if (!File.Exists(path)) { return state; }

                foreach (var line in File.ReadAllLines(path))
                {
                    var eq = line.IndexOf('=');
                    if (eq <= 0) { continue; }
                    var key = line.Substring(0, eq);
                    var value = line.Substring(eq + 1);
                    switch (key)
                    {
                        case "phase": state.Phase = value; break;
                        case "target": state.Target = value; break;
                        case "previous": state.Previous = value; break;
                        case "reason": state.Reason = value; break;
                        case "started": state.StartedUtc = ArchonUpdaterSwap.ParseUtc(value); break;
                        case "swapped": state.SwapUtc = ArchonUpdaterSwap.ParseUtc(value); break;
                    }
                }
            }
            catch (Exception)
            {
                return new ArchonUpdaterSwap.State();
            }
            return state;
        }

        private void SaveUpdaterSwap()
        {
            try
            {
                if (DataDirectory == null) { return; }
                Directory.CreateDirectory(DataDirectory);
                File.WriteAllText(
                    Path.Combine(DataDirectory, UpdaterSwapFileName),
                    "phase=" + UpdaterSwap.Phase + "\ntarget=" + UpdaterSwap.Target + "\nprevious=" + UpdaterSwap.Previous
                    + "\nreason=" + UpdaterSwap.Reason.Replace('\n', ' ').Replace('\r', ' ')
                    + "\nstarted=" + UpdaterSwap.StartedUtc.ToString("o") + "\nswapped=" + UpdaterSwap.SwapUtc.ToString("o") + "\n");
            }
            catch (Exception)
            {
            }
        }

        // ---- third-party plugin updates ----------------------------------------------------------------------
        //
        // The panel has found a newer version of a plugin that is already installed on this server, downloaded it once itself to check it was a file
        // this could apply, and recorded its SHA-256 and size. It sends those here; this plugin downloads its OWN copy (the panel never stores or serves
        // these files), and applies it only if it is byte for byte the file the panel checked. Then the same care as the Updater takes over this
        // plugin's own file: the old file is kept beside it as <name>.cs.bak, and if the new one does not come up loaded at the version that was
        // asked for, the old one is put back.
        //
        // Only ever an UPDATE of a plugin that is present: it will not install one that is missing. Fails closed like the Updater: a plugin that
        // cannot vouch for its own file (signed by the panel that manages it) applies nothing on anyone's say-so.
        //
        // What "loaded" means here: the framework's own registry (plugins.Find) holds a plugin of that name at that version, and it is not the same
        // instance that was running before the swap. A plugin that never loads - a compile error, say - never appears, and after ThirdPartyLoadWaitSeconds
        // the previous file is put back.

        internal const string ThirdPartySwapFileName = "thirdparty-swap.txt";
        internal const int ThirdPartyLoadWaitSeconds = 90;
        internal const int ThirdPartyMinimumReloadSeconds = 15;

        internal ArchonThirdParty.State ThirdParty = new ArchonThirdParty.State();
        internal Func<string, long, byte[]> ThirdPartyDownload = ArchonThirdParty.DefaultDownload;

        // A test can say which plugins are loaded without a framework behind it; at runtime this asks the framework.
        internal Func<string, ArchonThirdParty.LoadedPlugin> FindLoaded = null;

        // A test can make the zip reader unavailable (or supply one); at runtime this looks System.IO.Compression up by name. Null means "cannot".
        internal Func<Type> ZipTypeLoader = ArchonZipArchive.DefaultLoadType;

        private Type _zipType;
        private bool _zipChecked;
        private List<ArchonZipMapping.Rule> _thirdPartyZipRules;
        private long _thirdPartyInstallBytes;

        private ArchonThirdParty.Download _thirdPartyDownload;
        private Timer _thirdPartyTimer;
        private object _thirdPartyOldInstance;

        // archon.thirdparty.update <class> <version> <sha256> <size> <url>
        [ConsoleCommand("archon.thirdparty.update")]
        internal void CmdThirdPartyUpdate(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) { return; }

            if (!arg.HasArgs(5) || arg.HasArgs(6))
            {
                arg.ReplyWith(ArchonJson.Err("usage", "archon.thirdparty.update <class> <version> <sha256> <size> <url>"));
                return;
            }

            arg.ReplyWith(BeginThirdPartyUpdate(
                arg.GetString(0, ""), arg.GetString(1, ""), arg.GetString(2, ""), arg.GetString(3, ""), arg.GetString(4, "")));
        }

        // archon.thirdparty.zip <class> <version> <sha256> <size> <installBytes> <rules> <url>
        // The same update for a plugin that ships as a ZIP archive: <size> is the archive's length, <installBytes> the most the files to be installed may
        // add up to, <rules> the person's folder rules (ZipMapping.Encode on the panel's side).
        [ConsoleCommand("archon.thirdparty.zip")]
        internal void CmdThirdPartyZip(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) { return; }

            if (!arg.HasArgs(7) || arg.HasArgs(8))
            {
                arg.ReplyWith(ArchonJson.Err("usage", "archon.thirdparty.zip <class> <version> <sha256> <size> <installBytes> <rules> <url>"));
                return;
            }

            arg.ReplyWith(BeginThirdPartyZip(
                arg.GetString(0, ""), arg.GetString(1, ""), arg.GetString(2, ""), arg.GetString(3, ""), arg.GetString(4, ""), arg.GetString(5, ""), arg.GetString(6, "")));
        }

        // archon.thirdparty.status - the most recent third-party update this plugin carried out (or is carrying out).
        [ConsoleCommand("archon.thirdparty.status")]
        internal void CmdThirdPartyStatus(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) { return; }

            arg.ReplyWith(ArchonJson.Ok(ThirdParty.ToJson()));
        }

        internal string BeginThirdPartyUpdate(string className, string version, string sha256, string sizeText, string url)
        {
            return BeginThirdParty(false, className, version, sha256, sizeText, url, null, null);
        }

        internal string BeginThirdPartyZip(string className, string version, string sha256, string sizeText, string installBytesText, string rulesText, string url)
        {
            return BeginThirdParty(true, className, version, sha256, sizeText, url, installBytesText, rulesText);
        }

        private string BeginThirdParty(bool zip, string className, string version, string sha256, string sizeText, string url, string installBytesText, string rulesText)
        {
            if (ThirdParty.Phase == ArchonThirdParty.PhaseDownloading || ThirdParty.Phase == ArchonThirdParty.PhaseLoading
                || ThirdParty.Phase == ArchonThirdParty.PhaseApplying)
            {
                return ArchonJson.Err("busy", "a plugin update is already in progress (" + ThirdParty.Phase + ")");
            }

            if (Integrity.State != "valid" || !ArchonIntegrity.IsStamped(TrustedModulus, TrustedExponent))
            {
                return ArchonJson.Err("not_verified", "this plugin does not verify under its own key (" + Integrity.State + "), so it will not install anything");
            }

            if (!ArchonThirdParty.IsClassName(className) || className == "RustArchon" || className == "RustArchonUpdater")
            {
                return ArchonJson.Err("bad_class", "the plugin's class name must be a plain identifier, and not one of RustArchon's own");
            }

            if (!ArchonThirdParty.IsVersionText(version))
            {
                return ArchonJson.Err("bad_version", "the version must be 1 to " + ArchonThirdParty.MaxVersionLength + " letters, digits, dots, dashes or plus signs");
            }

            if (!ArchonThirdParty.IsSha256(sha256))
            {
                return ArchonJson.Err("bad_hash", "the hash must be 64 hexadecimal characters");
            }

            long size;
            if (!long.TryParse(sizeText, out size) || size < 1)
            {
                return ArchonJson.Err("bad_size", "the size must be a whole number of bytes, at least 1");
            }

            if (size > ArchonThirdParty.MaxFileBytes)
            {
                return ArchonJson.Err("too_large", "a download of more than " + ArchonThirdParty.MaxFileBytes + " bytes is never fetched");
            }

            Uri uri;
            if (!ArchonThirdParty.TryParseHttps(url, out uri))
            {
                return ArchonJson.Err("bad_url", "the address must be an absolute https address with no credentials in it, at most " + ArchonThirdParty.MaxUrlLength + " characters");
            }

            long installBytes = 0;
            List<ArchonZipMapping.Rule> rules = null;
            if (zip)
            {
                if (!long.TryParse(installBytesText, out installBytes) || installBytes < 1)
                {
                    return ArchonJson.Err("bad_install_bytes", "the number of bytes to install must be a whole number, at least 1");
                }

                if (installBytes > ArchonThirdParty.MaxInstallBytes)
                {
                    return ArchonJson.Err("too_large", "an archive that unpacks to more than " + ArchonThirdParty.MaxInstallBytes + " bytes is never installed");
                }

                rules = ArchonZipMapping.Decode(rulesText);
                if (rules == null || ArchonZipMapping.Resolve(new List<ArchonZipMapping.EntryInfo>(), rules).Problems.Count > 0)
                {
                    return ArchonJson.Err("bad_rules", "the folder rules could not be read, or one of them is not one that can be used");
                }

                if (!ZipSupported())
                {
                    return ArchonJson.Err("zip_unsupported", "this server cannot read zip archives (System.IO.Compression could not be loaded)");
                }
            }

            if (PluginDirectory == null || !Directory.Exists(PluginDirectory))
            {
                return ArchonJson.Err("plugins_folder_unknown", "the plugins folder this plugin lives in could not be found");
            }

            // Never while the Updater is replacing this plugin or the Updater: each would be watching the other move.
            if (MainUpdateInProgress() || UpdaterSwap.Phase == ArchonUpdaterSwap.PhaseDownloading || UpdaterSwap.Phase == ArchonUpdaterSwap.PhaseLoading)
            {
                return ArchonJson.Err("busy", "an update of the RustArchon plugin or its Updater is in progress");
            }

            string target;
            int found;
            ArchonThirdParty.FindInstalled(PluginDirectory, className, out target, out found);
            if (found == 0)
            {
                return ArchonJson.Err("not_installed", "no plugin file in the plugins folder declares a class named " + className + "; this only updates plugins that are installed");
            }

            if (found > 1)
            {
                return ArchonJson.Err("ambiguous", "more than one plugin file declares a class named " + className);
            }

            var installed = ArchonThirdParty.ReadInfoVersion(SafeReadText(target)) ?? "";
            // A zip is never "up to date": the archive holds more than the one file that could be compared, and a person chose to apply it.
            if (!zip && installed.Length > 0 && ArchonThirdParty.SameVersion(installed, version))
            {
                // The same version can still be a different file (an author who republished under it), but that is a person's decision to make, and
                // the panel asks for it: the request then comes with the hash the person saw. Nothing here can tell it apart from a repeat.
                if (ArchonThirdParty.Sha256Hex(SafeReadBytes(target)) == sha256.ToLowerInvariant())
                {
                    return ArchonJson.Err("up_to_date", "the installed file is already exactly this one");
                }
            }

            _thirdPartyOldInstance = null;
            var current = LookUpLoaded(className);
            if (current != null) { _thirdPartyOldInstance = current.Instance; }

            ThirdParty = new ArchonThirdParty.State
            {
                Phase = ArchonThirdParty.PhaseDownloading,
                ClassName = className,
                Target = version,
                Previous = installed,
                File = Path.GetFileName(target),
                Sha256 = sha256.ToLowerInvariant(),
                Size = size,
                Kind = zip ? ArchonThirdParty.KindZip : ArchonThirdParty.KindCs,
                StartedUtc = UtcNow()
            };
            _thirdPartyZipRules = rules;
            _thirdPartyInstallBytes = installBytes;
            SaveThirdParty();

            var download = new ArchonThirdParty.Download();
            _thirdPartyDownload = download;
            var download_url = uri.AbsoluteUri;
            var download_size = size;
            RunInBackground(delegate
            {
                try
                {
                    download.Bytes = ThirdPartyDownload(download_url, download_size);
                }
                catch (ArchonThirdParty.ChangedException e)
                {
                    download.Changed = e.Message;
                }
                catch (Exception e)
                {
                    download.Error = e.GetType().Name + ": " + e.Message;
                }
                finally
                {
                    download.Done = true;
                }
            });

            StartThirdPartyTicker();
            return ArchonJson.Ok("{\"phase\":\"downloading\",\"file\":" + ArchonJson.Quote(ThirdParty.File)
                + ",\"installedVersion\":" + ArchonJson.Quote(installed) + ",\"targetVersion\":" + ArchonJson.Quote(version)
                + (zip ? ",\"kind\":\"zip\"" : "") + "}");
        }

        // Runs once a second, on the game thread, only while a third-party update is in progress.
        internal void ThirdPartyTick()
        {
            if (ThirdParty.Phase == ArchonThirdParty.PhaseDownloading)
            {
                TickThirdPartyDownloading();
            }
            else if (ThirdParty.Phase == ArchonThirdParty.PhaseLoading)
            {
                TickThirdPartyLoading();
            }
            else
            {
                StopThirdPartyTicker();
            }
        }

        private void TickThirdPartyDownloading()
        {
            var download = _thirdPartyDownload;
            if (download == null || !download.Done)
            {
                if ((UtcNow() - ThirdParty.StartedUtc).TotalSeconds > ArchonThirdParty.DownloadTimeoutSeconds + 15)
                {
                    FailThirdParty("download_timeout", "no answer within " + (ArchonThirdParty.DownloadTimeoutSeconds + 15) + " seconds");
                }
                return;
            }

            if (download.Changed != null)
            {
                MismatchThirdParty("", download.Changed);
                return;
            }

            if (download.Error != null || download.Bytes == null)
            {
                FailThirdParty("download_failed", download.Error ?? "no data");
                return;
            }

            VerifyAndApplyThirdParty(download.Bytes);
        }

        private void VerifyAndApplyThirdParty(byte[] bytes)
        {
            // The whole point of the hash: this is the file the panel looked at, or it is not applied. The author changing a file without changing its
            // version is possible (if bad practice), and the panel decides what to do about it - not this plugin, and not on its own.
            var actual = ArchonThirdParty.Sha256Hex(bytes);
            if (actual != ThirdParty.Sha256)
            {
                MismatchThirdParty(actual, "the file is not the one the panel checked");
                return;
            }

            if (ThirdParty.Kind == ArchonThirdParty.KindZip)
            {
                ApplyThirdPartyZip(bytes);
                return;
            }

            var text = Encoding.UTF8.GetString(bytes);
            if (!ArchonThirdParty.DeclaresClass(text, ThirdParty.ClassName))
            {
                FailThirdParty("not_that_plugin", "the file does not declare a class named " + ThirdParty.ClassName);
                return;
            }

            var offered = ArchonThirdParty.ReadInfoVersion(text);
            if (offered != null && !ArchonThirdParty.SameVersion(offered, ThirdParty.Target))
            {
                FailThirdParty("version_mismatch", "asked for " + ThirdParty.Target + " but the file is " + offered);
                return;
            }

            // Somebody may have changed the plugins folder while the download ran.
            string target;
            int found;
            ArchonThirdParty.FindInstalled(PluginDirectory, ThirdParty.ClassName, out target, out found);
            if (found != 1 || Path.GetFileName(target) != ThirdParty.File)
            {
                FailThirdParty("changed", "the installed plugin file was moved, replaced or duplicated while the download ran");
                return;
            }

            if (MainUpdateInProgress() || UpdaterSwap.Phase == ArchonUpdaterSwap.PhaseDownloading || UpdaterSwap.Phase == ArchonUpdaterSwap.PhaseLoading)
            {
                FailThirdParty("busy", "an update of the RustArchon plugin or its Updater started while the download ran");
                return;
            }

            ApplyThirdParty(bytes, target);
        }

        private void ApplyThirdParty(byte[] bytes, string target)
        {
            var temp = target + ".new";
            var backup = target + ".bak";

            try
            {
                File.WriteAllBytes(temp, bytes);
                File.Copy(target, backup, true);
                ThirdParty.SwapUtc = UtcNow();
                ArchonUpdaterSwap.SwapInto(temp, target);
            }
            catch (Exception e)
            {
                ArchonUpdaterSwap.TryDelete(temp);
                FailThirdParty("apply_failed", e.GetType().Name + ": " + e.Message);
                return;
            }

            ThirdParty.Phase = ArchonThirdParty.PhaseLoading;
            ThirdParty.Reason = "";
            SaveThirdParty();
            Puts("Swapped in " + ThirdParty.File + " " + ThirdParty.Target + "; waiting for it to load.");
        }

        private void TickThirdPartyLoading()
        {
            var loaded = LookUpLoaded(ThirdParty.ClassName);
            var elapsed = (UtcNow() - ThirdParty.SwapUtc).TotalSeconds;

            // A new instance at the version asked for. When the instance from before the swap is not known (this plugin reloaded meanwhile), the
            // same-version case - a republished file - can only be told by waiting long enough for a reload to have happened.
            var newInstance = loaded != null && !ReferenceEquals(loaded.Instance, _thirdPartyOldInstance)
                && (_thirdPartyOldInstance != null || ThirdParty.Previous.Length == 0
                    || !ArchonThirdParty.SameVersion(ThirdParty.Previous, ThirdParty.Target) || elapsed >= ThirdPartyMinimumReloadSeconds);
            if (newInstance && ArchonThirdParty.SameVersion(loaded.Version, ThirdParty.Target))
            {
                ThirdParty.Phase = ArchonThirdParty.PhaseSucceeded;
                ThirdParty.Reason = "";
                SaveThirdParty();
                StopThirdPartyTicker();
                Puts(ThirdParty.File + " " + ThirdParty.Target + " loaded; the plugin update succeeded.");
                return;
            }

            if (elapsed > ThirdPartyLoadWaitSeconds)
            {
                RollBackThirdParty(loaded == null
                    ? ThirdParty.ClassName + " did not appear among the loaded plugins within " + ThirdPartyLoadWaitSeconds + " seconds"
                    : ThirdParty.ClassName + " did not come up at version " + ThirdParty.Target + " within " + ThirdPartyLoadWaitSeconds + " seconds (it is " + loaded.Version + ")");
            }
        }

        private void RollBackThirdParty(string reason)
        {
            if (ThirdParty.Kind == ArchonThirdParty.KindZip)
            {
                RollBackThirdPartyZip(reason);
                return;
            }

            string target = null;
            if (PluginDirectory != null && ThirdParty.File.Length > 0) { target = Path.Combine(PluginDirectory, ThirdParty.File); }
            var backup = target == null ? null : target + ".bak";

            try
            {
                if (backup == null || !File.Exists(backup))
                {
                    ThirdParty.Phase = ArchonThirdParty.PhaseFailed;
                    ThirdParty.Reason = "rollback impossible: no backup found (" + reason + ")";
                }
                else
                {
                    File.Copy(backup, target, true);
                    ThirdParty.Phase = ArchonThirdParty.PhaseRolledBack;
                    ThirdParty.Reason = reason;
                }
            }
            catch (Exception e)
            {
                ThirdParty.Phase = ArchonThirdParty.PhaseFailed;
                ThirdParty.Reason = "rollback failed: " + e.GetType().Name + ": " + e.Message + " (" + reason + ")";
            }

            SaveThirdParty();
            StopThirdPartyTicker();
            Puts("Update of " + ThirdParty.File + " to " + ThirdParty.Target + " " + ThirdParty.Phase + ": " + ThirdParty.Reason);
        }

        private void FailThirdParty(string code, string detail)
        {
            ThirdParty.Phase = ArchonThirdParty.PhaseFailed;
            ThirdParty.Reason = code + ": " + detail;
            SaveThirdParty();
            StopThirdPartyTicker();
            Puts("Update of " + ThirdParty.File + " to " + ThirdParty.Target + " failed: " + ThirdParty.Reason);
        }

        // The file is not the one the panel checked (or is not even the size it said). Nothing was written.
        private void MismatchThirdParty(string actual, string detail)
        {
            ThirdParty.Phase = ArchonThirdParty.PhaseMismatch;
            ThirdParty.ActualSha256 = actual;
            ThirdParty.Reason = "changed: " + detail;
            SaveThirdParty();
            StopThirdPartyTicker();
            Puts("Update of " + ThirdParty.File + " to " + ThirdParty.Target + " not applied: " + ThirdParty.Reason);
        }

        // At load: a swap that was made but not yet confirmed when this plugin (re)loaded keeps being watched, so a broken plugin is still put
        // back; a download that died with the previous instance changed nothing on disk and is only recorded.
        internal void ResumeThirdPartySwap()
        {
            if (PluginDirectory == null && ScriptFilePath != null) { PluginDirectory = Path.GetDirectoryName(ScriptFilePath); }
            if (DataDirectory == null && SettingsFilePath != null) { DataDirectory = Path.GetDirectoryName(SettingsFilePath); }

            ThirdParty = LoadThirdParty();
            if (ThirdParty.Phase == ArchonThirdParty.PhaseLoading)
            {
                StartThirdPartyTicker();
            }
            else if (ThirdParty.Phase == ArchonThirdParty.PhaseDownloading)
            {
                ThirdParty.Phase = ArchonThirdParty.PhaseFailed;
                ThirdParty.Reason = "interrupted: this plugin reloaded during the download";
                SaveThirdParty();
            }
            else if (ThirdParty.Phase == ArchonThirdParty.PhaseApplying)
            {
                // Files were being written when this plugin went away: some may be new and some old, so everything goes back from the manifest.
                DeleteZipStaging(ThirdParty.ClassName);
                RollBackThirdParty("interrupted: this plugin reloaded while the files were being written");
            }
        }

        private void StartThirdPartyTicker()
        {
            if (_thirdPartyTimer == null) { _thirdPartyTimer = timer.Every(1f, ThirdPartyTick); }
        }

        private void StopThirdPartyTicker()
        {
            if (_thirdPartyTimer != null) { _thirdPartyTimer.Destroy(); _thirdPartyTimer = null; }
        }

        private ArchonThirdParty.LoadedPlugin LookUpLoaded(string className)
        {
            if (FindLoaded != null) { return FindLoaded(className); }

            var found = plugins.Find(className);
            if (found == null) { return null; }
            return new ArchonThirdParty.LoadedPlugin { Instance = found, Version = found.Version.ToString() };
        }

        private static string SafeReadText(string path)
        {
            try { return File.ReadAllText(path); } catch (Exception) { return null; }
        }

        private static byte[] SafeReadBytes(string path)
        {
            try { return File.ReadAllBytes(path); } catch (Exception) { return new byte[0]; }
        }

        private ArchonThirdParty.State LoadThirdParty()
        {
            var state = new ArchonThirdParty.State();
            try
            {
                if (DataDirectory == null) { return state; }
                var path = Path.Combine(DataDirectory, ThirdPartySwapFileName);
                if (!File.Exists(path)) { return state; }

                foreach (var line in File.ReadAllLines(path))
                {
                    var eq = line.IndexOf('=');
                    if (eq <= 0) { continue; }
                    var key = line.Substring(0, eq);
                    var value = line.Substring(eq + 1);
                    switch (key)
                    {
                        case "phase": state.Phase = value; break;
                        case "class": state.ClassName = value; break;
                        case "target": state.Target = value; break;
                        case "previous": state.Previous = value; break;
                        case "file": state.File = value; break;
                        case "sha256": state.Sha256 = value; break;
                        case "actual": state.ActualSha256 = value; break;
                        case "reason": state.Reason = value; break;
                        case "size": long.TryParse(value, out state.Size); break;
                        case "kind": state.Kind = value == ArchonThirdParty.KindZip ? ArchonThirdParty.KindZip : ArchonThirdParty.KindCs; break;
                        case "backup": state.BackupDir = value; break;
                        case "installed": int.TryParse(value, out state.Installed); break;
                        case "started": state.StartedUtc = ArchonUpdaterSwap.ParseUtc(value); break;
                        case "swapped": state.SwapUtc = ArchonUpdaterSwap.ParseUtc(value); break;
                    }
                }
            }
            catch (Exception)
            {
                return new ArchonThirdParty.State();
            }
            return state;
        }

        private void SaveThirdParty()
        {
            try
            {
                if (DataDirectory == null) { return; }
                Directory.CreateDirectory(DataDirectory);
                File.WriteAllText(Path.Combine(DataDirectory, ThirdPartySwapFileName), ThirdParty.ToFileText());
            }
            catch (Exception)
            {
            }
        }

        // ---- third-party plugin updates: plugins that ship as a zip archive -------------------------------------
        //
        // The same request as above, for a plugin that is an archive of several files (the plugin, its default settings, its data, its language files).
        // The person's folder rules say where each file goes; the panel checked them against the archive and sends them here, but this plugin trusts
        // neither the archive nor the rules: it reads the archive itself, works out the mapping with its own copy of the algorithm (ArchonZipMapping),
        // and refuses the whole update if anything about it is wrong. Nothing is written until every file has been unpacked to a staging folder and
        // checked; then the files that are replaced are copied to a backup with a manifest, every file is written (the plugin's .cs LAST, since that
        // is what makes the framework reload the plugin, and it should find its settings and data already in place), and the framework's own registry
        // confirms the new version exactly as for a single file. If it does not, or this plugin dies mid-write, the manifest puts everything back.

        internal const string ZipManifestFileName = "manifest.txt";
        internal const string ZipStagingFolderName = "thirdparty-staging";
        internal const string ZipBackupFolderName = "thirdparty-backup";

        // A test can watch (or interrupt) each file about to be written: called with its destination path, on the game thread.
        internal Action<string> BeforeThirdPartyWrite = null;

        private sealed class ZipItem
        {
            public string Path;                     // in the archive
            public string Role;
            public string Relative;                 // below the role's folder, with forward slashes
            public string Destination;              // full path on the server
            public string Staged;                   // full path in the staging folder
            public long Size;
            public bool KeepExisting;
            public bool IsCode;
            public bool Existed;
            public bool Skipped;
            public ArchonZipArchive.Entry Entry;
        }

        internal bool ZipSupported()
        {
            return ZipArchiveType() != null;
        }

        private Type ZipArchiveType()
        {
            if (!_zipChecked)
            {
                try { _zipType = ZipTypeLoader == null ? null : ZipTypeLoader(); }
                catch (Exception) { _zipType = null; }

                // Only a type that really has everything the reader needs counts.
                if (_zipType != null && !ArchonZipArchive.CanRead(_zipType)) { _zipType = null; }
                _zipChecked = true;
            }
            return _zipType;
        }

        private string ZipStagingDirectory(string className)
        {
            return System.IO.Path.Combine(System.IO.Path.Combine(DataDirectory, ZipStagingFolderName), className);
        }

        private string ZipBackupDirectory(string className)
        {
            return System.IO.Path.Combine(System.IO.Path.Combine(DataDirectory, ZipBackupFolderName), className);
        }

        private void DeleteZipStaging(string className)
        {
            try
            {
                if (DataDirectory == null || !ArchonThirdParty.IsClassName(className)) { return; }
                var dir = ZipStagingDirectory(className);
                if (Directory.Exists(dir)) { Directory.Delete(dir, true); }

                // The folder that holds the staging folders goes too once it is empty, so an update leaves nothing behind.
                var parent = System.IO.Path.GetDirectoryName(dir);
                if (parent != null && Directory.Exists(parent) && Directory.GetFileSystemEntries(parent).Length == 0) { Directory.Delete(parent, false); }
            }
            catch (Exception)
            {
            }
        }

        private void ApplyThirdPartyZip(byte[] bytes)
        {
            try
            {
                ApplyThirdPartyZipCore(bytes);
            }
            catch (Exception e)
            {
                // Anything not foreseen. What was written (if anything) is put back; what was not is simply not applied.
                if (ThirdParty.Phase == ArchonThirdParty.PhaseApplying)
                {
                    RollBackThirdParty("apply_failed: " + e.GetType().Name + ": " + e.Message);
                }
                else if (ThirdParty.Phase == ArchonThirdParty.PhaseDownloading)
                {
                    FailThirdParty("apply_failed", e.GetType().Name + ": " + e.Message);
                }
            }
            finally
            {
                DeleteZipStaging(ThirdParty.ClassName);
            }
        }

        private void ApplyThirdPartyZipCore(byte[] bytes)
        {
            var className = ThirdParty.ClassName;
            var rules = _thirdPartyZipRules;
            var zipType = ZipArchiveType();
            if (zipType == null || rules == null)
            {
                FailThirdParty("zip_unsupported", "this server cannot read zip archives");
                return;
            }

            if (DataDirectory == null)
            {
                FailThirdParty("apply_failed", "this plugin has no data folder to unpack into");
                return;
            }

            ArchonZipArchive archive;
            try
            {
                archive = ArchonZipArchive.Open(zipType, bytes);
            }
            catch (Exception e)
            {
                FailThirdParty("bad_zip", "the file is not a zip archive this server can read (" + e.GetType().Name + ": " + e.Message + ")");
                return;
            }

            var plan = new List<ZipItem>();
            using (archive)
            {
                if (archive.TooManyEntries)
                {
                    FailThirdParty("too_many_files", "the archive has " + archive.EntryCount + " entries; the most that is unpacked is " + ArchonZipArchive.MaxEntries);
                    return;
                }

                // What the archive is and what the rules make of it: worked out here, from the archive itself.
                var infos = new List<ArchonZipMapping.EntryInfo>();
                foreach (var file in archive.Files) { infos.Add(new ArchonZipMapping.EntryInfo(file.Name, file.Length)); }

                var mapping = ArchonZipMapping.Resolve(infos, rules);
                if (mapping.Problems.Count > 0)
                {
                    FailThirdParty("bad_mapping", mapping.Problems[0].Code + " " + mapping.Problems[0].Path);
                    return;
                }

                long declared = 0;
                for (var i = 0; i < mapping.Entries.Count; i++)
                {
                    var mapped = mapping.Entries[i];
                    if (mapped.Action != ArchonZipMapping.EntryAction.Install) { continue; }

                    if (mapped.Size < 0 || declared > _thirdPartyInstallBytes - mapped.Size)
                    {
                        FailThirdParty("too_large_for_plan", "the files to install add up to more than the " + _thirdPartyInstallBytes + " bytes that were allowed");
                        return;
                    }
                    declared += mapped.Size;

                    string refusal;
                    var item = PlanZipItem(mapped, archive.Files[i], out refusal);
                    if (item == null)
                    {
                        FailThirdParty("bad_mapping", refusal);
                        return;
                    }
                    plan.Add(item);
                }

                if (plan.Count == 0)
                {
                    FailThirdParty("bad_mapping", "nothing_to_install");
                    return;
                }

                // Unpack to the staging folder. An entry that gives more bytes, or fewer, than the archive said it would is a hostile or a broken
                // archive: nothing is applied.
                var staging = ZipStagingDirectory(className);
                DeleteZipStaging(className);
                Directory.CreateDirectory(staging);
                long staged = 0;
                foreach (var item in plan)
                {
                    item.Staged = System.IO.Path.Combine(System.IO.Path.Combine(staging, item.Role), ToNativePath(item.Relative));
                    var problem = ExtractZipEntry(archive, item, _thirdPartyInstallBytes, ref staged);
                    if (problem != null)
                    {
                        FailThirdParty("bad_zip", problem);
                        return;
                    }
                }
            }

            // The archive is closed; from here on everything is read from the staging folder.
            // The plugin itself: exactly one staged .cs in the plugins folder declares the class, at the version asked for, and it replaces the file
            // that is installed (a differently named file would leave two files declaring one class).
            ZipItem code = null;
            var declares = 0;
            string codeText = null;
            foreach (var item in plan)
            {
                if (item.Role != ArchonZipMapping.RolePlugins || !item.IsCode) { continue; }
                var text = File.ReadAllText(item.Staged);
                if (ArchonThirdParty.DeclaresClass(text, className))
                {
                    declares++;
                    code = item;
                    codeText = text;
                }
            }

            if (declares != 1)
            {
                FailThirdParty("not_that_plugin", declares == 0
                    ? "no plugin file in the archive declares a class named " + className
                    : "more than one plugin file in the archive declares a class named " + className);
                return;
            }

            var offered = ArchonThirdParty.ReadInfoVersion(codeText);
            if (offered != null && !ArchonThirdParty.SameVersion(offered, ThirdParty.Target))
            {
                FailThirdParty("version_mismatch", "asked for " + ThirdParty.Target + " but the archive's plugin is " + offered);
                return;
            }

            if (!string.Equals(code.Relative, ThirdParty.File, StringComparison.Ordinal))
            {
                FailThirdParty("not_that_plugin", "the archive's plugin file is " + code.Relative + " but the installed one is " + ThirdParty.File);
                return;
            }

            // Somebody may have changed the plugins folder while the download and unpacking ran.
            string installedPath;
            int found;
            ArchonThirdParty.FindInstalled(PluginDirectory, className, out installedPath, out found);
            if (found != 1 || System.IO.Path.GetFileName(installedPath) != ThirdParty.File)
            {
                FailThirdParty("changed", "the installed plugin file was moved, replaced or duplicated while the update was prepared");
                return;
            }

            if (MainUpdateInProgress() || UpdaterSwap.Phase == ArchonUpdaterSwap.PhaseDownloading || UpdaterSwap.Phase == ArchonUpdaterSwap.PhaseLoading)
            {
                FailThirdParty("busy", "an update of the RustArchon plugin or its Updater started while the update was prepared");
                return;
            }

            // What is written: everything not kept. A file that is kept where one already exists is not touched and not backed up.
            var writes = new List<ZipItem>();
            foreach (var item in plan)
            {
                item.Existed = File.Exists(item.Destination);
                item.Skipped = item.KeepExisting && item.Existed;
                if (!item.Skipped && !item.IsCode) { writes.Add(item); }
            }
            foreach (var item in plan)
            {
                if (!item.Skipped && item.IsCode) { writes.Add(item); }
            }

            // Folders this update will have to create, so that a rollback removes those and nothing else.
            var created = new List<string>();
            foreach (var item in writes)
            {
                var dir = System.IO.Path.GetDirectoryName(item.Destination);
                while (dir != null && !Directory.Exists(dir))
                {
                    if (File.Exists(dir))
                    {
                        FailThirdParty("bad_mapping", "a file is in the way of the folder " + dir);
                        return;
                    }
                    if (!created.Contains(dir)) { created.Add(dir); }
                    dir = System.IO.Path.GetDirectoryName(dir);
                }
            }

            // One backup per plugin: the previous one goes. The manifest is complete before anything on the server is touched.
            var backupDir = ZipBackupDirectory(className);
            try
            {
                if (Directory.Exists(backupDir)) { Directory.Delete(backupDir, true); }
                Directory.CreateDirectory(backupDir);

                var lines = new List<string>();
                foreach (var item in writes)
                {
                    if (item.Existed)
                    {
                        var saved = System.IO.Path.Combine(System.IO.Path.Combine(backupDir, item.Role), ToNativePath(item.Relative));
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(saved));
                        File.Copy(item.Destination, saved, true);
                        lines.Add("E\t" + item.Destination + "\t" + item.Role + "/" + item.Relative);
                    }
                    else
                    {
                        lines.Add("N\t" + item.Destination);
                    }
                }
                foreach (var dir in created) { lines.Add("D\t" + dir); }
                File.WriteAllLines(System.IO.Path.Combine(backupDir, ZipManifestFileName), lines.ToArray());
            }
            catch (Exception e)
            {
                try { if (Directory.Exists(backupDir)) { Directory.Delete(backupDir, true); } } catch (Exception) { }
                FailThirdParty("apply_failed", "the backup could not be made: " + e.GetType().Name + ": " + e.Message);
                return;
            }

            // Recorded BEFORE the first write: a state left in "applying" means this plugin died part way, and everything goes back.
            ThirdParty.BackupDir = backupDir;
            ThirdParty.Installed = 0;
            ThirdParty.Reason = "";
            ThirdParty.Phase = ArchonThirdParty.PhaseApplying;
            SaveThirdParty();

            var written = 0;
            var stamped = false;
            try
            {
                foreach (var item in writes)
                {
                    if (item.IsCode && !stamped)
                    {
                        ThirdParty.SwapUtc = UtcNow();
                        stamped = true;
                    }
                    if (BeforeThirdPartyWrite != null) { BeforeThirdPartyWrite(item.Destination); }
                    WriteZipItem(item);
                    written++;
                }
            }
            catch (Exception e)
            {
                RollBackThirdParty("apply_failed: " + e.GetType().Name + ": " + e.Message);
                return;
            }

            if (!stamped) { ThirdParty.SwapUtc = UtcNow(); }
            ThirdParty.Installed = written;
            ThirdParty.Phase = ArchonThirdParty.PhaseLoading;
            ThirdParty.Reason = "";
            SaveThirdParty();
            Puts("Unpacked " + className + " " + ThirdParty.Target + " (" + written + " files); waiting for it to load.");
        }

        private static string ToNativePath(string relative)
        {
            return relative.Replace('/', System.IO.Path.DirectorySeparatorChar);
        }

        // Every file this update writes is checked against the folder it is meant for, again, here: not trusting the archive, and not trusting the
        // rules either. Returns null (and says why) for a file that must not be written.
        private ZipItem PlanZipItem(ArchonZipMapping.MappedEntry mapped, ArchonZipArchive.Entry entry, out string refusal)
        {
            refusal = null;
            var role = mapped.Role;
            var prefix = role + "/";
            if (!mapped.Destination.StartsWith(prefix, StringComparison.Ordinal))
            {
                refusal = "unsafe_path " + mapped.Path;
                return null;
            }

            var relative = mapped.Destination.Substring(prefix.Length);
            var folder = ArchonZipMapping.RoleFolder(role, PluginDirectory);
            if (folder == null || !ArchonZipMapping.IsSafePath(relative))
            {
                refusal = "unsafe_path " + mapped.Path;
                return null;
            }

            string destination;
            try
            {
                destination = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, ToNativePath(relative)));
            }
            catch (Exception)
            {
                refusal = "unsafe_path " + mapped.Path;
                return null;
            }

            if (!ArchonZipMapping.IsInside(folder, destination, StringComparison.Ordinal))
            {
                refusal = "unsafe_path " + mapped.Path;
                return null;
            }

            // This plugin's own state, and its own files, are not for an archive to overwrite.
            var fileName = System.IO.Path.GetFileName(destination);
            if ((DataDirectory != null && ArchonZipMapping.IsInside(DataDirectory, destination, StringComparison.OrdinalIgnoreCase))
                || (role == ArchonZipMapping.RolePlugins && relative.IndexOf('/') < 0
                    && (string.Equals(fileName, "RustArchon.cs", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(fileName, "RustArchonUpdater.cs", StringComparison.OrdinalIgnoreCase))))
            {
                refusal = "protected " + mapped.Path;
                return null;
            }

            if (Directory.Exists(destination) || ArchonZipMapping.PassesThroughLink(folder, destination))
            {
                refusal = "unsafe_destination " + mapped.Path;
                return null;
            }

            return new ZipItem
            {
                Path = mapped.Path,
                Role = role,
                Relative = relative,
                Destination = destination,
                Size = mapped.Size,
                KeepExisting = mapped.KeepExisting,
                IsCode = mapped.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase),
                Entry = entry
            };
        }

        // Streams to disk through one small fixed buffer (an entry is never held in memory) and counts the bytes as they are produced, not as any
        // header claims them. The bound for an entry is the size the archive declared for it, enforced from both sides; the bound for the whole
        // archive is the install size (and this plugin's own MaxInstallBytes), so a zip that declares small sizes but supplies far more writes at
        // most a buffer's worth past the limit, and nothing is applied.
        private static string ExtractZipEntry(ArchonZipArchive archive, ZipItem item, long installBytes, ref long stagedTotal)
        {
            try
            {
                var limit = Math.Min(installBytes, ArchonThirdParty.MaxInstallBytes);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(item.Staged));
                using (var input = archive.OpenEntry(item.Entry))
                using (var output = new FileStream(item.Staged, FileMode.Create, FileAccess.Write))
                {
                    return CopyBounded(input, output, item.Path, item.Size, limit, ref stagedTotal);
                }
            }
            catch (Exception e)
            {
                return item.Path + " could not be unpacked: " + e.GetType().Name + ": " + e.Message;
            }
        }

        // The copy itself, on plain streams so it can be held to account: at most 16 KB in memory, aborting the moment the entry has produced more
        // than it declared, or the whole archive more than <limit>. Returns null, or what went wrong.
        internal static string CopyBounded(Stream input, Stream output, string name, long declared, long limit, ref long stagedTotal)
        {
            var buffer = new byte[16384];
            long total = 0;
            while (true)
            {
                var want = (int)Math.Min(buffer.Length, declared - total + 1);
                var read = input.Read(buffer, 0, want);
                if (read <= 0) { break; }
                total += read;
                stagedTotal += read;
                if (total > declared)
                {
                    return name + " holds more bytes than the archive declared for it (" + declared + ")";
                }
                if (stagedTotal > limit)
                {
                    return "the files in the archive unpack to more than the " + limit + " bytes that were allowed";
                }
                output.Write(buffer, 0, read);
            }

            if (total != declared)
            {
                return name + " holds " + total + " bytes but the archive declared " + declared;
            }
            return null;
        }

        private static void WriteZipItem(ZipItem item)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(item.Destination));
            var temp = item.Destination + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                File.Copy(item.Staged, temp, true);
                if (File.Exists(item.Destination))
                {
                    ArchonUpdaterSwap.SwapInto(temp, item.Destination);
                }
                else
                {
                    File.Move(temp, item.Destination);
                }
            }
            finally
            {
                ArchonUpdaterSwap.TryDelete(temp);
            }
        }

        // Puts back everything the manifest says was changed: the files that were replaced come back from the backup, the files that were created
        // are deleted, and the folders this update created go if they are empty. A file that cannot be put back is named in the reason.
        private void RollBackThirdPartyZip(string reason)
        {
            var failures = new List<string>();
            string impossible = null;

            try
            {
                var expected = (DataDirectory == null || !ArchonThirdParty.IsClassName(ThirdParty.ClassName)) ? null : ZipBackupDirectory(ThirdParty.ClassName);
                var manifest = expected == null ? null : System.IO.Path.Combine(expected, ZipManifestFileName);
                if (expected == null || ThirdParty.BackupDir != expected || !File.Exists(manifest))
                {
                    impossible = "no backup found";
                }
                else
                {
                    RestoreFromZipManifest(expected, File.ReadAllLines(manifest), failures);
                }
            }
            catch (Exception e)
            {
                impossible = "the backup could not be read: " + e.GetType().Name + ": " + e.Message;
            }

            if (impossible != null)
            {
                ThirdParty.Phase = ArchonThirdParty.PhaseFailed;
                ThirdParty.Reason = "rollback impossible: " + impossible + " (" + reason + ")";
            }
            else if (failures.Count > 0)
            {
                ThirdParty.Phase = ArchonThirdParty.PhaseFailed;
                ThirdParty.Reason = "rollback failed: could not restore " + string.Join(", ", failures.ToArray()) + " (" + reason + ")";
            }
            else
            {
                ThirdParty.Phase = ArchonThirdParty.PhaseRolledBack;
                ThirdParty.Reason = reason;
            }

            DeleteZipStaging(ThirdParty.ClassName);
            SaveThirdParty();
            StopThirdPartyTicker();
            Puts("Update of " + ThirdParty.ClassName + " to " + ThirdParty.Target + " " + ThirdParty.Phase + ": " + ThirdParty.Reason);
        }

        private void RestoreFromZipManifest(string backupDir, string[] lines, List<string> failures)
        {
            var folders = new List<string>();
            var root = ArchonZipMapping.FrameworkRoot(PluginDirectory);
            if (root != null)
            {
                folders.Add(PluginDirectory);
                folders.Add(System.IO.Path.Combine(root, "data"));
                folders.Add(System.IO.Path.Combine(root, "lang"));
                folders.Add(System.IO.Path.Combine(root, "config"));
                folders.Add(System.IO.Path.Combine(root, "configs"));
            }

            // The manifest is a file on disk like any other: what it names is only touched if it is inside one of the folders an update can write to.
            // (A folder this update created may be one of those folders itself, when the folder did not exist before.)
            Func<string, bool, bool> allowed = delegate (string path, bool orEqual)
            {
                if (path == null || path.Length == 0) { return false; }
                foreach (var folder in folders)
                {
                    if (ArchonZipMapping.IsInside(folder, path, StringComparison.Ordinal)) { return true; }
                    if (orEqual && ArchonZipMapping.SamePath(folder, path)) { return true; }
                }
                return false;
            };

            var directories = new List<string>();
            var files = new List<string[]>();
            foreach (var line in lines)
            {
                var fields = line.Split('\t');
                if (fields[0] == "D" && fields.Length == 2) { directories.Add(fields[1]); }
                else if ((fields[0] == "E" && fields.Length == 3) || (fields[0] == "N" && fields.Length == 2)) { files.Add(fields); }
            }

            // In the reverse of the order they were written: the plugin's own file first.
            for (var i = files.Count - 1; i >= 0; i--)
            {
                var fields = files[i];
                var destination = fields[1];
                if (!allowed(destination, false)) { failures.Add(destination); continue; }

                try
                {
                    if (fields[0] == "E")
                    {
                        if (!ArchonZipMapping.IsSafePath(fields[2])) { failures.Add(destination); continue; }
                        var saved = System.IO.Path.Combine(backupDir, ToNativePath(fields[2]));
                        if (!ArchonZipMapping.IsInside(backupDir, saved, StringComparison.Ordinal) || !File.Exists(saved)) { failures.Add(destination); continue; }
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination));
                        File.Copy(saved, destination, true);
                    }
                    else if (File.Exists(destination))
                    {
                        File.Delete(destination);
                    }
                }
                catch (Exception)
                {
                    failures.Add(destination);
                }
            }

            // Only the folders this update made, deepest first, and only if nothing else has come to live in them.
            directories.Sort(delegate (string a, string b) { return b.Length.CompareTo(a.Length); });
            foreach (var directory in directories)
            {
                try
                {
                    if (allowed(directory, true) && Directory.Exists(directory)
                        && Directory.GetFileSystemEntries(directory).Length == 0)
                    {
                        Directory.Delete(directory, false);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        // ---- combat log --------------------------------------------------------------------------------------
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
        // game for about a minute. The manual command refuses while players are online unless the caller says override; the
        // automatic render (a world with no picture yet) does not wait for a quiet moment. Either way it is started a moment
        // after the command replies so the Worker gets its answer before the freeze.

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
        // on: a world with no map yet gets one - whoever is online. The rule is "no picture yet, so make it", not "only when
        // it is quiet"; the players-online guard belongs to the manual command (see archon.map.render), where someone can
        // say override. A few seconds' delay lets the other plugins finish loading first.
        private void OnServerInitialized()
        {
            ConsiderAutoRender();
        }

        internal void ConsiderAutoRender()
        {
            if (!Settings.Map || !MapWorldKnown() || MapFileBytes() >= 0) { return; }

            string code;
            string message;
            if (TryScheduleMapRender(false, true, 10f, out code, out message))
            {
                Puts("No map for this world yet: rendering it shortly (the game pauses for about a minute).");
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
                if (Capabilities[i] == "thirdparty-update" && ZipSupported())
                {
                    sb.Append(',').Append(ArchonJson.Quote(ThirdPartyZipCapability));
                }
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

    // What UpdateChecker reported: one notice per plugin, the newest. Third-party text from another plugin, so every field is
    // stripped of control characters and bounded before it is kept.
    internal static class ArchonUpdates
    {
        internal const int MaxNotices = 500;
        internal const int MaxNameLength = 100;
        internal const int MaxVersionLength = 50;
        internal const int MaxMarketplaceLength = 50;
        internal const int MaxUrlLength = 500;

        internal sealed class Notice
        {
            public string Name;
            public string CurrentVersion;
            public string LatestVersion;
            public string Url;
            public string Marketplace;
            public long FirstSeenMs;
            public long LastSeenMs;
            public int TimesSeen;
        }

        // Control characters removed (a line break in a log line or a header is never wanted), surrounding space trimmed, then cut
        // to the limit. Null is nothing.
        internal static string Clean(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) { return ""; }

            var sb = new StringBuilder(Math.Min(value.Length, maxLength));
            foreach (var c in value)
            {
                if (c < ' ' || c == (char)127) { continue; }
                sb.Append(c);
                if (sb.Length >= maxLength) { break; }
            }
            return sb.ToString().Trim();
        }

        internal sealed class Store
        {
            private readonly object _lock = new object();
            private readonly Dictionary<string, Notice> _byName = new Dictionary<string, Notice>();

            public int Count
            {
                get { lock (_lock) { return _byName.Count; } }
            }

            // True when this is news: a plugin not seen before, or one whose newest version changed. Saying the same thing again
            // (UpdateChecker does, every scan) only refreshes the times and returns false, so it is not logged again.
            public bool Record(string name, string currentVersion, string latestVersion, string url, string marketplace, long nowMs)
            {
                var cleanName = Clean(name, MaxNameLength);
                if (cleanName.Length == 0) { return false; }

                var key = cleanName.ToLowerInvariant();
                var current = Clean(currentVersion, MaxVersionLength);
                var latest = Clean(latestVersion, MaxVersionLength);
                var cleanUrl = Clean(url, MaxUrlLength);
                var cleanMarketplace = Clean(marketplace, MaxMarketplaceLength);

                lock (_lock)
                {
                    Notice existing;
                    if (_byName.TryGetValue(key, out existing) && existing.LatestVersion == latest)
                    {
                        existing.CurrentVersion = current;
                        existing.Url = cleanUrl;
                        existing.Marketplace = cleanMarketplace;
                        existing.LastSeenMs = nowMs;
                        if (existing.TimesSeen < int.MaxValue) { existing.TimesSeen++; }
                        return false;
                    }

                    if (existing == null && _byName.Count >= MaxNotices) { return false; }

                    _byName[key] = new Notice
                    {
                        Name = cleanName, CurrentVersion = current, LatestVersion = latest, Url = cleanUrl, Marketplace = cleanMarketplace,
                        FirstSeenMs = nowMs, LastSeenMs = nowMs, TimesSeen = 1
                    };
                    return true;
                }
            }

            public string ToJson()
            {
                List<Notice> notices;
                lock (_lock)
                {
                    notices = new List<Notice>(_byName.Values);
                }
                notices.Sort(delegate (Notice a, Notice b) { return string.CompareOrdinal(a.Name.ToLowerInvariant(), b.Name.ToLowerInvariant()); });

                var sb = new StringBuilder(64 + notices.Count * 200);
                sb.Append("{\"format\":1,\"count\":").Append(notices.Count).Append(",\"updates\":[");
                for (var i = 0; i < notices.Count; i++)
                {
                    var n = notices[i];
                    if (i > 0) { sb.Append(','); }
                    sb.Append("{\"n\":").Append(ArchonJson.Quote(n.Name));
                    sb.Append(",\"c\":").Append(ArchonJson.Quote(n.CurrentVersion));
                    sb.Append(",\"l\":").Append(ArchonJson.Quote(n.LatestVersion));
                    sb.Append(",\"u\":").Append(ArchonJson.Quote(n.Url));
                    sb.Append(",\"m\":").Append(ArchonJson.Quote(n.Marketplace));
                    sb.Append(",\"f\":").Append(n.FirstSeenMs);
                    sb.Append(",\"s\":").Append(n.LastSeenMs);
                    sb.Append(",\"t\":").Append(n.TimesSeen).Append('}');
                }
                sb.Append("]}");
                return sb.ToString();
            }
        }
    }

    // The pure parts of replacing the Updater: phases, the state it keeps, version handling, the download and the file swap.
    internal static class ArchonUpdaterSwap
    {
        internal const string PhaseIdle = "idle";
        internal const string PhaseDownloading = "downloading";
        internal const string PhaseLoading = "loading";
        internal const string PhaseSucceeded = "succeeded";
        internal const string PhaseFailed = "failed";
        internal const string PhaseRolledBack = "rolled-back";

        internal const int MaxDownloadBytes = 1024 * 1024;
        internal const int DownloadTimeoutSeconds = 30;
        internal const string TokenHeader = "X-RustArchon-Update-Token";

        internal sealed class State
        {
            public string Phase = PhaseIdle;
            public string Target = "";
            public string Previous = "";
            public string Reason = "";
            public DateTime StartedUtc = DateTime.MinValue;
            public DateTime SwapUtc = DateTime.MinValue;
        }

        internal sealed class Download
        {
            public volatile bool Done;
            public byte[] Bytes;
            public string Error;
        }

        internal sealed class Marker
        {
            public string Version;
            public DateTime Utc = DateTime.MinValue;
        }

        // [Info("RustArchonUpdater", "<author>", "x.y.z")] - the exact quoted title means the main plugin's own [Info] can never be
        // mistaken for it.
        private static readonly System.Text.RegularExpressions.Regex InfoVersion = new System.Text.RegularExpressions.Regex(
            "\\[Info\\(\"RustArchonUpdater\"\\s*,\\s*\"[^\"]*\"\\s*,\\s*\"(\\d+\\.\\d+\\.\\d+)\"\\)\\]");

        internal static string ReadInfoVersion(string scriptText)
        {
            if (scriptText == null) { return null; }
            var match = InfoVersion.Match(scriptText);
            return match.Success ? match.Groups[1].Value : null;
        }

        internal static bool IsVersion(string text)
        {
            if (string.IsNullOrEmpty(text)) { return false; }
            var parts = text.Split('.');
            if (parts.Length != 3) { return false; }
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0 || parts[i].Length > 9) { return false; }
                for (var j = 0; j < parts[i].Length; j++)
                {
                    if (parts[i][j] < '0' || parts[i][j] > '9') { return false; }
                }
            }
            return true;
        }

        // Negative when a is older than b, zero when equal, positive when newer. Both must be x.y.z.
        internal static int CompareVersions(string a, string b)
        {
            var left = a.Split('.');
            var right = b.Split('.');
            for (var i = 0; i < 3; i++)
            {
                var l = int.Parse(left[i]);
                var r = int.Parse(right[i]);
                if (l != r) { return l < r ? -1 : 1; }
            }
            return 0;
        }

        internal static DateTime ParseUtc(string text)
        {
            DateTime utc;
            if (DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out utc))
            {
                return utc.ToUniversalTime();
            }
            return DateTime.MinValue;
        }

        // On a worker thread. Redirects are refused (a token address should answer directly, and following one could send the
        // token elsewhere) and the body is capped so a hostile server cannot make it read without limit.
        internal static byte[] DefaultDownload(string url, string token)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = DownloadTimeoutSeconds * 1000;
            request.ReadWriteTimeout = DownloadTimeoutSeconds * 1000;
            request.AllowAutoRedirect = false;
            request.UserAgent = "RustArchon";
            request.Headers[TokenHeader] = token;

            using (var response = (HttpWebResponse)request.GetResponse())
            {
                if ((int)response.StatusCode != 200)
                {
                    throw new IOException("the server answered " + (int)response.StatusCode);
                }

                using (var stream = response.GetResponseStream())
                using (var buffer = new MemoryStream())
                {
                    var chunk = new byte[8192];
                    int read;
                    while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        buffer.Write(chunk, 0, read);
                        if (buffer.Length > MaxDownloadBytes)
                        {
                            throw new IOException("the download is larger than " + MaxDownloadBytes + " bytes");
                        }
                    }
                    return buffer.ToArray();
                }
            }
        }

        internal static void SwapInto(string temp, string target)
        {
            try
            {
                File.Replace(temp, target, null);
            }
            catch (Exception)
            {
                File.Delete(target);
                File.Move(temp, target);
            }
        }

        internal static void TryDelete(string path)
        {
            try { if (File.Exists(path)) { File.Delete(path); } } catch (Exception) { }
        }
    }

    // The pure parts of applying a third-party plugin update: phases, the state it keeps, checking what the panel sent, finding the installed file,
    // reading a plugin's version, and the download.
    internal static class ArchonThirdParty
    {
        internal const string PhaseIdle = "idle";
        internal const string PhaseDownloading = "downloading";
        internal const string PhaseLoading = "loading";
        internal const string PhaseSucceeded = "succeeded";
        internal const string PhaseFailed = "failed";
        internal const string PhaseRolledBack = "rolled-back";
        // The file downloaded is not the one the panel checked: nothing was written.
        internal const string PhaseMismatch = "mismatch";
        // Files of a zip are being written to the server. A state found in this phase when the plugin (re)loads means it died mid-write.
        internal const string PhaseApplying = "applying";

        internal const string KindCs = "cs";
        internal const string KindZip = "zip";

        internal const int DownloadTimeoutSeconds = 60;
        internal const int MaxRedirects = 5;
        internal const int MaxVersionLength = 40;
        internal const int MaxUrlLength = 500;

        // This plugin's own hard limits, whatever the panel says: a hostile or mistaken size claim must never make the game server allocate or write
        // without bound. (The same figures the Api enforces; the plugin never relies on the Api's numbers being sane.)
        internal const long MaxFileBytes = 134217728;          // 128 MiB: one file, or one archive as downloaded
        internal const long MaxInstallBytes = 536870912;       // 512 MiB: everything a zip may unpack to

        internal sealed class State
        {
            public string Phase = PhaseIdle;
            public string ClassName = "";
            public string Target = "";
            public string Previous = "";
            public string File = "";
            public string Sha256 = "";
            public string ActualSha256 = "";
            public string Reason = "";
            public long Size;
            // "cs" (one plugin file) or "zip" (an archive unpacked by the folder rules); for a zip, where the previous files were backed up and how
            // many files the update wrote.
            public string Kind = KindCs;
            public string BackupDir = "";
            public int Installed;
            public DateTime StartedUtc = DateTime.MinValue;
            public DateTime SwapUtc = DateTime.MinValue;

            public string ToJson()
            {
                return "{\"phase\":" + ArchonJson.Quote(Phase)
                    + ",\"kind\":" + ArchonJson.Quote(Kind)
                    + ",\"installed\":" + Installed
                    + ",\"class\":" + ArchonJson.Quote(ClassName)
                    + ",\"targetVersion\":" + ArchonJson.Quote(Target)
                    + ",\"previousVersion\":" + ArchonJson.Quote(Previous)
                    + ",\"file\":" + ArchonJson.Quote(File)
                    + ",\"sha256\":" + ArchonJson.Quote(Sha256)
                    + ",\"actualSha256\":" + ArchonJson.Quote(ActualSha256)
                    + ",\"reason\":" + ArchonJson.Quote(Reason) + "}";
            }

            public string ToFileText()
            {
                return "phase=" + Phase + "\nclass=" + ClassName + "\ntarget=" + Target + "\nprevious=" + Previous + "\nfile=" + File
                    + "\nsha256=" + Sha256 + "\nactual=" + ActualSha256 + "\nsize=" + Size
                    + "\nkind=" + Kind + "\nbackup=" + BackupDir.Replace('\n', ' ').Replace('\r', ' ') + "\ninstalled=" + Installed
                    + "\nreason=" + Reason.Replace('\n', ' ').Replace('\r', ' ')
                    + "\nstarted=" + StartedUtc.ToString("o") + "\nswapped=" + SwapUtc.ToString("o") + "\n";
            }
        }

        internal sealed class Download
        {
            public volatile bool Done;
            public byte[] Bytes;
            public string Error;
            // Set when what came back is not what the panel described (more bytes than it said): a changed file, not a failed download.
            public string Changed;
        }

        // What the framework says about a plugin that is loaded: which instance, and the version it reports.
        internal sealed class LoadedPlugin
        {
            public object Instance;
            public string Version;
        }

        internal sealed class ChangedException : Exception
        {
            public ChangedException(string message) : base(message) { }
        }

        // A class name as the panel gives it: a plain identifier, nothing that could be a path or a pattern.
        internal static bool IsClassName(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 100) { return false; }
            if (!(char.IsLetter(text[0]) || text[0] == '_') || text[0] > 127) { return false; }
            for (var i = 1; i < text.Length; i++)
            {
                var c = text[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')) { return false; }
            }
            return true;
        }

        internal static bool IsVersionText(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > MaxVersionLength) { return false; }
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '+')) { return false; }
            }
            return true;
        }

        internal static bool IsSha256(string text)
        {
            if (text == null || text.Length != 64) { return false; }
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) { return false; }
            }
            return true;
        }

        internal static bool TryParseHttps(string text, out Uri uri)
        {
            uri = null;
            if (string.IsNullOrEmpty(text) || text.Length > MaxUrlLength) { return false; }
            Uri parsed;
            if (!Uri.TryCreate(text, UriKind.Absolute, out parsed)) { return false; }
            if (parsed.Scheme != Uri.UriSchemeHttps || parsed.Host.Length == 0 || parsed.UserInfo.Length > 0) { return false; }
            uri = parsed;
            return true;
        }

        internal static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes ?? new byte[0]);
                var sb = new StringBuilder(hash.Length * 2);
                for (var i = 0; i < hash.Length; i++) { sb.Append(hash[i].ToString("x2")); }
                return sb.ToString();
            }
        }

        // Every .cs file directly in the plugins folder that declares "class <className> :" and carries an [Info] - the plugin, as the framework
        // will find it. The two RustArchon files are never candidates. Reports how many there are and, when there is exactly one, which.
        internal static void FindInstalled(string pluginDirectory, string className, out string path, out int count)
        {
            path = null;
            count = 0;
            string[] files;
            try { files = Directory.GetFiles(pluginDirectory, "*.cs", SearchOption.TopDirectoryOnly); }
            catch (Exception) { return; }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (name == "RustArchon.cs" || name == "RustArchonUpdater.cs") { continue; }

                string text;
                try { text = File.ReadAllText(file); }
                catch (Exception) { continue; }

                if (text.IndexOf("[Info(", StringComparison.Ordinal) >= 0 && DeclaresClass(text, className))
                {
                    count++;
                    if (count == 1) { path = file; }
                }
            }

            if (count != 1) { path = null; }
        }

        internal static bool DeclaresClass(string text, string className)
        {
            if (text == null || !IsClassName(className)) { return false; }
            return System.Text.RegularExpressions.Regex.IsMatch(text, "\\bclass\\s+" + className + "\\s*:");
        }

        // [Info("<title>", "<author>", "<version>")] - the version text as the author wrote it.
        private static readonly System.Text.RegularExpressions.Regex InfoVersion = new System.Text.RegularExpressions.Regex(
            "\\[Info\\(\\s*\"[^\"]*\"\\s*,\\s*\"[^\"]*\"\\s*,\\s*\"([^\"]*)\"\\s*\\)\\]");

        internal static string ReadInfoVersion(string scriptText)
        {
            if (scriptText == null) { return null; }
            var match = InfoVersion.Match(scriptText);
            return match.Success ? match.Groups[1].Value : null;
        }

        // The framework keeps a plugin's version as three numbers, so "1.2" and "1.2.0" are the same version there, and a leading "v" is not part of
        // it. Anything that is not numbers is compared as written.
        internal static string NormalizeVersion(string text)
        {
            var trimmed = (text ?? "").Trim();
            if (trimmed.Length > 0 && (trimmed[0] == 'v' || trimmed[0] == 'V')) { trimmed = trimmed.Substring(1); }

            var parts = trimmed.Split('.');
            if (parts.Length < 1 || parts.Length > 3) { return trimmed; }

            var numbers = new int[3];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out numbers[i]) || numbers[i] < 0) { return trimmed; }
            }
            return numbers[0] + "." + numbers[1] + "." + numbers[2];
        }

        internal static bool SameVersion(string a, string b)
        {
            return string.Equals(NormalizeVersion(a), NormalizeVersion(b), StringComparison.Ordinal);
        }

        // On a worker thread. Follows up to MaxRedirects redirects by hand so that every hop can be checked to be https; the body is read only up to
        // the size the panel saw, plus one byte to notice more (a changed file). It is never read without limit, and there is no arbitrary cap: the
        // panel's own figure is the bound.
        internal static byte[] DefaultDownload(string url, long expectedSize)
        {
            var current = url;
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                Uri uri;
                if (!TryParseHttps(current, out uri)) { throw new IOException("only https addresses are followed"); }

                var request = (HttpWebRequest)WebRequest.Create(uri);
                request.Timeout = DownloadTimeoutSeconds * 1000;
                request.ReadWriteTimeout = DownloadTimeoutSeconds * 1000;
                request.AllowAutoRedirect = false;
                request.UserAgent = "RustArchon";

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    var code = (int)response.StatusCode;
                    if (code >= 300 && code < 400 && code != 304)
                    {
                        var location = response.Headers["Location"];
                        Uri next;
                        if (string.IsNullOrEmpty(location) || !Uri.TryCreate(uri, location, out next)) { throw new IOException("a redirect with no usable address"); }
                        current = next.AbsoluteUri;
                        continue;
                    }

                    if (code != 200) { throw new IOException("the server answered " + code); }

                    // Never sized from what the server says (Content-Length): only from the bytes that actually arrive.
                    using (var stream = response.GetResponseStream())
                    {
                        return ReadBounded(stream, expectedSize);
                    }
                }
            }

            throw new IOException("more than " + MaxRedirects + " redirects");
        }

        // Reads a stream to its end, holding only what has arrived so far, and gives up the moment more than <expectedSize> bytes have: a stream
        // that never ends cannot make this allocate more than expectedSize (plus one chunk). A size above this plugin's own limit is never honoured.
        internal static byte[] ReadBounded(Stream stream, long expectedSize)
        {
            if (expectedSize > MaxFileBytes) { expectedSize = MaxFileBytes; }

            using (var buffer = new MemoryStream())
            {
                var chunk = new byte[8192];
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > expectedSize)
                    {
                        throw new ChangedException("the download is larger than the " + expectedSize + " bytes the panel checked");
                    }
                }
                return buffer.ToArray();
            }
        }
    }

    // The folder rules of a zip archive: this plugin's OWN copy of RustArchon.Shared.PluginZips.ZipMapping (a plugin is one file and cannot reference
    // the shared library). The same algorithm, and the plugin's tests hold it to the same answers as the original on every case they can think of and
    // several hundred generated ones. If one changes, so does the other. It is what makes the plugin independent of the panel: it works out for
    // itself, from the archive it downloaded, what would be written where, and refuses anything unsafe or ambiguous.
    internal static class ArchonZipMapping
    {
        internal const string RolePlugins = "plugins";
        internal const string RoleConfig = "config";
        internal const string RoleData = "data";
        internal const string RoleLang = "lang";
        internal const string RoleSkip = "skip";

        internal const string CodeBadRule = "bad_rule";
        internal const string CodeUnassigned = "unassigned";
        internal const string CodeUnsafePath = "unsafe_path";
        internal const string CodeExecutable = "executable";
        internal const string CodeCollision = "collision";
        internal const string CodeOutsidePlugins = "cs_outside_plugins";

        internal const int MaxRules = 60;
        internal const int MaxPathLength = 240;
        internal const int MaxEncodedLength = 3000;

        internal enum EntryAction { Install, Skip, Unassigned }

        internal sealed class Rule
        {
            public bool IsFolder;
            public string Source = "";
            public string Role = RoleSkip;
            public string SubFolder = "";
            public bool KeepExisting;
        }

        internal sealed class EntryInfo
        {
            public readonly string Path;
            public readonly long Size;
            public EntryInfo(string path, long size) { Path = path; Size = size; }
        }

        internal sealed class MappedEntry
        {
            public string Path;
            public long Size;
            public EntryAction Action;
            public string Role;
            public string Destination;
            public bool KeepExisting;
        }

        internal sealed class Problem
        {
            public string Code;
            public string Path;
        }

        internal sealed class Result
        {
            public readonly List<MappedEntry> Entries = new List<MappedEntry>();
            public readonly List<Problem> Problems = new List<Problem>();
        }

        private static readonly string[] ExecutableExtensions =
            { ".dll", ".so", ".dylib", ".exe", ".bat", ".cmd", ".sh", ".ps1", ".msi", ".com", ".scr", ".vbs", ".jar", ".bin" };

        internal static bool IsRole(string role)
        {
            return role == RoleSkip || role == RolePlugins || role == RoleConfig || role == RoleData || role == RoleLang;
        }

        // By its extension: the text after the last dot of the file's own name.
        internal static bool IsExecutable(string path)
        {
            var slash = path.LastIndexOf('/');
            var name = slash >= 0 ? path.Substring(slash + 1) : path;
            var dot = name.LastIndexOf('.');
            if (dot < 0 || dot == name.Length - 1) { return false; }
            var extension = name.Substring(dot);
            foreach (var known in ExecutableExtensions)
            {
                if (string.Equals(extension, known, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        internal static string NormalizePath(string path)
        {
            return path.Replace('\\', '/').TrimStart('/');
        }

        internal static bool IsSafePath(string path)
        {
            if (path.Length == 0 || path.Length > MaxPathLength) { return false; }

            foreach (var segment in path.Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == ".." || segment[segment.Length - 1] == '.'
                    || segment[segment.Length - 1] == ' ' || segment[0] == ' ')
                {
                    return false;
                }

                foreach (var c in segment)
                {
                    if (c < ' ' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|' || c == '')
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private sealed class Usable
        {
            public Rule Rule;
            public string Source;
        }

        internal static Result Resolve(IList<EntryInfo> entries, IList<Rule> rules)
        {
            var result = new Result();
            var usable = new List<Usable>();

            if (rules.Count > MaxRules)
            {
                result.Problems.Add(new Problem { Code = CodeBadRule, Path = "" });
            }

            for (var r = 0; r < rules.Count && r < MaxRules; r++)
            {
                var rule = rules[r];
                var source = NormalizePath(rule.Source ?? "");
                if (rule.IsFolder) { source = source.TrimEnd('/') + "/"; }

                var sub = NormalizePath(rule.SubFolder ?? "").TrimEnd('/');
                var label = rule.Source ?? "";
                if (source.Length <= (rule.IsFolder ? 1 : 0) || !IsSafePath(source.TrimEnd('/')))
                {
                    result.Problems.Add(new Problem { Code = CodeBadRule, Path = label });
                }
                else if (!IsRole(rule.Role))
                {
                    result.Problems.Add(new Problem { Code = CodeBadRule, Path = label });
                }
                else if (sub.Length > 0 && (rule.Role == RoleSkip || !IsSafePath(sub)))
                {
                    result.Problems.Add(new Problem { Code = CodeBadRule, Path = label });
                }
                else
                {
                    usable.Add(new Usable { Rule = rule, Source = source });
                }
            }

            var destinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                var path = NormalizePath(entry.Path);
                if (!IsSafePath(path))
                {
                    result.Entries.Add(new MappedEntry { Path = path, Size = entry.Size, Action = EntryAction.Unassigned, Role = "", Destination = "" });
                    result.Problems.Add(new Problem { Code = CodeUnsafePath, Path = path });
                    continue;
                }

                var match = Match(path, usable);
                if (match == null)
                {
                    result.Entries.Add(new MappedEntry { Path = path, Size = entry.Size, Action = EntryAction.Unassigned, Role = "", Destination = "" });
                    result.Problems.Add(new Problem { Code = CodeUnassigned, Path = path });
                    continue;
                }

                var rule = match.Rule;
                var source = match.Source;
                if (rule.Role == RoleSkip)
                {
                    result.Entries.Add(new MappedEntry { Path = path, Size = entry.Size, Action = EntryAction.Skip, Role = RoleSkip, Destination = "" });
                    continue;
                }

                var below = rule.IsFolder ? path.Substring(source.Length) : path.Substring(path.LastIndexOf('/') + 1);
                var sub = NormalizePath(rule.SubFolder ?? "").TrimEnd('/');
                var destination = rule.Role + "/" + (sub.Length > 0 ? sub + "/" : "") + below;
                result.Entries.Add(new MappedEntry
                {
                    Path = path, Size = entry.Size, Action = EntryAction.Install, Role = rule.Role, Destination = destination, KeepExisting = rule.KeepExisting
                });

                if (IsExecutable(path))
                {
                    result.Problems.Add(new Problem { Code = CodeExecutable, Path = path });
                }

                if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && rule.Role != RolePlugins)
                {
                    result.Problems.Add(new Problem { Code = CodeOutsidePlugins, Path = path });
                }

                if (!IsSafePath(destination))
                {
                    result.Problems.Add(new Problem { Code = CodeUnsafePath, Path = path });
                }
                else
                {
                    string other;
                    if (destinations.TryGetValue(destination, out other))
                    {
                        result.Problems.Add(new Problem { Code = CodeCollision, Path = path });
                    }
                    else
                    {
                        destinations[destination] = path;
                    }
                }
            }

            return result;
        }

        // The rule that decides the path: a rule for that exact file (the first one), else the deepest folder rule above it, else none.
        private static Usable Match(string path, List<Usable> rules)
        {
            Usable best = null;
            foreach (var candidate in rules)
            {
                if (!candidate.Rule.IsFolder)
                {
                    if (string.Equals(candidate.Source, path, StringComparison.Ordinal)) { return candidate; }
                    continue;
                }

                if (path.StartsWith(candidate.Source, StringComparison.Ordinal) && (best == null || candidate.Source.Length > best.Source.Length))
                {
                    best = candidate;
                }
            }
            return best;
        }

        // The rules from the command argument: URL-safe base64 of one line per rule, F|E, source, role, sub folder, 1|0 (tab separated). Null if
        // it is not that, or has no rules or too many.
        internal static List<Rule> Decode(string encoded)
        {
            if (string.IsNullOrEmpty(encoded) || encoded.Length > MaxEncodedLength) { return null; }

            try
            {
                var padded = encoded.Replace('-', '+').Replace('_', '/');
                padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
                var text = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(padded));
                var rules = new List<Rule>();
                foreach (var line in text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var fields = line.Split('\t');
                    if (fields.Length != 5 || !(fields[0] == "F" || fields[0] == "E") || !(fields[4] == "0" || fields[4] == "1"))
                    {
                        return null;
                    }

                    rules.Add(new Rule { IsFolder = fields[0] == "F", Source = fields[1], Role = fields[2], SubFolder = fields[3], KeepExisting = fields[4] == "1" });
                }

                return rules.Count > 0 && rules.Count <= MaxRules ? rules : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---- where the roles are on this server ------------------------------------------------------------

        // The framework's folder: the one the plugins folder is in (oxide or carbon).
        internal static string FrameworkRoot(string pluginDirectory)
        {
            if (string.IsNullOrEmpty(pluginDirectory)) { return null; }
            var trimmed = pluginDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetDirectoryName(trimmed);
            return string.IsNullOrEmpty(root) ? null : root;
        }

        // plugins: the plugins folder; data and lang: beside it; config: "configs" if that folder exists, else "config" if that does, else "configs"
        // for Carbon (a folder named carbon) and "config" for anything else (Oxide).
        internal static string RoleFolder(string role, string pluginDirectory)
        {
            var root = FrameworkRoot(pluginDirectory);
            if (root == null) { return null; }

            switch (role)
            {
                case RolePlugins: return pluginDirectory;
                case RoleData: return Path.Combine(root, "data");
                case RoleLang: return Path.Combine(root, "lang");
                case RoleConfig:
                    var configs = Path.Combine(root, "configs");
                    var config = Path.Combine(root, "config");
                    if (Directory.Exists(configs)) { return configs; }
                    if (Directory.Exists(config)) { return config; }
                    return string.Equals(Path.GetFileName(root), "carbon", StringComparison.OrdinalIgnoreCase) ? configs : config;
                default: return null;
            }
        }

        private static string Bare(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        internal static bool SamePath(string a, string b)
        {
            try { return string.Equals(Bare(a), Bare(b), StringComparison.Ordinal); }
            catch (Exception) { return false; }
        }

        // Strictly inside the folder (the folder itself is not).
        internal static bool IsInside(string folder, string path, StringComparison comparison)
        {
            try
            {
                var prefix = Bare(folder) + Path.DirectorySeparatorChar;
                return Path.GetFullPath(path).StartsWith(prefix, comparison);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // A folder between the role's folder and the file, or the file itself, that is a link (symbolic link, junction) could lead out of the folder.
        internal static bool PassesThroughLink(string folder, string destination)
        {
            try
            {
                var directory = Path.GetDirectoryName(destination);
                while (directory != null && IsInside(folder, directory, StringComparison.Ordinal))
                {
                    if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { return true; }
                    directory = Path.GetDirectoryName(directory);
                }

                return File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    // Reads a zip archive without the plugin depending on System.IO.Compression at COMPILE time: the game server compiles this file itself, and if its
    // compiler does not reference that assembly a plain "using" would stop the whole plugin from compiling. So the assembly is looked up by name when
    // it is needed, and everything below is reflection. If it cannot be found the plugin works as before and only loses the zip capability.
    internal sealed class ArchonZipArchive : IDisposable
    {
        internal const int MaxEntries = 2000;
        private const string Namespace = "System.IO.Compression.";

        internal sealed class Entry
        {
            public string Name;
            public long Length;
            internal object Raw;
        }

        private sealed class Members
        {
            public System.Reflection.ConstructorInfo Constructor;
            public object ReadMode;
            public System.Reflection.PropertyInfo Entries;
            public System.Reflection.PropertyInfo FullName;
            public System.Reflection.PropertyInfo Length;
            public System.Reflection.MethodInfo Open;
        }

        private readonly MemoryStream _stream;
        private readonly object _archive;
        private readonly Members _members;

        // Files only (folder entries are dropped), in archive order. Empty when there are more entries than MaxEntries.
        internal readonly List<Entry> Files = new List<Entry>();
        internal int EntryCount;
        internal bool TooManyEntries;

        private ArchonZipArchive(MemoryStream stream, object archive, Members members)
        {
            _stream = stream;
            _archive = archive;
            _members = members;
        }

        // The zip archive type if it can be loaded by name AND has every member this reader needs; otherwise null.
        internal static Type DefaultLoadType()
        {
            Type type = null;
            try { type = Type.GetType(Namespace + "ZipArchive, System.IO.Compression", false); }
            catch (Exception) { }

            if (type == null)
            {
                try { type = System.Reflection.Assembly.Load("System.IO.Compression").GetType(Namespace + "ZipArchive", false); }
                catch (Exception) { }
            }

            return Resolve(type) == null ? null : type;
        }

        internal static bool CanRead(Type zip)
        {
            return Resolve(zip) != null;
        }

        private static Members Resolve(Type zip)
        {
            if (zip == null) { return null; }

            try
            {
                var assembly = zip.Assembly;
                var mode = assembly.GetType(Namespace + "ZipArchiveMode", false);
                var entry = assembly.GetType(Namespace + "ZipArchiveEntry", false);
                if (mode == null || entry == null || !mode.IsEnum) { return null; }

                var members = new Members
                {
                    Constructor = zip.GetConstructor(new[] { typeof(Stream), mode }),
                    ReadMode = Enum.Parse(mode, "Read"),
                    Entries = zip.GetProperty("Entries"),
                    FullName = entry.GetProperty("FullName"),
                    Length = entry.GetProperty("Length"),
                    Open = entry.GetMethod("Open", Type.EmptyTypes)
                };

                if (members.Constructor == null || members.Entries == null || members.FullName == null || members.Length == null || members.Open == null)
                {
                    return null;
                }
                return members;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Throws (IOException and friends) for something that is not a readable zip archive.
        internal static ArchonZipArchive Open(Type zip, byte[] bytes)
        {
            var members = Resolve(zip);
            if (members == null) { throw new InvalidOperationException("the zip reader is not usable on this server"); }

            var stream = new MemoryStream(bytes, false);
            try
            {
                var archive = members.Constructor.Invoke(new object[] { stream, members.ReadMode });
                var result = new ArchonZipArchive(stream, archive, members);
                result.ListEntries();
                return result;
            }
            catch (System.Reflection.TargetInvocationException e)
            {
                stream.Dispose();
                var inner = e.InnerException ?? e;
                throw new IOException(inner.GetType().Name + ": " + inner.Message);
            }
            catch (Exception)
            {
                stream.Dispose();
                throw;
            }
        }

        private void ListEntries()
        {
            var entries = (System.Collections.IEnumerable)_members.Entries.GetValue(_archive, null);

            // How many, before any of them is looked at.
            var collection = entries as System.Collections.ICollection;
            if (collection != null)
            {
                EntryCount = collection.Count;
                if (EntryCount > MaxEntries) { TooManyEntries = true; return; }
            }

            var seen = 0;
            foreach (var raw in entries)
            {
                seen++;
                if (seen > MaxEntries)
                {
                    EntryCount = seen;
                    TooManyEntries = true;
                    Files.Clear();
                    return;
                }

                var name = (string)_members.FullName.GetValue(raw, null);
                if (name == null || name.EndsWith("/", StringComparison.Ordinal)) { continue; }
                Files.Add(new Entry { Name = name, Length = Convert.ToInt64(_members.Length.GetValue(raw, null)), Raw = raw });
            }
            if (collection == null) { EntryCount = seen; }
        }

        internal Stream OpenEntry(Entry entry)
        {
            try
            {
                return (Stream)_members.Open.Invoke(entry.Raw, null);
            }
            catch (System.Reflection.TargetInvocationException e)
            {
                var inner = e.InnerException ?? e;
                throw new IOException(inner.GetType().Name + ": " + inner.Message);
            }
        }

        public void Dispose()
        {
            var disposable = _archive as IDisposable;
            if (disposable != null) { try { disposable.Dispose(); } catch (Exception) { } }
            _stream.Dispose();
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
