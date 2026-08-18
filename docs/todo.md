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

None open: the kit shipped. A configured extension directory
(`Build:ImplantExtensionDirectory`) overlays out-of-tree handler sources onto
every implant-class build, with generated registrations feeding
`HandlerRegistry.Default`'s `additional` seam -- dropping a handler source in
and building yields an artifact that runs it, no fork to maintain -- and the
bake carries the class set plus the ungated contract-only verbs, so an
artifact compiled with an out-of-tree evasion or exploit handler advertises
the verb at handshake. See [extending/tradecraft.md](extending/tradecraft.md)
for the authoring shape and the seams' current limits.

## Implant reach (architecture.md Sec 8, extending/implants.md)

None open: the reach work shipped. The plain-HTTP envelope listener carries
the same rod.v1 frames as varint-length-delimited HTTPS bodies -- one POST is
one poll check-in over the same client certificates, dropping the gRPC/HTTP-2
requirement (its acceptance test is a from-scratch implant, no gRPC library,
that enrolls, checks in, and completes a task) -- and the Tier 0 conformance
harness (`tests/teamserver/Rod.Conformance.Tests/`) drives a candidate
implant against a live teamserver and reports pass/fail per contract clause:
the reference implant passes every clause, and a deliberately broken one
fails with the violated clause named.

## Security (architecture.md Sec 9)

None open: the tasking replay nonces shipped. The arm is negotiated at
handshake (`replay_nonces` advertised, echoed by the server, sticky on the
implant so a later handshake cannot downgrade it); every dispatched task for
a negotiating implant carries a per-implant monotonic `task_nonce` covered by
the tasking signature (the five-element canonical tuple), the implant refuses
any nonce at or below its accepted floor, and the refusal is reported as the
task's `Failed` result -- a replayed frame surfaces on the task. Non-advertising
implants keep the original four-element tuple unchanged (the evolution rules'
negotiated-addition shape); the reference implant advertises, and the
conformance harness's hostile probe replays a genuinely signed control frame
to pin the refusal.
