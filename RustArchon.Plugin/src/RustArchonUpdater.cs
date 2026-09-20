// Copyright ©2026 Scott Blomfield

// RustArchon Updater for Carbon and Oxide.
//
// A small, separate plugin whose only job is to replace the main RustArchon plugin with a newer, signed version, and
// to put the old one back if the new one does not come up. It exists as its own file because a plugin that fails to
// compile is unloaded and stays dead (Carbon lists it as failed), and a dead plugin cannot roll itself back. This one
// depends on nothing from the game (no Rust internals), so a Rust update that breaks the main plugin cannot break it,
// and it can fetch the fix. See docs/adr/0004-companion-plugin-rcon-pull-dormant-hooks-signed-updates.md.
//
// Written for the C# 7.3 subset (see RustArchon.cs). Every command is RCON-only.
//
// How an update goes:
//   1. The Panel (through the Worker) sends: archon.update <version> <url> [token]. The token is single-use and short-lived;
//      this plugin holds no credential of its own. From 0.3.0 the token is sent in a request header (X-RustArchon-Update-Token),
//      so it never appears in a URL that a proxy or web server log could keep. Without the third argument the URL itself carries
//      the token, as before: what an older Panel sends, and what this still accepts.
//   2. The file is downloaded on a worker thread (capped in size, redirects refused), never on the game thread.
//   3. It is accepted only if ALL of these hold: its last-line signature verifies under the key the INSTALLED main
//      plugin trusts (the key stamped into that file, which must itself verify - so the trust follows the plugin
//      through every key change and is never stale); if the new file stamps a DIFFERENT key (a bridge to a newer key)
//      it must also carry that new key's own signature just above the last line; its [Info] version is the one
//      asked for; and that version is newer than the one installed (no silent downgrade).
//   4. The old file is kept as RustArchon.cs.bak, the new one is swapped in (File.Replace), and the main plugin's
//      "loaded" marker is watched. If the new version writes its marker, the update succeeded. If not within the time
//      limit (it failed to compile, or crashed in Init), the .bak is put back.
// The Updater itself is only ever updated by hand: a failed Updater would have nothing left to recover it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Oxide.Plugins
{
    [Info("RustArchonUpdater", "RustArchon", "0.3.0")]
    [Description("Replaces the RustArchon plugin with a newer signed version, and rolls back if it does not load. RCON-only.")]
    public class RustArchonUpdater : RustPlugin
    {
        internal const string MainScriptFileName = "RustArchon.cs";
        internal const string StatusFileName = "update-status.txt";
        internal const string LoadedMarkerFileName = "loaded.txt";
        internal const int MaxDownloadBytes = 1024 * 1024;
        internal const int DownloadTimeoutSeconds = 30;
        internal const int MaxTokenLength = 128;
        internal const string TokenHeaderName = "X-RustArchon-Update-Token";
        internal const int LoadWaitSeconds = 45;

        // Phases of an update. "idle", "succeeded", "failed" and "rolled-back" are resting states.
        internal const string PhaseIdle = "idle";
        internal const string PhaseDownloading = "downloading";
        internal const string PhaseLoading = "loading";
        internal const string PhaseSucceeded = "succeeded";
        internal const string PhaseFailed = "failed";
        internal const string PhaseRolledBack = "rolled-back";

        // Resolved by Init unless something (a test) set them first.
        internal string PluginDirectory;
        internal string DataDirectory;
        internal string ScriptFilePath;

        // The key this Updater trusts: stamped in by the Panel that served it. Fields, not constants, only so a test
        // can supply one; nothing at runtime ever assigns them.
        internal string TrustedModulus = UpdaterIntegrity.TrustedModulus;
        internal string TrustedExponent = UpdaterIntegrity.TrustedExponent;

        internal Func<string, string, byte[]> Download = DefaultDownload;
        internal Func<DateTime> UtcNow = DefaultUtcNow;

        internal UpdateState State = new UpdateState();
        internal UpdaterIntegrity.Result Integrity = UpdaterIntegrity.Result.Unlocated;
        internal Task DownloadTask;

        private DownloadResult _download;
        private Timer _ticker;

        internal sealed class UpdateState
        {
            public string Phase = PhaseIdle;
            public string TargetVersion = "";
            public string Reason = "";
            public DateTime StartedUtc = DateTime.MinValue;
            public DateTime SwapUtc = DateTime.MinValue;
        }

        private sealed class DownloadResult
        {
            public volatile bool Done;
            public byte[] Bytes;
            public string Error;
        }

        private void Init()
        {
            if (PluginDirectory == null) { PluginDirectory = LocatePluginDirectory(); }
            if (DataDirectory == null) { DataDirectory = LocateDataDirectory(); }
            if (ScriptFilePath == null && PluginDirectory != null)
            {
                var own = Path.Combine(PluginDirectory, "RustArchonUpdater.cs");
                if (File.Exists(own)) { ScriptFilePath = own; }
            }

            Integrity = UpdaterIntegrity.CheckFile(ScriptFilePath, TrustedModulus, TrustedExponent);
            State = LoadState();

            // An update that was swapped in but not yet confirmed when this plugin (re)loaded: keep watching for the
            // marker instead of forgetting it and leaving a possibly dead main plugin unrecovered.
            if (State.Phase == PhaseLoading)
            {
                StartTicking();
            }
            else if (State.Phase == PhaseDownloading)
            {
                // The download died with the previous instance; nothing was changed on disk.
                State.Phase = PhaseFailed;
                State.Reason = "interrupted: the Updater reloaded during the download";
                SaveState();
            }

            Puts("Init v" + Version + " phase=" + State.Phase + " signature=" + Integrity.State
                + (Integrity.KeyFingerprint.Length > 0 ? " key=" + Integrity.KeyFingerprint : ""));
        }

        private void Unload()
        {
            StopTicking();
        }

        // archon.update <version> <url> [token]
        [ConsoleCommand("archon.update")]
        internal void CmdUpdate(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) { return; }

            if (!arg.HasArgs(2) || arg.HasArgs(4))
            {
                arg.ReplyWith(UpdaterJson.Err("usage", "archon.update <version> <url> [token]"));
                return;
            }

            arg.ReplyWith(Begin(arg.GetString(0, ""), arg.GetString(1, ""), arg.HasArgs(3) ? arg.GetString(2, "") : null));
        }

        // archon.update.status
        [ConsoleCommand("archon.update.status")]
        internal void CmdStatus(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) { return; }

            arg.ReplyWith(UpdaterJson.Ok(StatusJson()));
        }

        // Validates the request and starts the download. Returns the JSON reply.
        // token is null when the URL itself carries the credential (the form an older Panel sends).
        internal string Begin(string version, string url, string token = null)
        {
            if (State.Phase == PhaseDownloading || State.Phase == PhaseLoading)
            {
                return UpdaterJson.Err("busy", "an update is already in progress (" + State.Phase + ")");
            }

            if (!UpdaterLogic.IsVersion(version))
            {
                return UpdaterJson.Err("bad_version", "version must look like 1.2.3");
            }

            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                return UpdaterJson.Err("bad_url", "url must be an absolute http or https address");
            }

            if (token != null && !UpdaterLogic.IsToken(token))
            {
                return UpdaterJson.Err("bad_token", "the token must be 1 to " + MaxTokenLength + " letters, digits, dashes or underscores");
            }

            var installed = ReadInstalledVersion();
            if (installed == null)
            {
                return UpdaterJson.Err("main_missing", "the main RustArchon plugin file was not found or has no version");
            }

            if (UpdaterLogic.CompareVersions(version, installed) <= 0)
            {
                return UpdaterJson.Err("not_newer", "installed " + installed + ", offered " + version);
            }

            State = new UpdateState { Phase = PhaseDownloading, TargetVersion = version, StartedUtc = UtcNow() };
            SaveState();

            var result = new DownloadResult();
            _download = result;
            DownloadTask = Task.Run(delegate
            {
                try
                {
                    result.Bytes = Download(url, token);
                }
                catch (Exception e)
                {
                    result.Error = e.GetType().Name + ": " + e.Message;
                }
                finally
                {
                    result.Done = true;
                }
            });

            StartTicking();
            return UpdaterJson.Ok("{\"phase\":\"downloading\",\"installedVersion\":" + UpdaterJson.Quote(installed)
                + ",\"targetVersion\":" + UpdaterJson.Quote(version) + "}");
        }

        // Runs once a second, on the game thread, only while an update is in progress.
        internal void Tick()
        {
            switch (State.Phase)
            {
                case PhaseDownloading:
                    TickDownloading();
                    break;
                case PhaseLoading:
                    TickLoading();
                    break;
                default:
                    StopTicking();
                    break;
            }
        }

        private void TickDownloading()
        {
            var result = _download;
            if (result == null || !result.Done)
            {
                if ((UtcNow() - State.StartedUtc).TotalSeconds > DownloadTimeoutSeconds + 15)
                {
                    Fail("download_timeout", "no answer within " + (DownloadTimeoutSeconds + 15) + " seconds");
                }
                return;
            }

            if (result.Error != null || result.Bytes == null)
            {
                Fail("download_failed", result.Error ?? "no data");
                return;
            }

            VerifyAndApply(result.Bytes);
        }

        private void VerifyAndApply(byte[] bytes)
        {
            if (!UpdaterIntegrity.IsStamped(TrustedModulus, TrustedExponent))
            {
                Fail("updater_not_stamped", "this Updater carries no trusted key (a developer copy), so it will not install anything");
                return;
            }

            // The key to trust is the one the installed main plugin carries, provided that plugin verifies under it.
            // Not this file's own stamp: this file is only updated by hand, so its key would go stale at the first
            // key change, and a key retired since could still sign for it.
            string anchorModulus;
            string anchorExponent;
            var anchor = ReadInstalledTrust(out anchorModulus, out anchorExponent);
            if (anchor.State != "valid")
            {
                Fail("main_not_verified", "the installed plugin does not verify under its own key (" + anchor.State + "), so there is nothing to trust");
                return;
            }

            var check = UpdaterIntegrity.CheckLastLine(bytes, anchorModulus, anchorExponent);
            if (check.State != "valid")
            {
                Fail("signature_" + check.State, "the downloaded file did not verify against the installed plugin's key " + anchor.KeyFingerprint);
                return;
            }

            var text = Encoding.UTF8.GetString(bytes);
            var newModulus = UpdaterLogic.ExtractConstant(text, "TrustedModulus");
            var newExponent = UpdaterLogic.ExtractConstant(text, "TrustedExponent");
            if (!UpdaterIntegrity.IsStamped(newModulus, newExponent))
            {
                Fail("new_key_invalid", "the downloaded file carries no usable trusted key");
                return;
            }

            if (newModulus != anchorModulus || newExponent != anchorExponent)
            {
                // A bridge: signed by the key we trust now, moving to a different one. The new key must vouch for the
                // file too, or the new plugin would report itself invalid and the move could not be checked later.
                var cosign = UpdaterIntegrity.CheckCosignature(bytes, newModulus, newExponent);
                if (cosign.State != "valid")
                {
                    Fail("bridge_not_cosigned", "the file moves to key " + UpdaterIntegrity.Fingerprint(newModulus)
                        + " but that key has not signed it (" + cosign.State + ")");
                    return;
                }

                Puts("Key bridge: " + anchor.KeyFingerprint + " -> " + UpdaterIntegrity.Fingerprint(newModulus));
            }

            var offered = UpdaterLogic.ReadInfoVersion(text);
            if (offered == null)
            {
                Fail("no_version", "the downloaded file has no RustArchon [Info] version");
                return;
            }

            if (offered != State.TargetVersion)
            {
                Fail("version_mismatch", "asked for " + State.TargetVersion + " but the file is " + offered);
                return;
            }

            var installed = ReadInstalledVersion();
            if (installed == null || UpdaterLogic.CompareVersions(offered, installed) <= 0)
            {
                Fail("not_newer", "installed " + (installed ?? "none") + ", offered " + offered);
                return;
            }

            Apply(bytes);
        }

        private void Apply(byte[] bytes)
        {
            var target = Path.Combine(PluginDirectory, MainScriptFileName);
            var temp = target + ".new";
            var backup = target + ".bak";

            try
            {
                Directory.CreateDirectory(DataDirectory);
                File.WriteAllBytes(temp, bytes);
                File.Copy(target, backup, true);
                State.SwapUtc = UtcNow();
                SwapInto(temp, target);
            }
            catch (Exception e)
            {
                TryDelete(temp);
                Fail("apply_failed", e.GetType().Name + ": " + e.Message);
                return;
            }

            State.Phase = PhaseLoading;
            State.Reason = "";
            SaveState();
            Puts("Swapped in RustArchon " + State.TargetVersion + "; waiting for it to load.");
        }

        private void TickLoading()
        {
            var marker = ReadMarker();
            if (marker != null && marker.Version == State.TargetVersion && marker.Utc >= State.SwapUtc.AddSeconds(-2))
            {
                State.Phase = PhaseSucceeded;
                State.Reason = "";
                SaveState();
                StopTicking();
                Puts("RustArchon " + State.TargetVersion + " loaded; update succeeded.");
                return;
            }

            if ((UtcNow() - State.SwapUtc).TotalSeconds > LoadWaitSeconds)
            {
                RollBack("the new version did not report loading within " + LoadWaitSeconds + " seconds");
            }
        }

        private void RollBack(string reason)
        {
            var target = Path.Combine(PluginDirectory, MainScriptFileName);
            var backup = target + ".bak";

            try
            {
                if (!File.Exists(backup))
                {
                    State.Phase = PhaseFailed;
                    State.Reason = "rollback impossible: no backup found (" + reason + ")";
                }
                else
                {
                    File.Copy(backup, target, true);
                    State.Phase = PhaseRolledBack;
                    State.Reason = reason;
                }
            }
            catch (Exception e)
            {
                State.Phase = PhaseFailed;
                State.Reason = "rollback failed: " + e.GetType().Name + ": " + e.Message + " (" + reason + ")";
            }

            SaveState();
            StopTicking();
            Puts("Update " + State.TargetVersion + " " + State.Phase + ": " + State.Reason);
        }

        private void Fail(string code, string detail)
        {
            State.Phase = PhaseFailed;
            State.Reason = code + ": " + detail;
            SaveState();
            StopTicking();
            Puts("Update " + State.TargetVersion + " failed: " + State.Reason);
        }

        private static void SwapInto(string temp, string target)
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

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) { File.Delete(path); } } catch (Exception) { }
        }

        // ---- reading what is on disk ------------------------------------------------------------------------

        // The key the installed main plugin carries and whether that plugin verifies under it. The modulus and exponent
        // come back only when it does ("valid"); anything else leaves them null and nothing is trusted.
        internal UpdaterIntegrity.Result ReadInstalledTrust(out string modulus, out string exponent)
        {
            modulus = null;
            exponent = null;

            try
            {
                var path = PluginDirectory == null ? null : Path.Combine(PluginDirectory, MainScriptFileName);
                if (path == null || !File.Exists(path)) { return UpdaterIntegrity.Result.Unlocated; }

                var bytes = File.ReadAllBytes(path);
                var text = Encoding.UTF8.GetString(bytes);
                var m = UpdaterLogic.ExtractConstant(text, "TrustedModulus");
                var e = UpdaterLogic.ExtractConstant(text, "TrustedExponent");
                if (!UpdaterIntegrity.IsStamped(m, e)) { return new UpdaterIntegrity.Result("unsigned", ""); }

                var result = UpdaterIntegrity.Check(bytes, m, e);
                if (result.State == "valid")
                {
                    modulus = m;
                    exponent = e;
                }
                return result;
            }
            catch (Exception)
            {
                return new UpdaterIntegrity.Result("error", "");
            }
        }

        internal string ReadInstalledVersion()
        {
            try
            {
                var path = Path.Combine(PluginDirectory, MainScriptFileName);
                return File.Exists(path) ? UpdaterLogic.ReadInfoVersion(File.ReadAllText(path)) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private sealed class Marker
        {
            public string Version;
            public DateTime Utc;
        }

        private Marker ReadMarker()
        {
            try
            {
                var path = Path.Combine(DataDirectory, LoadedMarkerFileName);
                if (!File.Exists(path)) { return null; }

                var marker = new Marker { Utc = DateTime.MinValue };
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("version=", StringComparison.Ordinal)) { marker.Version = line.Substring(8); }
                    else if (line.StartsWith("utc=", StringComparison.Ordinal))
                    {
                        DateTime parsed;
                        if (DateTime.TryParse(line.Substring(4), null, System.Globalization.DateTimeStyles.RoundtripKind, out parsed))
                        {
                            marker.Utc = parsed.ToUniversalTime();
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

        // ---- state on disk (so a reload of this plugin mid-update does not lose track of it) ---------------------

        private void SaveState()
        {
            try
            {
                if (DataDirectory == null) { return; }
                Directory.CreateDirectory(DataDirectory);
                File.WriteAllText(
                    Path.Combine(DataDirectory, StatusFileName),
                    "phase=" + State.Phase + "\ntarget=" + State.TargetVersion + "\nreason=" + State.Reason.Replace('\n', ' ')
                    + "\nstarted=" + State.StartedUtc.ToString("o") + "\nswapped=" + State.SwapUtc.ToString("o") + "\n");
            }
            catch (Exception)
            {
            }
        }

        private UpdateState LoadState()
        {
            var state = new UpdateState();
            try
            {
                var path = DataDirectory == null ? null : Path.Combine(DataDirectory, StatusFileName);
                if (path == null || !File.Exists(path)) { return state; }

                foreach (var raw in File.ReadAllLines(path))
                {
                    var equals = raw.IndexOf('=');
                    if (equals <= 0) { continue; }
                    var key = raw.Substring(0, equals);
                    var value = raw.Substring(equals + 1);
                    DateTime when;
                    switch (key)
                    {
                        case "phase": state.Phase = value; break;
                        case "target": state.TargetVersion = value; break;
                        case "reason": state.Reason = value; break;
                        case "started":
                            if (DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out when)) { state.StartedUtc = when.ToUniversalTime(); }
                            break;
                        case "swapped":
                            if (DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out when)) { state.SwapUtc = when.ToUniversalTime(); }
                            break;
                    }
                }
            }
            catch (Exception)
            {
                return new UpdateState();
            }
            return state;
        }

        private string StatusJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"phase\":").Append(UpdaterJson.Quote(State.Phase));
            sb.Append(",\"targetVersion\":").Append(UpdaterJson.Quote(State.TargetVersion));
            sb.Append(",\"reason\":").Append(UpdaterJson.Quote(State.Reason));
            sb.Append(",\"installedVersion\":").Append(UpdaterJson.Quote(ReadInstalledVersion() ?? ""));
            sb.Append(",\"updaterVersion\":").Append(UpdaterJson.Quote(Version.ToString()));
            sb.Append(",\"signing\":").Append(Integrity.ToJson());
            string ignoredModulus;
            string ignoredExponent;
            sb.Append(",\"trust\":").Append(ReadInstalledTrust(out ignoredModulus, out ignoredExponent).ToJson());
            sb.Append('}');
            return sb.ToString();
        }

        // ---- ticking and locating --------------------------------------------------------------------------

        private void StartTicking()
        {
            if (_ticker != null || timer == null) { return; }
            _ticker = timer.Every(1f, Tick);
        }

        private void StopTicking()
        {
            if (_ticker == null) { return; }
            _ticker.Destroy();
            _ticker = null;
        }

        private static string LocatePluginDirectory()
        {
            foreach (var candidate in new[] { "carbon/plugins", "oxide/plugins" })
            {
                var dir = Path.Combine(Environment.CurrentDirectory, candidate);
                if (Directory.Exists(dir)) { return dir; }
            }
            return null;
        }

        private static string LocateDataDirectory()
        {
            foreach (var candidate in new[] { "carbon/data", "oxide/data" })
            {
                var dir = Path.Combine(Environment.CurrentDirectory, candidate);
                if (Directory.Exists(dir)) { return Path.Combine(dir, "RustArchon"); }
            }
            return null;
        }

        private static DateTime DefaultUtcNow()
        {
            return DateTime.UtcNow;
        }

        // On a worker thread. HttpWebRequest rather than HttpClient: it is the long-standing choice on the game
        // server's Mono runtime. Redirects are refused (a token URL should answer directly, and following one could
        // send the token elsewhere), and the body is capped so a hostile server cannot make it read without limit.
        internal static byte[] DefaultDownload(string url, string token)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = DownloadTimeoutSeconds * 1000;
            request.ReadWriteTimeout = DownloadTimeoutSeconds * 1000;
            request.AllowAutoRedirect = false;
            request.UserAgent = "RustArchonUpdater";
            if (token != null)
            {
                // A header, not part of the address: nothing between here and the Panel logs headers by default, but almost
                // everything logs URLs. Redirects are refused above, so this is only ever sent to the address that was given.
                request.Headers[TokenHeaderName] = token;
            }

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
    }

    // Small pure helpers, split out so they can be tested without a running server.
    internal static class UpdaterLogic
    {
        private static readonly Regex VersionText = new Regex(@"^\d+\.\d+\.\d+$");

        // [Info("RustArchon", "<author>", "x.y.z")] - the MAIN plugin's attribute. The exact quoted title means this
        // updater's own [Info("RustArchonUpdater", ...)] can never be mistaken for it.
        private static readonly Regex InfoVersion = new Regex("\\[Info\\(\"RustArchon\"\\s*,\\s*\"[^\"]*\"\\s*,\\s*\"(\\d+\\.\\d+\\.\\d+)\"\\)\\]");

        // The token as the Panel mints it: URL-safe base64 (letters, digits, - and _). Anything else is refused before it can
        // reach a header, which also rules out line breaks and other header injection.
        public static bool IsToken(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > RustArchonUpdater.MaxTokenLength) { return false; }
            foreach (var c in text)
            {
                var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok) { return false; }
            }
            return true;
        }

        public static bool IsVersion(string text)
        {
            return text != null && VersionText.IsMatch(text);
        }

        public static string ReadInfoVersion(string scriptText)
        {
            if (scriptText == null) { return null; }
            var match = InfoVersion.Match(scriptText);
            return match.Success ? match.Groups[1].Value : null;
        }

        // Numeric, part by part: 0.10.0 is newer than 0.9.0, which a plain string comparison would get wrong.
        public static int CompareVersions(string a, string b)
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

        public static string ExtractConstant(string scriptText, string name)
        {
            var match = Regex.Match(scriptText ?? "", "const string " + name + " = \"([^\"]*)\";");
            return match.Success ? match.Groups[1].Value : null;
        }
    }

    // The same check as ArchonIntegrity in RustArchon.cs, repeated because each plugin is a single file that cannot
    // share code with the other. The two are kept identical by a test that runs both over the same files.
    internal static class UpdaterIntegrity
    {
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
                return "{\"state\":" + UpdaterJson.Quote(State) + ",\"keyFingerprint\":" + UpdaterJson.Quote(KeyFingerprint) + "}";
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

    internal static class UpdaterJson
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
                            if (c < ' ') { sb.Append("\\u").Append(((int)c).ToString("x4")); }
                            else { sb.Append(c); }
                            break;
                    }
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
