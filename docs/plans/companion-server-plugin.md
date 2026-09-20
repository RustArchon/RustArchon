# Plan: the optional RustArchon server plugin (Oxide/Carbon)

Status: agreed with Scott 2026-09-19. Design and feasibility spike done; **nothing is built yet.**

An optional plugin that runs on the game server and supplies what vanilla WebRCON cannot: player positions,
tool-cupboard (TC) locations, structured events, the map image, and plugin installation/updates. Servers
without it keep working; the Panel says what needs it.

The reasoning for the architectural choices is in **[ADR-0004](../adr/0004-companion-plugin-rcon-pull-dormant-hooks-signed-updates.md)**.
This plan says what to build, in what order, and how to check it. It also covers RustArchon's side of the F7
report path; see "Boundary with the F7 work".

## Decisions (confirmed by Scott)

- **Impact is the top constraint.** The plugin does nothing between commands; hooks are dormant until asked.
- **Optional and detected.** Panel features turn on only when the plugin positively reports the capability.
- **Transport: RCON pull.** HTTPS only for large blobs (the map). Discord-webhook-style outbound HTTPS is known to
  work from these servers, so HTTPS for blobs is accepted without further testing.
- **Vanilla only.** No rustmaps or other external dependencies.
- **New repo `RustArchon.Plugin`**, added as a submodule; the Api embeds the plugin source so the plugin version
  tracks the Panel version.
- **Positions and TC data are Owner-only**, delegable to roles when the subscription supports roles.
- **A combat log is a must-have feature** (Scott, 2026-09-19), so damage is always tracked. It is a first-class
  deliverable in Phase 2 with its own Panel tab, not only a detail inside session replay. See "Combat log" in
  Phase 2.
- **Positions are recorded, not just polled** (Scott, 2026-09-19): the goal includes an admin replaying a player
  session (where they went and what they did), so capture must run whether or not anyone is watching. The
  replay UI comes later; the capture design is in Phase 2. **Confirmed:**
  - **On by default, opt-out per server.** Installing the plugin is what starts recording; the Panel shows it
    clearly and offers the opt-out.
  - **Retention is per plan**, using the existing `Plan.RetentionHistory` days ("days of console/chat/player
    history this plan retains"; seeded Wood 30, Stone 60, Metal 90, HQM 265).
  - **No delays and no access tiers beyond the existing Owner / role-delegation rule.**
  - **No detail levels: everything or nothing.** One recording switch. "Everything" is defined in Phase 2.
- **Map is generated automatically after a wipe** (see Phase 3), once per wipe, with an existence check and a
  `force` override.
- **Self-update is admin-triggered from the Panel, or automatic per server.** (Off by default when this was decided; since 2026-09-20 a *new* server has it on -
  see the ADR-0004 amendment - and an existing one keeps what it had.) Signing: RSA-2048/SHA-256/PKCS#1 v1.5,
  per-deployment key embedded in the served script, private key a Secret Platform Setting, signing UI
  platform-admin-only and audit-logged, key rotation via a bridge release. Production servers only trust our key.
- **`RconEvent` growth:** leave frame persistence as is and measure before reacting.
- **HTTP tests skipped.** `OnPlayerInput` cost accepted as unmeasured for now.

## What the spike established

Run against the Sheltered Gaming test server ("Rusty Amigos"): Carbon 2.0.259.0, Linux, Mono
(`clr=4.0.30319`), Rust build 2633, 67 plugins. The spike plugins were throwaway; their source is not kept.

| Question | Result |
|----------|--------|
| Do the risky namespaces compile on Carbon? | Yes: `System.Security.Cryptography`, `System.IO`, `System.Net.Http`, `System.Threading`. **Oxide untested.** |
| Can a plugin verify a signature? | ECDSA and RSA-PSS **not implemented** on Mono. **RSA PKCS#1 v1.5 works:** verified a .NET 10 signature from an embedded public key in ~9 ms per 300 KB; tampering rejected. `RSA.Create()` ignores `KeySize=2048` (gives 1024), so the plugin must only *verify*, never generate. |
| Can a plugin swap its own file? | Yes. `File.Replace` (atomic) and in-place overwrite both reload in 0.3-0.4 s, one reload each, ~80-95 ms compile. |
| Can it install/remove another plugin? | Yes; shows in `c.plugins` within seconds. |
| What does a failed compile do? | **Unloads the plugin and it stays dead**, listed under failed plugins with the compiler errors. Hence the Updater/main split. |
| Do dormant hooks stay dormant? | Yes: 0 calls over a 146 s baseline, across a world save. |
| Hook cost | Entity hook: ~3.7 calls/s idle, ~34 ns in our own code. Carbon's own accounting: 1 ms total over 773 fires (<= ~1.3 us/fire including dispatch), comparable to or below existing plugins (~0.5-1.8 us/fire). |
| RCON reply size, through the real Panel path | 64 KiB, 512 KiB, 2 MiB, 8 MiB single-line replies all intact (2 MiB in 0.3 s, 8 MiB in 1.2 s). Replies are **not** echoed into the console stream. RustAdmin, not the transport, was the earlier problem. 16/32 MiB not tried (RabbitMQ 4's default message cap is 16 MiB). |
| `world.rendermap` | Only server-side renderer. **Blocks the game thread ~53 s mid-game, 47.6 s at boot.** Output `map_<size>_<seed>.png` in the server root (24 MB at size 4500). Old wipes' PNGs accumulate. |
| Render at boot? | Works: `OnServerInitialized` fires, `world.rendermap` runs, the Worker's RCON connection stayed CONNECTED. |
| RCON restart | `server.save` then `restart 30 <reason>` works; **the server did not relaunch itself.** RCON answers nothing until boot completes; boot also spends ~100 s building navmeshes. |
| Frame persistence | The Worker stores every frame's **full text** in `RconEvent`, including background polls (an 8 MiB reply was stored whole; the table was 824 kB only because Postgres compressed the repetitive test payload). |
| UpdateChecker | v4.6.1 prints a report every scan, including `[NotFound]` for plugins on no marketplace; our plugin will be one, and `OnUpdateCheckerUpdateFound` will not fire for it. |

**Not verified:** anything on Oxide; `OnPlayerInput` cost; the cost of a cold-start scan to build the TC index;
whether `HttpClient` uploads work (skipped by decision); whether uMod accepts self-updating plugins (only matters
if we ever list there).

## Architecture

```
Game server (Carbon; Oxide best-effort)
  RustArchonUpdater.cs   tiny, stable; Carbon/BCL only; swaps + verifies + rolls back the main plugin
  RustArchon.cs          main plugin; dormant until the handshake enables capabilities
        ^  archon.* commands over RCON (pull)         |  HTTPS POST of the map PNG only
        |                                              v
   Worker  --- MassTransit --->  Api (persist)  <---  Panel ingest door (single-use token)
                                   ^
                                Panel reads via Api endpoints
```

The Worker owns RCON. The Panel never sends `archon.*` commands and never parses their output (Worker -> Api ->
Panel, per the standing rule). The Panel is the only public HTTP surface; the Api is not reachable from the game
server.

### Protocol

- Commands are `archon.<area>.<verb>`, **RCON-only** (ignored when `arg.Connection != null`, i.e. from in-game
  F1). Replies are one JSON envelope: `{"v":1,"ok":true,"data":{...}}` or `{"v":1,"ok":false,"err":"..."}`.
- `archon.hello` returns `{protocolVersion, build, updaterVersion, framework, capabilities[]}`.
- Lists are paginated (`offset`, `count`); target replies <= ~256 KiB even though 8 MiB works.
- Events: `archon.events <sinceSeq> [max]` returns items with `seq`, plus `lost: true` when the cursor has
  fallen off the bounded buffer, so the Worker knows to resync rather than assume continuity.
- The Panel/Worker enforce a minimum protocol version and show "plugin out of date" rather than misparsing.

### Update model

- The Api builds the served script from the embedded plugin source: **stamps the deployment's public key** and
  appends `// RUSTARCHON-SIG-V1: <base64 signature>`. The signature covers the exact bytes above that line.
- **First install is manual:** the admin downloads the script from the Panel and uploads it; that file is the
  trust root.
- **Update:** admin clicks Update -> Api mints a single-use, short-expiry token bound to `(serverId, purpose)` ->
  Worker sends `archon.update <version> <token-url>` -> the Updater downloads to `<name>.cs.new`, verifies the
  signature against the key embedded in the **currently running** main plugin, requires `version > current`
  (no silent downgrade), backs up `<name>.cs` to `.bak`, swaps with `File.Replace`, and waits for the new main
  plugin to write a `loaded.<version>` marker; if it does not appear in time it restores `.bak`. (The marker
  uses only `System.IO`, which the spike proved works.)
- **The Updater is never updated by itself**, because a failed Updater has no one to recover it. From plugin 0.9.0 the **main plugin**
  installs and updates the Updater and puts the old one back if the new one does not come up (see "Updater self-update" below), so there is no
  hand maintenance; the very first install of the main plugin is still by hand. (Earlier text: re-download from the Panel.) The handshake reports its version and the Panel warns when it is old.
- **Rotation:** a bridge release signed with the old key that embeds the new key. "Regenerate key" without that
  flow would strand every installed plugin, so it is gated behind the flow.
- RCON frames carrying token URLs are already flagged so only the site owner sees them; tokens are still
  single-use and short-lived.

## Phases

Each phase ships with its tests (see "Tests"). No PR is opened until Scott has tested and explicitly asks.

### Phase 1 - Foundation: repo, handshake, gating, signed download

**Build status (2026-09-19):** in progress on branch `feature/companion-plugin-foundation` (not committed).
Built and tested: the plugin (`archon.hello`, `archon.config`, C# 7.3 compile check), Worker handshake poll,
`ServerPluginHandshakeCaptured`, Api storage/reconcile/endpoints and migration, Panel plugin card with the two
switches. **Live smoke test passed (Carbon, Rusty Amigos, 2026-09-19):** the plugin loaded, `archon.hello` and
`archon.config` (get, set, and the unknown-key, bad-value and usage errors) behaved exactly as unit-tested, the
handshake travelled plugin -> Worker -> RabbitMQ -> Api -> database -> Panel card, a Panel toggle reached the
plugin as a non-interactive `archon.config set`, and **deliberately induced drift (a switch changed over RCON)
was corrected automatically at the next handshake.** Both migrations applied and every existing server
backfilled to on. The first load found one bug the unit tests could not: on the server `ConsoleSystem.Arg.Args` is
a `StringView[]`, not a `string[]`, so the plugin now uses `HasArgs`/`GetString`, and the test stub mirrors the
real type with a guard test. Observed: the first handshake arrived ~4 minutes after the plugin loaded (it rides
the 5-minute plugin-list poll); acceptable, and the card says so, but a shorter first poll is a possible tweak.

**Signing key, stamping and signed download: built (2026-09-19).** The Api generates the deployment's RSA-2048 key
on first download (a Secret Platform Setting, `PluginSigningKey`, written with an atomic set-if-empty so two
instances cannot mint two keys, never regenerated if the stored one is unreadable), stamps the public key into the
embedded plugin source, signs it, and serves it from `GET /api/rustservers/plugin/download`. The Panel's card offers
"Download plugin" and shows whether the installed file is signed by this Panel. The plugin checks its own file at
load and reports `signing` in `archon.hello`. Verified before any server saw it: the real Api output verifies under
the plugin's real `ArchonIntegrity` (contract tests), and the file the browser's Download button delivered is
**byte-identical** (SHA-256) to the Api's own output. Two bugs only the real stack could show, both fixed with
regression tests: `PlatformSetting.Value` was `varchar(1000)` and an encrypted key is ~2.3 KB (widened to 8000), and
`SetValueIfEmptyAsync`'s `ExecuteUpdate` bypassed the EF change tracker so a same-context re-read returned the stale
empty value (the repository now reloads tracked copies).

**Not yet built in Phase 1 (built since - see "Plugin admin, defaults and failed plugins" below):** the signing UI for self-hosters' custom builds, key
rotation (the bridge-release flow), audit logging of signing, and the failed-plugins extension. Still not built: the `c.plugins` hook-time columns. The `RustArchon.Plugin` repo
exists locally only; nothing is on GitHub until Scott confirms visibility and license.

- Create `RustArchon.Plugin` (Scott confirms visibility/license at creation), submodule it, embed the sources in
  the Api build.
- Plugin: `archon.hello` and `archon.config`; dormant; C# 7.3 subset. The Recording and Combat log switches
  (Phase 2 detail) exist from Phase 1 so the whole Panel -> Worker -> plugin settings path can be tested before any
  hook is built.
- **Signing:** generate the deployment keypair on first use (RSA-2048; the Api generates, the plugin never
  does). Private key = new Secret Platform Setting with its own `IApiKeyProtector` purpose and a
  `SecretPurposeFor` case; public key = plain setting. Signing UI platform-admin-only, audit-logged; rotation via
  the bridge flow. Stamping + signed download endpoint.
- **Worker:** poll `archon.hello`, but **only when the plugin list positively shows RustArchon** (the existing
  `c.plugins`/`o.plugins` poll already provides that). Publish `ServerPluginHandshakeCaptured`.
- **Api/Shared/Panel:** persist plugin status (version, protocol, framework, capabilities, last seen); server
  detail shows a plugin card and, for every plugin-only feature, a "requires the RustArchon server plugin"
  state with an install link. Unknown or absent capability = feature off.
- **Works without the plugin (extend the existing Carbon poll parser):** capture per-plugin hook time, fires,
  memory and compile time, and the **failed plugins list with compiler errors**, and show them in the Plugins
  tab (which plugins cost the server most; which ones broke on the latest Rust update).
- Unblocks the F7 wizard's plugin step.

### Phase 2 - Positions, TCs, events

- **Position recording and live view (one pipeline).** Demand-driven polling was rejected: it records nothing when
  nobody is watching, so there is nothing to replay. Instead:
  - **The plugin samples** active players into a **bounded in-memory buffer** on a low-frequency timer (default
    ~5 s), only when the `positions.record` capability is enabled. This is the one place the plugin does work
    between commands; it is opt-in and O(active players). Estimated at microseconds per player per sample,
    **unmeasured: measure at kickoff** and expose the sampler's own time in `archon.hello`/status, since Carbon's
    hook-time column may not cover timer callbacks.
  - **Compact samples:** timestamp, player id, position quantized to ~0.25 m, yaw, and a state flag (mounted,
    swimming, wounded, ...). **Delta-skip stationary players** (a moving player produces a sample, an idle one
    does not); record the logout position once at disconnect (that is where the sleeper is). Sleepers are not
    sampled. Linear interpolation between 5 s samples is enough to show *where they went*; combat-grade detail
    (1 s sampling while in combat) is a later refinement, not v1.
  - **The Worker drains by cursor**, `archon.replay.drain <cursor> <maxBytes>`, returning a packed chunk plus a
    `lost` flag if the buffer wrapped while the Worker was away (the replay shows a gap instead of inventing
    data). Sizing: 100 players x 5 s x 15 min is ~18k samples, roughly 200 KB of buffer, so a Worker outage of
    15 minutes loses nothing.
  - **Drain cadence is decoupled from recording:** slow (30-60 s, few large frames) for archival, faster (~5 s)
    only while a Panel viewer has the live map open. Because the plugin records regardless, drain speed never
    costs data. "Live" is just the newest samples of the same stream.
  - **Storage:** not a row per sample (~10^6/day on a busy server). One row per `(server, player, ~10 min)` chunk
    holding the packed samples, indexed by server, player and time. A retention job hard-deletes chunks older than
    the tenant's plan `RetentionHistory` days. **No such pruning job exists today** (nothing in the Api enforces
    `RetentionHistory`; `RconEvent`'s own comment calls its pruning "future"), so this phase builds one, written
    so it can also cover `RconEvent`. Rough volume, **an estimate not a measurement:** 100 concurrent players, half moving, ~10 MB/day per
    server before compression. Written by a purpose-built Api consumer, not by `RconEvent`.
  - **`RconEvent` note:** these drains are exactly the traffic the "leave it and measure" decision (ADR-0004)
    was made for. Fewer, larger frames keep the row count down; the Phase 2 measurement now has a concrete
    driver, so record the baseline before enabling it.
  - **Live and replay permissions:** the same rule as TCs. Owner-only, delegable to roles on plans with
    `HasRoles`. No delay for delegates. View actions audit-logged. Because delegates see live positions
    immediately, a delegated moderator who also plays could ghost; this was weighed and accepted as the
    Owner's call when delegating.
  - **Privacy:** this records player behavior tied to SteamIDs, **on by default**. Retention is bounded by plan,
    the Panel shows recording as on with an opt-out, and the terms/privacy text must say so; disclosing it to
    players is the server owner's responsibility.
- **Replay context ("what the player did"): everything or nothing.** One switch turns on the position sampler
  and all of the following; there are no user-visible levels. "Everything" means **every action a player takes
  or receives that can be captured through a cheap hook**, each tagged with server time, player and position:
  connect/disconnect/respawn; deaths and kills; **damage dealt and received whenever a player is on either side**
  (including versus NPCs, animals and entities), with weapon and hit detail; chat; buildings and deployables
  placed, upgraded and destroyed; TC (de)authorization; teleports; mount/dismount; opening containers.
  **Deliberately excluded, to confirm:** inventory and container contents and item-level movement, gather and
  crafting ticks, per-tick input, code-lock codes, and damage with no player involved (NPC versus NPC,
  environment). Those exclusions are what keep "everything" affordable; including them would multiply volume
  and hook cost by orders of magnitude (NpcSpawn alone shows ~40 million hook fires on the test server).
  - **Cost gate (measure after build; Scott, 2026-09-19):** damage tracking is required, so the gate is not "drop
    damage if it is costly"; it is "make it cheap enough". No separate spike: the damage hooks are measured on the
    built plugin, on a real server: hook fires per second and hook time from Carbon's `c.plugins`, what fraction
    of fires involve a player, and events per player-hour. Until those numbers exist, the per-server switch below
    is the safety net. If the measured cost is too high, the levers are narrower hooks, earlier exits in the hook
    body, batching, and coalescing repeated identical hits.
- **Combat log (must-have).** A filterable, per-hit record of combat involving players, on a Panel tab in Phase 2
  (it does not wait for the Phase 5 replay UI):
  - **Fields, as far as cheaply available:** server time; attacker (player, or entity/NPC type) and victim;
    weapon and ammo; damage amount and type; hit area and headshot; distance; both positions; the outcome (hit,
    wounded, killed). Confirm exactly what `HitInfo` yields on the target Rust build when the hook is written.
  - **Views:** by player (as attacker or victim), by time window, and by incident (a burst of related hits
    grouped into a fight). Also linked from an F7 report and from session replay, since combat evidence is the
    main reason an admin opens either.
  - **Authoritative kills:** when this capability is present it replaces the text-scraped kill feed
    (`KillFeedTextParser`), which is heuristic and cannot see player-vs-NPC/animal at all. The Panel keeps the
    scraper only for servers without the plugin, and says which source a row came from.
  - **Hooks:** something like `OnEntityTakeDamage` (fires for every damage to any combat entity, including
    NPC-vs-NPC and decay, so the body must exit immediately unless a player is on either side) plus the
    death/wound hooks for outcomes. The exact set is chosen at implementation, must be validated on the current
    Rust build, and is a Rust-internals dependency, which is the class of thing that breaks on Rust updates; the
    Updater plus failed-plugin visibility is the mitigation.
  - **Volume and retention:** this is likely the largest data source, not positions. Per-hit records mean a
    long, busy PvP server could produce millions of rows a day; with plan retention up to 265 days that is a real
    storage number. **Unmeasured; measure hits per player-hour after build**, so chunks carry a **format version**
    and the layout can change once real numbers exist. Store chunked and compressed like positions, not a row per
    hit.
  - **Per-server switches, controllable from the Panel (Scott, 2026-09-19):** two settings on each server,
    **Recording** (positions and player events) and **Combat log** (damage tracking), both **on by default**. This
    supersedes the earlier "not exposed in the Panel" note. They are switches, not detail levels: "everything"
    still means everything when on. The plugin exposes `archon.config get` and `archon.config set <key> <value>`
    (keys `recording`, `combat`), persisted in the plugin's local data so they survive reloads and reboots, and
    reported back in `archon.hello`. **Desired state is reconciled, as built in Phase 1:** the saved values live on
    the server (`PluginRecordingEnabled`, `PluginCombatLogEnabled`, default on). The **Api decides and the Worker
    executes**: when a handshake arrives, and immediately when the Panel saves a change, the Api compares saved
    against reported and sends `archon.config set` through the Worker's existing `SendRconCommand` pathway
    (non-interactive), so a Panel toggle, a plugin reinstall and a reboot all converge and the Panel never issues
    commands itself. Sending is skipped unless the plugin reported the `config` capability (fail closed). The
    switches are saved by their own endpoint, `PUT /api/rustservers/{id}/plugin-settings`, **not** by the
    full-record server update: that PUT does not send them, and carrying them there would let a rename silently
    reset a switch someone turned off. The plugin's own per-hook local switches remain as a support lever. Turning
    one off unsubscribes its hooks and stops its sampler immediately (Phase 2, when those hooks exist).
- **TC / bases (`archon.tcs`):** index TCs incrementally via `OnEntitySpawned`/`OnEntityKill`; on load, build the
  initial index with a **frame-sliced scan** under a per-frame time budget. **Measure the cold-start cost first**;
  the spike did not. Owner-only; view actions audit-logged. Code-lock codes are excluded.
- **Events:** ring buffer with sequence cursor. First consumers: structured kills including **player-vs-NPC/animal**
  (the case vanilla never prints); the F7 `OnPlayerReported` path is the F7 session's; `OnUpdateCheckerUpdateFound`
  handler (deduped on `(name, latestVersion)`, reported only when UpdateChecker is loaded) feeding "update
  available" on the Plugins tab.
- **Permissions:** new permissions for positions/bases, Owner by default, delegable to roles where the
  subscription includes roles. Follow the existing permission registry and subscription-feature gating; confirm
  the exact mechanism at kickoff.
- **After it is live:** measure `RconEvent` growth per server per day and decide whether to change persistence
  (agreed action; threshold to be set then).

**Phase 2 status - combat log slice (2026-09-19/20): built and tested; live capture awaiting a real player.**
- Plugin 0.3.0: `OnEntityTakeDamage` / `OnEntityDeath`, subscribed only while the Combat log switch is on; the body exits
  unless a real player (SteamID range) is on either side; 20,000-event ring buffer with sequence + boot id;
  `archon.events.drain <bootId> <cursor> [max]`; `archon.stats` (hook fires vs recorded, for measuring). Compiled on the
  real Carbon on the first push and reports the `combat` capability. Updated onto Rusty Amigos through the Updater.
- Worker: drains every 30 s while the plugin reports `combat` and the switch is on; cursor in memory only (the Api
  drops repeats); logs `archon.stats` every ~5 minutes.
- Api: `PluginCombatChunk` (one gzipped chunk per batch, format-versioned, player index, gap flag), dedupe by boot and
  sequence range (safe when batches are handled out of order), `GET /api/rustservers/{id}/combat` (newest first,
  time window, per player, tenant-filtered, 404 for another organization's server), and the **first retention job**:
  chunks past the plan's `RetentionHistory` (default 30 days when unknown) are deleted every 6 hours.
- Panel: Combat tab (always shown; says what it needs when the plugin or capability is missing).
- Verified live: the plugin answers the drain with a valid envelope every 30 s, the Api and Panel run, and the tab
  loads with "No combat recorded". **Not verified live:** an actual recorded hit, which needs a real player to be hit
  or to hit something. Hook cost is unmeasured (measure at the end, as decided).
- Known: every empty drain is also stored as a background `RconEvent` (~2,900 small rows/server/day); decide with the
  rest of the `RconEvent` measurement. No GIN index on the chunk player list yet (add when volumes exist).
- Not yet built in Phase 2: the replay UI and the events stream for kills.

**Phase 2 status - TC / bases slice (2026-09-20): built and tested; live on Rusty Amigos as plugin 0.4.1.**
- Plugin 0.4.1 (`tcs` capability): index of player-owned `BuildingPrivlidge`, kept current by `OnEntitySpawned` /
  `OnEntityKill` (subscribed only while Recording is on) plus a frame-sliced initial scan of
  `BaseNetworkable.serverEntities` (batches of 2000, 2 ms budget, restarts if the world list changes under it).
  `archon.tcs [offset] [max]` returns pages `{format, ready, total, offset, next, tcs:[{i,x,y,z,o,a:[{i,n?}]}]}`; the
  authorized list is read live so an (un)authorization needs no hook. Code-lock codes are never touched.
- **The game keeps only ids** (`authorizedPlayers` is a `HashSet<ulong>`), so a name is included only for a player in the
  world right now (`BasePlayer.FindAwakeOrSleepingByID`); everyone else is an id and the Panel shows the id. Resolving
  offline names (a known-players table on the Api side) is a possible refinement.
- 0.4.0 was pushed first and **failed to compile on the real Carbon** (my test stub had the old `PlayerNameID` shape);
  the Updater rolled back to 0.3.1 as designed, and 0.4.1 followed. Lesson recorded: compile the plugin against the real
  game and Carbon DLLs (Roslyn, C# 7.3) before any live push; the stubs alone are not evidence. To do: move that check
  into the repo as an opt-in test that skips when the game files are absent.
- Worker: polls every 60 s while the plugin reports `tcs` and Recording is on; walks pages (max 50) and publishes one
  snapshot. Api: one `PluginTcSnapshot` row per server (an older capture never replaces a newer one; tenant guard),
  `GET /api/rustservers/{id}/bases` gated by the new permission `RustServer.ViewBases` (Owner holds it, delegable, not
  part of ordinary server access), every view audit-logged with the viewer's email. Panel: Bases tab (needs-plugin,
  not-supported, forbidden, recording-off and still-scanning states).
- Hook and scan cost unmeasured (measure at the end, as decided).
**Phase 2 status - positions slice (2026-09-20): built and tested; live on Rusty Amigos as plugin 0.5.0.**
- Plugin 0.5.0 (`positions` capability): while Recording is on, a 5 s timer walks `BasePlayer.activePlayerList` (real SteamIDs only)
  and records a sample (time, id, x/y/z, yaw from `transform.eulerAngles.y`) only when the player moved 0.5 m or once a minute
  as a heartbeat; the first sample carries `e:on`, the last (on `OnPlayerDisconnected`) `e:off`, and names ride on the first,
  heartbeat and last samples only. A 30,000-sample ring (same boot id + sequence + lost/reset contract as combat, now a shared
  generic `ArchonRing<T>`) is drained with `archon.positions.drain`. Off means no timer and no hook. `archon.stats` reports
  sweeps, samples and the average sweep time in microseconds, for the end-of-build cost measurement.
- Worker drains every 30 s while the capability and Recording are on (same publish-before-cursor rule). Api: `PluginPositionChunk`
  (gzipped format-1 chunks, dedupe by boot and sequence range, per-player index), `GET /api/rustservers/{id}/positions` gated by the new
  permission `RustServer.ViewPositions` (Owner, delegable, audit-logged with viewer and player), and position chunks are pruned by
  the same plan-retention job. Panel: Positions tab (who was seen in the last 10 minutes at their newest position, facing, and a
  one-hour track with total flat distance per player).
- Not built: the map view (Phase 3), replay playback, live refresh faster than a manual Refresh, and sampling finer than 5 s.
- Yaw uses the body transform because `viewAngles` is not accessible to plugins (found by the real-DLL compile check); whether it
  tracks the player's look direction is confirmed live, not assumed.

- **Site admin sees bases in every tenant (Scott, 2026-09-20).** The rule is general: a site admin (holder of
  `Platform.ManageOrganizations`) passes every permission-gated endpoint in any organization, including where they are a
  member with a lesser role. Before this, only non-members were let in (as an owner) via the cross-tenant policy, so a site
  admin who had been added as a member got 403 on the owner-only Bases tab. Implemented as `SiteAdminAuthorizationHandler`
  beside JumpStart's handler; verified live.

### Phase 3 - Map

- **Auto-render after a wipe** (Scott's decision). Mechanism proven by the spike: in `OnServerInitialized`, if
  `map_<size>_<seed>.png` is missing **and no players are online**, run `world.rendermap`; otherwise do not
  freeze anyone. Adds ~48 s to that boot; on a wipe day that is a delay before the server opens, which the
  Panel setting text must say. A per-server on/off setting is pushed to the plugin's local config by the Worker
  (`archon.config`), since the plugin cannot ask the Panel at boot.
- **Manual render** from the Panel with a players-online guard (refuse unless overridden) and `force` to
  overwrite. The Worker must expect ~1 minute of a slow/unresponsive server and must not mark it down.
- **Upload, decoupled from boot:** the plugin holds no standing credential. After a render, `archon.map.status`
  reports `{file, bytes, uploaded:false}`; when the Worker sees that, it mints a single-use token and sends
  `archon.map.upload <url>`; the plugin POSTs the raw PNG (not base64) from a worker thread. Ingest endpoint on
  the Panel, ceiling >= 100 MB; object stored in Garage keyed by server + size + seed; served through an
  authenticated Panel endpoint. Old-wipe PNGs on the game server are left alone.
- Map tab: image plus monument coordinates (once per wipe) and live positions drawn client-side.
  `HttpClient` upload is **unverified**; treat the first live upload as its test.

**Phase 3 status (2026-09-20): built and verified live on Rusty Amigos as plugin 0.7.0.**
- Plugin: `archon.map.status`, `archon.map.monuments`, `archon.map.render [overwrite] [override]`, `archon.map.upload <url> <token>`, and a
  boot/load-time auto-render (`map` switch, on by default, local to the server; **not yet a Panel setting**). The render command replies
  at once and starts the game's `world.rendermap` a second later; it refuses with players online unless `override`, over an existing
  picture unless `overwrite`, and while an upload is reading the file. Measured live: a 4500 m world renders in **48.6 s**, the
  Worker's connection stayed up throughout, and the picture is 24,113,743 bytes. Monuments come from `FindObjectsOfType<MonumentInfo>()`
  because `TerrainMeta.Path.Monuments` is not reachable from a plugin (found by the real-DLL compile check); 42 named places on the test world.
- Collecting the picture (the design Scott asked for): the Worker reports the map state every 5 minutes (`PluginMapStatusCaptured`);
  the **Api** decides whether the picture is wanted, mints a single-use token (10 minutes, bound to one server and one map row, only its
  hash stored) and sends `archon.map.upload <panel>/ingest/plugin-map <token>` through the Worker (the Api mints because it owns the
  database that checks the token; the Worker delivers). Requests are throttled to once per 10 minutes per map. **The token travels in an
  `X-RustArchon-Upload-Token` header, not the address** (Scott, 2026-09-20: addresses get logged more than headers), and the address is
  constant: the token alone names the server and map, so no server id is in the URL. The plugin streams the file from a background thread,
  refuses redirects, and only accepts a base64url token so it can never inject a header. The Panel door streams (never buffers) to the
  Api, which redeems the token before reading a byte, requires a declared length under 120 MB and a PNG signature, then stores the picture
  in Garage and records its SHA-256. Live: requested 05:50:15, stored 05:50:17. (The Updater's download carried its token in the address at
  first; from Updater 0.3.0 it travels in a header too - see "Hardening pass" below.)
- Api/Panel: `PluginMap` per server per wipe (an old wipe's picture is kept), `GET api/rustservers/{id}/map` and `.../map/image` (ETag =
  content hash, gated like reading a server), and a Map tab: canvas over the picture with named places, bases and players as separate
  layers (the two sensitive ones hidden with a note when the viewer lacks their permission).
- **The picture is not the bare world square.** It is 5500 x 5500 px for a 4500 m world: the game adds 500 m of ocean on every side (one
  pixel per metre), so the picture spans world size + 1000. Found because the first version drew places in the wrong spots; verified
  against the picture (oil rigs and the underwater lab land on deep water, 33 of 34 checkpoints match; it was 24 of 34 before).
- **Display copy (2026-09-20).** Serving the 24 MB original through the Api, the Panel and the Blazor connection made the tab take over
  30 s. The Api now keeps a display-sized copy (first a 2048 px JPEG; see the zoom note below for what it became) next to the original, made once when the
  picture arrives (or on first request for one collected before this existed), served by default with its own entity tag; the original stays
  in Garage and is available with `?full=true`. Measured: 24.1 MB -> 536 KB, and opening the tab paints in 60-100 ms. The decode is strict:
  a picture that does not fully decode gets no preview and is served from the original rather than showing half a map. This adds
  **SkiaSharp** (MIT) to the Api, with the Linux no-dependencies native assets; **not yet exercised inside the Docker image**.
- **Zoom and pan (2026-09-20).** Wheel or pinch to zoom about the pointer, drag to pan, double-click to zoom in, and buttons for in, out and
  whole map, all handled in the page's script (nothing crosses the Blazor connection while dragging). The display copy is now at the picture's
  native resolution (capped at 6144 px for the largest worlds; about 1.6 MB as lossy WebP against 24 MB as a lossless PNG - the gap is compression, not resolution;
  WebP measured about 30% smaller than JPEG on this map and keeps hard edges cleaner; older copies are recognised by their key, remade and the old one removed). The cost is browser
  memory (about 120 MB decoded), fine on a desktop and untested on small phones. Zoom is capped at 8x because the game's
  own picture is 1 m per pixel: it is sharpest at about 6x on a ~1000 px canvas. **Tiling** (a pre-cut pyramid of static 256 px tiles, cut
  once when the picture arrives and fetched only where visible) would cut the first-load size on very large worlds and extend the crisp
  range to the original's native resolution, but not beyond it; it needs signed, cacheable tile addresses because a browser cannot send
  the Panel's login token. Not built.
- Not built: a Panel "draw the map" button (manual render with the players-online guard), a per-server auto-render switch in the Panel, and
  replay playback over the map (ADR-0005).

### Phase 4 - Updater and self-update

- `RustArchonUpdater.cs`, the update flow above, the Panel Update button (with a per-server "allow updates"
  setting; off by default when built, on for a new server since 2026-09-20), `PluginUpdateAttempt` audit rows, and the "updater is out of date" warning.
- Failure paths tested end to end on the test server: bad signature, wrong key, older version, corrupt download,
  main plugin that fails to compile (must roll back).

**Status (2026-09-19): built and verified live on Rusty Amigos over the LAN.**
- Built: Updater plugin, `PluginUpdateService` (preconditions, single-use token minted last and revoked on refusal),
  Api `internal/plugin/download`, Panel public door `/ingest/plugin/{serverId}/{token}` (`PluginDownloadProxy`),
  per-server "Allow updates" switch, Update and Download Updater buttons.
- Live, happy path: 0.2.0 -> 0.2.1 through the Panel. Token redeemed on the first request; Updater logged
  "RustArchon 0.2.1 loaded; update succeeded."; the handshake then reported 0.2.1, still valid, same key.
- Live, rollback: a validly signed 0.2.2 that does not compile (dev-only build, source restored afterwards).
  Carbon logged the compile failure; after 45 s the Updater logged "Update 0.2.2 rolled-back"; Carbon recompiled the
  restored 0.2.1 and it came back with recording and combat still on.
- Not yet run live (unit and contract tested, including real HTTP): bad signature, wrong key, older version,
  corrupt download, token reuse. The `PluginUpdateAttempt` audit rows and the "updater is out of date" warning are
  not built.
- The Updater's own updates were manual by design; from plugin 0.9.0 the main plugin does them (see "Updater self-update").

### Phase 4b - Key rotation that does not strand old plugins

Problem: a plugin (or Updater) can sit unattended for a year while the key rotates more than once. Today the Updater
refuses any file that trusts a different key (`key_changed`) and is itself only updated by hand, so a rotation would
strand every installed plugin.

Decisions (Scott, 2026-09-19):
- **One active key at a time.** Only the active key is stamped into newly served files.
- **All historical keys are kept**, encrypted, as *retired* keys. A retired key's only remaining use is signing a
  **bridge**: the file a server that reports that key receives, which embeds the active key. Because the Panel signs
  with whichever key the server reports, a plugin any number of rotations behind reaches the latest in one step - no
  chain. A reported fingerprint that is not in the history is refused, as today.
- **No inferred retirement.** "No server reports this key any more" proves nothing (offline, paused, behind), so keys
  are never removed or disabled on that basis, and there is no delete. The admin's rotation action shows how many
  servers last reported each key and when - information, not a trigger.
- **Revoked** is a separate, explicit state for a compromised key: it never signs again, so a server still on it can
  only be fixed by a manual re-download (the Panel already shows "signed by a different key" and offers it). Revoking
  is platform-admin only and audit-logged. Nothing is deleted, so the history stays complete.
- **Trust stays one-way.** Announcements over RCON only tell the Panel which key to sign with (it already gets the
  fingerprint from `archon.hello`); they never grant trust. The Updater trusts only what it holds.

Build:
- Updater 0.2: anchors its trust to the key stamped in the installed main plugin (so it follows every bridge and is
  never stale), and accepts a file that embeds a different key only if its signature verifies under the key it trusts
  now. **Installed by hand once, before any rotation** - the 0.1.0 Updater refuses key changes, and today only test
  servers run it.
- Dual-signature file: the embedded (new) key signs the payload, then the old key signs that; the last line is the old
  key's, which is what an existing Updater checks. The new main plugin's self-check learns to read the nested form,
  otherwise it would report itself invalid after a bridge.
- Api: key history (active/retired/revoked, with when and by whom), signing service that picks the signing key by the
  reported fingerprint, the rotation and revoke actions with audit rows, and the "servers last seen on this key" count.
- Tests: two rotations apart, one hop; revoked refuses; unknown fingerprint refuses; the bridged plugin reports valid;
  an old Updater and the new one both accept the dual-signature file; then live on Rusty Amigos.

**Platform admin page (required, Scott 2026-09-19)** - one page, Site Admin only, everything audit-logged:
- *Signing keys*: the active key and every retired/revoked one (fingerprint, created, retired, revoked and why, and
  "servers last reported on this key: N, last seen ..." as information only). Actions: **Rotate** (new active key, the
  old one becomes retired) and **Revoke** a retired key with a reason. Revoking the active key means rotate, then
  revoke the retired one, so there is never no active key. No delete.
- *Plugin releases*: today the plugin source is embedded in the Api build, so shipping a new version means shipping the
  Api. The page adds **Upload release**: a platform admin uploads a `RustArchon.cs` or `RustArchonUpdater.cs`, it is
  validated (right `[Info]` title, a version, each key placeholder present exactly once, no signature line or
  pre-stamped key already in it, size cap), stored as a release, and only served after an explicit **Publish**. The
  Panel serves the highest published version, falling back to the embedded build. A published release can be
  **withdrawn** (never deleted) if it turns out bad. Uploading code that the Panel then signs and pushes to servers
  with full privileges is the most sensitive action here, hence admin-only, validated, audit-logged, explicit publish.

Api design notes:
- The active key stays where it is (the Secret platform setting), so nothing existing changes. Retired and revoked keys
  live in a `PluginKeyHistory` table (encrypted with the same purpose, so the stored blob moves over unchanged) with a
  `PluginKeyEvent` audit table. Rotation is one transaction with a compare-and-swap on the setting, so two admins
  rotating at once cannot both win.
- An update token records the key fingerprint the server reported when it was minted; redeeming it returns that, and
  the file is built as a bridge signed with that key. Refusals: fingerprint unknown -> `not_signed_by_this_panel`;
  revoked -> `key_revoked` (manual reinstall); Updater older than 0.2.0 and a key change needed -> `updater_too_old`.
- The generic Platform Settings page must not be able to overwrite the active key with junk: that setting becomes
  read-only there, and this page is the only way to change it.

**Live bridge test PASSED (2026-09-19, Rusty Amigos):** Updater 0.2.0 hand-installed; key rotated twice from the admin
page (5bc8... -> 86e6... -> 82b4...); a 0.2.3 release uploaded and published through the page; Update from a server still on
the first key logged `Key bridge: 5bc818e16bb82c50 -> 82b49184449c98f6`, then `Init v0.2.3 ... signature=valid
key=82b49184449c98f6` and `update succeeded`. One hop across two rotations, and the upload/publish path works live.

**Status (2026-09-19): built, unit and contract tested.**
- Plugin: Updater 0.2.0 (trust anchored to the installed plugin; accepts a bridge) and main plugin 0.2.2 (reads the
  nested signature). Live: 0.2.1 -> 0.2.2 through the Panel with the OLD Updater 0.1.0 (same key), `update succeeded`.
- Api: `PluginKeyHistory`, `PluginAdminEvent`, `PluginRelease` tables (migration `AddPluginKeyHistoryAndReleases`);
  `PluginKeyService` (rotate, revoke, list with per-key server counts, compare-and-swap); signing with a retired key and
  `BuildBridgeAsync`; update tokens remember the key the server reported; new refusals `key_revoked`, `updater_too_old`;
  `PluginReleaseService` (validated upload as draft, publish, withdraw, highest published newer than the built-in wins);
  `PluginAdminController` (Site Admin); the generic settings endpoint refuses to overwrite the signing key.
- Panel: `/Admin/Plugin` (keys, releases, audit log), retired/revoked states and an outdated-Updater prompt on the
  server's plugin card.
- Built later (see "Plugin admin, defaults and failed plugins"): the syntax check of uploads and the rotation reminder.
- Live bridge test (Rusty Amigos): install Updater 0.2.0 by hand, rotate twice, publish a newer release, update.

**Signing key export and import (2026-09-20, built and tested; not yet released).** Why: one game server can be connected to several Panels
(a developer machine, a dev site and production), but a plugin trusts exactly one Panel's key, so the others can read from it and
cannot update it. Sharing a key makes them interchangeable; it is also the only way to back a key up (a Panel keeps its keys under its own
encryption ring, which does not travel), and losing the key strands every installed plugin.
- **Export** (site admin, audit-logged): every key (active, retired, revoked, with reasons and times) sealed into one JSON file: PBKDF2-
  HMAC-SHA256 (600,000 iterations, random salt) derives an AES-256-GCM key from a passphrase (12 to 256 characters); the header, including
  the list of fingerprints, is authenticated with the ciphertext, so changing anything makes the file unreadable. A wrong passphrase and a
  damaged file are indistinguishable on purpose. Unreadable stored keys stop the export rather than being left out of a "backup".
- **Import** (site admin, audit-logged, dry run first): merges by fingerprint. Keys new here join the history (they can sign a bridge, not
  new files); a revocation in the file is applied and a key revoked here is never brought back; the active key changes only if the admin
  ticks "make the file's active key active here", through the same compare-and-swap path as a rotation (the old active key is kept; a
  retired key that is reactivated leaves the history). Every key is re-checked on the way in (real RSA private key, 2048 to 4096 bits,
  fingerprint recomputed and matched, at most one active), all or nothing. Re-encrypted under the receiving Panel's own key ring.
- **What it does not do:** it moves private keys, so it widens who can sign. Guidance: share a key only between Panels that are equally
  trusted (the developer machine and the dev site), never with production. Verified live (export, download, check, wrong passphrase); the
  cross-Panel case is covered by tests that use two different encryption key rings, not by two running Panels.

### Hardening pass (2026-09-20, unattended)

Built on branch `feature/overnight-hardening` in each repository, tested, **not yet merged**. Nothing here changes the database schema.

- **Map picture size limit (a defect found in review).** The upload door checked the byte size and the PNG signature, but the display-copy
  maker decoded whatever the header declared: a few hundred bytes claiming 60,000 x 60,000 px would ask for ~14 GB. The declared size is now
  read from the PNG header (`PngHeader`) and held to 8192 px a side before anything is stored or decoded, a signature without a readable
  header is refused, and the renderer checks the size again before it allocates. (The game's largest world, 6000 m, is 7000 px.)
- **Retention grew from chunks to everything the plan advertises.** It lives in the Api, not the Worker: the Worker has no database (data flows
  Worker -> Api), and the Api already runs the periodic jobs (`PluginDataPruneService`, every 6 hours). It now also removes, per organization
  by its plan's `RetentionHistory` days (30 when there is no usable plan): console and chat events, kill-feed events, stats snapshots, and
  **the maps of past wipes** - a server's newest map is never removed; an older one goes once neither its last sighting nor its upload is inside
  the window, the pictures (original and display copy) are deleted from Garage first and the row only if that worked, so a storage failure
  retries next pass. Expired upload and update tokens go a day after they expire. Deletes are batched (5000 rows) so the first pass over a
  table that was never pruned does not hold one huge transaction. Player sessions are not touched.
- **Empty background answers are not stored.** Every poll's reply was stored as a console row, visible only in the site admin's unfiltered view.
  A drain that reports nothing new (and nothing lost or reset), an empty list (the player list of a server nobody is on) and a blank reply to a
  *background* command are now dropped in the Worker before publishing; a person's own command is always stored, as is anything with content,
  a "lost" or "reset" flag, or an error.
- **Updater 0.3.0: the download token moves to a header.** `archon.update <version> <url> [token]`; with a third argument the Updater sends
  `X-RustArchon-Update-Token` and the address carries no credential (`GET /ingest/plugin` on the Panel, `GET internal/plugin/download` on
  the Api, which finds the server from the token). The token must be URL-safe base64 (letters, digits, `-`, `_`), which also rules out header
  injection. The Api chooses the form from the Updater version the server reports: 0.3.0 or newer gets the header form, anything older (or
  unknown) still gets the token in the address, and that door stays. A server gets the new Updater through the Updater self-update below (no
  hand install); until then updates keep working exactly as before. Checked live against the local Api: a real token redeemed through the header
  door returned the signed 0.7.0 script with `Cache-Control: no-store`, and the same token answered 404 the second time.
- **Names on the Bases list.** The game only gives the plugin the ids on a cupboard, so anyone offline showed as a number. The Api now fills
  the gap when it serves the list, from the name each player used in their latest session on that server (the Worker records every connection),
  never overriding a name the plugin supplied, and adds the owner's name to each base (the map labels use it too). Stateless: nothing is stored
  differently, so a name appears the moment a session for that player exists. A player who has never connected since RustArchon began recording
  stays an id.
- **Test fixtures fixed** that depended on the checkout: one assumed LF line endings in the embedded plugin source, and the permission matrix
  sent JSON to the multipart upload endpoint (415 before authorization).

### Plugin update notices from UpdateChecker (2026-09-20, plugin 0.8.0)

Built on the same `feature/overnight-hardening` branches; **not yet run against a real UpdateChecker event** (see "Not verified").

- **What UpdateChecker gives us.** The hook is `OnUpdateCheckerUpdateFound(string name, string currentVersion, string latestVersion, string url, string marketplace)`,
  fired once per outdated plugin at each scan (documented on the plugin's Codefling page). **`url` is the plugin's marketplace page, not a
  download**, so it can be offered as a link but cannot drive an install. The class name is used for `name` ("BlueprintShare") where the server's plugin
  list has the title ("Blueprint Share"), so matching ignores case, spaces and punctuation. Its console report (visible in the stored console
  history from 2026-09-19) has the same fields, which is how the shape was confirmed on the live server (UpdateChecker 4.6.1, scanning on load
  and every 1440 minutes).
- **Plugin (0.8.0, capability `updates`).** Catches the hook and keeps the newest notice per plugin (`ArchonUpdates.Store`: name, current and
  latest version, url, marketplace, first and last time heard, times heard), bounded (500 plugins) and stripped of control characters because it is
  another plugin's text. A notice that is news is written to the console with every field; a repeat only refreshes the times. `archon.updates`
  returns the table. It is a table, not a history: UpdateChecker repeats itself at every scan, so nothing needs to survive a plugin reload.
- **Worker.** While the capability is offered it reads `archon.updates` every 5 minutes and publishes `PluginUpdatesCaptured` only when there are
  notices and the reply changed. An empty table is never published (it means "not heard yet", not "up to date") and is not stored as a console row.
- **Api.** `PluginUpdateNotice` (one row per server per plugin, everything as reported; migration `AddPluginUpdateNotices`), merged by plugin, an
  older report never replacing a newer one, nothing removed by a report that leaves a plugin out. Each new or newer notice is logged with all its
  fields. Pruned with the rest by plan retention when not heard again. `GET api/rustservers/{id}/plugin-updates` (same permission as reading the
  server) returns only notices that still hold - the plugin is still installed and its installed version is not provably at least the newest
  (versions that cannot be compared show the notice unless written identically) - so updating a plugin removes its notice without waiting for the
  next scan. The address is passed on only if it is an absolute http or https one without credentials.
- **Panel.** The Plugins tab shows a count, and for each plugin with a notice an "Update available: x.y.z" badge (its tooltip has the installed and
  newest versions, first and last heard, times heard) and a "View on <marketplace>" link (opens in a new tab, `noopener noreferrer nofollow`; the page
  repeats the http/https check). Shown text is never treated as markup.
- **Checked live** (local Api/Panel against Rusty Amigos, with rows made from the real 2026-09-19 scan output): Blueprint Share (installed v1.4.6, reported
  "BlueprintShare 1.4.6 -> 1.4.7") and HarborEvent (2.4.4 -> 2.4.6, Codefling) appeared with the right version and link; a hostile row for a plugin that is not
  installed was not listed. The rows were removed afterwards.
- **Not verified.** The plugin itself has not been pushed to Rusty Amigos: the update needs the game server to reach the Panel that serves it, and
  this laptop's only route (a firewall rule and LAN Panel address) is a security-setting change that was not made unattended. Push it from panel-dev
  once this is merged and the edge image is built (the Updater stays 0.2.0 - no change needed). The first real notice will arrive at the next UpdateChecker scan
  after the plugin loads (up to 24 hours), since UpdateChecker does not rescan when another plugin loads.
- **Later: an Update button.** Not possible from what UpdateChecker provides (a page, not a file). Paid marketplaces will not hand a server a file
  without a login, so this would need a per-marketplace answer (uMod publishes downloads; Codefling and others do not) and a way for the
  server owner to supply credentials. To be designed separately.

### Updater self-update (2026-09-20, plugin 0.9.0 and Updater 0.3.0)

Scott's requirement: no manual maintenance. The reason the Updater was manual is that it is the only thing that can roll back a bad main-plugin
update, so a bad Updater would have nothing to recover it. The answer is that each is the other's way back.

- **Who does what.** The **main plugin** downloads, verifies, swaps and watches the Updater (`archon.updater.update <version> <url> <token>`,
  `archon.updater.status`, capability `updater-update`); the Updater keeps doing the same for the main plugin. Only one is ever replaced at a
  time: each refuses to start while the other's state file says it is downloading or loading (`update-status.txt` / `updater-swap.txt`).
- **What is checked.** The last-line signature must verify under the key **this plugin** trusts (its own stamp, which must itself verify), the
  `[Info("RustArchonUpdater", ...)]` version must be the one asked for and newer than the installed one (no downgrade), and the file must
  arrive within the 1 MB cap with redirects refused and the token in a header. Otherwise nothing on disk changes.
- **Rollback.** The previous file is kept as `RustArchonUpdater.cs.bak`. The new Updater writes `updater-loaded.txt` (version and time) when it
  comes up - a copy that fails to compile never does - and the main plugin waits for that marker for 45 seconds, then restores the backup. The
  swap survives the main plugin reloading (the state is on disk and resumed at load). If there was no Updater at all (a **fresh install**), the
  file that did not come up is simply removed.
- **Key rotation.** The Panel signs the Updater with its active key, which the plugin that installs it must trust, so a server still on an older key
  is told to **update the plugin first** (`update_plugin_first`); that bridge moves it to the new key, after which the Updater can be updated.
- **Bootstrap.** No hand-install is needed even for servers running Updater 0.2.0: the old Updater updates the main plugin to 0.9.0 as always, and
  0.9.0 then updates the Updater. `plugin_too_old` tells the Panel's user to do the plugin first when the capability is missing.
- **Api.** `POST api/rustservers/{id}/plugin/update-updater` (`PluginUpdateService.StartUpdaterAsync`, same switch, permission and result shape as
  the plugin update). Preconditions: updates on, handshake known, the plugin's signature valid and its key the Panel's **active** one, the
  `updater-update` capability, and a newer (or missing) Updater. The token has a **purpose** (`main` or `updater`; new column, migration
  `AddPluginUpdateTokenPurpose`) so a token minted for one file can never fetch the other; the download door serves the Updater only to an
  `updater` token whose key is still active. The command always uses the header form (`archon.updater.update <v> <panel>/ingest/plugin <token>`).
- **Panel.** On the Plugins tab an outdated Updater shows "Update Updater", a missing one "Install Updater" (with the download kept as the by-hand
  route when the plugin is too old, the key is not the Panel's current one, or updates are off - with a hint to turn them on). The result reads
  "started" only; the outcome shows in the Updater's version after Refresh.
- **Tested** with a temp plugins folder (signed, unsigned, wrong key, tampered, wrong version, not newer, busy, timeout, missing/changed file,
  reload mid-swap, rollback, fresh install, real HTTP with the token in a header) and end to end with the real Api-signed Updater and the real
  Updater's marker.
- **Live on Rusty Amigos (2026-09-20).** Plugin 0.8.0 -> 0.9.0 through the Updater 0.2.0 already installed (the version number had to move on:
  0.8.0 was already installed without the capability, so the Panel saw nothing to update). Then, with no hand install, the Panel's "Update Updater"
  took the Updater 0.2.0 -> 0.3.0 in about two seconds ("Swapped in RustArchonUpdater 0.3.0" ... "Init v0.3.0" ... "Updater update succeeded",
  the Updater's header-token download used the LAN address). **Rollback, live:** a deliberately broken Updater 0.3.1 (a syntax error, served from a
  temporary edit of the source that was reverted afterwards) was swapped in at 19:35:36, Carbon logged `Failed compiling 'RustArchonUpdater.cs'`,
  and at 19:36:22 - 45 seconds after the swap - the main plugin logged `Updater update 0.3.1 rolled-back`, restored the backup and Carbon
  loaded Updater 0.3.0 again. Not exercised live: a fresh install (no Updater at all) and a main plugin that reloads mid-swap (both covered by tests).

### Automatic updates (2026-09-20)

Scott's decisions: an opt-in per-server switch (yes); **players being online is not a reason to wait** (a plugin reload is brief; the Worker collects what
the plugin holds every 30 seconds, so at most about that much recorded data is lost); an update that failed is not retried in a loop but a *different*
version is always tried (and bridges work as ever); a server on an older key is handled by the bridge, not skipped; release control is needed.

- **Switches.** Per server: "Update automatically" (`RustServer.PluginAutoUpdateEnabled`, off when built and on for a new server since 2026-09-20, shown only while "Allow updates" is on and
  turned off with it; the settings endpoint takes it as an *optional* field so a client that does not know about it cannot flip it). Site-wide: the
  platform setting **Automatic plugin updates** (`PluginAutoUpdatesEnabled`, default on) stops every automatic update at once - the emergency stop.
- **What it is.** `PluginAutoUpdater` (a background pass every 5 minutes, first one 3 minutes after the Api starts) presses the same buttons a person
  does: `PluginUpdateService.StartAsync` / `StartUpdaterAsync` with trigger `auto`. Every check (signature, key, version, capability, token) is the
  service's, so automation can never do what a click could not. It decides only when and in what order.
- **Order.** Current key and a plugin that can update the Updater: the **Updater first**, then the plugin. An older key: the **plugin first** (that is the
  bridge to the current key), the Updater after it on a later pass. One update at a time per server; the next waits until the last one's outcome is known
  (the reported version changed, or ten minutes passed). At most five updates start per pass, so a release does not reach every server at once. Servers
  whose plugin has not answered in 15 minutes are left alone.
- **No loops.** `PluginUpdateAttempt` rows (also the audit trail of manual updates: kind, from, to, trigger, state, code, times) record every request that
  reached a server: started, then succeeded (the version arrived) or failed (it did not - the new one was put back), or refused with the plugin's code.
  A (kind, version) that is failed, refused or still pending on a server is not sent again until a different version is served. Refusals that say nothing
  about the version (busy, plugin too old) and servers that could not be reached are not attempts and are simply tried again. Succeeded attempts are pruned
  by plan retention; failures and refusals are kept so a bad version is not retried when the window passes.
- **Release control.** The auto updater only ever follows *the version being served*: an uploaded release stays a draft until an administrator publishes it,
  and withdrawing it returns to the embedded build (never a downgrade: nothing newer than installed means nothing to do). Plus the site-wide switch above.
  Staged roll-outs are built (see "Plugin admin, defaults and failed plugins").
- **In the Add Server wizard (Scott's request).** An **Updates** step follows the Plugin step for anyone who chose to set the plugin up, gathering every
  update setting on one page with a plain explanation of each and why someone might want it: "Allow updates from this Panel" (one click instead of a
  new file each time; only a Panel-signed file is accepted and the previous version is put back if the new one does not start), "Update automatically"
  (fixes and features without remembering; the same checks as a click; players need not leave; a failed version is put back and not retried; leave it
  off to choose when the server changes) and a note on the Updater helper (installed already, or installable by the Panel once updates are allowed).
  Both switches start on for a new server (2026-09-20, see the defaults note below) and the page says so; "Keep as they are" leaves them, and everything is
  changeable later on the Plugins tab.
- **Panel.** The Plugins tab has the "Update automatically" switch under "Allow updates", and a "Recent updates" list (last five: automatic or manual,
  plugin or Updater, versions, outcome in words, time).
- **Tests.** Real Postgres for the updater (eligibility, order, one at a time, outcome resolution, failed and refused versions not retried, a fixed
  release retried, an unreachable server retried, staggering, one server's failure not stopping the others, no downgrade, players online ignored),
  the service's attempt recording, the settings endpoint (optional field, follows the updates switch), the attempts endpoint, retention, and the Panel.
  Not yet run live.

### Plugin admin, defaults and failed plugins (built 2026-09-20)

Built and tested together (Plugin 473, Api 1568, Panel 511, Worker 316, integration 49 tests). One migration (`AddPluginRolloutAndLoadFailures`: two new tables).
Not yet run live.

- **Syntax check on release uploads.** `PluginSourceSyntax` (Roslyn, the `Microsoft.CodeAnalysis.CSharp` package, nothing is run) reads an uploaded file as C# 7.3: the
  parser finds syntax errors, and the language-version errors ("Feature 'x' is not available in C# 7.3": switch expressions, `using var`, `??=`, ranges, ...) come
  from compiling it against the .NET core library only and keeping just those. A refusal is `syntax_error` with the first five problems and their lines. It cannot
  see calls into the game's own libraries (a type Rust renamed); that stays with the plugin's real-DLL compile check and the Updater's rollback. Applies to
  upload and to signing a file, so a hand-signed file is held to exactly what a published release is.
- **Signing UI for custom builds.** "Sign a file without publishing it" on `/Admin/Plugin`: choose the file, say why (required, 3 or more characters), get it back
  stamped with the active key, signed, and downloaded byte for byte. Nothing is stored or delivered. A stored draft or published release also has a download
  button (signed, to test on a server before publishing); a withdrawn one is refused. Site Admin only.
- **Audit logging of signing.** New audit lines: `FileSigned` (who, what version, checksum, key, why), `ReleaseSigned` (a stored release downloaded signed) and
  `KeyGenerated` (the first key, actor "system"). Deliberately **not** logged: the signing the Panel does for every ordinary download or update of the served
  file, which is the same public file every time and would only bury the lines that matter.
- **Rotation reminder.** `PluginKeyRotationReminderDays` (Platform Setting, 365 by default, 0 turns it off). The key's age counts from when it became active: the
  latest rotation or import that replaced the one before it, else the `KeyGenerated` line, else (a key made before that line existed) the setting row's creation.
  Shown in the keys card and as a dismissible banner on every page for site administrators (`PluginKeyReminderBanner`, silent for everyone else, who get a 403).
  It only reminds: rotating decides what every installed plugin trusts next, so nothing rotates by itself. Not emailed - that would need an email template.
- **Staged roll-outs.** `PluginRolloutHours` (Platform Setting, **0 by default: everyone at once**). When set, a newly served version becomes eligible for
  *automatic* updates on a growing share of servers over those hours (a straight line; a hash of server, file and version fixes who comes first, so it is stable
  across passes and instances and a different order for each version). The start is recorded once per (file, version) in `PluginRollout` the first time the
  automatic updater has servers to consider; a version that returns later is not put through the ramp again. A person's click is never held back; the emergency
  stop is still the site-wide "Automatic plugin updates" switch or withdrawing the release. The Plugin page shows the ramp's progress beside what is served.
- **Failed-plugin visibility.** Carbon prints a "failed plugins" section after the table in `c.plugins` (file, line:column, compiler message, long messages cut with
  `...` and carried on the next line). `CarbonFailedPluginParser` (Worker, tested on a reply captured from Rusty Amigos) reads it; `ServerPluginsCaptured` carries
  the list (`null` = not reported, empty = none failed); the Api keeps the server's current set in `PluginLoadFailure` (a fixed plugin disappears, an older
  report never brings one back, a server no longer on Carbon has none); `GET api/rustservers/{id}/plugin-failures` (same permission as reading the server); the
  Plugins tab shows "Failed to load" with each file's reasons, plainly saying so when the file is the RustArchon plugin or its Updater. A Carbon server whose every
  plugin failed is still recognized as Carbon. Oxide reports no such list. Compiler text is shown as text, never as markup.
- **Defaults.** A **new** server now has "Allow updates" and "Update automatically" on (the entity default; the column's own default is still off, so no migration
  and no existing server changed). The Add Server wizard's Updates step shows them on, explains each, and offers "Keep as they are".
- **Rate limits as Platform Settings** (category Reports): `ReportsPerServerPerMinute` (60, applied by the Api after the secret is verified),
  `ReportsPerAddressPerMinute` (120, applied by the Panel's public report door; the Panel asks the Api for it and remembers it for a minute, using the default
  until the first answer and keeping the last good value if the Api is unreachable) and `IntegrationChecksPerUserPerMinute` (20). Per-server limits are a later step.
- **Console rows.** Background poll answers that carry data are no longer all stored: a poll's answer is kept when it differs from the last one kept (plugin list,
  plugin status, tool cupboard pages) or once every 10 minutes (server info, stats, player list, drains), and always when it reports a failure, a stack trace, or
  a drain that lost events. The data itself is stored in its own tables; the console record's copy only has to show the poll happens. A person's own command is
  never thinned.
- **Recording disclosure.** Shown as a feature, in the customer's terms, where recording is offered (open in the wizard's plugin step) and where it is switched
  (folded beside the switches on the Plugins tab): what it gives you (live map, combat log, base locations, evidence in a dispute), what is recorded (positions a few
  seconds apart, fights, tool cupboard authorizations), who can see it (only your organization; positions need their own permission that the Owner holds and can
  delegate, every look logged), how long it is kept (the plan's days, then deleted), how to turn it off, and a line for the server's own rules. Every claim is one the
  code makes true today; if any of those change, this text changes with them.

### Phase 5 - Session replay UI (later)

Not in the first delivery, but Phase 2 starts capturing so history exists when it ships. Needs Phase 3's map image
and world size for the world-to-image mapping. An admin picks a player and a time window, sees the path drawn on
the map with a scrubber, and the L1 events pinned to the timeline. Natural entry points: from a player row, and
from an F7 report (open the reported player's replay around the report time), which is why the report path and
the replay data share server time. Gaps flagged by `lost` are drawn as gaps. Own tests and, if the UI is large,
its own plan. The UI's architecture (client-side clock, windowed read model, canvas, clip-first) is proposed in
[ADR-0005](../adr/0005-dvr-style-map-replay-client-clock-over-windowed-data.md); a prototype on fake data comes first.

### Later, separate ADR

Installing/updating **third-party** plugins from the Panel (host allowlist, required hash). Not planned here;
the mechanism exists after Phase 4 but the trust and sourcing questions need their own decision.

## Performance rules for the plugin

- No `OnTick`, and no timers doing work, **with one exception: the opt-in position sampler** (Phase 2), which is
  low-frequency, O(active players), and reports its own time. No full entity scans on the main thread in one go.
- Every hook is subscribed only while its capability is enabled; unsubscribed otherwise.
- Any O(entities) work is frame-sliced under a time budget; serialization happens off the game thread from a
  plain-data snapshot (Unity calls are main-thread only).
- Prove it: after each phase, read Carbon's `c.plugins` hook time/fires for our plugin and record it.

## Tests

Shipped with each phase, in every project touched.

- **Plugin project:** a compile check against stubs of the Oxide/Rust API surface at **C# 7.3** (the spike was
  checked this way before each upload; it catches our own mistakes, not framework restrictions), plus unit tests over the pure logic linked into a test project:
  envelope, pagination, ring buffer and `lost` detection, version comparison, signature verification using
  fixtures signed by .NET 10. This does **not** prove framework behavior.
- **Combat log:** hits are attributed to the right attacker and victim; a hit with no player on either side is
  **not** recorded; player-vs-NPC/animal and player-vs-entity hits are; outcome mapping (hit/wounded/killed);
  headshot and hit-area fields; incident grouping; the Panel prefers plugin kills over scraped kills when the
  capability is present and labels the source; filters by player and time; tenant isolation.
- **Replay pipeline:** sample packing/unpacking round trip, delta-skip of stationary players, `lost` gap
  handling, chunk append across drains, the retention purge deleting exactly the chunks older than the plan's
  `RetentionHistory` days (and none inside it), the recording opt-out actually stopping the sampler and the
  hooks, and tenant isolation on every replay read.
- **Worker:** parsers and poll loops against recorded/fake RCON responses, including the `c.plugins` hook-time and
  failed-plugins extensions; handshake only attempted when the plugin is present; **command sent non-interactive**.
- **Api:** entities and tenant isolation on every read; token single-use, expiry, and **server A's token rejected
  for server B**; signing-key permission (platform-admin only) and audit rows; new `SecretPurposeFor` case
  (the existing test enumerates every Secret setting); stamping and signature produce a file the plugin's verifier
  accepts.
- **Panel:** capability gating fails closed (absent/unknown capability = feature hidden and no command issued);
  positions/bases require the new permission; role delegation honors the subscription feature.
- **Live smoke on the test server, per phase:** load the plugin, run the handshake, exercise the feature, read
  `c.plugins` for our hook time, and for Phase 4 the failure paths above. Record results in the phase's PR.

## Boundary with the F7 work

[ADR-0003](../adr/0003-merge-native-and-plugin-reports-into-one-row.md) already assumes the plugin holds F7
reports in a ring buffer that the Worker polls; that matches this design. The F7 plan says the plugin download
and signing are this session's decision: they are decided here (Phase 1). The F7 wizard's plugin step is
unblocked by Phase 1's handshake. Plugin-side `OnPlayerReported` internals belong to the F7 session.

## Delivery order and repo workflow

1. Phases 1 to 4 in order. Phase 1 blocks the F7 wizard's plugin step.
2. Submodule PRs first (Api, Shared, Messaging, Worker, Panel, Plugin as touched), then one umbrella
   submodule-bump PR. Every PR gets a feature/fix/chore/breaking/docs label at creation. No PR until Scott has
   tested and asks.
3. **Stop Visual Studio before any migration or live run.** The Api/Worker/Panel can also be started for live checks
   with the launch configs (`--no-build`); the local dev sign-in signs in automatically.
4. Config lives in Platform Settings by default; anything else needs a stated reason first. Nothing in the plugin
   is app-specific to JumpStart, so no JumpStart work is expected; re-check per phase.

## Risks and open items

- **Replay data volume and privacy.** Continuous recording is the biggest new cost and obligation in the plan:
  storage grows with players and retention (estimate only), and it is behavioral data about identifiable players.
  Mitigations: opt-out per server with visible status, retention bounded by plan, chunked storage, owner-only
  access with audit logging. Note recording is **on by default**, which raises the disclosure obligation - met as a feature, in the customer's terms
  (see "Recording disclosure" below).
- **Sampler and damage-hook cost are unmeasured.** They are the only always-on work the plugin does, and there are
  no levels to fall back on; measure before shipping (see the cost gate). The damage hook is the one to worry
  about: it fires for all combat entities and damage cannot be dropped.
- **Combat log storage is the biggest unknown.** Per-hit volume times up to 265 days of retention could dwarf
  everything else in the plan; the row format is not fixed until hits per player-hour are measured on a PvP
  server. The test server is PVE ("Builder-focused PVE"), so it will understate player-vs-player traffic.
- **Retention** is enforced by `PluginDataRetention` in the Api (see "Hardening pass" below): plugin chunks, console and chat, kill feed,
  stats snapshots, the maps of past wipes and spent tokens. **Player sessions are deliberately not pruned** (they hold the VPN, ban and
  geolocation lookups and the names the Panel shows); if the plan's "player history" days should cover them too, that is a decision for
  the owner, not a side effect.

- **Oxide is untested.** Carbon is the first-class target; claim Oxide support only after a compile-and-run test
  on an Oxide server.
- **Rust updates break plugin internals monthly.** Keep the main plugin's Rust-internal surface small; the
  Updater plus failed-plugin visibility in the Panel is the mitigation.
- **Boot render delays server opening by ~48 s** on the first boot after a wipe.
- **`RconEvent` growth:** background polls' empty answers are no longer stored (see "Hardening pass"), and the table is now pruned to the plan's
  retention; what remains is the non-empty poll answers (server info, player list, tool cupboards) which are one row per poll.
- **TC index cold start:** cost unmeasured.
- **No relaunch after RCON restart:** the Panel must not promise a restart, only a stop.
- **Private signing key** sits in the same database as the Data Protection key ring that protects it; an "import
  a pre-signed artifact" path is the escape hatch if production signing later needs to move offline.
- **Leftover from the spike:** the eight `ArchonSpike*.cs` plugins and their data files are still loaded on
  Rusty Amigos and should be removed once Scott is done with them.
