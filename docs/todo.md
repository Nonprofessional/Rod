# Rod -- Todo

Open work only. An item leaves this file the moment it ships -- its record
is the commit history, and the design it followed lives in
[architecture.md](architecture.md); the one designed-but-deferred item
(sealing) stays in Sec 9. Nothing here is an archive of the done.

Each item names the architecture section it serves and carries a one-line
acceptance criterion (_AC:_), so "done" stays testable. Follow the
[repository conventions](../AGENTS.md).

Lean is the standing default: an addition must say what an engagement
cannot do without it; refactors, deletions, and answering with docs
instead of code are first-class items here, equal to features. New work
starts from a gap an actual engagement surfaces.

## Transports and identity (architecture.md Sec 8, Sec 9)

The end state these items build toward, in one paragraph: the .NET implant
carries exactly one web check-in client -- the envelope POST cycle, the
shape every mainstream HTTP(S) C2 uses, polled on a jittered sleep --
with authentication at the application layer under a per-artifact key.
The `http` and `https` listeners are single-port and indistinguishable
from ordinary web traffic (no TLS certificate request anywhere), the
token stays enrollment-only, and tasking keeps its signature and replay
nonces regardless of transport. `mTLS` is the dedicated interactive
listener -- per-implant certificates, the persistent gRPC stream, live
channels -- built against only when an engagement wants them. One source
tree; the bake selects which transport modules compile in.

- [ ] **Move implant keys to ECDSA P-256.** First-run RSA-2048 keygen
      costs ~100ms on-target for no benefit over a modern curve, and RSA
      leaves and handshakes are the largest certificates on the wire. The
      enroll protocol already carries an algorithm-agnostic SPKI; issue
      over the EC half.
      _AC:_ enrollment issues over an ECDSA public key, the mTLS check-in
      presents it, and first-run keygen is effectively instantaneous.
