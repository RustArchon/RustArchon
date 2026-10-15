# 001. Read-only diagnostic log endpoint — Spec

- Status: Approved
- Date: 2026-10-14
- Related: [ADR-0004](../../adr/0004-companion-plugin-rcon-pull-dormant-hooks-signed-updates.md) (RCON pull, capability-gated, bounded buffers with sequence cursors), [ADR-0001](../../adr/0001-public-report-ingest-via-panel-with-per-server-url-token.md) (Panel is the one public door; the Api never publishes a port)

## Problem
Production plugins are not updating on a game server and there is no way to see why without logging in to the prod host. Logs exist only on the console of each process, are not retained, and are not queryable. The owner will not grant SSH or login access, but wants an assistant to be able to read the relevant logs and help diagnose.

## Desired behavior
- A read-only HTTPS endpoint is reachable at the Panel's public address. The Api stays unpublished; the Panel forwards authorised requests to the Api over the existing internal channel, as ADR-0001 does for report ingest.
- The endpoint returns recent log entries from the Api and the Worker and the Panel itself, each tagged with its source.
- Each process keeps a bounded window of its most recent log entries in memory. The Worker's entries reach the Api so one query covers both. Entries are lost on restart; that is accepted.
- **Plugin logs.** The RustArchon plugin (main and Updater) on a game server is a further log source, addressed per game server. The plugin keeps a bounded buffer of its own log entries, including a record of every update attempt: which plugin, from/to version, each step reached, the outcome, and the reason for any failure. A request for a plugin source is answered on demand by asking the plugin through the existing Panel -> Api -> Worker -> RCON path; the plugin does nothing between requests beyond recording into its buffer. Plugin entries use the same fields, filters, summary mode, cursor paging and gap reporting as the other sources.
- **Decision logging on the plugin-update path.** Whenever the Api decides whether a plugin update is offered, started, skipped or refused (the plan gate, server opt-in, days-before-wipe gate, download lookup, validation, and the auto-updater), it logs the decision at Information or above, naming the server, the plugin, the gate that refused, and the reason, with the same wording regardless of whether a person or the auto-updater triggered it. A skipped update is no longer visible only at Debug with a bare result code.
- **The plugin's update-attempt record is new.** The plugin keeps no update-attempt record today; this change introduces it.
- **Update attempts survive reloads.** An update reloads the plugin, which would wipe an in-memory buffer at exactly the moment it matters. The most recent update attempts are therefore kept on the game server's disk (bounded in number and size) and are returned by the plugin after it reloads or the server restarts.
- **Plugin availability is explicit.** A server whose plugin is absent, too old to support log retrieval, unreachable, or has not positively reported the log capability is reported as such by name and reason, never as an empty log (fail closed, per ADR-0004).
- Every entry carries a sequence number that increases by one per entry within its source (Api, Worker, Panel, and each game server's plugin) and is assigned where the entry is created. Callers page forward by passing the last sequence number they received for a source ("after"), never by timestamp. Each response states, per source, the highest sequence number returned and the oldest one still held, and explicitly flags a gap whenever entries after the caller's cursor are no longer available (evicted, or lost in transit from the Worker). A caller can therefore tell with certainty whether it has seen every entry, or exactly how many it missed.
- Callers filter by source, minimum level (the client chooses its own verbosity per request, down to Debug), logger category, time range, free-text match, and maximum count. Each process therefore captures entries at Debug or above into its buffer, regardless of what its console shows. Count applies per source (a cursor is per source), with a documented default and hard cap. A request with no cursor returns each source's most recent matching entries; a request with a cursor returns the entries after it. Within a response, entries are ordered by time (oldest first) so a page reads top to bottom and the next page continues from the end. (Clarified during implementation: the original "newest-first" cannot coexist with forward, gap-free cursor paging.)
- Access requires a dedicated log-read token, checked at the Panel before anything else is done. The token can do nothing except read logs. There is a single active token at a time. A Site Admin creates it (shown once), revokes and rotates it in Platform Settings. The endpoint does not exist (404) until a token has been created.
- Rejections are indistinguishable from each other (missing, wrong, revoked token all return the same response), and repeated failures are rate-limited.
- Secrets never leave the process (for the plugin: the game server), including single-use update tokens and token-bearing download URLs: values that look like tokens, passwords, API keys, RCON passwords, connection strings and signing keys are redacted before entries are stored or returned. The token is carried in a request header, not the URL, so it does not appear in proxy access logs.
- The primary consumer is an AI assistant using plain HTTP from a shell, so the endpoint is designed for that:
  - Authentication is a single request header; no cookies, redirects, browser challenge or interactive login.
  - Output is newline-delimited JSON, one entry per line, with stable field names: source, instance id, sequence, UTC timestamp, level, category, message, exception (full text, length-capped), and the structured properties (e.g. ServerId, Plugin) as fields so entries can be filtered by them.
  - Filtering by structured property (e.g. ServerId, Plugin) is supported in addition to level/category/text.
  - A summary mode returns counts by source, level and category over a range, without the entries, so the assistant can triage before pulling lines.
  - Responses are bounded in size, and the paging metadata is in a final line or header so output stays streamable.
  - A self-describing help response documents the parameters, fields and paging with a worked example.
  - A small helper script in the repo pages through the cursor to completion, writes the result to a local file, and prints only the summary, so large outputs never need to be read in full.
  - The token is given to the assistant in the conversation by the owner, who rotates it when troubleshooting ends; it is never put in a URL. The helper script takes the token as an argument or an environment variable set per command, so nothing has to be relaunched.
- The endpoint is read-only: it cannot change settings, data, or log levels.
- Every successful and rejected access is itself logged (token id, filters used, not the response).

## Acceptance criteria
- [ ] The endpoint is served by the Panel; no new port is published on the Api.
- [ ] A request without a token, or with a wrong or revoked token, is rejected with the same generic 401 and returns no log data.
- [ ] With no token created, the endpoint returns 404 (feature off).
- [ ] A valid token returns entries from the Api and from the Worker, each tagged with its source.
- [ ] A request for Debug returns Debug entries; a request for Warning returns only Warning and above.
- [ ] Filters by source, minimum level, category, time range, text and count each narrow the result correctly; count is capped.
- [ ] The buffer is bounded: after exceeding capacity, oldest entries drop and memory stays flat.
- [ ] Paging with the "after" cursor returns every entry exactly once, in order, with no duplicates and no omissions, across repeated calls while new entries keep arriving.
- [ ] If the caller's cursor points to entries already evicted from the buffer, the response flags the gap and states how many entries were lost.
- [ ] If Worker entries are lost between Worker and Api, the sequence-number gap is visible in the response; it is never silently skipped.
- [ ] Filters (level, category, text) narrow results without breaking cursor continuity: the response reports the cursor position reached, so filtered pages do not rescan or skip entries.
- [ ] For each way an update can be refused or skipped (each gate, missing download, failed validation), the Api logs at Information or above the server, plugin, refusing gate and reason; a test exists per refusal path.
- [ ] For a server running a supporting plugin, a request for its plugin source returns plugin entries, including the most recent update attempts with plugin, versions, steps, outcome and failure reason.
- [ ] After the plugin reloads (as during an update) or the game server restarts, the last update attempts are still retrievable.
- [ ] For a server with no plugin, an unsupported plugin version, or an unreachable server, the response names the source as unavailable with the reason; it is never an empty list.
- [ ] Plugin entries containing update tokens or token-bearing URLs are redacted on the game server before they leave it.
- [ ] A plugin that is not being queried adds no recurring work beyond writing into its bounded buffer.
- [ ] Output is valid NDJSON with the documented field names; a structured property filter (ServerId, Plugin) returns only matching entries.
- [ ] Summary mode returns counts by source, level and category without entries.
- [ ] The help response and the helper script exist, and the script retrieves a full cursor-paged range into a file using only a token supplied as an argument or per-command environment variable.
- [ ] The client-supplied count limit is honoured up to the hard cap and the buffer size.
- [ ] Entries containing secrets at Debug and Information level (bearer tokens, `password=`-style values, API keys, connection strings, RCON passwords) are returned redacted.
- [ ] The log-read token is accepted by no other Panel or Api endpoint.
- [ ] Repeated failed attempts from one client address are throttled, using the real client address as seen behind Cloudflare.
- [ ] `docs/SPEC.md` exists, describes the system as built (including this feature), and has been reviewed by the owner.
- [ ] A Site Admin can create, view the id of, and revoke the token from the Panel; the secret is shown only at creation.
- [ ] Token use and rejections appear in the logs.
- [ ] If the Api or Worker is unreachable, the endpoint still returns what it can and names the unavailable source rather than failing silently.

## Out of scope
- Persistent or searchable long-term log storage; shipping logs to external systems.
- Database queries, settings values, server state or any non-log diagnostics (e.g. a plugin-update "why not" summary).
- Changing log levels at runtime.
- Streaming or pushing plugin logs continuously; plugin logs are fetched on demand only.
- Logs from other plugins on the game server, and the game server's own console log.
- Fixing the plugin-update problem itself; this change only provides visibility.

## SPEC.md sections affected
- `docs/SPEC.md` does not exist. This change creates it, drafted from the existing code and reviewed by the owner, and includes a "Diagnostics / logging" section for this feature.

## Decisions (from the owner, 2026-10-14)
- Panel logs are included as a source.
- Single active token for now.
- The client requests how many entries it wants per call (up to a hard cap, and never more than the buffer holds), and pages with a sequence-number cursor so it knows it has missed nothing. Buffer capacity is fixed server-side; a caller cannot resize server memory. The owner suggested a 100k cap as the alternative; the cursor was chosen because it gives certainty at any buffer size. Buffer size and per-call cap are set in the plan, bounded by a memory budget.
- Cloudflare blocking is deferred until it actually occurs.
- Redaction is tested against Debug-level content as well as Information.
- Client chooses log verbosity per request. Taken as read-time filtering over a Debug-floor buffer, not runtime reconfiguration of the processes' own log levels (still out of scope). **Confirm this reading.**
- The Panel is fronted by Cloudflare, not Authentik, so there is no SSO bypass to build.
- Worker entries are intended to travel over the existing MassTransit/RabbitMQ transport (design in the plan).
- A `docs/SPEC.md` is drafted as part of this change.

## Known weaknesses
- **Worker logs depend on RabbitMQ.** Worker entries reach the Api only via MassTransit/RabbitMQ. If RabbitMQ is down or the Worker cannot publish, the Worker's recent logs are unavailable through this endpoint, and that is exactly when they matter. No workaround is planned. The endpoint reports the Worker as unavailable (not empty) when it has received no entries recently, and anyone troubleshooting must treat a silent Worker source as a possible messaging failure, not proof of a quiet Worker.
- **Buffers are in memory.** A restart loses history, including the moments before a crash, and restarts reset sequence numbers, so a caller must treat a sequence number lower than its cursor as a restart and start over for that source (the response signals this explicitly, e.g. with a per-process instance id).
- **Plugin logs need a new plugin version on the game server.** Until the new version is installed, plugin logs for that server are unavailable. Delivering it uses the plugin update path, which may itself be what is failing; hand-installation may be required for the first rollout.
- **Plugin logs depend on the whole chain** (Panel, Api, RabbitMQ, Worker, RCON connection, plugin). Any broken link makes the plugin source unavailable, and the response should say which link failed where it can.
- **Cloudflare may block a non-browser client.** Deferred; to be handled if and when it happens.

## Open questions
None.
