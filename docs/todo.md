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

- [ ] **Check in from the .NET implant over the envelope cycle.** The
      cleartext envelope check-in is served (identity by handshake id
      today), but the in-tree implant speaks gRPC only -- an engagement
      whose egress is HTTP-shaped cannot run the reference implant single-
      port on `http`, and every build carries the gRPC stack it does not
      need. Add the envelope POST client as the implant's default check-in
      for `http://` and `https://` beacon endpoints (poll cadence; channel
      verbs refuse, as poll mode already does). The build parser's
      cleartext-enroll refusal (the beacon-split requirement added when
      http beacons could not work at all) relaxes with it: naming a beacon
      listener over cleartext stays available as the hardened option, and
      the Build form stops demanding it.
      _AC:_ a stage2 built against a plain `http` listener with no beacon
      named enrolls and checks in online over that single cleartext port,
      and the same artifact shape runs against an `https` listener.
- [ ] **Authenticate check-ins with a per-artifact key, not a TLS client
      certificate.** A TLS CertificateRequest is itself a fingerprint --
      an ordinary website never asks the visitor for one, so an IDS flags
      the handshake -- and mainstream HTTP(S) C2s (Cobalt Strike, Havoc,
      Mythic) authenticate implants at the application layer instead:
      per-build symmetric keys, metadata encrypted and signed under them.
      Mint a key per build (the envelope-key shape), bake it, cover a
      nonce in every check-in, verify in the beacon routes, and stop
      requesting client certificates on the `Https` transport entirely.
      The same key envelopes the check-in frames, so the cleartext `http`
      posture carries confidential content, not just authenticated
      content -- the Cobalt Strike metadata model. The build surface
      keeps the two phases as two independent Advanced knobs: the
      enroll-body shaping stays a three-way pick, while check-in
      protection is its own toggle (default on; off is the lab-debug
      plaintext frame -- the disguise ladder does not apply to binary
      frames, and the key is the authentication).
      _AC:_ a stage2 built against an Https listener performs enrollment
      and check-ins whose TLS handshake carries no certificate request,
      authenticated by the baked key, and reports online.
- [ ] **Trim each build to the transport it dials.** Every artifact today
      compiles the whole implant tree, so a plain-HTTP build still carries
      the gRPC client it can never use -- surface, size, and fingerprint
      for nothing. Select transport modules at bake time from the same
      tree (whole source files in or out per build, the BakedProfile
      generation mechanism extended), with the trimmer as the backstop.
      _AC:_ a stage2 built for an `http`/`https` listener contains no
      gRPC client code, and an mTLS-shaped build keeps the stream mode.
- [ ] **Compile only the verbs the artifact carries.** Class-based verb
      gating today is behavioral: a reduced-class build compiles the full
      handler set and bakes a gutted verb list, so the code for
      capabilities the artifact will never run still ships inside it --
      surface, size, and a forensic confession in one. Extend the
      bake-time trimming (the same whole-file mechanism as the transport
      selection) to handler modules: the build names the verbs, unused
      handler sources stay out of the compilation, and the reduced
      classes become genuinely reduced binaries.
      _AC:_ a build whose verb set excludes keylogging contains no
      keylog handler code, and a full Stage2 build is unchanged.
- [ ] **Retire the `HttpsEnvelope` listener entry.** It exists to name an
      endpoint whose purpose is envelope-only reach; once the envelope
      cycle is the default web check-in on `http`/`https`, every web
      listener serves it and the entry says nothing the transport list
      does not. Remove the entry (existing definitions migrate to the
      nearest surviving transport), leaving `https`, `http`, `mtls`,
      `dns`, `smb`, `tcp`.
      _AC:_ the create form and the transport enum surface six transports,
      and an engagement that held an https-envelope definition binds it
      again after a restart under its migrated shape.
- [ ] **Harden the implant certificate profile.** Issued leaves carry the
      implant id as the CN and the engagement id under a custom OID -- a
      GUID common name with an unknown extension is itself a toolchain
      fingerprint, and host forensics reads both. Move the identity into
      URI SAN entries (the shape legitimate service certificates use) and
      make the remaining fields match a conventional profile. Serves the
      mTLS posture, which stays for operators who want the PKI shape.
      _AC:_ an issued leaf exposes no GUID CN and no custom OID; identity
      binds through SANs; pinning and check-in verification are unchanged.
- [ ] **Move implant keys to ECDSA P-256.** First-run RSA-2048 keygen
      costs ~100ms on-target for no benefit over a modern curve, and RSA
      leaves and handshakes are the largest certificates on the wire. The
      enroll protocol already carries an algorithm-agnostic SPKI; issue
      over the EC half.
      _AC:_ enrollment issues over an ECDSA public key, the mTLS check-in
      presents it, and first-run keygen is effectively instantaneous.
