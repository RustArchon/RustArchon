# 003. Diagnostic snapshot views, and no unreadable plugin reply goes unrecorded — Spec

- Status: Complete
- Date: 2026-10-15
- Related: change 001 (read-only diagnostic logs, [ADR-0006](../../adr/0006-worker-log-forwarding-and-diagnostic-log-source-model.md)), change 002 (Panel explains plugin-update refusals), memory rule "admin must be able to diagnose any issue from the UI"

## Problem
Diagnosing prod with the log endpoint alone has twice been slower than it should be.
1. The Api could not read the RustArchon plugin's reply to a third-party update command, so it reported "the plugin may be too old" for updates that actually succeeded, recorded nothing, and let a second click through. The raw reply that would explain it is logged nowhere, so no log query could reveal it.
2. Logs answer "what happened"; they do not answer "what is the state right now" (connection, plugin status and signing, update offers and why each is blocked, recent update attempts, recent RCON replies, settings). That state is in the database and the Panel, so a person can see some of it, but an assistant helping them cannot, and pasting screenshots is slow.

The owner wants a path that makes this far easier in the long run, including a future MCP server through which an assistant can read system state directly, without ever exposing secrets or personal data.

## Desired behavior
**A. An unreadable plugin reply is recorded, never misreported.**
- When the Api cannot understand a plugin's reply to any command it sends (third-party update, plugin update, map, and so on), it logs at Warning, naming the server and command, the start of the raw reply (bounded in length, redacted as every log entry is).
- Such a reply is never reported as "the plugin is too old". The person is told the plugin's reply could not be read, and that the details are in the diagnostic log.
- (The underlying parse bug is fixed once the real reply is seen; that fix is a follow-up bug-fix with a test built from the real reply, not part of this spec.)

**B. Diagnostic snapshot views.** The existing diagnostic door (Panel public address, same single read-only token, same throttle and audit) also serves named, read-only *views* of current system state.
- A view is a named, documented, versioned query with fixed parameters (for example a server id). There is no free-form entity or table access.
- **Allow-list, fail closed.** Each view returns only fields that have been explicitly listed as safe. A field that exists in the data model but has not been listed is never returned. Adding a column to an entity exposes nothing until someone lists it, and a test fails the build if an entity feeding a view has any property that is neither listed as exposed nor explicitly listed as hidden.
- **Never exposed, in any view:** passwords, RCON passwords, API keys, tokens and their hashes, signing private keys, report secrets, encrypted values of any kind, and billing and payment data. Player data (Steam IDs, names, chat, IP addresses) appears only inside `server-rcon-recent` frames, exactly as the Console tab already shows it; no other view returns player data.
- Initial views:
  - **server**: connection status and detail, when it last changed, owning worker, last heartbeat, enabled state, the server's update switches and hold days, the plan's feature flags that gate updates.
  - **server-plugin**: the RustArchon plugin's reported version, protocol, capabilities, signing state and key fingerprint, whether that key is this Panel's, when it last reported, and the plugin list with versions.
  - **server-updates**: each outdated plugin with its offer state, `BlockCode` and detail, file kind and validation state, exclusions, and the recent update attempts (the RustArchon plugin's own and third-party), with state, code and message; plus the automatic updater's eligibility for the server and the platform's last and next pass.
  - **server-rcon-recent**: the most recent N RCON frames for the server (command and reply, direction, time, interactive or not), bounded in count and size, with secrets redacted by the existing log redactor (chat, names, Steam IDs and IP addresses are left as they are). Includes non-interactive frames, because a command the Api itself sent is one of the things most often needed.
  - **platform-settings**: setting names and values for settings that are not secret; secret ones are listed by name as "set / not set" only.
  - **plugin-keys**: signing key fingerprints and states (active, retired, revoked) and counts of servers reporting each; never key material.
- Every view response states the time it was read, is bounded in size, and says explicitly, by name and reason, when part of it is unavailable (for example a server that is disconnected), never as an empty value.
- Views are read from the Api's own data through the Api's own services, never by the Panel reaching into the database or the Worker.

**C. Built for a future MCP server.** No MCP server is built in this change. The views are shaped so one can be added later as a thin layer.
- A machine-readable catalog endpoint lists every view: its name, version, description, parameters (name, type, required) and the fields it returns with types, in a stable documented format.
- Each view is reachable by one stable name and a flat set of typed parameters, so each maps one-to-one to a future tool. Request and response bodies are JSON with stable field names; errors use one documented shape; nothing depends on the helper script.
- Anything the helper script can do (paging, summary, writing to a file) is available over the endpoint itself.
- Adding a view or a field is backward compatible; removing or renaming one needs a new view version, and the catalog reports versions.
- The same access rules apply to a future MCP as to any client: it presents the same token, and the allow-list is enforced by the server, never by the client.

**D. Same trust boundary as change 001.** One read-only token, created, rotated and revoked by a Site Admin in Platform Settings. It can read logs and views and nothing else, and is accepted by no other endpoint. Missing, wrong or revoked tokens get the same generic rejection; repeated failures are throttled; every access is audited (token id, view and parameters, never the response). The endpoint remains 404 until a token exists. The token travels in a request header, never the URL.

## Acceptance criteria
- [ ] A plugin reply the Api cannot parse is logged at Warning with server, command and a bounded, redacted start of the reply; the person is not told the plugin is too old; a test per command path uses a non-JSON reply.
- [ ] Each initial view returns only its listed fields; a test enumerates each exposed entity's properties and fails if any is neither exposed nor explicitly hidden.
- [ ] No view returns passwords, RCON passwords, API keys, tokens or hashes, key material, report secrets, encrypted values or billing or payment data (test with seeded values for each).
- [ ] The server-rcon-recent view returns recent frames including non-interactive ones, bounded in count and size, with secrets redacted and chat, names, Steam IDs and IP addresses left intact.
- [ ] Views for a disconnected server, a server with no plugin, and an unknown server each say so by name and reason, not as an empty result; an unknown id is a 404-style answer that does not reveal whether other ids exist.
- [ ] Every view response includes the time it was read and is bounded in size.
- [ ] The catalog lists every view with name, version, description, typed parameters and typed return fields, and a test fails if a view exists that is not in the catalog or vice versa.
- [ ] A client given only the catalog can call every view without reading source or prose documentation.
- [ ] The token for views is the change 001 token; no token (or a wrong or revoked one) gets the same generic rejection as for logs; with no token the endpoint is 404.
- [ ] The token is accepted by no endpoint outside the diagnostic door.
- [ ] Access to a view is audited with the token id, view name and parameters, not the response; repeated failures are throttled.
- [ ] Views read through Api services; the Panel serves them by forwarding, as it does for logs, and publishes no new Api port.
- [ ] Documentation and the runbook describe the views, the allow-list rule and how to add a view or field safely.

## Out of scope
- The MCP server itself, and any tool-calling client.
- Write access of any kind, including changing settings, replaying commands or sending RCON commands.
- Entity-wide or table-level reads; players' personal data; billing, tickets, reports, and tenant business data.
- The fix to the reply-parsing bug (follows once the real reply is seen).
- Changes to log capture, retention or redaction rules from change 001.

## SPEC.md sections affected
- Diagnostics: add the snapshot views, the allow-list rule, the catalog, and the never-exposed list.
- Plugin updates: unreadable replies are logged, not reported as "too old".

## Open questions
All resolved by the owner on 2026-10-15:
1. One token: the existing log-read token also reads views.
2. RCON frames: chat, names, Steam IDs and player IP addresses are not redacted; only secrets are, by the existing redactor. Latest 200 frames, including non-interactive ones. (The owner's reasoning: no reason to redact chat; what the Console tab already shows is what the view shows.)
3. The token stays platform-wide and Site Admin-created. A tenant-specific token is out of scope.
4. A new ADR-0007 records the allow-list and catalog-first decision, drafted with the plan.
