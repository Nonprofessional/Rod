# ADR 0011 -- Reference redirector: opaque L4 TCP forwarder (Native AOT)

- **Status:** Accepted
- **Date:** 2026-08-13
- **Related:** [architecture.md](../architecture.md) Sec 7 (deployment/tradecraft)
  and Sec 8 (transports, listeners, and redirectors);
  [ADR 0009](0009-single-in-tree-toolchain-dotnet.md) (the .NET Native AOT
  direction this forwarder instantiates);
  [ADR 0004](0004-offensive-tradecraft-boundary.md) (the in-repo tradecraft
  boundary); the operations runbook at
  [../operations/redirectors.md](../operations/redirectors.md)

## Context

architecture.md Sec 8 has always described redirectors as near-stateless
forwarders that "terminate transport only as needed and forward opaque
payloads," and the production-hardening todo ("Redirector deployment story")
asked for the real redirector hosts behind the listener's public endpoint. The
direction was set by [ADR 0009](0009-single-in-tree-toolchain-dotnet.md): no
redirector shipped in-tree yet, and when one did it would be a .NET Native AOT
forwarder, not the Go binary ADR 0001 originally imagined.

Half of the rotation story already shipped at M4.4: `POST /listeners/{id}:repoint`
swaps a listener's public endpoint without touching the backend bind, so a
burned redirector is *severed* the moment its listener points elsewhere. What
was missing was the other half -- an actual forwarder binary an operator
deploys as the redirector, plus the deploy/rotate runbook that makes the swap
end to end rather than "just in the registry." This ADR records the design of
that forwarder and closes the todo item.

## Decision

Rod ships a single in-tree reference redirector: an **opaque L4 TCP forwarder**,
published as a **.NET Native AOT** single binary, that fronts exactly one
teamserver listener per process.

- **L4 byte splice, not an L7 reverse proxy.** The forwarder accepts a TCP
  connection on its public endpoint, optionally checks the source IP against a
  CIDR allow-list, connects the configured upstream (the listener's bind
  address), and copies bytes verbatim in both directions with correct
  half-close. It never inspects or alters the payload.
- **Why L4: the beacon is mTLS.** The implant enrolls over HTTPS and beacons
  over an mTLS stream (HTTP/2 with a client certificate bound to
  `(implant_id, engagement_id)`, architecture.md Sec 9). An L7 proxy that
  terminates TLS would have to re-present the server identity and could not
  preserve client-certificate authentication end to end; an L4 splice carries
  both the HTTPS enroll request and the mTLS beacon stream through unchanged,
  preserving the channel the security model depends on.
- **Why opaque: the redirector is untrusted.** architecture.md Sec 9 keeps
  redirectors out of the tenancy and identity model ("redirectors never enforce
  tenancy") and reserves payload confidentiality for a future Sealing layer so
  "untrusted redirectors cannot read or alter" tasking. An L4 forwarder cannot
  read the inner payload even if it wanted to, which is exactly the posture the
  model assumes.
- **Native AOT, BCL-only, single rule per process.** `PublishAot` produces a
  stripped native binary with no runtime install (2 MB on linux-x64), the
  property ADR 0009 wanted. The project is BCL-only and reflection-free to stay
  AOT-clean; `IsAotCompatible` plus repo-wide `TreatWarningsAsErrors` makes any
  AOT violation fail the build, and CI publishes the binary on every change.
  v1 runs one forwarding rule per process; multi-port fronting is one process
  per port, which is cheaper and more robust (a burned port does not drag the
  others down).
- **Source-IP filtering only.** The allow-list is the only routing an opaque L4
  forwarder can do. The malleable `User-Agent`/URI routing of Sec 7 lives inside
  TLS and is therefore invisible to this forwarder; it stays a
  TLS-terminating-edge concern an operator layers on at deployment time, not
  something the in-tree forwarder claims to do.
- **In-tree, in `Rod.slnx`.** Unlike the reference implant (external because
  the teamserver build unit compiles it per payload-build request), the
  redirector is a plain standalone binary with no in-house references, so it
  ships in `Rod.slnx` for central build, test, and format coverage.

## Rotation flow (the end-to-end swap)

1. Deploy a fresh redirector binary on a new host, pointed at the listener's
   bind address.
2. Repoint the listener: `POST /listeners/{id}:repoint` with the new
   redirector's public endpoint. The Kestrel bind is untouched; the old
   endpoint stops resolving to any listener and is severed.
3. Verify traffic through the new endpoint, then decommission the burned host.

The teamserver never restarts and the backend bind never moves; only the public
endpoint implants dial changes. The full runbook with build, deploy, verify, and
teardown steps is at [../operations/redirectors.md](../operations/redirectors.md).

## Rationale

- **mTLS correctness forces L4.** Preserving the client certificate is
  non-negotiable, and the only way to do that without terminating TLS is to
  forward opaque bytes. An L7 proxy that did not terminate TLS would still be an
  L4 forwarder in practice.
- **L4 is the documented, mainstream technique.** It is `socat`/`rinetd`
  semantics, squarely inside ADR 0004's "standard, mainstream, documented
  techniques" boundary; the redirector carries no evasion and no payload
  awareness.
- **Native AOT matches the original ask.** ADR 0001 reached for Go on the edge
  for a tiny, static, no-runtime forwarder; ADR 0009 noted Native AOT now
  delivers that from .NET. Publishing confirms it: a single 2 MB stripped
  binary, no runtime install.
- **The contract is unchanged.** Nothing about the wire protocol, the listener
  abstraction, or the repoint endpoint changes; the forwarder is pure edge
  infrastructure behind the existing public endpoint.

## Consequences

- **Positive:** the redirector rotation is now end to end -- a burned redirector
  is swapped by deploying a fresh binary and repointing the listener, with no
  backend change and no service interruption. One in-tree toolchain covers the
  teamserver, implant, and redirector.
- **Positive:** because the forwarder is opaque, the future Sealing layer (Sec 9)
  composes cleanly -- sealed payloads already pass through verbatim.
- **Negative:** an L4 forwarder cannot do User-Agent/URI routing or serve a cover
  site; deployments that need malleable L7 routing must terminate TLS at the edge
  themselves, in front of (or instead of) this forwarder. That is an operator
  deployment concern, not an in-tree capability.
- **Negative:** one rule per process means multi-port fronting runs several
  processes. This is deliberate (robustness and simplicity) but is more moving
  parts than a single multi-listener daemon.

## Alternatives considered

- **L7 reverse proxy (ASP.NET Core).** Could route by URI/User-Agent and serve a
  cover site, but it terminates TLS and so breaks the beacon's client-certificate
  authentication unless it forwards at L4 anyway. Rejected for the in-tree
  reference: the L4 behavior is the part the security model needs, and the L7
  extras are deployment-time concerns.
- **L4 forwarder with a malleable L7 peek for plaintext HTTP.** Adding an
  HTTP-aware mode for the (plaintext-able) enroll channel would re-introduce
  transport-specific logic for marginal benefit and complicate the AOT-clean,
  reflection-free property. Rejected for v1; a deployment that needs it layers a
  TLS-terminating edge in front.
- **Multi-rule JSON config.** A single process holding several listeners is more
  state to configure and a single point of failure across ports. Rejected in
  favor of one process per port (near-stateless, cheap, independently swappable).
- **Keep the redirector out of tree (status quo through ADR 0009).** Leaves the
  rotation half-finished -- repoint exists but there is nothing to deploy. This
  ADR closes that gap.
