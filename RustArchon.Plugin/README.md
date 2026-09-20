# RustArchon.Plugin

The optional RustArchon companion plugin for Rust servers running Carbon (first-class) or Oxide (untested).
It supplies what vanilla WebRCON cannot: player positions, combat log, structured events, the map image, and
plugin updates. Servers without it keep working; the Panel says what needs it.

Design: [`docs/adr/0004-companion-plugin-rcon-pull-dormant-hooks-signed-updates.md`](../docs/adr/0004-companion-plugin-rcon-pull-dormant-hooks-signed-updates.md)
and [`docs/plans/companion-server-plugin.md`](../docs/plans/companion-server-plugin.md) in the umbrella repo.

**Status:** Phase 1 in progress. This repo exists locally only; visibility and license are not decided yet, so
there is deliberately no `LICENSE` file.

## Layout

- `src/RustArchon.cs` - the plugin. One file, because the game server compiles it itself.

Tests live in the umbrella repo, next to the other test projects, because the plugin is a single `.cs` file and
cannot be a project reference:

- `RustArchon.Plugin.Tests` - compiles the file against stubs of the Oxide/Rust API and exercises its commands.
- `RustArchon.Plugin.CompileCheck` - builds the same file at **C# 7.3** with warnings as errors.

Both prove *our* code. Neither proves the framework's behavior; that needs the live smoke test on a real server.

## Rules for this file

- **C# 7.3 subset**, no nullable annotations, no records, no `using var`, no switch expressions. The game
  server's compiler and Mono runtime are narrower than a modern project's (ECDSA and RSA-PSS are not
  implemented there; RSA PKCS#1 v1.5 is).
- **Every command is RCON-only:** ignore a call that arrives with a connection (an in-game F1 console).
- **Dormant by default:** hooks are subscribed only while their switch is on.
- **A capability is listed in `archon.hello` only once its code exists,** so the Panel never enables a feature
  the installed build cannot perform.
- Replies are one JSON envelope: `{"v":1,"ok":true,"data":{...}}` or `{"v":1,"ok":false,"err":"...","message":"..."}`.

## Commands (protocol 1)

| Command | Does |
|---------|------|
| `archon.hello` | Returns plugin, version, protocol, runtime, capabilities, and the two switches. |
| `archon.config get` | Returns the switches. |
| `archon.config set <recording\|combat> <true\|false>` | Sets one switch and saves it. |
