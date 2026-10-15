# 0006. Worker log forwarding over MassTransit and a per-source diagnostic log model

- Status: Accepted
- Date: 2026-10-14
- Deciders: Scott Blomfield
- Related: `docs/changes/001-read-only-diagnostic-logs/`, [ADR-0001](0001-public-report-ingest-via-panel-with-per-server-url-token.md), [ADR-0004](0004-companion-plugin-rcon-pull-dormant-hooks-signed-updates.md)

Options and trade-offs below are my analysis (proposed); the choice to use RabbitMQ was the owner's suggestion on 2026-10-14 and is to be confirmed by accepting this ADR.

## Context
A read-only log endpoint must return logs from the Api, Panel, Worker and the plugin on each game server through one door (the Panel; the Api is unpublished, ADR-0001). The Worker has no inbound interface: it is a message consumer and RCON client. The plugin follows ADR-0004 (RCON pull, bounded buffers drained by sequence cursor, dormant between calls). Callers need to know with certainty that they missed nothing, and an outage in the transport must be visible rather than look like silence.

## Decision drivers
- Reuse existing channels; no new network exposure or port.
- Gap-free, verifiable paging (the consumer is an AI assistant that cannot eyeball completeness).
- The Worker must never be slowed or blocked by diagnostics.
- Zero idle cost on the game server (ADR-0004).
- Failure of the transport is itself something to diagnose.

## Options considered
### Option A: Worker publishes `WorkerLogBatch` to RabbitMQ; the Api keeps a per-instance ring (proposed choice)
- For: existing transport and contract conventions; Worker only publishes (fire-and-forget, bounded, drop-oldest); sequence numbers assigned at the origin make loss visible; no Worker inbound surface.
- Against: depends on RabbitMQ being healthy, which is often the thing being diagnosed (accepted and documented: the endpoint reports the Worker as unavailable, not empty); the competing-consumer queue means more than one Api instance would split batches; risk of a publish→log→publish feedback loop (mitigated by timer batching, bounded buffers and excluding `MassTransit.*` below Warning from forwarding).

### Option B: Api pulls from the Worker with a request/response message (like `SendRconCommand`)
- For: pull model matches ADR-0004; no continuous traffic.
- Against: needs a fanout request to every Worker and aggregation of N replies; still RabbitMQ-dependent; Worker must answer while possibly overloaded; the Api cannot see Worker logs from before its last query unless the Worker buffers, which it would anyway.

### Option C: Worker exposes an HTTP endpoint
- For: independent of RabbitMQ.
- Against: new inbound surface and port on a component that has none; needs routing from Api to the right instance; contradicts the "Api/Worker unpublished" posture.

### Option D: Ship logs to an external system (Seq, Loki)
- For: durable and searchable.
- Against: new infrastructure and dependency; explicitly out of scope in the spec.

## Decision
Proposed: Option A, with these rules:
1. **Source model.** Sources are `api`, `panel`, `worker`, and `plugin:<serverId>`. Every entry has `source`, `instanceId` (process boot id), and `seq`, monotonic by one per entry within `(source, instanceId)`, assigned where the entry is created. Gap and reset detection follow the plugin's existing `ArchonRing` drain contract.
2. **Contract.** `WorkerLogBatch(InstanceId, FirstSeq, LastSeq, Entries)` in `RustArchon.Messaging.Contracts`. Entries are already redacted at the Worker. A new field means a new optional parameter with a default (the repo's existing compatibility pattern).
3. **Delivery semantics.** At-most-once, best effort. A failed publish drops that batch; the sequence gap shows it. The Worker never blocks on, retries indefinitely, or logs about forwarding failures at a rate that feeds itself.
4. **Plugin logs** are pulled on demand through `SendRconCommand` (non-interactive) with the plugin's `archon.logs.drain`; they are not forwarded continuously (ADR-0004).
5. **Credential type.** The log-read token is a new credential: separate from the internal API key and every other secret, stored only as a SHA-256 hash, valid only for the log endpoint.
6. **Topology constraint.** One Api instance consumes `WorkerLogBatch` today. Scaling the Api out requires revisiting this (a fanout queue per instance, or a shared store).

## Consequences
- Positive: no new exposure; verifiable completeness; Worker unaffected by diagnostics load; consistent with existing patterns.
- Negative / accepted trade-offs: Worker logs vanish from the endpoint exactly when RabbitMQ fails; in-memory only, lost on restarts; extra message volume at Debug.
- Revisit if: the Api is scaled to multiple instances; RabbitMQ outages repeatedly hide the Worker; durable log retention is wanted (then Option D); Worker log volume strains the broker.
