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

- [ ] **Check in over plain HTTP from the .NET implant.** The cleartext
      envelope check-in (identity by handshake id) is served, but the
      in-tree implant speaks gRPC only -- so a build against a plain
      `http` listener still must name a TLS beacon listener, and an
      engagement whose only egress is cleartext HTTP-shaped cannot run
      the reference implant on one port. When the baked beacon endpoint
      is `http://`, drive the envelope POST cycle instead of the gRPC
      stream (poll cadence; channel verbs refuse, as poll mode already
      does).
      _AC:_ a stage2 built against a plain `http` listener with no beacon
      named enrolls and checks in online over that single cleartext port.
- [ ] **Harden the implant certificate profile.** Issued leaves carry the
      implant id as the CN and the engagement id under a custom OID -- a
      GUID common name with an unknown extension is itself a toolchain
      fingerprint, and host forensics reads both. Move the identity into
      URI SAN entries (the shape legitimate service certificates use) and
      make the remaining fields match a conventional profile.
      _AC:_ an issued leaf exposes no GUID CN and no custom OID; identity
      binds through SANs; pinning and check-in verification are unchanged.
- [ ] **Move implant keys to ECDSA P-256.** First-run RSA-2048 keygen
      costs ~100ms on-target for no benefit over a modern curve, and RSA
      leaves and handshakes are the largest certificates on the wire. The
      enroll protocol already carries an algorithm-agnostic SPKI; issue
      over the EC half.
      _AC:_ enrollment issues over an ECDSA public key, the mTLS check-in
      presents it, and first-run keygen is effectively instantaneous.
