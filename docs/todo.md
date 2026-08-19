# Rod -- Todo

Open work only: completed items are checked off and trimmed, and their detail
lives in the commit history, [architecture.md](architecture.md). The one
designed-but-deferred security item (sealing) stays in architecture.md Sec 9.

Add items freely; check them off as they ship. Each item carries a one-line
acceptance criterion. Keep the [repository conventions](../AGENTS.md): small
focused commits, English only, the offensive-tradecraft boundary
(architecture.md Sec 13), and reference the architecture section, not a
historical milestone id, from commit bodies.

## Tasking and sessions (architecture.md Sec 10.3)

None open: the streaming task shape (interactive shells) shipped as
`shell.interact`.

## Tradecraft extension kit (architecture.md Sec 10.2, Sec 13)

None open: the kit shipped -- a configured extension directory
(`Build:ImplantExtensionDirectory`) overlays out-of-tree handler sources onto
every implant-class build. See [extending/tradecraft.md](extending/tradecraft.md).

## Implant reach (architecture.md Sec 8, extending/implants.md)

None open: the plain-HTTP envelope check-in shipped (one POST is one poll
check-in, no gRPC requirement), and the Tier 0 conformance harness drives a
candidate against a live teamserver per contract clause
(`tests/teamserver/Rod.Conformance.Tests/`).

## Security (architecture.md Sec 9)

None open: the tasking replay nonces shipped -- negotiated at handshake,
per-implant monotonic, covered by the tasking signature, with the refusal
surfacing on the task.
