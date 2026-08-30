# Licensing notice

RustArchon (this project - the web front end, API, worker, and their shared/messaging/RCON
libraries) is licensed under the **GNU Affero General Public License v3.0 or later
(AGPL-3.0-or-later)**. See [`LICENSE`](LICENSE) for the full text.

Because RustArchon is a network-accessible application, AGPL-3.0 §13 requires that anyone
interacting with it remotely be offered a way to get its corresponding source. That's what the
"Source" link in the site footer and the app sidebar points at - the RustArchon GitHub
organization, [github.com/RustArchon](https://github.com/RustArchon).

## Third-party licensing: JumpStart

RustArchon is built on top of [JumpStart](https://github.com/cyberknet/JumpStart), a separate
framework with its own repository, maintained independently of this project. **JumpStart is
licensed under GPL-3.0-or-later, not AGPL, and is not itself part of RustArchon's AGPL
codebase.**

Combining a GPL-3.0 library with an AGPL-3.0 application is an explicitly-permitted pairing: GPLv3
§13 grants a special permission allowing a GPLv3-covered work to be conveyed in combination with a
work licensed under the GNU Affero GPL, and AGPLv3 §13 grants the mirror-image permission in the
other direction. That's the specific carve-out that makes this combination possible - the two
licenses would not otherwise be compatible for linking. The combined work distributed here is
under AGPL-3.0-or-later; JumpStart's own source remains available, under its own license, at its
own repository linked above.

This isn't legal advice - if licensing compliance here matters for your situation, verify this
reasoning independently (starting with the [FSF's license compatibility
guidance](https://www.gnu.org/licenses/gpl-faq.html#AllCompatibility)) rather than relying solely
on this note.
