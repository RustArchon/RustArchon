# Deploying RustArchon on a Proxmox LXC

A concrete runbook for running the whole stack continuously on a Proxmox LXC container, fronted by a
Cloudflare Tunnel (so this deliberately has no reverse-proxy/TLS setup of its own - see "Before
deploying this anywhere real" in the main [README](README.md) for the parts of this that apply to any
deployment, not just this one).

This is an early, working-but-not-finished deployment - see "Known gaps" at the bottom before you
point real users at it.

This deployment pulls prebuilt images from `ghcr.io/rustarchon/` rather than building from source on
the LXC - `.github/workflows/publish-images.yml` builds and pushes `rustarchon-api`/`-panel`/`-worker`/
`-web` on every push to this repo's own `main` (i.e. once a "bump submodules" PR merges here - see that
workflow's own remarks for why it's gated on that specific branch, not each submodule's).

**Two one-time setup steps that whole workflow depends on**, neither of which it can do for itself:

- **A `SUBMODULES_PAT` repo secret.** `RustArchon.Web` is a private repo (the one proprietary,
  non-AGPL project in this org); the workflow's default `GITHUB_TOKEN` can't clone it as a submodule,
  which fails the checkout step for *every* matrix leg, not just `rustarchon-web`'s own - confirmed by
  hand, the very first run failed exactly this way in under 20 seconds, before any image ever started
  building. Create a fine-grained PAT (GitHub Settings → Developer settings → Personal access tokens →
  Fine-grained tokens) scoped to this org's repos with `Contents: Read-only`, then add it as this
  repo's own Actions secret named `SUBMODULES_PAT` (Settings → Secrets and variables → Actions → New
  repository secret).
- **Package visibility**, set by hand once **after** the first successful run (org → Packages →
  package → Package settings → Change visibility): public for `rustarchon-api`/`-panel`/`-worker`
  (their source is already public, an AGPL repo - see the main README's License section), private for
  `rustarchon-web`. GHCR packages default to private on first publish regardless of what the workflow
  intends, and nothing re-checks this later.

## 1. Create the LXC

In the Proxmox web UI, **Create CT**:

- **Template**: Debian 12 (bookworm) - matches the `.NET` container base images' own OS closely enough
  that nothing here assumes anything Debian-specific beyond `apt`.
- **Unprivileged container**: leave checked (default). Docker runs fine unprivileged on modern Proxmox/
  LXC - you do not need a privileged container for this.
- **Resources**: 2 vCPUs / 4GB RAM / 20GB disk is a reasonable starting point for the whole stack
  (Postgres, RabbitMQ, Valkey, and the four .NET services) at low traffic. Bump disk if you expect a
  lot of RCON console/chat history to accumulate - that's the biggest long-term storage growth vector.
- **Network**: a static IP (or a DHCP reservation) on your LAN is worth it here - simpler to point
  `cloudflared` at a stable address than to re-authenticate the tunnel every time the IP changes.

Proxmox's own LXC templates already run a real Linux kernel shared with the host, which is what lets
Docker work inside one at all (no nested virtualization needed) - but Docker **does** need a few
kernel features some minimal LXC configs restrict. If `docker run hello-world` fails with cgroup or
`overlay2` errors after step 2, edit the container's `Options` in Proxmox and enable **Nesting**
(`Features` → `nesting=1`) - most current Proxmox versions default this on for new containers, but it's
worth checking.

## 2. Install Docker inside the LXC

SSH into the container (`pct enter <vmid>` from the Proxmox host, or SSH directly if you gave it a
static IP), then:

```bash
apt update && apt install -y ca-certificates curl gnupg
install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/debian/gpg -o /etc/apt/keyrings/docker.asc
chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/debian $(. /etc/os-release && echo "$VERSION_CODENAME") stable" \
  | tee /etc/apt/sources.list.d/docker.list > /dev/null
apt update && apt install -y docker-ce docker-ce-cli containerd.io docker-compose-plugin
systemctl enable --now docker
```

Confirm it works: `docker run --rm hello-world`.

## 3. Clone the repo

```bash
apt install -y git
git clone https://github.com/RustArchon/RustArchon.git /opt/rustarchon
cd /opt/rustarchon
```

Plain clone, deliberately **without** `--recurse-submodules` - this deployment pulls prebuilt images
(step 5) rather than building from source, so the LXC never needs `RustArchon.Api`/`.Panel`/`.Web`/
`.Worker`/`.Shared`/`.Messaging`/`JumpStart`/`.Rcon`'s actual source, or the multi-GB .NET SDK image
that building them would pull in - just `docker-compose.yml`, `docker-compose.prod.yml`, and `.env`,
all of which live directly in this repo's own root. (If you'd rather build from source here instead -
e.g. while iterating on a change before it's merged - add `--recurse-submodules` back to the clone and
use plain `docker compose up -d --build` in step 5 instead of the two-file `pull`/`up`.)

## 4. Log in to ghcr.io

`.github/workflows/publish-images.yml` publishes `rustarchon-api`/`-panel`/`-worker` as **public**
images (no login needed to pull those) but `rustarchon-web` stays **private** (see that workflow's
remarks) - and `docker compose pull` in step 6 pulls all four in one go, so the LXC needs to
authenticate regardless. Create a classic GitHub PAT with just the `read:packages` scope (Settings →
Developer settings → Personal access tokens), then:

```bash
echo '<your-PAT>' | docker login ghcr.io -u <your-github-username> --password-stdin
```

## 5. Configure `.env`

Back in `/opt/rustarchon` (the clone from step 3 - `cd` there again if this is a new shell session):

```bash
cd /opt/rustarchon
cp .env.example .env
```

Then edit `.env` and set real values - **do not deploy with the `.env.example` placeholders**:

- `RUSTARCHON_JWT_SECRET_KEY` / `RUSTARCHON_INTERNAL_API_KEY` - generate with
  `openssl rand -base64 48` (run it twice, once for each - they must be different from each other, and
  each at least 32 characters).
- `POSTGRES_PASSWORD` / `RABBITMQ_DEFAULT_USER` / `RABBITMQ_DEFAULT_PASS` - anything you like; these
  never leave the container network (see `docker-compose.yml`'s port bindings).
- `RUSTARCHON_ADMIN_EMAIL` / `RUSTARCHON_ADMIN_CODE` - the email you'll register with and a one-time
  code of your choosing, so your own account becomes the platform admin on first sign-up. See
  ".env.example"'s own comments for the exact flow.
- `PANEL_PUBLIC_URL` / `WEB_PUBLIC_URL` - the two hostnames you'll put behind the Cloudflare Tunnel in
  step 7, e.g. `https://panel.yourdomain.com` and `https://www.yourdomain.com`. **Set these before
  first `docker compose up`** - `PANEL_PUBLIC_URL` in particular is checked against the browser's CORS
  Origin header (see the comment above it in `docker-compose.yml`); getting it wrong after the fact
  means editing `.env` and running `docker compose up -d` again to pick up the change (no rebuild or
  re-pull needed, just a recreate).
- `COOKIE_DOMAIN` - set to `.yourdomain.com` (leading dot) once `PANEL_PUBLIC_URL`/`WEB_PUBLIC_URL`
  are real subdomains of the same domain, so RustArchon.Web can read the session cookie
  RustArchon.Panel issues - see the README's "Cross-app session (SSO)" section for what this enables
  and how to tell if it's broken.
- Leave `ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT` at `Production` (the `.env.example` default) -
  only ever set these to `Development` for troubleshooting on a machine nobody else can reach.

## 6. Bring the stack up

Still in `/opt/rustarchon` (`cd` there again if this is a new shell session - all `docker compose`
commands from here on assume it, since that's where `docker-compose.yml`/`.prod.yml`/`.env` live):

```bash
cd /opt/rustarchon
docker compose -f docker-compose.yml -f docker-compose.prod.yml pull
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d
docker compose ps
```

(Both `-f` flags every time on this host - or set `export COMPOSE_FILE=docker-compose.yml:docker-compose.prod.yml`
once in your shell profile so plain `docker compose ...` picks up both automatically.) `pull` grabs the
latest published `:latest` tag for `rustarchon-api`/`-panel`/`-worker`/`-web` from ghcr.io (see step 4)
rather than building anything locally - see `docker-compose.prod.yml`'s own remarks for why plain `up
-d` (no `--build`) is what makes that stick.

All eight services (`postgres`, `rabbitmq`, `valkey`, `rustarchon-api`, `rustarchon-worker`,
`rustarchon-panel`, `rustarchon-web`, plus `watchtower` - see the update note below) should show
`running` (the three infra ones `healthy`). Databases,
tables, and RabbitMQ's queue topology are all created automatically on first start - there's no manual
migration step. `restart: unless-stopped` on every service means the whole stack comes back on its own
after an LXC reboot or a Docker daemon restart, as long as `systemctl enable docker` from step 2 stuck
(confirm with `systemctl is-enabled docker`).

If something doesn't come up, `docker compose logs -f <service>` is the first place to look.

**Updates are automatic.** The `watchtower` service (see `docker-compose.prod.yml`) polls ghcr.io once
an hour and recreates any of the four app containers whose `:latest` tag now points at a new digest -
so a merge to the umbrella repo's `main`, which re-runs the publish workflow, reaches this host on its
own within the hour. It deliberately only touches the four RustArchon services, never `postgres`,
`rabbitmq` or `valkey` (see that file's own remarks for why those stay manual).

To force an update immediately rather than waiting for the next poll, re-run the same `pull` + `up -d`
pair above - Compose only recreates containers whose image digest actually changed.

To see what watchtower has been doing:

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml logs watchtower
```

Both `-f` flags matter here: `watchtower` is defined in `docker-compose.prod.yml`, so plain `docker
compose logs watchtower` reads only `docker-compose.yml` and fails with `no such service`. (`docker
compose ps` does list it either way - it finds running containers by project label rather than from
the file, which makes the mismatch look stranger than it is.) `docker logs rustarchon-watchtower-1`
also works, using the container name instead of the service name.

That log is also where you'd see whether it can actually reach the private `rustarchon-web` package -
if that one silently never updates while the other three do, the `docker login` from step 4, and the
`~/.docker/config.json` mount that depends on it, are what to check.

## 7. Point the Cloudflare Tunnel at it

You said you already have a tunnel providing the reverse proxy and TLS - the two things it needs to
route to are the ports `rustarchon-panel` and `rustarchon-web` publish on the LXC itself:

- `PANEL_PUBLIC_URL`'s hostname (e.g. `panel.yourdomain.com`) → `http://localhost:8080`
- `WEB_PUBLIC_URL`'s hostname (e.g. `www.yourdomain.com`) → `http://localhost:8081`

(`localhost` here means the LXC's own loopback, from `cloudflared`'s point of view - both ports are
published by Docker to the LXC's host network, not restricted to loopback-only the way Postgres/
RabbitMQ/Valkey's dev-convenience ports are.) If `cloudflared` itself runs as a systemd service on this
same LXC (the common setup), that's all there is to it. If you run it as a container instead, put it on
`rustarchon-net` and route to `http://rustarchon-panel:8080`/`http://rustarchon-web:8080` (the
container's own port, not the host-published one) instead.

## 8. First login

`RequireConfirmedAccount = true` is on, and no real email provider is wired up yet (see "Known gaps"
below) - so the confirmation link your own registration sends doesn't arrive by email. Get it from the
Worker's log instead:

```bash
docker compose logs rustarchon-worker | grep "Would send email"
```

Register at `https://panel.yourdomain.com/Account/Register` with the exact `RUSTARCHON_ADMIN_EMAIL`/
`RUSTARCHON_ADMIN_CODE` pair from your `.env`, then paste the confirmation link from that log line into
your browser. You'll end up both the Owner of your own tenant (the normal sign-up flow) and a platform
admin (your email already matches `RUSTARCHON_ADMIN_EMAIL`) in one step.

## Known gaps

Carried over from the main README's "Before deploying this anywhere real" - still true here:

- **No real email provider.** Every account's confirmation/password-reset link only ever reaches
  `rustarchon-worker`'s log output (see step 7). Fine for a single admin bootstrapping their own
  account; not fine for onboarding real users until `RustArchon.Worker`'s `IEmailDeliveryProvider` is
  swapped from `NoOpEmailDeliveryProvider` to a real one (SMTP, SendGrid, etc.).
- **Single-instance Data Protection key rings.** Both named volumes (`dataprotection-keys`,
  `dataprotection-keys-session`) work fine for exactly one `rustarchon-api`/`rustarchon-panel`/
  `rustarchon-web` instance each, which is all this compose file ever runs - but if you ever scale any
  of them to more than one replica, they need to move to a shared, durable store all replicas can
  reach instead (see the comment in each project's `Program.cs`).
- **Back up `dataprotection-keys` and the `postgres-data` volume together, never one without the
  other.** `rustarchon-api` encrypts every stored RCON password with a key from that key ring before
  writing it to Postgres; if the volume is ever lost or recreated on its own (a bad restore, a `docker
  volume rm`, moving to a new host without copying it over) while the Postgres data survives, every
  previously-saved RCON password becomes permanently undecryptable - confirmed by hand, it throws a
  `CryptographicException` from `RconCredentialProtector.Unprotect`, and that server's connection just
  breaks silently until someone re-enters its password. Restoring a backup of one without the matching
  backup of the other reintroduces the exact same problem.
- **The main README's "What's not built yet" section predates most of what's now running** (server
  registration, live console/chat, player history, Stats/Control tabs, pricing plans) - it's worth a
  pass to bring current, but hasn't been refreshed as part of this deployment work.
