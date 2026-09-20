# ADR-0005: DVR-style map replay - client-side clock over windowed, time-indexed data

**Status:** Proposed
**Date:** 2026-09-19
**Deciders:** Scott Blomfield
**Plan:** Phase 5 of [docs/plans/companion-server-plugin.md](../plans/companion-server-plugin.md); expands into its own
plan once the prototype (Action Item 1) has been judged.

## Context

Phase 2 records player positions and per-hit combat; Phase 5 of the plugin plan promises a "session replay UI" in
one paragraph. Scott's intent (2026-09-19) is bigger than that paragraph: a **DVR-feeling** playback over the server
map, with the player's location, icons for PvP, each type of PvE, and attacks on buildings and player-owned
objects, and play, fast-forward and scrub. There is no first-person view to fall back on, so the map overlay *is* the
product. It is meant as a market differentiator, which makes the feel of it (responsiveness, scrubbing, pacing)
a first-class requirement, not polish.

What the data gives us today, from the built plugin (0.3.0+) and the plan:

- **Combat events are self-locating.** Each carries server time (`t`, Unix ms), both parties' positions
  (`apos`, `vpos`), weapon, damage, damage type, headshot and distance. A hit can be drawn on the map with no
  position sample at all.
- **Non-player parties are identified only by prefab short name** (`Attacker`/`Victim` = `ShortPrefabName`,
  e.g. an NPC, an animal, a wall or a door). There is no "PvE type" field, and **the owner of a struct or deployable
  is not recorded**, so "attack on a *player-owned* object" cannot be attributed to a player yet.
- **Damage with no player on either side is dropped**, zero-damage hits are dropped, and attacker-less hits are
  throttled. A replay is therefore a record of *player-involved* action only.
- **Positions are not built yet.** The plan samples about every 5 s, delta-skips stationary players, records the
  logout position, and flags buffer wraps with `lost`. Movement between samples is interpolated. Storage is one
  chunk per `(server, player, ~10 min)`.
- **The Panel reads through the Api** (Worker -> Api -> Panel); the Panel never talks to the plugin.
- The Panel is Blazor Server (interactive render mode). Every interaction that crosses the circuit costs a network
  round trip, which is fatal for a 60 fps playhead.

## Decision

### 1. Playback is a client-side clock over data already in the browser

The playhead, play/pause, speed and scrub run **entirely in the browser** on a frame loop. No frame, tick or drag
position crosses the Blazor circuit. There is no server-rendered video or streamed frames. Blazor owns selection,
permissions and data fetching; a JavaScript module owns the clock and drawing.

### 2. One time-indexed replay read model, fetched in bounded windows

The Api exposes a single read endpoint (shape, not final:
`GET /api/rustservers/{id}/replay?from=&to=&players=`) that merges **position samples and combat events on the same
server-time base** (Unix ms) and returns them in time order. The browser requests **bounded segments** (for example
five minutes) ahead of the playhead and drops segments behind it, so a scrub to any time costs one small request and a
long session never has to be downloaded whole. Gaps (`lost` from the drain, or no sampler data) are returned as
explicit gap spans, never as missing rows. It is tenant-filtered and returns 404 for another organization's server,
like the combat endpoint.

### 3. Rendering: 2D canvas over the map image

The map image is a background; players, trails, hit icons and structure-attack icons are drawn on a single
`<canvas>` with a world-to-image transform (Phase 3 supplies the map image and world size). Interpolation between
samples is done client-side per frame.

### 4. Be honest about fidelity

- A position **interpolated between 5 s samples** is drawn differently from one **anchored by a combat event's own
  coordinates**, and the path snaps to event anchors instead of ignoring them.
- Gaps are drawn as gaps.
- No smoothing that invents movement the data does not support.

An admin using this as evidence must be able to tell recorded from inferred at a glance.

### 5. Icon classification happens at read time from prefab names

Which icon a non-player party gets (NPC, animal, structure, deployable, vehicle...) is decided by a **mapping from
prefab short name to category, applied when the replay is read**, not stored in the chunks. Unknown prefabs get a
generic icon with the raw name in the tooltip. Improving the table then improves every historical recording.

### 6. Clip first, full-session second, prototype before capture

- The primary entry point is a **clip**: open from a combat-log row or an F7 report at that moment, with a window
  around it (for example T-30 s to T+60 s), the involved players highlighted and the rest faded. Whole-session replay
  from a player row is the second entry point on the same component.
- The timeline is a **density strip**, not a bare line: combat activity by kind along the scrub bar, with jump to
  previous/next fight.
- **Before any capture or Api work, build a clickable prototype on fake data** and judge the feel. The design
  decisions above are what the prototype must be consistent with; the UX itself is decided by using it.

### 7. Permissions follow positions

A replay shows positions, so it is governed exactly as Phase 2 positions are: Owner-only, delegable to roles on plans
with `HasRoles`, retention bounded by the plan's `RetentionHistory`, and every view audit-logged. A clip opened from
an F7 report is still a position view and is subject to the same check.

## Options Considered

### Playback engine

| Option | Assessment |
|--------|-----------|
| **A (chosen): Client clock over windowed data** | Scrubbing and speed changes are instant and cost no server work. Needs only ordinary read endpoints. Works with Blazor Server because per-frame state never touches the circuit. |
| B: Playhead in Blazor state (server-driven ticks) | Every frame or drag is a SignalR round trip; it cannot feel like a DVR, and it multiplies circuit load by viewers x frame rate. |
| C: Server-rendered video/frames | Heavy, expensive per viewer, no interactivity (no click-to-follow, no filters), and pointless when the source data is a few thousand points. |

### Renderer

| Option | Assessment |
|--------|-----------|
| **A (chosen): Canvas 2D in a JS module** | Comfortably handles 100 moving players, trails and hit pulses; no framework dependency; simple to hit-test icons. |
| B: SVG or DOM elements per marker (Blazor-rendered or JS) | Simple, but hundreds of animated nodes and per-frame style updates are the wrong shape, and Blazor-rendered markers bring back the circuit problem. |
| C: WebGL / a map or scene library | More headroom than needed; a dependency and a learning cost for a scale canvas already covers. **Revisit only if the prototype shows canvas cannot hold the frame rate.** |

### Data delivery

| Option | Assessment |
|--------|-----------|
| **A (chosen): Bounded windows, fetched ahead of the playhead** | Scrub cost is one small request; memory is bounded; suits chunked storage (a window maps to a few chunks). |
| B: Download the whole session up front | Simple client, but long sessions on busy servers get large, and the first paint waits on the slowest part. |
| C: Server pushes a live stream | Only "live view" wants this, and that is the same data as the newest window; not needed for replay. |

## Trade-off Analysis

The client-clock design puts real logic in JavaScript, in a codebase that is otherwise C#/Razor. That is the cost of
a DVR-grade feel, and it is contained: one module with a narrow contract (load a window, set the clock, draw), covered
by its own tests, with the Blazor component passing it data and receiving only coarse events back (clip loaded, a hit
selected). Fetching in windows adds edge cases (a window boundary in the middle of a fight, prefetch under fast
forward at 64x) that the prototype should provoke on purpose. Read-time classification costs a little CPU per read in
exchange for retroactive improvement, and keeps the stored chunk format free of an icon taxonomy that will change.

## Consequences

- **Easier:** any admin surface that references "this moment" (combat log, F7 report, a player row) can open the same
  component with a window; the feature reuses the chunked storage and retention job already decided.
- **Harder:**
  - The capture side must keep **positions and combat on one server-time base**; a clock skew between them is
    visible as a player who shoots from the wrong place.
  - Replay fidelity is capped by capture: 5 s sampling looks coarse for a fast fight. Combat events' own positions
    soften this, and 1 s sampling while in combat (already listed in the plan as a later refinement) becomes a
    real candidate, **decided after the prototype shows whether it is needed**.
  - "Attacks on player-owned objects" needs the **owner of the struct or deployable** captured on the event. That is
    a plugin and chunk-format change (chunks are format-versioned for this reason) and a privacy consideration, to be
    decided at that point.
  - A JavaScript module in the Panel needs its own test approach.
- **To revisit:** WebGL if canvas is not enough; sharing or exporting a clip (a strong feature, but a privacy and
  permissions question, so its own decision); mobile/touch scrubbing; how live view and replay share the timeline.
- **Dependencies:** [ADR-0004](0004-companion-plugin-rcon-pull-dormant-hooks-signed-updates.md) (capture, chunking,
  retention, fail-closed capability gating: replay is enabled only when the plugin **positively reports** the
  capability). Needs the Phase 3 map image and world-to-image mapping.

## Open questions (Scott to decide, or defer to the prototype)

1. **Clip export and sharing.** Skipped as a decision here, but likely the most valuable extension. Own ADR later?
2. **Owner attribution for structure attacks.** Record `OwnerID` on events, and for which entity types?
3. **Do PvE encounters belong in a player's replay by default,** or behind a filter? The volume of NPC and animal
   hits will dominate a busy timeline.
4. **Speed steps.** Assumed 1x, 4x, 16x, 64x plus jump-to-next-fight; the prototype should confirm.

## Action Items

1. [ ] Build a clickable prototype on fake data (canvas, window fetch faked, clip and full-session entry points,
       density strip, speed steps, fades and follow-a-player) and judge the feel before any other work.
2. [ ] Decide the open questions above after the prototype.
3. [ ] Write the replay plan (own document) from what the prototype teaches; add tests alongside each slice.
4. [ ] Confirm positions and combat share one time base when the position sampler is built (Phase 2, positions slice).
5. [ ] Add the prefab-to-category mapping table with a generic fallback when the read endpoint is built.
