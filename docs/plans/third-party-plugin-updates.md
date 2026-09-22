# Plan: applying third-party plugin updates

Status: **draft for Scott's review, 2026-09-21. Delivery steps 1 (the three gates), 2 (validating the file), 3 (applying a `.cs` update, with backup and rollback) and 4 (zips) are all built and uncommitted. Steps 3 and 4 have never run on a real game server.**
Sections marked *Scott's rules* are his words from 2026-09-21; sections marked *Proposal* are mine and are not decided.

Step 1, as built: `Plan.OffersThirdPartyPluginUpdates` (a checkbox on the admin Plans page); `RustServer.ThirdPartyPluginUpdatesEnabled`
(default off) and `ThirdPartyPluginUpdateHoldDays` (default 7, 0 to 21); `MonthlyWipeSchedule` and `ThirdPartyPluginUpdateGate` in
`RustArchon.Api/Services`; `GET/PUT api/rustservers/{id}/third-party-update-settings`; a card on the server's Plugins tab, shown only when the
plan offers the feature. HQM and Gold (Comped) offer the feature (Scott: an HQM differentiator, and Gold gets it too): HQM through the seeder on a fresh install, and both
by name through the migration `GrantThirdPartyPluginUpdatesToHqmAndGold` on an existing database. **Nothing applies an update yet**, so opting
in currently has no effect. The largest hold is 21 days (my proposal, not
Scott's): wipes can be a little under 28 days apart, so a hold of 28 would leave some servers held for good.

RustArchon already tells a server which of its plugins are out of date (UpdateChecker notices) and, since the
download-link work, can show a direct download link found through ServerArmour's marketplace index
(`PluginDownloadLookup`). This plan covers **applying** those updates. It does not cover RustArchon's own plugin,
which is updated by `PluginAutoUpdater` and the Updater.

## Scott's rules

**Auto-apply is behind three gates.** All three must pass:

1. **Plan gate.** The organization's plan must offer the feature.
2. **Server setting gate.** The server has opted in.
3. **Time gate.** The server has a number of days before the monthly wipe in which auto-apply is off.
   The default is 7. **0 means the server takes every update right up to wipe day, even if it may break** - it is
   not "this server does not wipe monthly". The reference is the first Thursday of the month (the forced wipe);
   servers that wipe weekly do not have this compatibility problem, so nothing else is modelled.

**No redistribution.** We may not have the licence to host these files, so RustArchon does not store or serve them.
We download a file to inspect and validate it (is it something we can apply, does it compile) and keep only the
result. The RustArchon plugin on the game server downloads its own copy from the marketplace.

**Validate once, unless the file changes.** A re-download is allowed. If its SHA-256 matches the one we validated,
the checks are skipped. If it differs, the author may have replaced the file under the same version number (bad
practice, but possible), so it is validated again.

**No size cap.** If uMod, or another authorized distributor that ServerArmour returns, distributes the file, we take
it. A **compile check** is part of validation.

**File shapes.**

- A single `.cs` file is dropped into the plugins directory.
- A zip (language folders such as `en`/`ru`, plus `.cs`, images, config) needs the user to say which files go to
  which directories. Those instructions are per server and do not carry to another server, so **zips are gated behind
  user confirmation**: nothing is applied until the user has given instructions.
- When the next update contains the same files, the saved mapping is reused. The user is asked whether to save the
  mapping so future updates apply automatically. The wording must say, in effect, *"provided that future updates
  contain only the same files in the same structure"*. A saved mapping is only used when that is true; if an update
  has files the mapping does not cover, it stops and asks again.

**Backup and rollback.** Sounds good to Scott: the previous file is kept on the game server and put back if the new
one fails to load.

## Proposal: how it fits together

### Settings

| Where | Setting |
|---|---|
| `Plan` | a flag for automatic third-party plugin updates (beside `HasRoles`, `RetentionHistory`), editable on the admin Plans page |
| `RustServer` | opt-in (default off), and days before the monthly wipe (default 7, 0 = no pause) |
| Platform Settings | none new; the existing `PluginDownloadLookupEnabled` switch continues to govern lookups |

### Time gate

Auto-apply is paused from *(first Thursday of the month, minus N days)* until the wipe has happened. With N = 0 the
pause is empty. After the wipe the held updates are applied on the next pass. The wipe moment is **the first Thursday of every month at 19:00 London time** (Scott, 2026-09-21): 18:00 UTC during
British Summer Time and 19:00 UTC otherwise. It follows UK daylight saving, not US. It is computed with the
`Europe/London` time zone and kept as one constant in one place.

### Step 2, as built

`PluginFileValidationJob` (every 10 minutes, at most 10 files a pass, 2 s apart; Platform Setting `PluginFileValidationEnabled`, on by default)
downloads the file behind each found address **that somebody is waiting on** - a plugin with an update notice on a server that opted in, on a plan
that offers the feature - and records on the existing `PluginDownloadLookup` row: SHA-256, kind (`cs`, `zip`, `other`), size, the plugin's `[Info]`
name, author and version, a zip's file list, and whether it can be applied (`Valid`, `Invalid`, `NeedsInstructions`, `Failed`) and why. **The file is
never kept.** A failed download backs off (15 minutes growing to 6) and honours a host's Retry-After. `RecheckAsync` (for step 3) downloads again:
the same hash skips the checks, a different one is looked at afresh and recorded (`PreviousFileSha256`, `FileHashChanges`).

Found by trying the three real uMod files (BlueprintShare, MonumentAddons, RaidableBases), which all come out `Valid`:
- **The compile check is syntax-level, not a real compile.** A real one needs the game's own assemblies, which the Api does not and should not hold.
  It catches an HTML page saved in place of the file, a truncated or mangled file, and a wrong plugin; a call to something Rust removed is found by the
  server's own compiler when the file is applied, and the `.bak` is put back.
- **It reads the file as the newest C#, not 7.3.** MonumentAddons and RaidableBases use C# 9 syntax and run on Carbon; a 7.3 limit rejected both.
  (The RustArchon plugin's own file, and release uploads, stay on 7.3.)
- **Not clean UTF-8 is fine.** BlueprintShare has a stray Windows-1252 dash; only a NUL byte, mostly control characters or mostly undecodable bytes
  mean "not text". UTF-16 with a byte order mark is read.
- **No size cap** (Scott); the download's timeout (90 s) is the only bound. The fetch follows up to five redirects by hand, https only, and only ever
  connects to public internet addresses, so a redirect cannot reach this network's own machines.
- A zip is only listed, never opened further; an entry whose path would leave its folder makes the archive `Invalid`.

### Step 3, as built

**In the RustArchon plugin** (`RustArchon.cs`, C# 7.3, capability `thirdparty-update`): `archon.thirdparty.update <class> <version> <sha256> <size> <url>`
and `archon.thirdparty.status`. The plugin downloads its **own** copy (https only, redirects followed by hand and every hop checked, read only up to the
size the Api recorded, so there is no arbitrary cap). It applies the file only if its SHA-256 is the one the Api checked; otherwise it writes nothing and
reports `mismatch` with the hash it actually got. It finds the installed file by the plugin's **class** (the one `.cs` in the plugins folder that declares
it; never the two RustArchon files, never a subfolder), so it only ever *updates* a plugin that is present. It keeps `<file>.cs.bak`, swaps the file in, and
confirms by asking the framework (`plugins.Find`) for a **new instance** at the new version; if that does not happen within 90 seconds it puts the backup
back. State survives the RustArchon plugin reloading. Like the Updater, it refuses unless the plugin verifies under its own key.

**In the Api:** `ThirdPartyPluginUpdateService` (starts an update: plan, opt-in, hold window, plugin signed by a key this Panel knows, capability, plugin
installed and outdated, a checked `cs` file, nothing else in progress; settles outcomes by asking the plugin for its status; on `mismatch` it checks the
file again), `ThirdPartyPluginAutoUpdater` (a pass every 5 minutes, at most 5 starts, one per server at a time), table `ThirdPartyPluginUpdate` (migration
`AddThirdPartyPluginUpdates`), Platform Setting `ThirdPartyPluginAutoUpdatesEnabled` (on by default; stops every new automatic start, and updates already
under way are still followed to their outcome), `GET api/rustservers/{id}/third-party-updates`, `POST .../apply` and `POST .../recheck`.

**In the Panel:** on each outdated plugin's row, an "Update now" button (or "Try again" after a failure), the state in words, "Check again" (downloads the
file once more, regardless of state - never blocked by another update being busy on that server, since it only touches the platform's shared record of
the file), and for a changed file a "Review and apply" step first. A person's click carries the hash they were shown, so a file that changed meanwhile is
refused.

**My proposals, not Scott's rules:** a person can apply an update by hand whether or not the server opted in and inside the pre-wipe window (the plan must
still offer the feature); a changed file gets a second confirmation before applying; the site-wide switch above; an update that did not work out is not
started again automatically for that version, but a person can retry it; the plugin's own gate that it verifies under its own key.

**Corrected 2026-09-22 (Scott: the "changed" wording and dead end).** Two things were wrong, both now fixed:
1. The Panel's copy asserted a cause ("the author changed this file without changing its version") the platform cannot actually prove - only that the
   Api's check and the plugin's download produced different hashes. A CDN edge, a header/encoding difference between how the Api and the plugin each
   fetch the file, or plain timing between the two downloads can produce the same mismatch with nobody having changed anything. The copy now states the
   observation, not a cause.
2. Applying a `changed` offer used to require the platform's recorded hash to have moved from what was already sent - if a recheck (automatic or manual)
   came back to the *same* hash, there was no way to apply at all ("it says check it before applying, but there's no way to apply it after checking").
   `CanApply` for a `changed` offer is now the same as every other retryable state (capable, not busy, zip-capable if a zip) - a same hash is stated as a
   fact in `Detail`, never a block. "Check again" (`IThirdPartyPluginUpdateService.RecheckFileAsync`, throttled 20s per file - `PluginFileRecheckThrottle`)
   lets a person force a fresh download on demand rather than waiting for a mismatch or the periodic validation job to trigger one.

**Added 2026-09-22 (Scott: exclude a plugin, e.g. Raidable Bases).** An UpdateChecker notice names the plugin the marketplace listing describes -
which is not always what is actually installed. Scott's example: uMod's "Raidable Bases" listing is the free demo of a build he bought directly from
its author (Chaos, off uMod), so the notice's "latest version" is never a newer version of what he runs; nothing about the gates above catches this,
since it is not about pace or timing, it is that the notice does not apply here at all. A person now excludes a plugin **per server** (table
`PluginUpdateExclusion`, migration `AddPluginUpdateExclusions`, unique on server + normalized plugin name, an optional note kept and shown back) -
blocked from both automatic *and* by-hand applies once excluded, since a by-hand apply would be just as wrong: it would overwrite the different,
paid build with the free one the notice describes. `IThirdPartyPluginUpdateService.ExcludeAsync`/`IncludeAsync`;
`POST api/rustservers/{id}/third-party-updates/exclude` and `.../include`; a new offer state `excluded`. In the Panel: "Don't update this" opens an
optional reason field next to any outdated plugin not mid-apply; an excluded plugin shows why instead of Apply/Check again, with "Allow updates
again" to lift it.

### Step 4 (zips), as built

Scott's decisions, 2026-09-21: a zip is applied by **folder rules** a person gives (a source folder goes to a destination folder, keeping the structure
below it; a folder or a single file can be skipped; a file rule beats a folder rule and the deepest folder rule wins); the rules are saved and sent to the
RustArchon plugin; a saved mapping keeps working when a new update adds files **inside a mapped folder**, and stops for a new top-level folder or file;
a mapped file is **installed** (an existing file is replaced, and backed up) unless the person ticks "keep the file already there"; destinations are
Plugins, Config, Data and Lang (his example's "images" was a slip: those go to Data); DLLs and other program files are never installed from an archive
(a plugin's extension, such as the Discord one Rustcord needs, is a separate item installed by hand with a restart).

- **Shared** `ZipMapping` (`RustArchon.Shared/PluginZips`): the one algorithm - `Resolve` (what happens to each file, and every problem: unassigned, collision
  ignoring case, program file, plugin source outside the plugins folder, unsafe path, bad rule), `Encode`/`Decode` for the console command. The plugin
  has its own C# 7.3 copy, held to the same answers by parity tests.
- **Checking the archive once** (`PluginFileInspector`): each file's path and declared size, and every `.cs` in it read once (which is the plugin, its
  version, does it read as C#), kept on the lookup row (`ZipEntries`, `ZipSourceFindings`). An archive with no plugin file for the plugin, or an older one,
  is `Invalid`; program files are only listed, so they can be skipped.
- **Applying** (`ThirdPartyPluginUpdateService`): rules from the request, else saved ones (an automatic start needs saved rules that have already produced a
  working update - `Trusted`); `PlanZip` checks them against the archive as recorded, with no second download, then sends
  `archon.thirdparty.zip <class> <version> <sha256> <size> <installBytes> <rules> <url>` (capability `thirdparty-zip`, advertised only when the server's
  runtime can read zips; the plugin loads the zip reader by name at run time so a server without it costs only the capability). Saved rules are tried
  against each new archive: covered means every file is installed or skipped.
- **Saving** (`PluginZipMapping`): kept when the server accepts the request, trusted only if that update comes up loaded.
- **The page** (`ZipMappingEditor`): the archive as folders, a destination or skip for each folder and file, a live preview from the same rules,
  the consent wording, Apply only for a set that would work.

**Size bounds (added after Scott asked whether a lying zip header could exhaust memory, as the map upload once could):** the Api's download was read
into memory without a bound; it is now capped (Platform Setting `PluginFileMaxMegabytes`, default 100, never above `ZipMapping.MaxArchiveBytes` = 128 MB,
which the plugin also enforces itself) by a `Content-Length` check and by a counter on the body, since the header may be false or missing. An archive is
refused before it is opened if its central directory - counted by walking the records, not from the count in its own header - lists more than 2000 files.
Source files inside an archive are read through a bound (8 MB each, 64 MB per archive), never by declared size. A mapping that would install more than
`MaxInstallBytes` (512 MB) is refused. The plugin bounds its own download by the size it was told (itself capped), streams extraction to disk with a fixed
buffer and counts bytes as they are produced. This departs from Scott's earlier "no size cap": the bounds are memory safety, generous and configurable, and
he should confirm them.

### Validation (Api, once per plugin version)

The download-link job already keeps one global row per marketplace, plugin and version. Validation extends that row:

- fetch the file (no size cap), compute the SHA-256, keep the hash and the result, **never the file**;
- `.cs`: the plugin's `[Info]` name and version match the notice, and it compiles against the real game DLLs
  (the same check used for our own plugin);
- zip: recorded as "needs instructions", with the entry list, so a saved mapping can be compared to it later;
- a failure is recorded with its reason and shown to the user; it is not retried until a newer version appears.

### Applying (the plugin on the game server)

The Api tells the plugin the download URL and the validated SHA-256. The plugin downloads its own copy and hashes it.

- **Hash matches:** apply. For `.cs`, copy `Plugin.cs` to `Plugin.cs.bak`, swap in the new file, and if the plugin
  fails to compile or load, restore the `.bak` and report the failure. This is the pattern `RustArchonUpdater`
  already uses. Only one backup per plugin is kept. For a zip, the backup is a copy of every file the update
  overwrites, and rollback restores them together.
- **Hash differs:** the plugin does not apply and reports the mismatch. The Api re-downloads and validates again
  (Scott's rule above) and stores the new hash, keeping the earlier one in the row's history.
  A server that has not yet applied that version is **asked first** rather than updated automatically, because the
  author changed code without changing the version (confirmed by Scott 2026-09-21). Servers that already applied it
  are left as they are.

### Zips (later step)

A zip update is never applied by the auto path until the user has given instructions for that server. The user picks
entries and destination directories, then is asked whether to save the mapping, with the wording above. Saved mappings
belong to one server and one plugin.

## Proposal: delivery order

1. Plan flag, server opt-in, days-before-wipe setting and the time gate, with tests.
2. Validation: hash, name/version check, compile check; the re-download and hash-compare path.
3. `.cs` apply in the plugin, with `.bak` and rollback. **Built.**
4. Zip instructions and saved mappings. **Built** (never run on a real server).

Each step ships with its tests. Nothing is committed or opened as a PR until Scott has tested.

## Open questions

- **Zips on a real server.** Whether Carbon's plugin compiler lets a plugin use `System.IO.Compression` is unknown (the assembly is in the server's
  `Managed` folder, but that says nothing about what the compiler references). The plugin reads zips only through reflection and advertises
  `thirdparty-zip` only if the reader loads, so the answer decides whether zips work on a server and can never stop the plugin compiling. It is found out by
  running a build of the plugin on a real server, which needs Scott's go-ahead.
- **The size bounds** (see step 4 above) depart from Scott's "no size cap" and need his confirmation.
- Step 3 has never run against a real Carbon server: TLS to the marketplace from Mono, that `plugins.Find` sees a plugin Carbon has just reloaded, and the
  reload timing (90 seconds is my guess) are unproven. Plugin version is still 0.9.0; a release that carries this needs a higher one, or servers already on
  0.9.0 never get the capability.
