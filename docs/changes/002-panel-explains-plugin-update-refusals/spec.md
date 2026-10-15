# 002. The Panel explains every plugin-update refusal, and can re-sign the served plugin — Spec

- Status: Complete
- Date: 2026-10-14
- Related: change 001 (diagnostic logs), memory rule "admin must be able to diagnose any issue from the UI"

## Problem
On the production Panel, third-party plugin updates were not happening. The cause (`not_signed_by_this_panel`: the RustArchon plugin on the game server was not signed by this Panel's key) was visible only in the diagnostic log added by change 001. Nothing in the Panel said so. This breaks the project rule that any "why is this (not) happening?" is answerable from the Panel.

Specific gaps found:
1. The Plugins tab's update offers decide "can this be applied?" from status freshness and capabilities only. The signing, key-revoked and plugin-too-old checks run only when an update starts, so an offer looks applicable and then is refused, or (for the automatic pass) is silently skipped.
2. The automatic pass's gates (platform switch, plan, server opt-in, days-before-wipe hold) and its own refusals are not shown per server, and there is no indication of when it last ran or will next run.
3. There is nowhere to see which RustArchon plugin version this Panel is currently serving and fetch it signed with the active key. Fixing "the plugin on the server is not signed by this Panel" requires exactly that file, and the Plugin admin page only offers a signed download of an already-published release, not of the built-in plugin served before any release is published.

## Desired behavior
- Every reason an update to a plugin on a server is not started, by a person or by the automatic pass, is shown on that server's Plugins tab next to the plugin, in plain language, with what to do about it. This covers every refusal code the update path can produce, including: not signed by this Panel, signing key revoked, RustArchon plugin too old, plugin not reporting / stale, plan does not include it, server not opted in, held ahead of wipe, platform switch off, excluded, another update in progress.
- The server's Plugins tab shows the automatic updater's state for that server: whether it will consider the server at all (and if not, why), when it last ran and when it next runs.
- When the reason is "the plugin on this server is not signed by this Panel", the explanation says so and links to the Plugin admin page's way to get the correct file.
- The Plugin admin page shows the RustArchon plugin (and Updater) version currently served, whether it is built-in or a published release, and offers "Download signed with the active key" for it, with the same audit-log entry that signed downloads of releases already create. The file is the same for every server, because signing is by Panel key, not by server; the page says so and gives the install instruction (replace the plugin file on the game server once; after that updates flow).

## Acceptance criteria
- [ ] For each refusal code the update path can return, a server whose state triggers it shows that reason (text and code) on the offer, without starting an update.
- [ ] A server whose RustArchon plugin is signed by another Panel's key shows the not-signed-by-this-Panel explanation on its offers and on the Plugins tab.
- [ ] The decision shown on an offer is the same one a click or the automatic pass would get (one source of truth, not a copy of the rules).
- [ ] The server page shows the automatic updater's eligibility for the server, with the reason when ineligible, and last/next pass times.
- [ ] The Plugin admin page shows the served version and source, and a signed download of the currently served file works when no release has been published.
- [ ] Downloading it writes an audit-log entry; no secrets are exposed; permissions match the existing signed-download of a release.
- [ ] Every new message is localized like existing Panel strings.

## Out of scope
- Changing which gates exist or their default values.
- Automatically replacing the plugin on a server whose plugin is not signed by this Panel (that would defeat the signing trust model).
- Per-server signing (signing is per Panel key).
- Changes to diagnostic logging (change 001).

## SPEC.md sections affected
- Plugin updates / Plugins tab: offers carry their full blocked reason; automatic updater status.
- Plugin admin page: served version and signed download of the served plugin.

## Open questions
1. Should the automatic updater's last/next pass times be per platform (one pass for all servers) shown on each server, or only on the admin Plugin page? Proposed: on both.
2. Should the Panel also tell the admin when a server's plugin was found signed by a different Panel as a persistent warning on the server header (not just the Plugins tab)? Proposed: yes, a small badge.
