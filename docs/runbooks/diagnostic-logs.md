# Diagnostic logs: reading production logs without logging in

Read-only access to the recent logs of the Api, the Worker, the Panel and the RustArchon plugin on each game server, through one address on the Panel. Built for handing to someone you trust (or an assistant working with you) without giving them SSH, a login, or anything that can change anything. Design: [change 001](../changes/001-read-only-diagnostic-logs/spec.md), [ADR-0006](../adr/0006-worker-log-forwarding-and-diagnostic-log-source-model.md).

## Turning it on, and off

1. Sign in as a Site Admin and open **Admin → Diagnostic logs**.
2. **Create token.** It is shown once. Copy it now; it cannot be retrieved again.
3. Give it to whoever needs it. It opens the log address and nothing else.
4. When you are done: **Revoke** (or **Replace token**, which also kills the old one at once). Do this every time. A leaked token reads logs until you do.

No token created = the address does not exist (404). A revoked or replaced token is refused exactly like a wrong one (401).

The refusal throttle (default 10 refused requests per address per minute) is the Platform Setting *Refused log requests per network address per minute*.

## Using it

The address is `GET <your Panel address>/diagnostics/logs`, with the token in the `X-RustArchon-Log-Token` header (never in the address). `/diagnostics/logs/help` returns the endpoint's own documentation.

The helper pages through everything for you, into a file, and prints only a summary:

```bash
export RUSTARCHON_LOG_TOKEN=...        # or pass --token
python tools/diagnostic-logs/fetch_logs.py --base https://panel.example.com --summary-only --since 1h
python tools/diagnostic-logs/fetch_logs.py --base https://panel.example.com --sources api,worker,panel --level warning --since 6h --out logs.ndjson
```

Useful filters: `--level debug|information|warning|error`, `--since 15m|2h|1d`, `--category <prefix>`, `--text <substring>`, `--prop ServerId:<guid>`, `--prop Plugin:<name>`. Sources: `api`, `worker` (every Worker instance), `panel`, `plugin:<server id>` (asked of the plugin on that game server on demand; never in the default set).

## Reading the answer

Entries are one JSON object per line (`source`, `instance`, `seq`, `ts`, `level`, `category`, `message`, `exception`, `props`), then a final `_meta` line. Per source, `_meta` says:

| Field | Meaning |
|---|---|
| `available: false` + `reason` | That source could not be read. The reason says why (no Worker heard from, plugin too old, server not connected, Api unreachable…). It is never shown as an empty log. |
| `lost` > 0 | That many entries after your cursor are gone: the buffer wrapped, or a Worker batch never arrived. |
| `reset: true` | That process restarted; sequence numbers started over. |
| `warning` | The source has gone quiet (a Worker whose last batch is old). |
| `oldest` / `head` | The range still held. |

## Things that will mislead you if you forget them

- **Buffers are in memory** (about 5,000 entries per source) and are lost on restart. Our own code is captured at Debug; framework chatter (SQL commands, sockets, routing, message-bus internals) is left out below Warning so it cannot push out the entries that matter. At Debug on a busy process the buffer can still cover only minutes.
- **Worker logs travel over RabbitMQ.** A silent or missing Worker may mean RabbitMQ is the problem, not that the Worker is quiet. Check the Worker source's `reason` / `warning` before concluding anything.
- **The plugin source needs a plugin that reports the `logs` capability** (version 0.11.0 or later) and a connected server. Its update-attempt entries are kept on the game server's disk and survive the reload an update causes; the rest of its log does not.
- **If the Api is down**, the Panel can still answer with its own entries for a token it verified in the last 15 minutes, and names every other source as unavailable. A token revoked while the Api is down keeps working for the Panel's own entries for up to those 15 minutes.
- **Cloudflare** may challenge a non-browser client. If requests get a challenge page instead of logs, add a skip rule for `/diagnostics/*`.
- Secrets are redacted before storage and again on output; `[REDACTED]` is deliberate. Redaction is pattern based and cannot be proven complete, which is why the token should be short-lived.

## Recipe: "why isn't this server updating its plugins?"

1. What did the Api decide, and why? Every refusal names the gate:

   ```bash
   python tools/diagnostic-logs/fetch_logs.py --base $BASE --sources api --since 24h --prop ServerId:<server id> --text "plugin update" --out api.ndjson
   ```

   Codes you may see: `plan_does_not_offer`, `not_opted_in`, `held_for_wipe` (the days-before-wipe window), `no_handshake` / `stale_status` (the RustArchon plugin has not reported), `not_signed_by_this_panel`, `plugin_too_old`, `no_download` (no download address found), `file_changed`, `already_tried`, `not_connected`, `timeout`. "No outdated third-party plugins are known on server …" means the server's UpdateChecker has reported nothing, so there is nothing to update.
2. What did the plugin on the game server do?

   ```bash
   python tools/diagnostic-logs/fetch_logs.py --base $BASE --sources plugin:<server id> --category update --level debug --out plugin.ndjson
   ```

   You get each request it was sent (accepted, or refused with the code), each download/verify/swap step, and the outcome with its reason, including attempts from before the last reload.
3. Done? **Revoke the token.**
