# ADR-0004: Companion server plugin - RCON pull, dormant hooks, signed two-plugin updates

**Status:** Accepted
**Date:** 2026-09-19
**Deciders:** Scott Blomfield
**Plan:** [docs/plans/companion-server-plugin.md](../plans/companion-server-plugin.md)

## Context

Vanilla WebRCON cannot give RustArchon several things the Panel wants: player positions, tool-cupboard
(TC) locations and authorized players, structured combat events (including player-vs-NPC/animal kills, which
vanilla prints nothing for), the map image, and file access for installing or updating plugins. An optional
Oxide/Carbon plugin can supply them. Three constraints shape how:

- **Server impact must be as close to zero as possible.** The plugin runs on the game thread of someone else's
  server.
- **It is optional.** Servers without it must keep working, and the Panel must say what needs it.
- **It runs code with full server privileges,** so anything that changes its code is a supply-chain risk.

A feasibility spike on a real Carbon server (Carbon 2.0.259.0, Linux, Mono `clr=4.0.30319`, Rust build 2633,
67 plugins) measured the behavior this decision rests on. The numbers are in the plan.

## Decision

### 1. Transport: RCON pull, with HTTP only for blobs

The plugin registers RCON-only console commands (`archon.*`) returning a JSON envelope `{v, ok, data, err}`,
paginated. It **does nothing between calls**, with one exception: when the `positions.record` capability is
enabled (on by default per server, with an opt-out), a low-frequency sampler and the player-action hooks record
into bounded in-memory buffers. This is needed for session replay; polling only while someone is watching would
record nothing. It is a single switch with no detail levels, and retention follows the tenant's plan
(`Plan.RetentionHistory`). The Worker polls on its own schedule, so
data still flows Worker -> Api -> Panel. Events and samples accumulate in bounded buffers that the Worker drains
with a sequence cursor, so drain cadence never costs data.
The only exception is large binary payloads (the map PNG), which the plugin POSTs over HTTPS to the Panel when the
Worker asks, using a single-use token.

### 2. Dormant by default, capability-gated

On load the plugin unsubscribes every hook. The Worker's `archon.hello` handshake reports
`{protocolVersion, build, framework, capabilities[]}`; hooks are subscribed only for capabilities that are
enabled. The Panel enables a feature only when its capability is **positively reported** (fail closed).

### 3. Updates: signed, admin-triggered, two plugins

- **Signature:** RSA-2048, SHA-256, PKCS#1 v1.5. Each Panel deployment has its own keypair; the public key is
  stamped into the served script. An update is accepted only if signed by the key embedded in the **currently
  running** version.
- **Trigger:** off by default; an admin clicks Update in the Panel, the Api mints a single-use expiring token,
  and the Worker sends `archon.update <version> <token-url>`.
- **Two plugins:** a tiny, stable **Updater** (Carbon/BCL APIs only, no Rust internals) swaps the **main**
  plugin, confirms it loaded, and restores a backup if not.

## Options Considered

### Transport

| Option | Assessment |
|--------|-----------|
| **A (chosen): RCON pull, HTTP for blobs** | Zero idle cost (apart from the position sampler and player-action hooks, on by default with an opt-out); uses the channel that already exists and is authenticated; needs no inbound or extra outbound connectivity for normal data; Worker controls the rate. |
| B: Plugin pushes everything by webhook | Needs outbound HTTPS for every feature; the plugin needs timers and a standing credential; no backpressure. |
| C: Plugin prints structured lines and the Worker scrapes the console | Cheapest to write, but pollutes server logs, drops events on reconnect, and is the fragility the plugin exists to remove. |

Measured: single-line replies of 64 KiB, 512 KiB, 2 MiB and 8 MiB all arrived intact through the real
Panel -> Worker -> Carbon path (2 MiB in 0.3 s, 8 MiB in 1.2 s) and the server did not echo them into the console
stream. The problem sizes seen earlier were RustAdmin, not the transport. Replies are still kept small and
paginated.

### Signature scheme

| Option | Assessment |
|--------|-----------|
| **A (chosen): RSA-2048 + SHA-256, PKCS#1 v1.5** | Works on the game server's Mono runtime; verified a signature made by .NET 10 from an embedded public key in ~9 ms per 300 KB, and rejected a tampered payload. |
| B: ECDSA P-256 | **Rejected: `NotImplementedException` on Mono.** |
| C: RSA-PSS | **Rejected: "padding mode not valid" on Mono.** |
| D: Ed25519 | Rejected: not in the Mono runtime, and a single-file plugin cannot bundle a library. |

### Trust anchor

| Option | Assessment |
|--------|-----------|
| **A (chosen): per-deployment key, embedded in the running version** | Self-hosters sign their own builds; our production servers only trust our key and the reverse. No pairing step; rotation is a bridge release signed with the old key. |
| B: One vendor key baked into every plugin | A self-hosted Panel could not distribute a customized build; the AGPL intent is defeated. |
| C: No signing, rely on TLS to the Panel | A compromised or self-hosted Panel could push arbitrary code to every connected server. |

### Update recovery

| Option | Assessment |
|--------|-----------|
| **A (chosen): Updater + main plugin** | **Measured: a failed compile unloads the plugin and it stays dead** (Carbon lists it under failed plugins). A dead plugin cannot roll itself back, and Rust's monthly updates do break plugins that use internals (four plugins on the test server currently fail on this Rust build). The Updater has no Rust dependencies, so it survives and can fetch a fixed main plugin. |
| B: Single plugin that swaps itself | Cannot recover from its own failed compile. |

## Trade-off Analysis

Pull costs latency: the Worker sees changes at its poll interval, not instantly. That is acceptable for
positions, TCs and events at this product's scale, and it is what makes idle cost zero and keeps the plugin free
of timers and credentials. Signing with per-deployment keys means a compromised Panel that also holds its private
key can still push code, but that Panel can already run any RCON command; the signature protects against
tampering in hosting or transit and against a Panel that holds RCON but not the key. Splitting into two plugins
costs a second file and a rarely updated updater, in exchange for a recovery path that a single file cannot have.

## Consequences

- **Easier:** near-zero idle impact and a provable cost (Carbon's `c.plugins` reports per-plugin hook time and
  fires: the spike's hooks plugin measured 1 ms total over 773 fires). Optional by construction. One code path
  for polling, consistent with the Worker -> Api -> Panel rule.
- **Harder:**
  - **The Worker persists every RCON frame's full text in `RconEvent`,** including background polls. Frequent
    plugin polls will grow that table. Decision (Scott, 2026-09-19): leave it as is and measure before reacting;
    the plan schedules the measurement.
  - The plugin must stay on a C# 7.3 subset and never generate keys (Mono's `RSA.Create()` ignores
    `KeySize = 2048`).
  - The private signing key becomes high-value: stored as a Secret Platform Setting (same protection as the
    Stripe key, decryptable from a database dump because the Data Protection key ring lives in the same
    database), platform-admin-only signing, audit-logged.
  - The server does not relaunch itself after an RCON `restart`/`quit` (confirmed on the test server), so a Panel
    "restart" can only promise a stop.
- **To revisit:** Oxide compatibility (only Carbon was tested); `OnPlayerInput` cost (no players online during
  the test); the `RconEvent` growth policy once real poll traffic exists.
- **Dependencies:** [ADR-0003](0003-merge-native-and-plugin-reports-into-one-row.md) already assumes the
  ring-buffer-and-poll shape decided here. The F7 wizard's plugin step consumes this ADR's handshake.

## Action Items

1. [ ] Create the `RustArchon.Plugin` repo (Scott confirms visibility and license at creation) and add it as a
       submodule.
2. [ ] Implement handshake, capability gating, and the Panel banner (plan Phase 1).
3. [ ] Implement the signing key, script stamping and signed download (plan Phase 1).
4. [ ] Implement the Updater plugin and Panel-triggered update (plan Phase 4).
5. [ ] After Phase 2 goes live, measure `RconEvent` growth and decide whether to change persistence.

## Amendment (2026-09-20): the Updater is replaced by the main plugin

The decision above kept the Updater manual because a failed Updater has no one to recover it. That left a recurring hand-maintenance step for every
customer, which the product cannot have. The two plugins are now each other's recovery: the main plugin installs or updates the Updater (verifying the
same signature under the key it trusts, keeping a backup, and putting the backup back if the new Updater's `updater-loaded.txt` marker does not appear
within 45 seconds), and the Updater continues to do the same for the main plugin. They never replace one another at the same time. What remains manual is
the first install of the main plugin, which is the trust root. Details and the tests are in the plan, "Updater self-update".
