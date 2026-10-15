# 0007. Diagnostic views with a deny-list attribute and a machine-readable catalog

- Status: Accepted
- Date: 2026-10-15
- Deciders: Scott Blomfield
- Related: `docs/changes/003-diagnostic-snapshot-views/`, [ADR-0001](0001-public-report-ingest-via-panel-with-per-server-url-token.md), [ADR-0006](0006-worker-log-forwarding-and-diagnostic-log-source-model.md)

The owner's decisions on 2026-10-15 were: the same single log-read token reads the views; the definitive list of what may never be sent is a `[DiagnosticsHidden]` attribute on the entity property (a deny-list, chosen over an allow-list of exposed fields); the token stays platform-wide and Site Admin-created (no tenant-scoped token); chat, names, Steam IDs and IP addresses are not redacted (secrets are); and the design should suit a future MCP server. The options and trade-offs below are my analysis (proposed); the choice is confirmed by accepting this ADR.

## Context
The log door (ADR-0006) answers "what happened". Diagnosing also needs "what is the state now": connection, plugin status and signing, update offers and why each is blocked, recent update attempts, recent RCON replies, non-secret settings. The owner wants an assistant to read that state directly, expects to add an MCP server later, and has a hard rule that secrets never leave. The system holds encrypted RCON passwords, signing keys, report secrets, billing data and tokens, in tenant-scoped and platform-wide tables. The Api is unpublished; the Panel is the one public door (ADR-0001).

## Decision drivers
- Never send a secret: one definitive, reviewable list of what is prohibited, kept next to the data it describes.
- One credential and one trust boundary (the existing token), not a second one to rotate.
- A client, including a future MCP server, can discover and use everything from machine-readable metadata, with no prose or source reading.
- Stable, versionable interface (the repo's boundaries rule: design as if an outside consumer will use it).
- Smallest thing that answers the diagnostic questions; no speculative surface.

## Options considered
### Option A: Curated, named, versioned views over listed entities, with a `[DiagnosticsHidden]` deny-list attribute and a catalog (the owner's choice)
- For: one definitive prohibited list, attached to the property it describes, found where a developer adds a column; no per-field exposure lists to maintain, so a new harmless column shows up in the views without work; one stable name and flat typed parameters per view maps one-to-one to a future MCP tool; the catalog is generated from the same reflection, so it cannot drift.
- Against (my analysis, proposed): it fails open. A new sensitive column that nobody marks is returned by every view that includes its entity, until someone notices. The owner's standing rule is that security-gated features need a positive safe signal. Mitigated below by a backstop test, which does not change the decision that the attribute is the definitive list.

### Option B: A generic entity reader over every entity
- For: nothing to add per question; broadest answer.
- Against: needs per-tenant and cross-tenant rules; behaves like a read-only database credential if the token leaks; every entity, including ones nobody has reviewed, would be in scope. (Distinct from Option A: A names the entities and the questions; B does not.)

### Option C: A read-only database role or SQL endpoint
- For: trivial to build.
- Against: no filtering at all, with encrypted values and keys in the open; incompatible with "never reach into another component's data store".

### Option D: Build the MCP server now
- For: the most direct experience for the assistant.
- Against: it adds a second client surface and a hosting and credential question before the data interface exists; it needs Option A's views underneath anyway. It can be added later as a thin layer over the catalog without changing what is visible.

## Decision
Option A, with these rules:
1. **Views over named entities, not an entity reader.** A view is a named, versioned, read-only query with fixed typed parameters and a typed response, built from a stated list of entities plus computed parts. There is no entity-name or table-name parameter.
2. **The definitive prohibited list is an attribute.** `[DiagnosticsHidden("reason")]` on an entity property means that property is never returned by any view. A view returns a listed entity's other properties. Hidden properties include the RCON password, report secrets, API keys, tokens and their hashes, key material, encrypted values and billing and payment fields, and anything else the owner marks. Navigation properties are not followed unless a view names them.
3. **Backstop (proposed by me; not the decision itself).** A test fails the build if a property of a viewed entity looks secret (by the log redactor's name list, an encrypted column, or a byte array) and is not marked hidden, and the seeded-secret tests in each view check that no secret value appears in the output. These make an unmarked sensitive column loud; they do not replace the attribute as the list.
4. **Catalog.** `GET /diagnostics/views` returns every view's name, version, description, parameters (name, type, required) and return fields with types, generated by reflection over the viewed entities minus the hidden properties plus the computed parts. A test asserts the catalog matches what each view actually returns.
5. **Envelope.** Every view answers `{ view, version, readAtUtc, data, unavailable[] }` with `unavailable` naming each part that could not be read and why (never an empty value standing in), bounded in size. Errors use one shape. Adding a field is backward compatible; renaming or removing one is a new view version, and the catalog reports versions.
6. **One door and one token.** The Panel serves `GET /diagnostics/views[/{name}]` by forwarding to an Api `internal/diagnostics/views` endpoint authenticated by the internal-service key, using the log-read token, the throttle, the generic rejection and the 404-when-off rule from ADR-0006. The token is platform-wide and accepted nowhere else. Every access is audited with token id, view and parameters, never the response.
7. **Secrets in free text.** The one view that returns free text, recent RCON frames, passes each message through the existing log redactor before return. Chat, names, Steam IDs and IP addresses are not redacted (the owner's decision: they are not secret and the Console tab already shows them).
8. **MCP-readiness.** No MCP server is built now. Anything the log helper script does (paging, summary, writing to a file) is also available over HTTP. A future MCP maps one view to one tool using the catalog and presents the same token.

## Consequences
- Positive: one definitive prohibited list next to the data; less to maintain per view; an assistant (and later an MCP) can discover and call everything from the catalog; one credential; no new port.
- Negative / accepted trade-offs: this is a fail-open design for new columns (see Option A): a sensitive column added without the attribute is exposed until the backstop test, a seeded-secret test or a review catches it, which sits against the owner's fail-closed rule and is accepted by the owner's choice; a new diagnostic question may need a new view; the token still reaches every tenant's servers (accepted: Site Admin-only and platform-wide by decision); views depend on the Api being up (there is no Panel-only fallback as there is for the Panel's own logs).
- Revisit if: an unmarked secret is ever returned (that should trigger a move to an exposed-fields list, as a new ADR); a tenant-scoped token is ever wanted (it would need a tenant filter in every view and its own ADR); or an MCP server needs anything the HTTP catalog cannot express.
