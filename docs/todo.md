# Rod -- Todo

Open work only: completed items are checked off and trimmed, and their detail
lives in the commit history and [architecture.md](architecture.md). The
designed-but-deferred security items (sealing, replay nonces) stay in
architecture.md Sec 9.

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

The protocol is the product; the bar is that a from-scratch implant can be
written against `docs/extending/implants.md` alone. The plain-HTTP envelope
listener shipped: one POST is one poll check-in over the same client
certificates, dropping the gRPC/HTTP-2 requirement -- the acceptance test is
a from-scratch implant, no gRPC library, that enrolls, checks in, and
completes a task against it.

- [ ] **Tier 0 conformance harness.** A rig that drives a candidate implant
      against a live teamserver and reports pass/fail per contract clause
      (enroll shapes, handshake order, result/chunk discipline, signature
      verification, kill-date refusal). _AC:_ pointing the harness at the
      reference implant passes, and at a deliberately broken one fails with
      the violated clause named.

## Security (architecture.md Sec 9)

- [ ] **Tasking replay nonces.** Command signing binds tasking to its implant
      but a captured signed frame still verifies on replay to the same
      implant. Add per-session nonces to the signed tuple. _AC:_ a replayed
      task frame is rejected by the implant and the rejection surfaces on the
      task.
