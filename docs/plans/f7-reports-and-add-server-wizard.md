# Plan: F7 report support and the Add Server wizard

Status: agreed with Scott 2026-09-19. **Delivery steps 1 and 2, and the RustArchon side of wizard step 5, were built on 2026-09-20** on branch
`feature/f7-reports-and-server-wizard` (umbrella + Api, Shared and Panel submodules), committed locally and not pushed. **Track B's plugin-report
consumer is not built** - see "Implementation notes" at the end for what was built, where it deviates from this plan, and what is still open.

This plan covers two related pieces of work that ship together:

1. **F7 (in-game player report) support** in the Panel, in two tracks.
2. **An Add Server wizard** that replaces the inline Add form on `/servers`.

The Carbon/Oxide plugin itself is designed in a **separate session** (see
"Boundary with the plugin session"). This plan specs only RustArchon's side of it.

## Background: what Rust gives us

Facepunch documents one delivery channel we can rely on (https://wiki.facepunch.com/rust/receiving-reports):

- `server.reportsServerEndpoint` - an HTTP URL the game server POSTs reports to.
- `server.reportsServerEndpointKey` - an optional key sent with each report. **We do not use it**;
  the secret goes in the URL instead (see "Auth").
- The POST is `application/x-www-form-urlencoded` with fields `data` (JSON), `userid` (reporter SteamID)
  and `key`.
- `data` JSON: `Subject`, `Message`, `Type` (0 General, 1 Bug, 2 Cheat, 3 Abuse, 4 Idea),
  `TargetReportType`, `TargetId`, `TargetName`, and `AppInfo` (client/system info, server address,
  level position, minutes played, and a Base64 JPG `Image` for offensive-content reports).
- `server.printReportsToConsole` prints a minimal summary to the console. **Its line format is not
  documented. Do not build a console parser blind.**

**Known risk, unverifiable by us:** the wiki notes that player-targeted reports (cheat/abuse) do not seem
to reach the endpoint - it "only receives other reports ie; general, bug, ideas". We cannot test this,
because filing a real F7 report sends it to Facepunch. This is why the plugin (Track B) is the reliable
route for player-targeted reports, and why the Panel must not promise native forwarding delivers them.

## Decisions (confirmed by Scott)

The reasoning for the three architectural decisions lives in ADRs; read those for *why*. Summaries only here:

- **[ADR-0001](../adr/0001-public-report-ingest-via-panel-with-per-server-url-token.md)** - The Panel is the
  public ingest door (`POST {PanelBaseUrl}/ingest/reports/{serverId}/{token}`), proxying to an Api internal
  endpoint. The secret is a per-server random token in the URL (no separate key convar), authenticated before the
  body is read, stored encrypted and reversible, rotatable. Visible to Tenant Owner + acting-as Site Admin (not
  Site-Admin-only). Signed-token/public-key auth deferred; verification stays pluggable. Required test: server
  A's token is rejected at server B's URL.
- **[ADR-0002](../adr/0002-wizard-connection-test-uses-the-normal-worker-connection.md)** - The wizard's
  connection test is the normal Worker connection (create the server, watch live status); Discard removes a
  half-created server. No separate probe, because it would put the plaintext RCON password on the bus.
- **[ADR-0003](../adr/0003-merge-native-and-plugin-reports-into-one-row.md)** - Native and plugin reports for the
  same event merge into one row (per-source raw payloads kept), rather than one source winning.

Smaller decisions not worth an ADR:

- **Screenshots:** the original is stored in Garage via `IObjectStorage`; the report row keeps the object key;
  the Panel displays it through an authenticated Panel endpoint (Garage stays internal). No size cap.
- **RCON frames are already hidden from tenants.** Non-interactive frames are filtered server-side (REST
  `GET {id}/events` and `RconHub`); only a Site Admin acting as the tenant sees them. So RCON reads sent as
  **non-interactive** are not a tenant-visible leak. Residual, accepted: the frame is still persisted in
  plaintext in `RconEvent`, visible to an acting-as Site Admin (who can already see the token).
- **Plugin repo:** the plugin has its **own development repo** (`RustArchon.Plugin`) and is
  **hosted/distributed by the Panel**. Plugin reports use RCON pull (Worker -> Api -> Panel), not HTTP push, so
  the public ingest route is for the **native endpoint only**.
- **The token is part of the URL, so exclude the ingest path from our own request logging** (ADR-0001).

## Architecture

```
Native path
  Game server --POST form (data,userid)--> Panel /ingest/reports/{serverId}/{token}   (public, anonymous)
                                             | authenticates token BEFORE reading the body; enforces body ceiling
                                             v
                                           Api /internal/reports/ingest   (InternalApiKey, never published)
                                             | verifies token (fail closed), parses, dedupes, persists
                                             +--> Garage: original screenshot
                                             +--> RconHub push to the Panel UI

Plugin path (Track B, RustArchon side)
  Plugin ring buffer <--RCON poll (cursor)-- Worker --ServerReportReceived--> Api consumer --> same store
```

## Shared foundation

**Data (Api)**
- New tenant-scoped `ServerReport` entity, indexed on `(TenantId, RustServerId, ReceivedAtUtc desc)`.
- Fields: reporter SteamID + name, target SteamID + name, type (General/Bug/Cheat/Abuse/Idea), subject, message,
  position, minutes played, `Source` (Native | Plugin), a structured plugin-detail blob (stored, not
  interpreted), screenshot object key, **raw payload per source** (ADR-0003), parse-failed flag, status (New/Reviewing/Actioned/
  Dismissed), reviewed-by, reviewed-at.
- `RustServer.ReportsSecret`: encrypted with `IApiKeyProtector` (new purpose), null until first generated, so
  existing servers accept nothing until an admin enables forwarding (fail closed).
- One EF migration. **Scott must stop Visual Studio before it runs.**

**Ingest behavior**
- Fail closed: accept only if the server exists, has a configured secret, and the token matches. Unknown
  server, no secret, and wrong token all return the **same generic rejection**.
- Lenient parsing: `Type` accepts int or string, unknown fields ignored, everything except serverId/token is
  optional, the raw `data` JSON is always kept. A report that fails to parse is stored and flagged, not dropped.
- Rate-limit per server. Limits are Platform Settings, not hardcoded.
- Verification is **pluggable** (a request may later carry a signature; the Api picks the check by what it
  presents).
- Screenshot: decode the Base64 image, store the original in Garage, keep only the key on the row, delete by
  prefix when the server is deleted.

**Read/action endpoints (Api)**: list (paged; filter by server/type/status/target), detail, screenshot fetch,
status action, key get + rotate. All tenant-scoped. A status change is an *action* (modal confirmation), not an
edit page. `RconHub` pushes `ReceiveServerReport` to clients watching a server.

## Track A - script-less

**Panel**
- Public ingest route (above); one generic status for every rejection, 200 for accepted.
- Authenticated screenshot endpoint `GET /reports/{id}/screenshot`: fetches from the Api with the user's token
  (tenant-checked) and streams with `Cache-Control: private`. Never a public URL.
- **Reports tab** on `ServerDetail`: a **standalone `@if` block** (not an `else if` chain - see the
  ServerDetail tab-chain gotchas), `?tab=reports`. List with type/status filters, unread badge, live updates;
  detail view with screenshot, plugin data when present, link to the reported player's record; status action.
- **Report forwarding card:** the endpoint URL with copy button; ready-to-paste
  `server.reportsServerEndpoint "<url>"`; a note to persist it with `server.writecfg` or the server config;
  Rotate action (modal - the old URL stops working immediately); a caveat that native forwarding may not
  deliver player-targeted reports (the wiki's claim) and the plugin covers them. The secret is generated on
  first view by an authorized user, not at server creation.

## Track B - RustArchon's side only

How the plugin captures `OnPlayerReported` and answers RCON is **the plugin session's job**. What we own:

- **Consumption:** the Worker polls a plugin reports command with a sequence cursor and publishes
  `ServerReportReceived`; an Api consumer persists it with `Source = Plugin`.
- **Fail closed:** the path activates only when the plugin's handshake positively reports a `reports`
  capability. Absence of the capability means inactive.
- **Dedupe/merge:** a general/bug/idea report can arrive both natively and from the plugin, carrying different
  data (native: system info + screenshot; plugin: player-targeted detail). **Merge into one row** rather than
  preferring one. Match on `(server, reporter, type, subject, short window)`.
- **Panel:** the Reports tab shows plugin-only fields when present; the forwarding card shows plugin state.

Blocked on the plugin session's handshake, capabilities and reports command.

## Add Server wizard

Replaces the **Add** path of the inline card on `/servers` (`ServersList.razor`, which currently shares one card
for Add and Edit). Dedicated page `/servers/new`, resumable at `/servers/{id}/setup`. **Edit is unchanged**
(except optionally gaining the Verify buttons - see open items). The Add Server button just navigates to the
wizard, so there is no old route to redirect.

1. **Basics** - name, description. The existing plan-limit check runs first; duplicate names get the existing 409.
2. **Connect** - host, port, RCON password, with guidance on enabling WebRCON, the launch parameters and the
   firewall.
   - **The connection test is the normal Worker connection.** Creating the server here starts it; the wizard
     shows the live `ConnectionStatus` + detail (the existing signal). A separate probe was rejected: it would
     need the plaintext RCON password to travel on the message bus, whereas today the Worker fetches it decrypted
     from the Api's internal endpoint. That stays.
   - Success: show the detected Oxide/Carbon framework and server info.
   - Failure: show the reported error with troubleshooting hints and a retry.
   - Abandon: a **Discard** button deletes the half-created server (frees the plan slot; leaves no stuck row).
3. **Integrations (skippable)** - Steam Web API key and geolocation provider/key, moved out of the add form,
   each with a **Verify** button:
   - **Steam Web API key:** a cheap authenticated call using the existing `GetPlayerBans` endpoint with a known
     public SteamID. *(Believed: invalid key -> 403, valid -> 200. Confirm the exact behavior when building.)*
   - **Geolocation key:** a lookup of a fixed public IP through the selected provider (proxycheck.io, iphub.info,
     ipinfo.io). Each signals a bad key differently, so each needs its own mapping. No player data is used.
   - Provider `None`: no button.
   - The Panel sends the typed value to a new Api endpoint and the **Api** makes the outbound call. Verifying
     does **not** save the key; it is saved only when the step is saved.
   - Reuse `ISteamApiClient` / `IGeolocationProvider`, but add **dedicated validate methods** returning a
     distinct result (Valid / InvalidKey / RateLimited / Unreachable / Unknown). The existing lookups swallow
     failures and log a warning, which cannot distinguish "bad key" from "transient error".
   - **Fail closed:** only a positive provider signal shows "verified". A timeout, rate limit or unrecognized
     response shows "couldn't verify, try again". Skipping verification still lets the user save; the field is
     shown as unverified.
   - Abuse guards: only fixed provider hosts are ever called (never a user-supplied URL, so no SSRF); the
     endpoint is authenticated and rate-limited so it cannot become a key-testing service.
4. **Report forwarding** - shows the URL with copy and paste instructions. A **Verify** button asks the Worker
   to read `server.reportsServerEndpoint` back over RCON and compare it to our URL. Sent as a **non-interactive**
   command. *(Believed: a bare convar name returns its current value. Confirm against a real server - it is a
   harmless read and files nothing with Facepunch.)*
5. **Plugin (optional)** - explain what it adds; the user accepts or skips. If accepted: show the install path for
   the detected framework (Panel-hosted download), then poll the existing plugin list and the handshake, showing a
   live checklist (installed / responding / capabilities available / version current). The wizard **only
   consumes** a plugin-status surface; what "configured correctly" means is defined by the plugin session.
6. **Updates (added 2026-09-20, only when the plugin was accepted)** - every update-related setting on one page, each with a plain
   explanation of why someone might want it: "Allow updates from this Panel", "Update automatically", and a note on the
   Updater helper plugin (see the companion plugin plan, "Automatic updates"). Skippable; changeable later on the Plugins tab.
7. **Done** - summary and a link to the server.

A skipped or failed step never blocks finishing, **except step 2** - the server must connect to be useful.

New pieces: Api - discard endpoint, verify-forwarding request + stored verification state, integration verify
endpoints. Messaging/Worker - `VerifyReportForwarding` request and result (Worker -> Api -> Panel, per the
data-flow rule). Panel - the wizard pages and a hub push for verification results. All new UI strings go through
`Localizer`.

## Boundary with the plugin session

Interface points only; everything else is that session's call:

- The native endpoint contract (above), for reference.
- The plugin's handshake/capability/reports-command surface, which RustArchon consumes (Track B, wizard step 5).
- The Panel serves the plugin download; how the file reaches the Panel and how it is signed is the plugin
  session's decision.

## Delivery order

1. **Report foundation + Track A:** entity, migration, internal ingest and Panel proxy, screenshot handling,
   Reports tab, forwarding card. *(Stop VS before the migration.)*
2. **Wizard steps 1-4:** basics, connect and test, integrations with Verify buttons, report forwarding with
   verification. No plugin dependency.
3. **Wizard step 5 + plugin-report consumer (Track B):** blocked on the plugin session.

Repo workflow: submodule PRs first (Api, Shared, Messaging, Worker, Panel as touched), then one umbrella
submodule-bump PR. No PR is opened until Scott has tested and explicitly asks. Every PR needs a
feature/fix/chore/breaking/docs label at creation. Tests ship alongside each layer, in every project.

## Tests

Shipped with each layer. Required cases include:

- Ingest: valid, malformed, oversized, missing/wrong token, no-secret server, all giving the generic rejection;
  **server A's token rejected on server B**; lenient parsing; raw payload kept; parse-failed flagged.
- Screenshot storage and deletion-with-server; tenant isolation on every read endpoint; key/rotate permissions.
- Dedupe/merge of native + plugin reports.
- The verify-forwarding RCON command is sent **non-interactive** (that flag is what keeps it tenant-invisible).
- Integration Verify: each provider's valid / invalid-key / transient / unknown mapping; fail-closed behavior.
- Wizard step transitions, Discard cleanup, plan-limit gate, duplicate-name handling.

**Limit on what tests prove:** we cannot send real F7 reports, so fixtures are built from the documented wiki
schema. They prove our parsing, auth and dedupe - **not Rust's real behavior**. The raw payload kept on every row
is what lets the first real report from a consenting customer server settle the open questions.

## Open items

1. **Abandoned wizard servers.** Suggested: a visible "setup incomplete" state that lets the user resume, and no
   auto-delete in v1 (silently deleting a customer's server is worse than a leftover row). Not yet confirmed.
2. **Verify buttons on the Edit Server form.** Suggested: include them (the same component). Not yet confirmed.

## Unverified assumptions to confirm when building

- Steam invalid-key response code (believed 403).
- Reading a Rust convar by bare name over RCON returns its value (believed yes).
- The Oxide `OnPlayerReported` hook signature and that it carries target details for player reports (from a
  search summary; plugin session owns confirming this).
- Whether the native endpoint really omits player-targeted reports (the wiki's claim; unverifiable by us).

## Implementation notes (2026-09-20)

Built and tested (Api 1,386 unit + 49 integration, Panel 421, Worker 285, all passing). Nothing was run against a live server or a live
database: the migration is generated (`20260920142426_AddServerReports`) but **not applied**, and no Api/Panel process was started.

**Deviations from the plan above, and why**

- **Verifying forwarding needs no new Worker or Messaging pieces.** The Api reads `server.reportsServerEndpoint` back with the existing
  `SendRconCommand` request/response (sent non-interactive) and compares the reply itself. Only the Api's `ReportForwardingService.Interpret`
  parses it. Nothing in `RustArchon.Worker` or `RustArchon.Messaging` changed, so neither submodule has a branch.
- **Discard reuses the existing `DELETE /api/rustservers/{id}`**, not a new endpoint. A new `ServerReportCleanupConsumer` (on
  `ServerLifecycleChanged` = Deleted) removes the server's reports and screenshots.
- **The wizard polls the server's status once a second** rather than subscribing to the hub. Simpler, and it survives a page refresh.
- **Changing a report's status is a direct button, not a confirmation modal.** It is trivially reversible (there is a Reopen button); a
  modal would only add friction. Rotating the report address and discarding a half-made server *do* confirm.
- **Live push carries no report.** `RconHub` sends `ReceiveServerReportsChanged` with no payload: the group it goes to is everyone who may see
  the server, wider than everyone who may read its reports, so the Panel re-reads through the endpoint that checks the permission.
- **Rate limits are Platform Settings** (category Reports; done 2026-09-20): `ReportsPerAddressPerMinute` (Panel, 120 per address on `/ingest/reports`),
  `ReportsPerServerPerMinute` (Api, 60, applied only after the secret is verified) and `IntegrationChecksPerUserPerMinute` (20). One number for everyone;
  per-server limits are deliberately a later step. The Panel cannot read Platform Settings itself, so it asks the Api (`GET /internal/reports/limits`) and
  remembers the answer for a minute.
- **The forwarding card is collapsed on the Reports tab** and only fetches (which mints the secret) when opened; the wizard opens it at once.
- **`GET .../report-forwarding` mints the secret on first read**, as planned - and nothing else does (a check on a server that has never had
  one answers "not set" without minting).
- **Geolocation Verify is only as good as the providers' documentation.** None of the three document how a bad key is signalled. iphub.info
  and ipinfo.io are "valid" only on a 200 with the fields an authorised call returns; proxycheck.io answers a good and an unrecognised key
  identically, so it can only ever be refused or "could not confirm". None of it has been run against a live provider.

**Not built: Track B's plugin-report consumer (wizard step 5 is built).** The plugin's handshake reports no `reports` capability and there is
no reports command to poll, so there is nothing for the Worker to consume yet. What *is* done and tested: `ReportIngestService.IngestPluginAsync`
and the merge rule (ADR-0003), so once the plugin exposes reports the Worker poll and an Api consumer are small. Wizard step 5 uses the
plugin machinery that already exists (plugin list, `plugin-status`, the signed download, `PluginSigningStates`).

**Still open**

1. Abandoned wizard servers: a "Finish setup" link now appears on any enabled server that is not connected (no persisted "setup complete"
   flag, no auto-delete). Whether that is enough is unconfirmed.
2. ~~Rate limits as Platform Settings~~ - done (above).
3. The unverified assumptions listed in the section above, especially what `server.reportsServerEndpoint` prints and how each geolocation
   provider signals a bad key.
