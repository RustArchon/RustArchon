# 001. Read-only diagnostic log endpoint — Plan

- Status: Approved (implementation done except the items under "Not done / not verified")
- Spec: `spec.md` (Approved 2026-10-14)
- ADRs: [0006](../../adr/0006-worker-log-forwarding-and-diagnostic-log-source-model.md) (Accepted)

## Approach
Every process (Api, Panel, Worker) gets an in-memory, bounded, Debug-floor log buffer exposed through a new **app-agnostic** `RustArchon.Diagnostics` project: a logger provider, a ring buffer that assigns per-source sequence numbers, a secret redactor, and the query/paging/summary logic. Nothing in it knows about Rust or tenants (an upstream-to-JumpStart candidate later, per the JumpStart reusability rule).

Sources and how each reaches the one endpoint:

| Source | Where its entries live | How the Api gets them |
|---|---|---|
| `api` | Api's own buffer | local |
| `panel` | Panel's own buffer | Panel merges its buffer into the response itself |
| `worker` | Worker's buffer, plus a copy in the Api | Worker publishes `WorkerLogBatch` over MassTransit on a timer; the Api keeps one ring per Worker instance (ADR-0006) |
| `plugin:<serverId>` | the plugin's buffer on the game server | on demand: the Api sends `archon.logs.drain …` through the existing `IRequestClient<SendRconCommand>` (non-interactive) and parses the reply |

Public door: the **Panel** serves `GET /diagnostics/logs` (anonymous route, token in header `X-RustArchon-Log-Token`), exactly following ADR-0001: cheap checks and a per-address throttle in the Panel, the token forwarded to the Api's `/internal/logs` (InternalApiKey scheme), verification and the secret stay in the Api. Token = 256-bit random, shown once, stored only as a SHA-256 hash, compared in constant time; created, rotated, revoked by a Site Admin. One active token. No token → feature off (404). Missing/wrong/revoked → one identical 401.

Sequence/cursor model reuses the plugin's existing `ArchonRing` drain contract (bootId, head, cursor, lost, reset): a cursor is `(instanceId, seq)` per source; the response's metadata line states per source `instanceId`, `oldest`, `head`, `returnedThrough`, `lost`, and `reset` (instance changed).

Output: NDJSON entries, then a final `{"_meta": …}` line. `mode=summary` returns counts only. `/diagnostics/logs/help` returns a plain-text/JSON description with a worked example. A Python 3 standard-library helper (`tools/diagnostic-logs/fetch_logs.py`; Python is already on the dev machine, no new dependency) pages by cursor to a file and prints a summary.

Decision logging: the Api's `ThirdPartyPluginUpdateService` routes all 25 refusals through one `Refused(code, …)` helper and the auto-updater silently skips non-ready offers; both gain Information-level decision logs (change-only logging in the auto-updater so a 5-minute pass doesn't repeat itself).

Plugin: new `ArchonRing<LogItem>` log buffer, `archon.logs.drain <bootId> <cursor> [max]`, capability `logs` in `archon.hello`, plus a bounded on-disk update-attempt journal (the plugin today persists only the single most recent third-party update, in `thirdparty-swap.txt`, and exposes it via `archon.thirdparty.status`; there is no history). Plugin redaction is its own small copy (the plugin is a single compiled file and cannot reference a library), guarded by a parity test in the style of `RustArchonZipMappingParityTests`.

Config as Platform Settings (not env vars): `LogReadFailuresPerAddressPerMinute` (default 10, fetched by the Panel like `ReportIngestLimit`). Buffer capacities and the per-call cap are code constants (5,000 entries; per-call cap 5,000), matching the spec's "fixed server-side".

## Decisions needing your approval with this plan
1. **New project `RustArchon.Diagnostics`** (project reference, no new package). Worker references only Messaging, and Messaging is a contracts library, so a shared provider needs its own home. The Worker Dockerfile will need to copy it.
2. **ADR-0006** (drafted, Proposed): the `WorkerLogBatch` message and the per-source model.
3. **Plugin release:** this change bumps the plugin version; plugin logs on prod only work once the new version is on the game server (hand-install may be needed first time, per spec Known weaknesses).
4. Not doing: upstreaming the Diagnostics project to JumpStart now (separate repo/PR). Say if you want it moved there first.

## Steps
Each step is TDD (red → green → refactor) unless marked otherwise. Project layout per `AGENTS.md` (xUnit test projects per component).

**A. Diagnostics core (new project + tests project)**
- [x] 1. Redactor — `RustArchon.Diagnostics/LogRedactor.cs`. Cases: bearer tokens, `password=`/`pwd=`/`secret=`/`key=` values, connection strings, `Authorization` headers, RCON passwords, URLs with `token=`/userinfo, long base64/hex runs after key-like names; tested with Debug- and Information-style sample messages and exception text.
- [x] 2. Ring buffer with per-source sequence + instance id + eviction and gap math — `LogRing`, `LogEntry`. Cases: bounded capacity, monotonic seq, `lost` count when cursor evicted, `reset` on instance change, concurrent writers.
- [x] 3. `ILoggerProvider` writing to the ring, Debug floor, redacting on write (message, exception text, scope/state properties as fields), category exclusion hook.
- [x] 4. `LogQuery` + `LogQueryEngine`: min level, category, time range, text, structured property filters, count cap, per-source cursors, cursor-reached semantics under filtering, summary mode, NDJSON writer with `_meta` line.

**B. Worker → Api forwarding**
- [x] 5. `WorkerLogBatch` contract in `RustArchon.Messaging/Contracts` (instanceId, first/last seq, entries); Worker `LogForwarder` hosted service: timer batching (≈2 s / ≤200 entries), drops oldest on overflow (never blocks), excludes `MassTransit.*` below Warning to avoid feedback. Tests with an in-memory bus.
- [x] 6. Api `WorkerLogBatchConsumer` + `WorkerLogStore` (one ring per Worker instance, bounded number of instances, seq-gap detection incl. lost batches); "Worker unavailable" when nothing received recently.

**C. Api endpoint and token**
- [x] 7. Token service: generate/hash/constant-time verify/rotate/revoke, audit-logged; Platform-Settings-backed storage. Site-admin endpoints under the existing Platform permission (create → returns secret once; status → id + created time; revoke).
- [x] 8. `InternalLogsController` (`[Authorize(AuthenticationSchemes="InternalApiKey")]`, route `internal/logs`): off → 404, bad token → 401 uniform, query/summary/help, merges `api` + `worker` + plugin sources, names unavailable sources with reasons, access logging (token id + filters, not response). Test that the token is accepted by no other controller.
- [x] 9. Plugin source adapter: given serverId + cursor, send `archon.logs.drain` via `IRequestClient<SendRconCommand>` (Interactive:false), parse the drain envelope, map to `LogEntry`; unavailable reasons for: plugin not listed, no `logs` capability (read from stored handshake), not connected, timeout, parse error.

**D. Panel door**
- [x] 10. `LogReadProxy` + routes `GET /diagnostics/logs` and `/diagnostics/logs/help`, rate-limit policy `log-read` (failed-attempt-weighted, keyed on real client address; limit from `LogReadFailuresPerAddressPerMinute`), streaming pass-through, `Cache-Control: no-store`, merges the Panel's own buffer, forwards token in header to Api. Verify the client-address handling behind Cloudflare (forwarded headers config) and note result in the PR.
- [ ] 11. Panel Platform Settings UI section: create/rotate/revoke token, show id and created time, secret once with copy button. (UI: bUnit/component test + live browser check with seeded data per project practice.)
- [x] 12. Register providers: Api, Panel, Worker `Program.cs` add the Diagnostics provider; Worker Dockerfile copies the new project.

**E. Decision logging (Api)**
- [x] 13. `ThirdPartyPluginUpdateService.Refused` logs server, plugin, code, message at Information (Warning for `file_changed`/`timeout`); one test per code path (25 sites) asserting the log entry.
- [x] 14. `ThirdPartyPluginAutoUpdater`: log non-ready offers with state and reason on change per (server, plugin); the Debug-only refusal log becomes Information.

**F. Plugin**
- [x] 15. Plugin log ring + `archon.logs.drain` + `logs` capability in `archon.hello`; plugin-side redactor; parity test against the Diagnostics redactor's shared test vectors. Zero idle cost: no timers, no hooks.
- [x] 16. Update-attempt journal: append at each phase transition of third-party/main/updater updates (plugin class, from/to, phase, sha/size, reason, redacted URL), bounded file (last 20 attempts) in the plugin data directory, loaded at boot into the ring marked as persisted; survives reload/restart. Tests in `RustArchon.Plugin.Tests` using the existing stubs (`RustArchonThirdPartyUpdateTests` patterns). Bump plugin version; keep the Updater and signing flow unchanged.
- [ ] 17. **Compile against the real game DLLs before any live push** (standing rule): run `RustArchon.Plugin.CompileCheck` with the linuxgsm DLLs.

**G. Helper, docs, SPEC**
- [x] 18. `tools/diagnostic-logs/fetch_logs.py` (stdlib only): `--base`, token via `--token` or `RUSTARCHON_LOG_TOKEN`, per-source cursors, loops to completion, writes NDJSON to a file, prints summary and any gap/reset warnings. Tests for paging/gap handling with a local fake server (pytest-free: `unittest`).
- [x] 19. Help-response content and a short runbook `docs/runbooks/diagnostic-logs.md` (token lifecycle, example calls, how to read gap/reset/unavailable, the RabbitMQ blind spot).
- [ ] 20. `docs/SPEC.md`: review the agent-drafted version with you, add the Diagnostics/logging section, add ADR-0006 and an index `docs/adr/README.md` covering ADR-0001…0006. Set spec and plan to Complete.

## Verification
| Acceptance criterion | Verified by |
|---|---|
| Served by Panel; no new Api port | Step 10 route test; `docker-compose*.yml` diff shows no Api `ports:` |
| Missing/wrong/revoked token → same 401 | Step 8 controller tests (three cases compare byte-identical bodies/headers); step 10 pass-through test |
| No token created → 404 | Step 8 test |
| Entries from Api and Worker, tagged by source | Step 6 + 8 integration test with in-memory bus; manual check against dev stack |
| Debug vs Warning min level | Step 4 tests |
| Filters, count cap | Step 4 tests |
| Buffer bounded | Step 2 test (capacity + allocation flat after N× capacity writes) |
| Exactly-once paging under concurrent writes | Step 2 and 4 concurrency test; step 18 helper test |
| Evicted cursor flagged with lost count | Step 2/4 tests |
| Lost Worker batches visible as gap | Step 6 test (drop a batch, assert `lost`) |
| Filtered pages keep cursor continuity | Step 4 test |
| Refusal paths logged, one test per path | Steps 13–14 (25 + auto-updater cases) |
| Plugin source returns plugin entries incl. attempts | Steps 9, 15, 16 tests |
| Update attempts survive reload/restart | Step 16 test (persist, reload, drain) |
| Unavailable plugin named with reason | Step 9 tests per reason |
| Plugin redaction on the game server | Step 15 tests + parity vectors |
| No recurring plugin work | Step 15 review: no timers/hook subscriptions added; test asserts none registered |
| NDJSON + property filter | Step 4 tests |
| Summary mode | Step 4 tests |
| Help + helper script | Steps 18–19 |
| Client count limit honoured to cap | Step 4 tests |
| Secrets redacted at Debug and Information | Step 1 tests |
| Token accepted nowhere else | Step 8 test iterating the Api's controllers/endpoints |
| Failed attempts throttled by real client address | Step 10 limiter test; real-address behaviour checked by hand with forwarded headers |
| Docs: SPEC.md exists and reviewed | Step 20, your review |
| Create/view/revoke token from Panel; secret shown once | Step 7 tests; step 11 UI test plus live browser pass |
| Token use and rejections logged | Steps 8 and 10 tests |
| Unreachable Api/Worker named, not silent | Steps 6, 8, 10 tests |

Final gate before reporting done: `dotnet build` with warnings as the repo configures, the formatter/analyzers the repo defines, and all test projects green (Api.Tests, Panel.Tests, Worker.Tests, Plugin.Tests, Diagnostics.Tests), plus a live pass in the dev stack with seeded data (token create → fetch → page → rotate → old token rejected). Nothing is committed or pushed without your say-so.

## Risks
- **RabbitMQ blind spot** for Worker logs (spec Known weakness): surfaced as "unavailable", documented in the runbook.
- **Feedback loop** Worker→bus→logs→Worker: mitigated by timer-based batching, bounded drop-oldest, and excluding `MassTransit.*` below Warning from forwarding; tested in step 5.
- **More than one Api or Worker instance:** the Api's competing-consumer queue would split Worker batches across Api instances. Documented in ADR-0006 as unsupported today (single Api instance); the gap indicators make a split visible rather than silent.
- **Redaction misses.** Pattern-based redaction cannot be proven complete. Mitigations: tests with real-shaped samples at Debug/Information, redact on write and again on read, never log request bodies or headers, and the token being log-read only. Rotate the token after use.
- **Plugin delivery to prod** depends on the update path that may be failing; first rollout may be a hand-install. Plugin compile on real game DLLs is a hard gate (step 17).
- **Rate limiting behind Cloudflare:** the Panel's forwarded-header config trusts `X-Forwarded-For`; if the real address is in `CF-Connecting-IP`, the throttle would key on Cloudflare's edge. Step 10 checks this and adjusts, noted in the PR.
- **Log volume at Debug** may cover only minutes on a busy process; the cursor makes this safe but the summary will show `oldest` so truncation is obvious.
- **Cloudflare bot protection** may block the helper script; deferred per your decision.
- **Scope size:** ~20 steps across five projects and a plugin release. Suggested delivery order is A→B→C→D→E first (usable, and E may already show the cause), then F→G.

## Implementation notes (what differs from the plan above, and what is not done)

**Changes to the plan as written**
- The query-string parser (`LogQueryParser`) and a response merger (`LogNdjsonMerger`) live in `RustArchon.Diagnostics`, because both the Api and the Panel need the same parsing and the Panel has to combine its own source with the Api's answer. Not separate steps in the plan.
- `GET internal/logs/limits` and the Platform Setting `LogReadFailuresPerAddressPerMinute` (default 10) exist, as planned for the throttle's configuration.
- Framework chatter below Warning is not captured (see the live-pass note below); the spec's "every entry at Debug or above" means our own code's entries.
- The Panel keeps a small in-memory cache of tokens the Api accepted in the last 15 minutes (`VerifiedLogTokenCache`, hashes only) so it can still serve its own logs when the Api is unreachable, which the spec requires; the cost is that a token revoked while the Api is down keeps working for the Panel's own entries for up to 15 minutes.
- A Worker instance is its own source (`worker:<8 characters of its instance id>`), not one `worker` source, because Workers scale out and each has its own sequence numbers. The query parameter `worker` selects all of them.
- The spec's "newest-first" ordering was clarified to "oldest first within a response, most recent entries when there is no cursor" (cursor paging cannot be newest-first); `count` is per source.
- Source addresses use `CF-Connecting-IP` for the throttle when present (the Panel sits behind Cloudflare).
- Existing plugin tests were edited: the capability lists they assert gained `logs` (three places), and the zip tests' "nothing was written anywhere" snapshot now also ignores `update-journal.txt`, the plugin's own record of itself (it already ignored `thirdparty-swap.txt`). No assertion was weakened for a plugin or user file.
- Refusals logged by the update service are logged for every trigger; the automatic pass says the same refusal once per change (static, bounded memory), a person's click always.

**Not done / not verified**
- Step 11: the Diagnostic logs page is unit-tested (bUnit) but has not been looked at in a browser.
- Step 17: the real-game-DLL compile was not run. The plugin change touches no game types (only `System`, `System.Text.RegularExpressions`, `System.IO`), and the C# 7.3 compile check passes, but the standing rule says to do it before a live push.
- Step 20: `docs/SPEC.md` is updated, but this plan and the spec are not marked Complete until you have reviewed the result.
- (Resolved) The Postgres-backed Api tests were run once Docker was up: all 2,547 in `RustArchon.Api.Tests` pass, including the per-refusal log assertions, and all 49 in `RustArchon.Api.IntegrationTests`.
- (Resolved, with fixes) A live pass on the dev stack (Api + Panel, no Worker) was done on 2026-10-15. It found four bugs that no unit test could, all now fixed and covered by tests that reproduce them: (1) the Panel set the response status code after it had written the body, which a real response refuses (the tests used a lenient `DefaultHttpContext`; they now use a strict response); (2) the NDJSON writer wrote synchronously to the response stream, which Kestrel refuses (a new async-only stream test reproduces it); (3) the helper script dropped its `sources` filter when narrowing, so a one-source request got every source; (4) the helper narrowed paging to `worker:<id>`, which the parser and controller rejected. It also showed that at Debug the Api buffer filled with framework chatter (EF commands, Kestrel, MassTransit) in about 15 seconds, so `LogCaptureDefaults` now leaves framework categories out below Warning (our own `RustArchon.*` and `JumpStart.*` stay at Debug). Verified live: token create/replace/revoke in the browser (old token refused at once), 401 vs 404 behaviour, refusal throttle (429 after about 10 refusals), `--summary-only`, gap-free paging with a page size of 5 (every entry once, contiguous), the Worker source reported unavailable with its reason, and the Panel answering from its own logs with the Api stopped (token verified a minute earlier) and 503 for an unknown token.
- Still not exercised live: the Worker to Api log forwarding (no Worker was started, because starting one connects to the live Rusty Amigos server) and the `plugin:<id>` source (needs plugin 0.11.0 on a connected game server).
- The Updater plugin's own steps (it swaps the main plugin while the main plugin is unloaded) are not captured; the main plugin logs the outcome after it comes back.
