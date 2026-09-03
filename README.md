# RustArchon

A web-based RCON client for [Rust](https://rust.facepunch.com/) game servers, built on the
[JumpStart](https://github.com/cyberknet/JumpStart) framework.

## License

RustArchon is licensed under [AGPL-3.0-or-later](LICENSE). It depends on JumpStart, which is a
separate project under its own GPL-3.0-or-later license - see [`NOTICE.md`](NOTICE.md) for how the
two combine and why that's permitted. Not affiliated with Facepunch Studios; Rust is a trademark of
Facepunch Studios Ltd.

## Solution layout

Each project below is its own GitHub repository under the [RustArchon
org](https://github.com/RustArchon), wired into this one (the umbrella repo) as a git submodule -
see "Cloning this repo" below. `RustArchon.Web` and `RustArchon.Panel` used to be a single Blazor
project; they're split because they carry different licenses (see "License" above) and are deployed
as two separate apps on two separate subdomains.

```
RustArchon/  (this repo - umbrella: RustArchon.slnx, docker-compose.yml, .env.example, docs)
├── RustArchon.Web/        Marketing site (home/features/pricing/contact) - www.rustarchon.com.
│                          NOT AGPL (see RustArchon.Web/README.md). No database, no API clients, no
│                          Identity - it only links out to RustArchon.Panel for sign-up/login.
├── RustArchon.Panel/      The actual product - sign-up, login, servers, console, invitation-code
│                          admin - panel.rustarchon.com. Owns the ASP.NET Core Identity database;
│                          everything else (servers, tenants, roles) goes through generated API
│                          clients calling RustArchon.Api.
├── RustArchon.Api/        The RESTful API - RustServer entity, repository, controller, JWT
│                          bearer auth, and multi-tenant data isolation.
├── RustArchon.Shared/     DTOs shared between RustArchon.Panel and RustArchon.Api (RustServerDto,
│                          CreateRustServerDto, UpdateRustServerDto).
├── RustArchon.Rcon/       A Rust WebRCON client library - not RustArchon-specific business logic,
│                          just "how to talk to a Rust server's RCON" (connect, send commands, parse
│                          chat/console/player-list/ban-list/plugin-list responses, detect Oxide vs.
│                          Carbon). Has no dependency on anything else in this solution.
├── RustArchon.Messaging/  MassTransit message contracts shared between RustArchon.Api and
│                          RustArchon.Worker (RabbitMQ is the transport - see docker-compose.yml).
├── RustArchon.Worker/     The persistent-connection host - one RustArchon.Rcon client per server
│                          this instance owns, publishing captured frames/status/heartbeats via
│                          RustArchon.Messaging. Horizontally scalable; see its own remarks.
└── JumpStart/             Submodule pointing at github.com/cyberknet/JumpStart - a separate
                           project under its own GPL-3.0-or-later license (see NOTICE.md), not part
                           of RustArchon's own AGPL codebase.
```

`RustArchon.Panel`/`.Api`/`.Shared` reach JumpStart via a plain `ProjectReference` to
`../JumpStart/JumpStart/JumpStart.csproj` (not a NuGet package), so changes to the framework are
picked up on the next build with no repack/publish step - this is also why those three repos can't
be built standalone from their own individual clones; you need this umbrella repo's submodule layout.
`RustArchon.Web`/`.Messaging`/`.Rcon` have no such dependency and build standalone just fine.

### Cloning this repo

```bash
git clone --recurse-submodules https://github.com/RustArchon/RustArchon.git
```

Forgot `--recurse-submodules`, or pulled a change that added/moved one? `git submodule update --init
--recursive` from the repo root fills in every submodule (including `JumpStart/`) after the fact.

## How it works

- **Sign up** (`Account/Register`) creates an ASP.NET Core Identity user, same as any JumpStart app.
  Immediately after, `NewTenantBootstrapper` (Blazor) mints a short-lived identity assertion and calls
  `POST /api/account-bootstrap/ensure-tenant` on the API, which:
  1. Creates a new `Tenant` for the user (named "`{email}`'s Organization" by default).
  2. Adds a `UserTenant` membership.
  3. Creates a tenant-scoped `Owner` role granting `RustServer.Get/List/Create/Update/Delete`.
  4. Assigns the user to that role, within that tenant.

  This is best-effort and idempotent (a user who already belongs to a tenant is a no-op), mirroring
  JumpStart's own `DemoNewUserBootstrapper` pattern - see its remarks in
  `RustArchon.Panel/Services/NewTenantBootstrapper.cs`.

- **Add a server** (`/servers`) calls `RustServersController`, a standard
  `ApiControllerBase<RustServer, ...>` - every action is automatically scoped to the caller's tenant
  and gated by an `[EntityAuthorize]` permission claim (`RustServer.Create`, etc.), which the Owner
  role granted above satisfies.

- **RCON passwords are never stored in plaintext.** `RustServersController` encrypts the password via
  `IRconCredentialProtector` (ASP.NET Core Data Protection) before it is ever persisted, and
  `RustServerDto` (the read model) doesn't expose it at all.

Everything else - JWT issuance/exchange, tenant isolation, permission checks - is JumpStart itself;
nothing here re-implements it. See JumpStart's `docs/` for the full mechanism (start with
`core-concepts.md`, `multi-tenancy.md`, and `entity-authorization.md`).

## Running locally

Prerequisites: .NET 10 SDK, PostgreSQL. For a quick local instance:

```bash
docker run --name rustarchon-postgres -e POSTGRES_PASSWORD=postgres -p 5432:5432 -d postgres:17
```

The default `appsettings.Development.json` connection strings already point at `localhost:5432` with
the `postgres`/`postgres` default credentials above - override them (or the
`ConnectionStrings__DefaultConnection` environment variable) if you're pointing at a different
instance. `RustArchon.Api` and `RustArchon.Panel` use two separate databases (`RustArchon` and
`RustArchon_Identity`) on the same PostgreSQL server; both are created automatically by
`dotnet ef database update`/the `Database.Migrate()` call in each `Program.cs` - you don't need to
create either database by hand first. `RustArchon.Web` has no database at all.

```bash
# 1. API first
cd RustArchon.Api
dotnet ef database update
dotnet run
# Swagger at https://localhost:7130/swagger

# 2. The Panel app, in another terminal
cd RustArchon.Panel
dotnet ef database update
dotnet run
# https://localhost:7199

# 3. The marketing site, in a third terminal (optional - only Panel is needed to exercise the product)
cd RustArchon.Web
dotnet run
# https://localhost:7099
```

Both `RustArchon.Api` and `RustArchon.Panel`'s `Program.cs` files also call `Database.Migrate()` on
startup as a development convenience, so the explicit `dotnet ef database update` step above is
optional once you've run each project at least once - see the comments in each `Program.cs` before
relying on this in anything beyond a single-instance deployment.

### Sign-up is invitation-gated by default

Registration requires a valid invitation code by default - this is the soft-launch gate, a real
platform setting (see `PlatformSettingsRegistry`/`InvitationsController`), toggled from the
platform-settings admin page rather than an environment variable. `RUSTARCHON_INVITATION_CODES_ENABLED`
still exists, but only as that setting's *initial* value the very first time the API seeds it - not an
ongoing switch. On a fresh database there are no codes and no accounts yet, so nobody - including the
person meant to become the platform admin - can register through the normal flow.
`AdminInvitationSeeder` solves this: set both of the following in `.env` (see `.env.example`) before
first startup:

- `RUSTARCHON_ADMIN_EMAIL` - your own email.
- `RUSTARCHON_ADMIN_CODE` - any code string you choose.

Both are read by `RustArchon.Api` directly by that exact name (see `Program.cs`) - no config-section
rename in between, so the same name works whether it reaches the app via Docker Compose or a plain
`appsettings.Development.json` entry for native debugging.

On startup, `RustArchon.Api` seeds a single-use invitation code with that exact value, bound to that
email (so it's useless to anyone else even if it leaks). Register at `/Account/Register` with that
email/code pair and you'll end up an Owner of your own tenant (the normal sign-up flow already does
this) *and* a platform admin able to manage further invitation codes at `/Admin/InvitationCodes` -
your email already satisfies the `PlatformAdmin` policy, so no separate "create admin" step exists.
Seeding is idempotent (safe to leave configured indefinitely) and never touches a code that's already
been redeemed.

Register an account in RustArchon.Panel, then go to **Servers** to add your first Rust server (host,
RCON port - Rust's default is `28016` - and RCON password).

## Docker

The whole stack (`rustarchon-web`, `rustarchon-panel`, `rustarchon-api`, `rustarchon-worker`,
PostgreSQL, RabbitMQ, Valkey) is defined in `docker-compose.yml` at the repo root. The build context for every
service is this repo's own root - JumpStart is a submodule mounted inside it (see "Cloning this repo"
above), so unlike before the split into separate repos, Docker never needs to see anything outside
this one directory.

```bash
git submodule update --init --recursive   # if you didn't clone with --recurse-submodules
cp .env.example .env                       # then fill in real values - see the comments in .env.example
docker compose up --build
```

That builds every image from source locally. `.github/workflows/publish-images.yml` also publishes
`rustarchon-api`/`-panel`/`-worker`/`-web` to `ghcr.io/rustarchon/` on every push to this repo's own
`main` - a deployment that doesn't need to iterate on source (see [DEPLOYMENT.md](DEPLOYMENT.md)) can
pull those instead of building, via the `docker-compose.prod.yml` overlay.

`rustarchon-web` (`http://localhost:8081`) and `rustarchon-panel` (`http://localhost:8080`) are the
two services published to the host - the API and RabbitMQ's AMQP port are reachable only from other
containers on the compose network, and the RabbitMQ management UI is bound to `127.0.0.1:15672` only.
Fronting both published ports behind real `www.`/`panel.` subdomains with TLS is a reverse-proxy step
done outside this compose file - see "Before deploying this anywhere real" below, and
[DEPLOYMENT.md](DEPLOYMENT.md) for a concrete Proxmox-LXC-plus-Cloudflare-Tunnel runbook. Both databases and the RabbitMQ
topology are created automatically on first start; there's no manual setup step beyond the `.env` file
above.

**RabbitMQ durability needs two things, not one.** The `rabbitmq-data` volume alone isn't enough to
survive the container being recreated (`docker compose down`, an image bump, anything short of a
plain restart) - RabbitMQ's node identity is derived from the container hostname, which Docker
randomizes on every recreation by default. Without pinning it, a recreated container boots as a
brand-new, empty node sitting right next to its own old data on the volume, rather than finding it -
confirmed by hand while building the email-queue feature, not just reasoned about. `hostname:
rabbitmq` in `docker-compose.yml`'s `rabbitmq` service is what fixes this; don't remove it.

### Debugging from Visual Studio

Docker Compose orchestrator support lets you `F5` straight into a running container with breakpoints,
while `docker compose up` brings up everything else (Postgres, RabbitMQ, the other two app
containers) alongside it. This repo ships working `Dockerfile`s for all three app projects, but not
the Visual-Studio-specific orchestrator project file, since that's IDE-generated and version-specific
rather than something to hand-author. To wire it up:

1. Open `RustArchon.slnx` in Visual Studio.
2. Right-click each of `RustArchon.Web`, `RustArchon.Panel`, `RustArchon.Api`, and `RustArchon.Worker`
   → **Add** → **Container Orchestrator Support** → **Docker Compose** → **Linux**. Visual Studio
   detects the existing `Dockerfile` in each project and, after the first project, offers to add the
   rest into the same `docker-compose` project rather than creating a separate one each time - accept
   that.
3. Visual Studio generates its own `docker-compose.override.yml` for debug-specific settings
   (remote debugger injection, `ASPNETCORE_ENVIRONMENT=Development`, etc.) alongside the
   `docker-compose.yml` already here - it's expected for both to coexist.
4. Set the generated `docker-compose` project as the startup project and `F5` as usual.

Verified end-to-end by hand with a real `docker compose up --build` - five real bugs turned up that
way and got fixed: `RustArchon.Web`'s `Dockerfile` still assumed the old per-project build context;
`RustArchon.Panel`'s `Dockerfile` never copied `RustArchon.Messaging` even though `RustArchon.Shared`
needs it; `rustarchon-web`'s compose service was missing `ApiBaseUrl` entirely (silently defaulting to
`https://localhost:7130`, unreachable inside the container); Npgsql logged a harmless-but-noisy missing
`libgssapi_krb5.so.2` warning on every Postgres connection; and, the significant one -
`RustArchon.Panel`/`RustArchon.Web`'s published output was silently missing `_framework/blazor.web.js`
entirely (breaking all Blazor interactivity) because publishing framework-dependent, with
`--no-restore`, inside this Dockerfile's layer-caching pattern skips recomputing static web assets
against the real source. Both now publish self-contained instead, which sidesteps it - see the two
Dockerfiles' own comments for the full explanation.

## Before deploying this anywhere real

A few things were deliberately left as local-development defaults and need attention first:

- **JWT `SecretKey`** in both `appsettings.Development.json` files is a placeholder. Generate a real
  32+ character secret per environment and keep it out of source control (user-secrets, environment
  variables, or a secrets manager) - it must be identical in both projects.
- **Data Protection key ring** (used to encrypt RCON passwords) is persisted to disk - a local
  `App_Data/dataprotection-keys` folder outside Docker, a named volume (`dataprotection-keys`) inside
  it - rather than left at .NET's default per-machine profile, so recreating the container doesn't
  invalidate every stored password. This covers exactly one `RustArchon.Api` instance/volume, though:
  running **more than one instance** still requires moving to a shared, durable store all instances
  can reach (a database, blob storage, etc. - see the comment in `RustArchon.Api/Program.cs`), since a
  local disk volume isn't shared across instances the way it needs to be.
  See [ASP.NET Core Data Protection key storage providers](https://learn.microsoft.com/aspnet/core/security/data-protection/implementation/key-storage-providers).
- **Email delivery** is queued, not immediate: RustArchon.Panel's `IEmailSender<ApplicationUser>`
  (`QueuedEmailSender`) doesn't send anything itself - it calls `RustArchon.Api`'s internal `/internal/email`
  endpoint, which publishes an `EmailRequested` message that a `RustArchon.Worker` instance picks up
  and sends (`EmailRequestedConsumer`), with MassTransit retrying a failed send before giving up. No
  real mail provider is wired up yet, though: the Worker's `IEmailDeliveryProvider` is still
  `NoOpEmailDeliveryProvider`, which just logs what it would have sent. Until that's replaced,
  registration/password-reset links are only visible in `RustArchon.Worker`'s console output (search
  for "Would send email") - swap that one DI registration for a real provider (SMTP, SendGrid, etc.)
  before production.
- **CORS/JWT issuer-audience values, and the Web↔Panel cross-links,** currently point at `localhost`
  ports - update `CorsSettings:BlazorServerUrl` (API), `ApiBaseUrl` (Panel), `MarketingBaseUrl`
  (Panel), and `PanelBaseUrl` (Web) for your real hostnames.
- **`COOKIE_DOMAIN`** must be set to `.rustarchon.com` (the leading dot matters - it covers the
  parent domain and every subdomain) once Web and Panel are actually fronted by their real `www.`/
  `panel.` subdomains - see "Cross-app session (SSO)" below. Leave it unset for any plain-port/
  single-hostname deployment, local or otherwise.

### Cross-app session (SSO)

RustArchon.Web can tell whether a visitor already has an active RustArchon.Panel session (shows
"Dashboard" instead of "Log In"/"Sign Up" in the nav) via a **real shared cookie**, not a lightweight
hint: Panel's Identity cookie gets
`Domain=.rustarchon.com` in production (`CookieDomain` config - empty locally, where
`localhost:5100`/`:5200` already share cookies as the same hostname on different ports), and both
apps point `AddDataProtection()` at the same physical key storage with the same `ApplicationName`
(`"RustArchon.Session"`) so Web can decrypt and validate the cookie Panel issued. Web registers just
enough cookie authentication to populate `HttpContext.User`/Blazor's cascading auth state - no
database, no `Microsoft.AspNetCore.Identity.EntityFrameworkCore` package, no sign-in/sign-out logic
of its own; Panel remains the only place a session is ever created or destroyed.

The shared key storage is `DataProtection:SessionKeyPath` in both apps - defaults to
`App_Data/dataprotection-keys-session/` at this repo's own root (a sibling of both `RustArchon.Web/`
and `RustArchon.Panel/`) for native dev, or the `dataprotection-keys-session` named volume in
`docker-compose.yml`. This is a **separate key ring from `RustArchon.Api`'s own** `/keys` volume
(used for RCON password encryption, a different purpose entirely) - the two are never meant to
overlap, and use different `ApplicationName`s so they couldn't even if pointed at the same folder.

If this ever silently stops working (Web always shows Log In even when Panel is clearly signed in),
it fails safe, not loud - check that both apps' `DataProtection:SessionKeyPath` actually resolve to
the same physical location, and that Panel's `AddIdentityCookies()` default cookie name
(`.AspNetCore.Identity.Application`, verified empirically rather than assumed while building this)
hasn't changed out from under `RustArchon.Web/Program.cs`'s hardcoded match for it.

## What's not built yet

Sign-up and server registration are complete. The always-on WebRCON capture pipeline (every
registered server gets a persistent connection, with full history even while nobody's watching) is
under construction. What exists so far:

- **`RustArchon.Rcon`** - a standalone Rust WebRCON client library (connect, send commands, parse
  chat/console/player-list/ban-list/plugin-list responses, detect Oxide vs. Carbon). No dependency on
  anything else in this solution - it doesn't know RustArchon exists.
- **`RustArchon.Worker`** - holds one `RustArchon.Rcon` client per server it owns, publishing captured
  frames/connection status/heartbeats as `RustArchon.Messaging` contracts over RabbitMQ. Horizontally
  scalable (see its own remarks on the queue-based ownership mechanism).

Neither is wired up to `RustArchon.Api` or the UI yet - the Worker can talk WebRCON and publish what
it captures, but nothing on the API/database/UI side consumes any of it yet (no `RconEvent` history
table, no SignalR live tail, no console page, no credential-handoff endpoint telling the Worker which
servers to actually connect to). Also not yet built: a command console/log viewer, and any UI for
renaming a tenant or inviting teammates into it (the framework-level `TenantsController`/
`ITenantsApiClient` already supports the latter - see JumpStart's `docs/multi-tenancy.md`).
