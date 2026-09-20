# ADR-0002: The Add Server wizard tests the RCON connection through the normal Worker connection

**Status:** Accepted
**Date:** 2026-09-19
**Deciders:** Scott Blomfield
**Plan:** [docs/plans/f7-reports-and-add-server-wizard.md](../plans/f7-reports-and-add-server-wizard.md)

## Context

The new Add Server wizard must "test the RCON connection" before guiding the user further. RustArchon's data
flow is Worker -> Api -> Panel: the Worker owns every RCON socket, the Api persists, and the Panel reads through
the Api (the Panel never RCON-commands or parses).

Relevant existing behavior:

- `RustServer.RconPassword` is stored encrypted, never in plaintext. The Worker obtains the decrypted password
  by calling the Api's internal endpoint (`InternalRustServerInfo`); it does not travel on the message bus.
- `RustServersController.Create` already enforces the plan limit and duplicate-name rule, then publishes
  `ConnectToServer`, after which a Worker claims the server and reports `ConnectionStatusChanged`, which the
  Panel already displays (`ConnectionStatus` + `ConnectionStatusDetail`).

## Decision

The wizard's "Connect" step **creates the server for real and watches the existing live connection status.**
The Worker's normal connection *is* the test. There is no separate one-shot probe.

To avoid stuck half-created servers, the wizard offers **Discard**, which deletes the server (freeing its plan
slot). The wizard is resumable at `/servers/{id}/setup`.

## Options Considered

### Option A (chosen): Create, then observe the normal connection

| Dimension | Assessment |
|-----------|------------|
| Complexity | Low - reuses `ConnectToServer`, `ConnectionStatusChanged` and the existing Panel status display |
| Cost | Low |
| Team familiarity | High |

**Pros:** no new credential-handling path; exercises the real production code path, so a passing test means the
server genuinely works; reuses existing failure detail.
**Cons:** a server row (and a plan slot) exists from step 2, before setup is finished; a failed attempt leaves a
failing row unless discarded.

### Option B: One-shot probe request over the message bus

**Cons:** the request would need the plaintext RCON password to travel on RabbitMQ, contradicting the
never-plaintext rule. Rejected.

### Option C: A probe using an encrypted or handle-based credential

**Cons:** the Worker would need a new way to redeem a temporary credential (a short-lived stored handle plus a new
internal endpoint), which is more machinery than the value it adds over Option A. Rejected as over-built.

### Option D: The Api or Panel opens the RCON connection itself

**Cons:** breaks the Worker -> Api -> Panel flow and puts RCON socket ownership somewhere other than the Worker.
Rejected.

## Trade-off Analysis

Option A trades a temporary server row for zero new credential-handling surface and a test that is the real thing.
The row's downsides (plan slot, leftover on abandon) are bounded by Discard and a resume state, and are the same
footprint the current Add form already has once the user saves.

## Consequences

- **Easier:** no new secret-handling code; the connection test cannot pass while production would fail.
- **Harder:** an abandoned wizard leaves a server row. v1 mitigation is a visible "setup incomplete" resume state
  and a manual Discard; **no auto-delete** (silently deleting a customer's server is worse than a leftover row).
- **To revisit:** auto-clean of unfinished servers after some period, if leftovers prove a nuisance.

## Action Items

1. [ ] Add a Discard endpoint that soft-deletes a half-created server and frees its plan slot.
2. [ ] Add the wizard's resume route `/servers/{id}/setup`.
3. [ ] Tests: Discard cleanup, plan-limit gate, duplicate-name handling, connect success/failure display.
