# Rod -- Todo

Open work only. An item leaves this file the moment it ships -- its record
is the commit history, and the design it followed lives in
[architecture.md](architecture.md); the designed-but-deferred items (the
redirector control plane, Sec 8; sealing, Sec 9) stay where they are
designed. Nothing here is an archive of the done.

Each item names the architecture section it serves and carries a one-line
acceptance criterion (_AC:_), so "done" stays testable. Follow the
[repository conventions](../AGENTS.md).

Lean is the standing default: an addition must say what an engagement
cannot do without it; refactors, deletions, and answering with docs
instead of code are first-class items here, equal to features. New work
starts from a gap an actual engagement surfaces.

Active is advisory order, not a queue: depth before breadth -- making the
platform's standing promises real (the compiled capability surface, the
advertised reach) ahead of widening the story, the small bounded items
behind those -- and the right item to take is still the one an engagement
actually needs. On hold is parking, not rejection: each item there names
the fact or decision that reopens it, and a cleared condition prompts the
move back into Active -- it does not make the move on its own.

## Active

- **The DNS front: the node's datagram relay and the typed dial's
  dissolution** (serves architecture.md Sec 8). The control plane shipped
  for every TCP family (the web pair, the raw socket, DoH); the dns
  transport's front remains the recorded seam because the L4 forwarder
  carries no datagram carriage and the dns front's public endpoint is a
  different shape from the listener's own -- the resolver implants dial
  (host[:port]), not the zone. Shape: one generic UDP relay in the
  redirector binary (source-keyed datagram forwarding, protocol-blind --
  the one carriage addition the data plane ever takes, per the "carriage
  dimension done once" discipline), rules carrying transport `udp` for dns
  listeners, front attach accepting the dns family with its resolver-shaped
  endpoint, and the build parser baking `dns://<front resolver>/<zone>`
  when the listener's primary front names one -- which dissolves the typed
  dial's free-form escape for good.
  _AC:_ a dns listener fronted by an enrolled node serves the chunked TXT
  enroll exchange and a poll round trip through the front, and a build
  against the fronted listener bakes the front's resolver without a typed
  endpoint in the request.

## On hold

- **iOS shell for the Rust implant** (serves architecture.md Sec 12.2).
  Parked on a macOS build host: the Apple link needs Xcode's SDK, which
  the Linux host cannot carry. Shape: the same library-plus-shell pattern
  as the Android item against aarch64-apple-ios; delivery is inherently
  sideloading territory (a signed carrier app or a jailbroken device), an
  operational constraint the runbook documents rather than something the
  build can remove.
  _AC:_ cross-compiling from a macOS host produces a static library a
  carrier app links, and the enrollment leg runs.
