# RustArchon Quick Start

The minimum required to get a working, single-instance RustArchon deployment running and reachable by
its first admin. This is deliberately narrow - full explanations, every optional Platform Setting, and
the reasoning behind each gotcha live in [DEPLOYMENT.md](DEPLOYMENT.md); this doc links there rather than
repeating it. How you expose the result to the internet (a reverse proxy, a Cloudflare Tunnel, or
nothing at all for a LAN-only deployment) is entirely your call - nothing below assumes one.

## 1. Install Docker

Any host that can run Docker Compose works. See
[Docker's own install docs](https://docs.docker.com/engine/install/) for your OS; DEPLOYMENT.md's step 2
has the exact `apt` commands for a Debian-based host if you want a copy-paste version.

## 2. Make Docker survive a reboot

```bash
systemctl enable --now docker
```

Confirm it stuck with `systemctl is-enabled docker`. Every RustArchon service is `restart:
unless-stopped`, so once this is on, the whole stack comes back on its own after a reboot with no
further action - which step 9 below exists to actually verify.

## 3. Get the repo

```bash
git clone https://github.com/RustArchon/RustArchon.git
cd RustArchon
```

Plain clone, no `--recurse-submodules` - this quick start pulls prebuilt images (step 5) rather than
building from source, so you never need the submodules' own code. If you're pulling images, log in to
GHCR now too, since `rustarchon-web`'s image is private:

```bash
docker login ghcr.io -u <your-github-username>
# password prompt: a classic PAT with just the read:packages scope
```

## 4. Set environment configuration

```bash
cp .env.example .env
```

Edit `.env` and set real values - **do not deploy with the placeholders.** At minimum:

- `RUSTARCHON_JWT_SECRET_KEY` / `RUSTARCHON_INTERNAL_API_KEY` - `openssl rand -base64 48`, run twice,
  each at least 32 characters and different from the other.
- `POSTGRES_PASSWORD` / `RABBITMQ_DEFAULT_USER` / `RABBITMQ_DEFAULT_PASS` - anything; these never leave
  the container network.
- `RUSTARCHON_ADMIN_EMAIL` / `RUSTARCHON_ADMIN_CODE` - the email you'll register with and a one-time
  code of your choosing. This pair is what makes your first registration become the platform admin in
  step 6.
- `PANEL_PUBLIC_URL` / `WEB_PUBLIC_URL` - the hostnames you'll reach this at, even if that's just
  `http://localhost:8080` / `:8081` for now. Set these before first `up` - changing `PANEL_PUBLIC_URL`
  later means editing `.env` and re-running `up -d`, not a rebuild.
- `GARAGE_RPC_SECRET` / `GARAGE_ADMIN_TOKEN` - `openssl rand -hex 32`, run twice. The `garage` container
  refuses to start at all without both.
- Leave `GARAGE_S3_ACCESS_KEY` / `GARAGE_S3_SECRET_KEY` blank for now - step 6 generates them.
- Leave `ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT` at `Production`.

## 5. Bring up the stack

```bash
docker compose pull
docker compose up -d
docker compose ps
```

All nine services should show `running` (the four infra ones `healthy`). Databases, tables, and
RabbitMQ's topology are created automatically - no manual migration step.

## 6. Bootstrap Garage (required, not optional)

The platform's built-in theme is a real `Theme` row backed by object storage - skip this and it never
seeds, which surfaces as real errors, not just a missing entry in the Themes list. Do this before step 7:

```bash
docker compose exec garage /garage status
# copy the node ID shown as "NO ROLE ASSIGNED"

docker compose exec garage /garage layout assign -z local -c 1G <node-id>
docker compose exec garage /garage layout apply --version 1

docker compose exec garage /garage bucket create rustarchon-themes
docker compose exec garage /garage key create rustarchon-api
docker compose exec garage /garage bucket allow --read --write --owner rustarchon-themes --key rustarchon-api
```

The last command prints an access key ID and secret exactly once - put them in `.env` as
`GARAGE_S3_ACCESS_KEY` / `GARAGE_S3_SECRET_KEY`, then recreate just the API container to pick them up:

```bash
docker compose up -d rustarchon-api
```

Full explanation of every flag here is in DEPLOYMENT.md's own Garage section - this is the same set of
commands, just without the why.

## 7. First login

Register at `https://<your-panel-host>/Account/Register` with the exact
`RUSTARCHON_ADMIN_EMAIL` / `RUSTARCHON_ADMIN_CODE` pair from step 4. This one registration skips email
confirmation and signs you in immediately, as both the Owner of your own tenant and a platform admin.

## 8. Mandatory platform settings

Everything below is at Admin → Platform Settings and takes effect immediately, no restart needed. See
[DEPLOYMENT.md](DEPLOYMENT.md#platform-settings-reference) for what every other, non-mandatory setting
on that page does.

**General**

| Setting | What to put there |
|---|---|
| Site name | Your platform's display name - shown in the nav bar and every email. |
| Site URL | Your public marketing address (if you don't have one yet, your Panel URL works). |
| Panel base URL | Usually already correct - seeded from `PANEL_PUBLIC_URL`. Confirm it matches. |

**Registration**

| Setting | What to put there |
|---|---|
| Require invitation codes to register | Leave on until you're ready for public sign-ups. |

**Email** - real email delivery, not optional once anyone but you needs to register or reset a password:

| Setting | What to put there |
|---|---|
| Email service provider | `Smtp`, `Resend`, or `SendGrid`. |
| API key / SMTP host, port, TLS, username, password | Whichever fields your chosen provider shows. |
| From address | The sender address on every email RustArchon sends. |
| From name | The sender display name alongside it. |

**Before moving on**, use the "send a test email" button on this page. A typo here fails silently
otherwise - you won't find out until a real user tries to register and never gets their confirmation
link.

**Payments - optional:** Stripe isn't required for the platform to run; leave both fields blank and the
feature is simply not offered. See [DEPLOYMENT.md](DEPLOYMENT.md#stripe-payments-optional) for the full
Stripe Dashboard walkthrough (restricted key creation, webhook endpoint, event selection) if and when
you want to enable it - it fills in "Stripe secret key" and "Stripe webhook signing secret" on this same
settings page.

## 9. Reboot

Reboot the host and confirm `docker compose ps` shows every service back up on its own, with no manual
`up -d` needed. This is the real test of step 2 - and, since a fresh boot re-reads `.env`, also confirms
nothing in step 4 or 6 was left as a placeholder.
