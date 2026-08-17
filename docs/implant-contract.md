# Rod -- Implant contract

The compliance ladder for a from-scratch implant. The wire protocol is the
product (architecture.md Sec 4.2, Sec 12.2): any language that can speak it
can be a Rod implant. This file defines what "speak it" minimally means, what
is optional hardening, and the rules that keep the minimum small while the
platform grows. The reference .NET implant (`src/implant/dotnet/`) implements
every tier; it is the worked example, not the obligation.

The contract sources are `src/teamserver/Rod.Protocol/protos/rod.proto` (the
wire messages), the HTTP enrollment route, and the baked build profile. A
community implant compiles the proto with its own toolchain and provides
everything else itself.

## Tier 0 -- Interop (required)

The smallest implant that enrolls, checks in, and executes tasking:

1. **Enroll.** Generate an RSA-2048 key pair. POST the public key with the
   stager token to the enroll endpoint (HTTP, JSON; the route and body shape
   follow the reference implant's enroll client). Receive the implant id, the
   engagement id, a DER leaf certificate over the submitted public key, and
   the CA chain. Keep the private key; never transmit it.
2. **Beacon.** Open the `Beacon.CheckIn` bidirectional stream over mTLS,
   presenting the leaf certificate (gRPC over HTTP/2; the transport profile
   names the endpoint). Holding the stream open (stream mode) and
   drain-then-close-sleep cycles (poll mode) are both Tier 0 -- the server
   treats them identically.
3. **Handshake.** Send `HandshakeRequest` (protocol version 1.0, the implant
   id, the advertised verb list) as the first frame; require
   `HANDSHAKE_STATUS_OK`. Treat every other status as permanent: log it and
   terminate rather than retry.
4. **Task loop.** Parse each downstream `TaskRequest`, execute its verb
   against its opaque argument string, and write a `TaskResult` echoing the
   task id with the outcome and output. The verb grammar belongs to the
   implant's own handlers; the server gates verbs, it does not parse
   arguments.

That is the whole obligation. An implant that stops here interoperates fully:
it appears on the roster, is taskable, and its results and audit trail are
indistinguishable from the reference implant's.

## Tier 1 -- Hardening (the implant author's choice)

Each item hardens the implant with no server-side counterpart requirement --
the server cannot observe whether an implant adopted any of them:

- **Tasking signature verification.** Verify the `TaskRequest` signature
  (RSASSA-PSS over SHA-256 on the canonical tuple documented on the proto
  message) against the CA certificate from enrollment before executing, and
  report a failure as a `Failed` task rather than executing. The reference
  implant verifies; skipping verification leaves the implant trusting the
  channel, which is the pre-signing posture (architecture.md Sec 9).
- **Kill date.** Refuse to start past the baked kill date and re-check it
  each beacon cycle. The teamserver refuses handshakes past it regardless;
  the local check bounds a lost implant that can no longer reach any server.
- **Beacon discipline.** The baked sleep with jitter, and exponential
  backoff on consecutive failures, so a down teamserver is not polled at
  beacon rate. The check-in mode is the implant's choice on the same stream
  contract: hold the connection open (stream) or drain-then-close-sleep
  (poll, the low-and-slow shape).

## Tier 2 -- Optional features

Adopt per deployment need; absence degrades the feature, not interop:

- **Exfil chunking** -- `ExfilChunk` frames after a `TaskResult` stream bulk
  data into the artifact store.
- **Malleable enroll presentation** -- the baked URI path, User-Agent,
  headers, timeout, and body envelope shape the enroll request.
- **Child derivation** -- the parent-naming enroll flow behind
  `lateral.move` (architecture.md Sec 5.2).

## Evolution rules

These rules bind every future protocol change; they are what keeps Tier 0
from quietly growing:

1. **Additive only.** New fields take new field numbers; numbers are never
   reused or repurposed. Unknown fields and enum values are ignored, never
   errors. A newer server must serve a Tier 0 implant unchanged.
2. **No new mandatory work on the task path.** A protocol addition that
   would require every implant to implement new cryptography or new
   processing to keep interoperating must instead be negotiated (a handshake
   capability) with a fallback to the existing shape, or it does not ship.
3. **Every addition lands in a tier.** A change to this file accompanies any
   change to rod.proto: the change states its tier and what a Tier 0 implant
   does about it (the usual answer: nothing).
4. **Weight stays server-side.** Capability reach grows in the teamserver,
   the tradecraft modules, and the build pipeline -- not in the minimum an
   implant must carry (architecture.md Sec 14).

## Calibration note

Tier 0's heaviest piece is the gRPC/HTTP-2 channel, not the crypto or the
messages. For a target language with a weak gRPC story, the recorded escape
hatch is a plain-HTTP-envelope listener (architecture.md Sec 8): the same
proto payloads carried as opaque HTTP bodies over the same client
certificates, dropping the gRPC requirement without changing the protocol
semantics.
