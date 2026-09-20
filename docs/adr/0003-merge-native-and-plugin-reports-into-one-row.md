# ADR-0003: Merge native and plugin reports for the same event into one row

**Status:** Accepted (the matching heuristic is provisional - see Consequences)
**Date:** 2026-09-19
**Deciders:** Scott Blomfield
**Plan:** [docs/plans/f7-reports-and-add-server-wizard.md](../plans/f7-reports-and-add-server-wizard.md)

## Context

An F7 report can reach RustArchon by two independent routes:

- **Native:** the game server POSTs to `server.reportsServerEndpoint` (see ADR-0001). The payload carries
  `AppInfo` (client/system info, position, minutes played) and, for offensive-content reports, a Base64
  screenshot. Facepunch's wiki says this route appears to receive only general, bug and idea reports, not
  player-targeted ones - which we cannot verify, because filing a real report sends it to Facepunch.
- **Plugin:** the optional Carbon/Oxide plugin (designed in a separate session) hooks `OnPlayerReported`, holds
  events in a ring buffer, and the Worker polls it over RCON, publishing `ServerReportReceived`. It is the
  reliable route for player-targeted reports and can carry richer detail.

If both are active, a general, bug or idea report arrives twice with **different** data on each side. Neither
route is a superset of the other.

## Decision

The ingest treats both as inputs to **one `ServerReport` row per real-world report**:

- Dedupe on `(server, reporter, type, subject, short time window)`.
- **Merge, do not choose:** keep the native system info and screenshot *and* the plugin's player-targeted detail on
  the same row. Set `Source` to reflect what contributed.
- **Keep each source's raw payload**, so a bad merge can be audited and undone.

Both routes go through **one ingestion service**, so the rule lives in one place.

## Options Considered

### Option A (chosen): Merge into one row

| Dimension | Assessment |
|-----------|------------|
| Complexity | Medium - needs a match rule and per-source raw retention |
| Data fidelity | Highest - nothing from either source is lost |

**Pros:** admins see one report with the full picture; no duplicate noise in the list or unread badge.
**Cons:** the match heuristic can mis-merge or fail to merge.

### Option B: Prefer the plugin's row, drop native

**Cons:** loses the native screenshot and system info. Rejected.

### Option C: Prefer the native row, drop plugin

**Cons:** loses player-targeted detail, the main reason the plugin exists. Rejected.

### Option D: Keep both as separate rows and cross-link them

**Pros:** never mis-merges.
**Cons:** every duplicated event shows twice, inflating the list and the unread badge. Rejected as noisy.

## Trade-off Analysis

The two sources are complementary, not redundant, so any "pick one" option destroys real data (B, C), and option D
avoids the matching risk by pushing the cost onto every admin, every time. Merging accepts a small, auditable
matching risk to keep the data complete and the inbox clean.

## Consequences

- **Easier:** one coherent report per event, with all available detail.
- **Harder:**
  - The match key is a heuristic. Two genuinely distinct reports with the same reporter, type and subject inside
    the window would merge. We have no real traffic to tune the window against, and we cannot generate any
    (see the plan's testing limits), so the window is provisional. Per-source raw payloads are what make a wrong
    merge recoverable.
  - The entity needs per-source raw payload retention, not a single raw field.
- **To revisit:** the time window and the match fields, once the first real reports from a consenting customer
  server exist.
- **Dependency:** the plugin side is blocked on the plugin session's handshake, capabilities and reports command.
  The path activates only when the plugin's handshake positively reports a `reports` capability (fail closed).

## Action Items

1. [ ] Build a single ingestion service used by both the native endpoint and the plugin consumer.
2. [ ] Store a raw payload per source on `ServerReport`.
3. [ ] Tests for merge, non-merge (different reporter/type/subject/outside window), and audit of both raw payloads.
4. [ ] Revisit the match window after real traffic exists.
