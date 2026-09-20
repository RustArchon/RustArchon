# ADR-0001: Public F7 report ingest via the Panel, authenticated by a per-server URL token

**Status:** Accepted
**Date:** 2026-09-19
**Deciders:** Scott Blomfield
**Plan:** [docs/plans/f7-reports-and-add-server-wizard.md](../plans/f7-reports-and-add-server-wizard.md)

## Context

Rust game servers can forward in-game (F7) reports to an HTTP endpoint we control, configured with the
`server.reportsServerEndpoint` convar (and an optional `server.reportsServerEndpointKey`). The game server
POSTs `application/x-www-form-urlencoded` with `data` (JSON), `userid` and `key`
(https://wiki.facepunch.com/rust/receiving-reports). It cannot set custom headers or sign requests: the only
credential it can present is a static string, in the URL or in the `key` form field.

RustArchon constraints that shape the answer:

- `RustArchon.Api` never publishes a port. The Panel is the one public door, and already terminates another
  anonymous inbound webhook (`/webhooks/stripe`) and forwards to the Api.
- The caller is an untrusted internet host, so the endpoint must fail closed and resist abuse.
- A game server that is compromised (an attacker holding its RCON password can read the convar back) gives up
  whatever credential it holds. We cannot prevent that; we can only limit what the credential is good for.
- Reports can carry a Base64 screenshot, so the body can be large, and Scott wants no cap on stored screenshots.
- Customers must be able to set this up for their own servers without operator help.

## Decision

1. **Ingest lives in the Panel**, at `POST {PanelBaseUrl}/ingest/reports/{serverId}/{token}` (anonymous), and
   proxies to an Api `/internal/reports/ingest` endpoint using the existing internal-key channel. The public
   base URL is the existing `PanelBaseUrl` platform setting; no new setting.
2. **The secret is a per-server token inside the URL.** We do not use Rust's separate `key` convar, so setup is a
   single paste of one value.
3. **The token is authenticated before the request body is read.** The body-size ceiling therefore applies only
   to authenticated senders.
4. **Token properties:** independently random per server (256-bit CSPRNG), never shared or derived from anything
   (tenant, RCON password), bound to the serverId in the URL, compared in constant time, stored encrypted and
   *reversible* (the Panel redisplays it), and rotatable with the old value dying immediately.
5. **Fail closed:** a server with no configured secret accepts nothing. Unknown server, no secret, and wrong
   token all return the same generic rejection, so server ids cannot be probed.
6. **Who can see and rotate it:** the tenant Owner (whoever manages that server), plus a Site Admin when acting
   as that tenant.
7. **Verification stays pluggable**, so a signed-request scheme can be added later without changing the endpoint
   or the data model.

## Options Considered

### Option A (chosen): Panel proxy + per-server token in the URL

| Dimension | Assessment |
|-----------|------------|
| Complexity | Low - mirrors the existing Stripe webhook pattern |
| Cost | Low |
| Scalability | Fine - the Panel only authenticates and forwards |
| Team familiarity | High |

**Pros:** one value to paste; authenticates before reading the body (so an anonymous caller cannot force a large
upload); Api stays unpublished; secrets and verification stay in the Api.
**Cons:** URLs are logged more readily than form fields (see Consequences); the credential is reversible, so it
must be stored encrypted rather than hashed.

### Option B: Expose the Api publicly

**Pros:** removes one hop.
**Cons:** widens the public attack surface for every Api endpoint, and contradicts the deliberate "Api never
publishes a port" posture. Rejected.

### Option C: Use Rust's separate `key` form field

| Dimension | Assessment |
|-----------|------------|
| Complexity | Low |

**Pros:** it is the documented mechanism.
**Cons:** the key is *inside* the body, so the body must be read before authenticating, which forces a large
anonymous body ceiling and lets unauthenticated callers make us buffer data. Setup also needs two values
(URL + key) instead of one. Rejected.

### Option D: A shared or tenant-level secret

**Cons:** a compromise of one server (whose RCON password lets an attacker read the convar back) would let them
post reports to every server sharing the secret. Rejected; the per-server property is a hard requirement, with a
required test that server A's token is rejected at server B's URL.

### Option E: Public-key / signed-token authentication

**Pros:** a RustArchon database leak would not expose per-server credentials; replay protection.
**Cons:** does **not** reduce blast radius for a compromised game server, which must hold a usable credential
either way; cannot work for the script-less path at all (Rust's native endpoint cannot sign); needs a home for
the private key outside the database, which conflicts with the "config belongs in Platform Settings" rule unless
approved as an exception. Deferred - not needed for v1, and the pluggable verification step keeps the door open.

## Trade-off Analysis

The credential must live on the game server, so any scheme has the same worst case: a compromised server can
forge reports *for that server*. What we control is how far that reaches (one server's inbox, because tokens are
per-server and bound to the id) and how cheaply it is recovered from (rotation). Option A gets that with the least
machinery and the simplest customer setup. Options C and E either weaken the pre-body authentication or add
complexity without changing the game-server-compromise case.

## Consequences

- **Easier:** customer self-serve setup (copy one URL); reuse of the Stripe-webhook shape; cheap rejection of
  unauthenticated traffic.
- **Harder / cautions:**
  - The token is part of the URL, so **the ingest path must be excluded from our own request logging**, and
    reverse-proxy access logs may still capture it. Its reach is limited to forged reports for one server, and
    it is rotatable.
  - Visibility is Owner + acting-as Site Admin, **not** Site-Admin-only: "Site Admin" is the platform-operator
    role, so restricting to it would stop customers setting up their own servers.
  - Any RCON read that carries the URL (the wizard's verify step) is stored in `RconEvent` in plaintext. It is
    hidden from tenants because it is sent non-interactive, and visible only to an acting-as Site Admin, who can
    already see the token. Accepted residual.
- **To revisit:** signed requests for the plugin path, if we decide the extra hardening is worth it (see Option E).

## Action Items

1. [ ] Add `RustServer.ReportsSecret` (encrypted via `IApiKeyProtector`, new purpose), null until first generated.
2. [ ] Implement Panel `/ingest/reports/{serverId}/{token}` and Api `/internal/reports/ingest`.
3. [ ] Exclude the ingest path from request logging.
4. [ ] Tests: token for server A rejected at server B; missing/wrong/no-secret all give the same generic rejection.
