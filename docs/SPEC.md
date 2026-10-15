# RustArchon — Specification

Current state of the system. Updated with every change that alters behavior, interfaces, or structure.

## Purpose
RustArchon is a multi-tenant SaaS for administering Rust (game) servers. Customers (Organizations / tenants) register servers, then use a web Panel to watch a live RCON console and chat, review player history and sessions, handle in-game F7 reports, see a map and combat log, and keep server plugins current. The platform operator (Site Admin) manages plans, billing, tickets, themes, email and the companion-plugin release/signing pipeline. An optional companion plugin (Carbon first-class, Oxide untested) supplies data vanilla WebRCON cannot: positions, combat events, base (TC) snapshots, the map image, and plugin updates.

## Components

Layout: an umbrella repo (this one) holding docker-compose, docs, tests and the plugin source, with the other projects as git submodules (`RustArchon.Plugin` and `RustArchon.Diagnostics` are plain folders in the umbrella) and `JumpStart` as a sibling framework project. Deployed as Docker containers: `rustarchon-api`, `rustarchon-worker`, `rustarchon-panel`, `rustarchon-web` (plus `watchtower` in `docker-compose.prod.yml`) on one internal network `rustarchon-net`.

### Data-flow and trust rules (cross-cutting)
- **Server data flows Worker -> Api -> Panel.** The Worker owns RCON sockets, polls, and publishes captured data on RabbitMQ; the Api consumes and persists; the Panel reads through the Api. The Panel and Api never open RCON connections ([ADR-0002](adr/0002-wizard-connection-test-uses-the-normal-worker-connection.md), [ADR-0004](adr/0004-companion-plugin-rcon-pull-dormant-hooks-signed-updates.md)).
- **The Api publishes no port.** The Panel is the one public door; anonymous inbound traffic (game-server and Stripe) terminates at Panel routes that proxy to the Api's `/internal/*` ([ADR-0001](adr/0001-public-report-ingest-via-panel-with-per-server-url-token.md)). Only `rustarchon-panel` (8080) and `rustarchon-web` (8081) publish ports to the host; Postgres, RabbitMQ, Valkey and Garage publish dev-convenience ports in `docker-compose.yml`.
- **Internal channel:** service-to-service calls carry a shared secret in header `X-Internal-Api-Key` (env `RUSTARCHON_INTERNAL_API_KEY`), authenticated by the Api's `InternalApiKey` scheme with a constant-time comparison. It yields a principal with no permission claims. Endpoints opt in with `[Authorize(AuthenticationSchemes = "InternalApiKey")]`.
- **User channel:** the Panel signs users in with ASP.NET Core Identity (cookie), exchanges the session for a JWT (JumpStart `JwtExchangeHandler`), and calls the Api through generated Refit clients (`RustArchon.Panel/Clients`). The Api authenticates with JWT bearer (`RUSTARCHON_JWT_SECRET_KEY`).
- **Secrets at rest:** RCON passwords, per-server Steam/geolocation API keys, report-forwarding tokens, plugin private keys and secret platform settings are encrypted with ASP.NET Data Protection before persisting (`IRconCredentialProtector`, `IApiKeyProtector`). RCON passwords are never in DTOs or messages; the Worker fetches the decrypted password over the internal channel.
- **Data Protection:** the Api's key ring is stored in its database (`DataProtectionKeys`, application name `RustArchon`). The Panel and Web share a separate file-system key ring (`DataProtection__SessionKeyPath`, application name `RustArchon.Session`, volume `dataprotection-keys-session`) so Web can read the Panel's session cookie. Losing the Api key ring while keeping its database makes stored RCON passwords undecryptable.
- **Fail closed:** a feature gated on plugin capability is enabled only when the capability is positively reported ([ADR-0004](adr/0004-companion-plugin-rcon-pull-dormant-hooks-signed-updates.md)); report ingest accepts nothing for a server with no secret.

### RustArchon.Api
- Responsibility: system of record and business logic: tenancy, plans/billing, servers, players, reports, tickets, themes, plugin release/signing/update orchestration; the only component that writes the `RustArchon` database.
- Interface (REST, JWT unless noted; `[Authorize(Policy=…)]` where stated):

| Area | Routes (prefix) | Notes |
|---|---|---|
| Servers | `api/rustservers` (+ `/{id}/enable`, `disable`, `command`, `events`, `players`, `players/history`, `players/inactive`, `players/{steamId}[/sessions\|/kills]`, `kills`, `serverinfo/history`, `connection-log`, `plugins`, `plugins/poll`, `plugin-status`, `plugin-settings`, `plugin/update`, `plugin/update-updater`, `setup-complete`, `plan-limit`) | tenant-scoped; `command` goes to the owning Worker over the bus |
| Per-server plugin data | `api/rustservers/{id}/` `combat`, `positions`, `bases`, `map` (`map/image`), `plugin-failures`, `plugin-updates`, `plugin-update-attempts`, `third-party-updates` (`apply`,`recheck`,`exclude`,`include`), `third-party-update-settings` (incl. whether the automatic pass will act on the server, and why not, plus last/next pass) | |
| F7 reports | `api/rustservers/{id}/reports` (list, `count`, `{reportId}`, `/screenshot`, `/status`, `/assignee`, `/notes`, `assignees`); `api/rustservers/{id}/report-forwarding` (`rotate`, `verify`) | |
| Tenancy / org | `api/organization` (+ `members`, `roles`, `invitations`, `settings`), `api/invitations`, `api/invitations/organization`, `api/account-bootstrap` (`ensure-tenant`, `claim-ticket`) | |
| Site admin | `api/admin/organizations`, `api/admin/users`, `api/admin/tickets`, `api/admin/ticket-statuses`, `api/admin/plugin` (keys, releases, sign, events), `api/admin/diagnostics/log-token` (status, create/replace, revoke the diagnostic log token), `api/platform-settings`, `api/email-templates`, `api/email-placeholders`, `api/themes`, `api/invitation-codes`, `api/plans` (+ announcements, supersede, move-subscribers) | policies `PlatformAdmin`, `ManagePlatformSettings`, `ManagePlans`, `ManageOrganizations`, `ManageBilling`, `ManageDiscounts`, `ViewReports` |
| Billing | `api/subscription` (quote, change, checkout-session, billing-history), `api/billing` (payments, reversals, credit notes, void/write-off, chargeback evidence), `api/discounts`, `api/reports` (receivables, renewals, signups, ledger, etc.) | |
| Tickets / notes | `api/tickets`, `api/notes`, `api/communications`, `api/users/me/profile` | |
| Anonymous | `api/public/{branding,plans,ticketing}`, `api/public/tickets` (+ `guest/{token}`), `api/invitations/{status,validate,redeem}` | reachable only via the Panel/Web network path; ticket submission is rate-limited and captcha-guarded |
| Internal (`X-Internal-Api-Key`) | `internal/email`, `internal/email/templated`, `internal/communications/{id}/viewed`, `internal/registrations/{userId}/discard-organization`, `internal/rust-servers/{id}` (decrypted RCON info), `internal/email-settings`, `internal/ticketing-settings`, `internal/stripe/*` (webhook secret, payments, failed payments, disputes), `internal/theme-assets/…`, `internal/reports/{serverId}` + `internal/reports/limits`, `internal/plugin/download…`, `internal/plugin/map…`, `internal/users/{id}/profile`, `internal/logs` (+ `help`, `limits`; checks the caller's log token itself) | used by Worker and Panel |
| Hub | SignalR `/hubs/rcon` (`RconHub`): `JoinServerGroup`/`LeaveServerGroup` (tenant-checked, interactive events); `JoinUnfilteredServerGroup` only for a site admin acting as the tenant | server-to-client push comes from bus consumers |

- Background services (hosted): `ServerClaimSweepService`, `PluginDataPruneService`, `PluginAutoUpdateService`, `ThirdPartyPluginAutoUpdateService`, `PluginDownloadLookupService`, `PluginFileValidationService`, `SubscriptionScheduleService`, `DunningService`, `NexusComplianceNotificationService`.
- Bus: consumes the Worker-published contracts (see Messaging) via 20 MassTransit consumers (including `WorkerLogBatchConsumer`, which keeps the Workers' forwarded log entries in memory) and publishes `ConnectToServer`, `ServerLifecycleChanged`, `SendRconCommand`, `PollServerNow`, `EmailRequested`, `SendTestEmail`, `TicketCreated`, `TicketMessageAdded`, `ServerPluginSettingsChanged`, `CommunicationDelivered`.
- Applies EF migrations at startup (`Database.Migrate()`); seeds built-in roles, plans, queues, ticket statuses, default theme, bootstrap invitation code.
- Depends on: Postgres (`RustArchon`), RabbitMQ, Valkey (optional), Garage via `IObjectStorage`, JumpStart, Messaging, Shared; Stripe, geolocation and Steam Web API providers, captcha providers, per-server uMod marketplace lookups.

### RustArchon.Panel
- Responsibility: the user-facing Blazor Server product and the only public HTTP surface for the platform; owns the Identity database.
- Interface:
  - Public anonymous routes (proxies to Api): `POST /ingest/reports/{serverId}/{token}` (F7 reports, per-address rate limit), `GET /ingest/plugin` and `GET /ingest/plugin/{serverId}/{token}` (plugin download), `POST /ingest/plugin-map` (map PNG upload, header `X-RustArchon-Upload-Token`), `POST /webhooks/stripe` (signature verified in Panel using the secret fetched from the Api), `GET /track/email/{id}.gif`, `GET /theme-assets/{themeId}/{*path}`, `GET /Culture/Set`, `GET /diagnostics/logs` and `GET /diagnostics/logs/help` (read-only diagnostic logs; token in header `X-RustArchon-Log-Token`; refusals throttled per address; see "Diagnostic logs" below).
  - Blazor pages: `/servers`, `/servers/new` (Add Server wizard), `/servers/{id}` (tabs incl. console, players, Reports, Map, Plugins, settings), `/servers/{id}/setup` (resume wizard), `/servers/{id}/players/{steamId}`; `/Organization[/Roles|/New|/Invitations/Accept]`; `/Account/{Subscription,BillingHistory}`; `/Tickets…`; billing/customer/payment report pages; `/Admin/*` (DiagnosticLogs, Organizations, Users, Plans, Discounts, Invoices, PlatformSettings, Plugin, Themes, EmailTemplates/Placeholders, Tickets, TicketStatuses, InvitationCodes, chargeback packet). Identity UI under `Components/Account`.
  - Live console: `RconHubClient` connects to the Api hub with the circuit's JWT.
- Depends on: Api (REST + SignalR), Postgres `RustArchon_Identity`, Valkey (read-only cache reads for branding), shared session key ring, JumpStart, Shared.

### RustArchon.Worker
- Responsibility: persistent-connection host: one RCON client per owned server, polling, command dispatch, email and ticket-webhook delivery. Has no database.
- Interface (bus only; no HTTP surface):
  - Consumes `ConnectToServer` (shared durable queue `rustarchon-worker-connect-to-server`, competing consumers — claims a server), and per-instance non-durable fanout queues `rustarchon-worker-lifecycle-{id}` (`ServerLifecycleChanged`), `…-command-{id}` (`SendRconCommand` -> `RconCommandResult`), `…-poll-{id}` (`PollServerNow`); durable `rustarchon-worker-email` (`EmailRequested`, 5 retries with backoff), `rustarchon-worker-test-email` (`SendTestEmail`, no retry), `rustarchon-worker-ticket-events` (`TicketCreated`, `TicketMessageAdded`; webhook delivery, same retry).
  - Publishes `RconFrameCaptured`, `ConnectionStatusChanged`, `ServerConnectionHeartbeat`, `PlayerConnected/Disconnected/Killed`, `PlayerSessionSnapshotUpdated`, `ServerInfoSnapshotCaptured`, `ServerPluginsCaptured`, `ServerPluginHandshakeCaptured`, `PluginCombatEventsCaptured`, `PluginTcSnapshotCaptured`, `PluginPositionsCaptured`, `PluginMapStatusCaptured`, `PluginUpdatesCaptured`, `WorkerDiagnosticLogged`, `WorkerLogBatch`, `CommunicationDelivered`.
  - RCON commands it issues: `playerlist`, `serverinfo`, `version`/`c.version`, `c.plugins`, the user/admin-supplied command, and the plugin protocol `archon.hello`, `archon.stats`, `archon.events.drain`, `archon.logs.drain`, `archon.positions.drain`, `archon.tcs`, `archon.map.status`, `archon.map.monuments`, `archon.updates`, plus update commands (see Plugin). Reconnects with backoff.
- Depends on: RabbitMQ, Api internal endpoints (`internal/rust-servers/{id}`, `email-settings`, `ticketing-settings`) via `IInternalApiClient`, Rcon, Messaging. Email goes through `IEmailDeliveryProvider` (SMTP, Resend, SendGrid, no-op, suppressed by `RUSTARCHON_SUPPRESS_EMAIL_DELIVERY`), chosen from Platform Settings.

### RustArchon.Web
- Responsibility: proprietary public marketing site (Home, Features, Pricing, Contact); no database, no Identity.
- Interface: Blazor pages; nav shows Dashboard vs Log In by reading (never writing) the Panel's session cookie using the shared key ring. Links into the Panel via `PanelBaseUrl`.
- Depends on: shared session key ring; Panel URL config.

### RustArchon.Rcon
- Responsibility: standalone WebRCON client library: connect, send commands, parse chat/console/player list/bans/server info/plugin lists, detect Oxide vs Carbon (`ModFrameworkKind`).
- Interface: `RustWebRconClient` plus parsers, entities and one event per entity type. No RustArchon dependency.
- Depends on: `Websocket.Client` NuGet. Used by the Worker.

### RustArchon.Messaging
- Responsibility: MassTransit message contracts shared by Api, Worker and (for some) Panel; plain records, no dependencies.
- Interface: the `Contracts/*` records named above; `RustArchonPlugin` holds plugin names, capability names, token header names (`X-RustArchon-Upload-Token`, `X-RustArchon-Update-Token`) and signing-state constants; `PollServerNowKinds` = `plugins`, `updates`.
- Depends on: nothing.

### RustArchon.Shared
- Responsibility: DTOs and shared enums (`PricingModel` Flat/PerUnit, `SubscriptionStatus`, report, plugin, billing DTOs) used by Panel and Api; `PluginZips/ZipMapping`.
- Interface: pure data contracts; `RustServerDto` never carries the RCON password.
- Depends on: JumpStart.

### RustArchon.Diagnostics
- Responsibility: app-agnostic, in-memory log capture and query shared by Api, Panel and Worker: a logger provider that keeps our own code's entries at Debug and above (framework categories only from Warning) in a bounded ring (secrets redacted on the way in and again on the way out), per-source sequence numbers with gap/reset reporting, filters (level, category, time, text, structured property), summary mode, NDJSON output, the shared query-string parser, and a merger for combining two responses. Nothing in it knows Rust or tenants (a candidate to move into JumpStart).
- Interface: `LogRing`, `RingLoggerProvider`/`AddDiagnosticRing`, `LogQueryParser`/`LogQueryEngine`, `ILogSource`, `LogNdjsonWriter`/`LogNdjsonMerger`, `LogRedactor`.
- Depends on: `Microsoft.Extensions.Logging` only. Lives as a plain folder in the umbrella (like the plugin), copied into the Api, Panel and Worker images.

### RustArchon.Plugin (companion game-server plugin)
- Responsibility: optional in-game plugin that exposes data over RCON and updates itself and other plugins. Two single-file C# 7.3 plugins (the game server compiles them): **main** `RustArchon` (`src/RustArchon.cs`) and **Updater** `RustArchonUpdater` (`src/RustArchonUpdater.cs`, Carbon/BCL APIs only).
- Interface (RCON-only console commands; reply is one JSON envelope `{v, ok, data|err, message}`; protocol version 1):
  - Main: `archon.hello` (plugin, version, protocolVersion, runtime, capabilities), `archon.config get|set`, `archon.stats`, `archon.events.drain`, `archon.positions.drain`, `archon.tcs`, `archon.map.render|status|upload|monuments`, `archon.updates`, `archon.updater.update|status`, `archon.thirdparty.update|zip|status`.
  - Updater: `archon.update <version> <url> [token]`, `archon.update.status`.
  - Own log: a bounded ring of its recent entries (everything it says, secrets removed first), read on demand with `archon.logs.drain <bootId> <cursor> [max]` (same cursor contract as the other drains, plus `oldest`); capability `logs`, listed last. Update requests, steps and outcomes are also journaled to `update-journal.txt` (newest 100) so they survive the reload an update causes. No timers or hooks of its own.
- Capabilities reported: `config`, `combat`, `tcs`, `positions`, `map`, `updates`, `updater-update`, `thirdparty-update`, plus `thirdparty-zip` when zips are supported. Hooks are dormant until a capability's switch is on. Position sampling and combat hooks are the only work between calls and are controlled by per-server switches.
  - Large payloads (map PNG) are POSTed by the plugin to the Panel `/ingest/plugin-map` with a single-use token; plugin/updater files are fetched from the Panel `/ingest/plugin…`.
- Signing: RSA-2048, SHA-256, PKCS#1 v1.5. Each deployment has its own keypair (Platform Setting `PluginSigningKey`, history in `PluginKeyHistory`); the public key is stamped into the served script. An update is accepted only if signed by the key embedded in the currently running version. The main plugin and the Updater each update the other, with backup and restore ([ADR-0004](adr/0004-companion-plugin-rcon-pull-dormant-hooks-signed-updates.md)).
- Depends on: Oxide/Carbon runtime on the game server. Tests live in the umbrella: `RustArchon.Plugin.Tests` (stubs) and `RustArchon.Plugin.CompileCheck` (C# 7.3, warnings as errors).

### JumpStart (dependency, `./JumpStart`)
- Responsibility: separate, app-agnostic GPL-3.0-or-later Blazor/ASP.NET framework supplying entity base classes with audit tracking, generic repositories, API base controllers, Refit API-client wiring, JWT authentication and token exchange, entity-permission authorization (roles, role permissions, user permissions), and multi-tenancy (`Tenant`, `UserTenant`, `TenantInvitation`, `ITenantScoped`/`ITenantScopedOptional`, `TenantSwitcher` component).
- Interface: referenced by `ProjectReference` from Api, Panel and Shared (Worker, Rcon and Messaging have no JumpStart dependency per their READMEs). RustArchon's `ApiDbContext` derives from `JumpStartDbContext`.
- RustArchon-specific behavior must not live in JumpStart.

## Key flows

**Server connection.** Panel/Api create `RustServer` -> Api publishes `ConnectToServer` -> one Worker claims it from the shared queue, fetches the decrypted RCON info from the Api, connects, and publishes status and heartbeats; `RustServer.AssignedWorkerId`/`LastHeartbeatUtc` record ownership; `ServerClaimSweepService` re-offers unowned servers; `ServerLifecycleChanged` (Updated/Disabled/Deleted) fans out to every Worker. The Add Server wizard's connection test is this normal connection, observed ([ADR-0002](adr/0002-wizard-connection-test-uses-the-normal-worker-connection.md)).

**Live console.** Worker -> `RconFrameCaptured`/`ConnectionStatusChanged` -> Api consumers persist `RconEvent` and push via `RconHub` groups -> Panel. Non-interactive (background) frames go only to the unfiltered group, admitted only for a site admin acting as the tenant.

**Commands.** Panel -> Api `POST …/command` -> `SendRconCommand` to the owning Worker's per-instance queue -> `RconCommandResult`.

**F7 reports.** Game server POSTs to `{PanelBaseUrl}/ingest/reports/{serverId}/{token}` -> Panel (rate-limited per address) forwards to Api `POST internal/reports/{serverId}` with the token in header `X-RustArchon-Report-Token`. The Api validates the token before reading the body, applies a per-server rate limit, then `ReportIngestService.IngestNativeAsync`; plugin-sourced detail enters through `IngestPluginAsync`; both merge into one `ServerReport` row with raw payloads kept ([ADR-0003](adr/0003-merge-native-and-plugin-reports-into-one-row.md)). Body ceiling 64 MiB; screenshots go to Garage. The per-server token (`RustServer.ReportsSecret`) is stored encrypted, redisplayed to the tenant Owner (and a site admin acting as that tenant), and rotatable ([ADR-0001](adr/0001-public-report-ingest-via-panel-with-per-server-url-token.md)). Reports carry status, assignee and notes.

**Plugin data.** Worker polls via `archon.*` drain commands with sequence cursors -> `Plugin*Captured` messages -> Api stores compressed chunks (`PluginCombatChunk`, `PluginPositionChunk`, `PluginTcSnapshot`), map metadata, handshake/status; `PluginDataPruneService` trims by plan `RetentionHistory`. Map image: Api asks the plugin (via Worker) to upload using a single-use token; the Panel proxies the POST; Api stores the PNG and a preview in Garage.

**RustArchon plugin update.** Admin uploads a release (`PluginRelease`, kinds main/updater, states uploaded/published/withdrawn) and signs/publishes it under the active key. Per server, "Allow updates" and "Update automatically" are on for new servers; `PluginAutoUpdateService` applies updates (optionally staged by `PluginRolloutHours`) and a platform switch `PluginAutoUpdatesEnabled` stops all automatic updates. Api mints a single-use expiring `PluginUpdateToken`, the Worker sends `archon.update <version> <url> [token]`; the Updater (or main plugin for the Updater) downloads via the Panel, verifies, swaps, confirms load and restores the backup on failure. Attempts are recorded in `PluginUpdateAttempt`; key rotation is a bridge release signed with the old key; revocation is manual. The Plugin admin page can download the plugin or updater **currently served** (built-in or published release) signed with the active key (`GET api/admin/plugin/served/{kind}/download`, audit-logged) — one file for every server, since signing is per Panel — and shows the automatic third-party pass's last/next times.

**Third-party plugin updates.** Plugins' UpdateChecker notices (`PluginUpdateNotice`) are reported via `archon.updates`. `PluginDownloadLookupService` resolves a direct download link per plugin+version via ServerArmour's marketplace index (`PluginDownloadLookup`); `PluginFileValidationService` downloads the file to inspect it (SHA-256, kind cs/zip/other, `[Info]` metadata, syntax-level checks) and keeps only the result, never the file. Applying requires three gates — plan offers it (`Plan.OffersThirdPartyPluginUpdates`), server opted in (`RustServer.ThirdPartyPluginUpdatesEnabled`), and outside the pre-wipe hold window (`ThirdPartyPluginUpdateHoldDays`, default 7; wipe = first Thursday of the month 19:00 Europe/London) — for automatic updates (`ThirdPartyPluginAutoUpdateService`, switch `ThirdPartyPluginAutoUpdatesEnabled`). A person can also apply by hand via `POST …/third-party-updates/apply`, carrying the hash they were shown. The Api sends `archon.thirdparty.update <class> <version> <sha256> <size> <url>`; the plugin downloads its own copy, applies only if the SHA-256 matches, keeps a `.bak` and restores it if the new version does not load. Zips need per-server user-supplied file mappings (`PluginZipMapping`). Each attempt is a `ThirdPartyPluginUpdate` row with state, code and message. **Diagnosable from the Panel (change 002):** one function (`ThirdPartyPluginUpdateService.GetServerRefusalAsync`) holds every server-level precondition (server enabled, plan, plugin reported and fresh, signed by a key this Panel knows and has not revoked, capable; plus opt-in and wipe hold for automatic starts) and is used by both a start and by the offers, so each offer carries the refusal `BlockCode` and a plain sentence (e.g. `not_signed_by_this_panel`: install this Panel's plugin file once). The settings read adds `AutoState` (`eligible` or the first blocking code, including `platform_switch_off`) and the automatic pass's last/next times (`ThirdPartyAutoUpdateStatus`, in memory: after an Api restart it reads "not run yet"). The server page shows a header badge when the installed plugin trusts another Panel's key.

**Diagnostic logs ([ADR-0006](adr/0006-worker-log-forwarding-and-diagnostic-log-source-model.md), [runbook](runbooks/diagnostic-logs.md)).** A Site Admin creates the one log token (Admin → Diagnostic logs; stored only as a SHA-256 hash in the `DiagnosticLogToken` platform setting; replacing or revoking it works at once). A caller sends it in `X-RustArchon-Log-Token` to the Panel's `GET /diagnostics/logs`; the Panel throttles refusals per address (Platform Setting `LogReadFailuresPerAddressPerMinute`), forwards to the Api's `internal/logs`, which alone decides whether the token is live (no token ever created: 404; anything else refused: one bare 401), serves the `api` ring, every Worker instance's forwarded ring (`worker:<id>`) and, on demand, `plugin:<server id>` through the Worker's RCON connection (`archon.logs.drain`), and the Panel merges in its own `panel` ring. Every entry carries a per-source sequence number; responses report per source `instance`, `oldest`, `head`, `returnedThrough`, `lost`, `reset`, so a caller pages without missing anything and knows exactly what it missed. Workers publish `WorkerLogBatch` over RabbitMQ every 2 s (at most once; a failed publish drops the batch and the sequence gap shows it). If the Api cannot be reached the Panel still answers from its own logs for a token the Api accepted in the last 15 minutes and names the other sources unavailable. Output is NDJSON; `tools/diagnostic-logs/fetch_logs.py` pages to completion into a file.

**Billing.** Plans -> Subscriptions -> Periods -> Invoices/Lines -> Payments/Allocations/Credit notes. `SubscriptionScheduleService` issues invoices and applies scheduled plan changes; `DunningService` moves past-due subscriptions to Suspended after the grace period. Stripe Checkout (payment mode only), Stripe Tax and dispute/refund handling go through Api services; Stripe events arrive at the Panel `/webhooks/stripe` and are recorded through `internal/stripe/*`.

**Registration and tenancy.** Sign-up in the Panel creates the Identity user and, via `account-bootstrap/ensure-tenant`, an Organization (JumpStart `Tenant`) with the user as Owner on the default plan. Registration may be gated by invitation codes (Platform Setting `InvitationCodesEnabled`). The configured admin email plus bootstrap code registers the first Site Admin and skips email confirmation. Roles are per-tenant (built-in global roles seeded) and gated by plan (`Plan.HasRoles`). A Site Admin (policy `ManageOrganizations`) passes permission-gated endpoints in any tenant via an app-level authorization handler, and can act as a tenant (token claim).

## Data model

Stores: **RustArchon** (Postgres, Api-owned via `ApiDbContext`, JumpStart base tables included), **RustArchon_Identity** (Postgres, Panel-owned via `ApplicationDbContext`), **Valkey** (cache only), **Garage** (S3-compatible objects, single bucket `rustarchon-themes` by default, accessed only through `IObjectStorage`). Tenant-scoped entities implement `ITenantScoped` and are filtered by the caller's tenant context.

### RustArchon database (Api)
| Entity | Key fields / relationships |
|---|---|
| Tenant (Organization), UserTenant, Role, RolePermission, UserRole, UserPermission, TenantInvitation | JumpStart. Roles with `TenantId` null are global built-ins |
| TenantBillingAddress, UserProfile, Note, InvitationCode | billing jurisdiction for tax; per-user profile; site-admin notes; sign-up codes |
| RustServer | TenantId, Host, Port, RconPassword (encrypted), IsEnabled, ConnectionStatus(+Detail, ChangedAtUtc), AssignedWorkerId, LastHeartbeatUtc, SteamApiKey/GeolocationApiKey (encrypted) + GeolocationProvider, SetupCompletedAtUtc, plugin switches (PluginRecordingEnabled, PluginCombatLogEnabled, PluginUpdatesEnabled, PluginAutoUpdateEnabled), ThirdPartyPluginUpdatesEnabled, ThirdPartyPluginUpdateHoldDays, ReportsSecret (encrypted), ReportForwardingVerifiedAtUtc |
| RconEvent, ConnectionLogEntry | RustServerId, captured console/chat frames (type, message, direction, interactive); connection history |
| PlayerSession | RustServerId, SteamId, DisplayName, IpAddress, Connected/DisconnectedAtUtc, geolocation (country, VPN, proxy), Steam ban/playtime info |
| PlayerKillEvent, ServerInfoSnapshot | kill feed; periodic server info |
| ServerReport | TenantId, RustServerId, Source (native/plugin), Type, Status, reporter/target Steam ids and names, Subject, Message, Position, ScreenshotObjectKey (Garage), NativePayload/PluginPayload/PluginDetailJson, ParseFailed, ReviewedBy, AssignedToUserId; ServerReportNote(ServerReportId, Content) |
| ServerPlugin, ServerPluginStatus, PluginLoadFailure | installed plugins (name, author, version, framework); handshake (protocol, version, capabilities, reported switches, SigningState, SigningKeyFingerprint); compile failures |
| PluginCombatChunk, PluginPositionChunk | RustServerId, BootId, First/LastSequence, From/ToUtc, Format, Data (compressed), PlayerIds, PrecededByGap |
| PluginTcSnapshot, PluginMap | base snapshots; map metadata (world size/seed, MonumentsJson, ObjectKey + PreviewObjectKey in Garage, upload state) and PluginMapUploadToken |
| PluginRelease, PluginKeyHistory, PluginRollout, PluginAdminEvent, PluginUpdateToken, PluginUpdateAttempt, PluginUpdateNotice | release (Kind, Version, SourceText, Sha256, State); RSA public parts + encrypted private key + State; staged roll-out; audit; single-use token (hash, expiry, redeemed); attempt (state, code); third-party UpdateChecker notices |
| PluginDownloadLookup | global (not tenant) marketplace lookup + validation result per plugin+version (DownloadUrl, FileSha256, FileKind, ValidationState); no file bytes |
| ThirdPartyPluginUpdate, PluginZipMapping, PluginUpdateExclusion | per-server apply attempts; saved zip file mapping (RulesJson, Trusted); excluded plugins |
| Plan, PlanPrice, ScheduledPlanChange | Name, PricingModel (Flat/PerUnit), MaximumServers, MaximumUsers, RetentionHistory, HasRoles, OffersThirdPartyPluginUpdates, OnePerOwner, Active, SupersededByPlanId |
| Subscription, SubscriptionPeriod | TenantId, PlanId, Start/EndDate, Status (Active, PastDue, Suspended, Ended…), reasons |
| Invoice, InvoiceLine, InvoiceNumberSequence, BlockedInvoiceIssuance | Number, Status, Issued/DueOn, Subtotal/TaxTotal/DiscountTotal/Total, AmountPaid/Credited, ProviderInvoiceId |
| Payment, PaymentAllocation, CreditNote | Amount, Status, Method, provider ids, dispute fields; allocation to invoice with reversal |
| Discount, DiscountRedemption | |
| Ticket, TicketMessage, TicketNote, TicketStatus, Queue | tenant ticketing incl. guest access token |
| Communication, CommunicationBatch | outbound email records |
| EmailTemplate, EmailTemplateTranslation, EmailPlaceholder | |
| Theme | package metadata + update-check fields; asset files in Garage |
| PlatformSetting | Key, DisplayName, Category (General, Registration, Billing, Payments, Email, Ticketing, Captcha, Plugin, Reports, Diagnostics), ValueType, Value (secrets encrypted), visibility conditions |
| DataProtectionKey | Api Data Protection key ring |

### RustArchon_Identity database (Panel)
ASP.NET Core Identity tables (`ApplicationUser`, roles, claims, logins, tokens, passkeys) plus `LegacyUserCultures`. No tenant or server data.

### Valkey
Cache of Platform Settings (written by the Api, read-through by the Panel for branding), the active theme and an app-generation marker. Optional: with no `Valkey:ConnectionString` the Api runs straight against Postgres.

### Garage
Theme package assets, F7 report screenshots, plugin map PNGs and WebP previews, keyed by object key stored on the owning row.

## External dependencies
| Dependency | Purpose | Abstraction | ADR |
|---|---|---|---|
| PostgreSQL 17 | both databases | EF Core (`ApiDbContext`, `ApplicationDbContext`) | — |
| RabbitMQ 4 | Worker/Api messaging | MassTransit + `RustArchon.Messaging` contracts | [0002](adr/0002-wizard-connection-test-uses-the-normal-worker-connection.md), [0004](adr/0004-companion-plugin-rcon-pull-dormant-hooks-signed-updates.md) |
| Valkey 8 | cache | `IPlatformSettingsCache`, `IActiveThemeCache`, `IAppGenerationCache`, Panel `IValkeyCache` | — |
| Garage (S3) | object storage | `IObjectStorage` | — |
| Rust game server (WebRCON) | console, data | `RustArchon.Rcon` | [0004](adr/0004-companion-plugin-rcon-pull-dormant-hooks-signed-updates.md) |
| Carbon / Oxide | plugin host | `RustArchon.Plugin` | [0004](adr/0004-companion-plugin-rcon-pull-dormant-hooks-signed-updates.md) |
| Stripe (Checkout, Tax, webhooks) | payments, sales tax | `IStripeCheckoutService`, `IStripeTaxService`, `IStripeRefundService`, `IStripeDisputeService`, `IStripeCredentialProvider` | — |
| SMTP / Resend / SendGrid | email | `IEmailDeliveryProvider` (Worker) | — |
| Captcha (reCAPTCHA, Turnstile) | public ticket form | `ICaptchaVerifier` | — |
| Geolocation (proxycheck.io, iphub.info, ipinfo.io) and Steam Web API | player enrichment, per-server keys | `IGeolocationProvider`, `ISteamApiClient` | — |
| ServerArmour marketplace index / uMod | third-party plugin download links and files | `PluginDownloadResolver`, `PluginFileDownloader` | — |
| GitHub Container Registry, Watchtower | image distribution and auto-update | compose files | — |
| JumpStart | framework | `ProjectReference` | — |

## Constraints
- .NET 10; Blazor Server (Panel, Web); Docker Compose deployment; images pulled from `ghcr.io/rustarchon/` in production.
- Required environment: `RUSTARCHON_JWT_SECRET_KEY`, `RUSTARCHON_INTERNAL_API_KEY`, database and RabbitMQ credentials. Everything else tunable defaults to a DB-backed Platform Setting; environment variables need prior justification.
- Plugin source must stay a C# 7.3 subset in one file per plugin (Mono compiler on the game server; ECDSA and RSA-PSS unsupported there).
- Api never reachable from outside the Docker network. Anonymous endpoints authenticate before reading bodies and return a uniform rejection for unknown server, missing secret and wrong token.
- Each mutable plugin feature requires a positively reported capability; the Panel must say what needs the plugin when it is absent.
- Position data is governed as sensitive: retention bounded by `Plan.RetentionHistory`.
- Third-party plugin files are never stored or redistributed; downloads are https-only, redirects checked, public addresses only.
- Back up `postgres-data` with the Api's Data Protection keys (now in Postgres) and `garage-meta` with `garage-data` together.
- Diagnostic logs are in memory only (about 5,000 entries per source, lost on restart) and Worker logs depend on RabbitMQ; the Api must be a single instance for Worker log batches to reach one consumer (ADR-0006).
- Horizontal scaling: Workers scale freely (competing consumers); Panel/Web session key ring is a local volume and must move to shared storage before more than one replica.
- Garage requires a one-time manual layout/bucket/key bootstrap after first start.
- Email delivery is a no-op until a provider is configured in Platform Settings (the first admin registration skips confirmation to make that possible).
- Licensing: Api, Panel, Worker, Rcon, Messaging, Shared are AGPL-3.0-or-later; Web is proprietary; JumpStart is GPL-3.0-or-later.

## Decisions
See `docs/adr/README.md`. Key ADRs:
- [0001](adr/0001-public-report-ingest-via-panel-with-per-server-url-token.md) — F7 ingest through the Panel with a per-server URL token (Accepted).
- [0002](adr/0002-wizard-connection-test-uses-the-normal-worker-connection.md) — wizard connection test is the normal Worker connection (Accepted).
- [0003](adr/0003-merge-native-and-plugin-reports-into-one-row.md) — native and plugin reports merge into one row (Accepted; match heuristic provisional).
- [0004](adr/0004-companion-plugin-rcon-pull-dormant-hooks-signed-updates.md) — plugin: RCON pull, dormant hooks, signed updates (Accepted, with 2026-09-20 amendments).
- [0005](adr/0005-dvr-style-map-replay-client-clock-over-windowed-data.md) — DVR-style map replay (Proposed; no replay endpoint exists yet).
- [0006](adr/0006-worker-log-forwarding-and-diagnostic-log-source-model.md) — Worker log forwarding over MassTransit and the per-source diagnostic log model (Accepted).

Changes: `docs/changes/001-read-only-diagnostic-logs/` (spec, plan). Runbooks: `docs/runbooks/diagnostic-logs.md`.

Plans: `docs/plans/f7-reports-and-add-server-wizard.md`, `companion-server-plugin.md`, `third-party-plugin-updates.md`.

## Unverified
- ADR-0001 names the Api endpoint `/internal/reports/ingest`; the code is `POST internal/reports/{serverId}` with the token in a header. The spec follows the code.
- `AGENTS.md` says the Data Protection key ring is a local file system and `DEPLOYMENT.md`/`docker-compose.yml` still mount a `dataprotection-keys` volume for the Api, but `RustArchon.Api/Program.cs` persists keys to `ApiDbContext`. The spec follows the code; whether the volume and `DataProtection__KeyPath` env are now unused was not checked.
- Third-party update apply (`archon.thirdparty.*`, including zips) is described in the plan as never run on a real game server; its live behavior is unverified.
- Panel/Web/Worker behavior was taken from READMEs and Program.cs registrations, not exercised; "Worker has no database" and "Web only reads the cookie" come from READMEs.
- Exact contents of `Tenant`, `UserTenant` and built-in role/permission sets (JumpStart and `PermissionCatalog`) were not read in detail.
- Worker polling intervals and the exact set of RCON commands issued by `ServerConnectionActor` (beyond `playerlist`, `serverinfo`, `version`, `c.version`, `c.plugins`, `archon.*`) were not enumerated.
- Panel server-detail tab list, `RconHub` client-side event names, and the full per-controller route list (some controllers were summarized by prefix) were not exhaustively checked.
- Whether Garage uses one bucket for all object kinds: `ObjectStorageOptions.BucketName` defaults to `rustarchon-themes` and `S3ObjectStorage` uses a single bucket, but the deployment may configure a different name.
- Release states for `PluginRelease` (uploaded/published/withdrawn) and `SubscriptionStatus` values after Suspended were inferred from controller routes and a partial enum read.
- The Valkey keys written (exact key names) and the app-generation marker's consumers were not read.
- The plugin's license and repo visibility are marked undecided in `RustArchon.Plugin/README.md`; the AGPL list above covers only the other projects.
