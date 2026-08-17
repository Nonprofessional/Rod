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

- [ ] **Dispatch without polling.** The beacon writer polls each implant's
      queue every 25 ms; replace the poll with a per-queue channel wake so a
      queued task is pushed immediately and an idle fleet costs nothing.
      _AC:_ a queued task is dispatched on push, with no poll loop in the
      writer path.
- [ ] **Staged uploads.** `file.push` carries its payload inline in the task
      arguments (1 MiB cap). Add the per-verb typed-arm path (Sec 10's escape
      hatch) so a larger upload streams down in chunks the way `file.pull`
      streams up. _AC:_ a 10 MiB file lands whole on the target through the
      tasking channel.
- [ ] **Interactive shells.** Shell tasking is one-shot today. Add the
      streaming task shape (a session-scoped PTY channel for `shell.exec`)
      over the existing stream contract. _AC:_ an operator types into a live
      shell on a connected implant.

## Payload generation (architecture.md Sec 6)

- [ ] **Stage-1 stager artifact.** The stager class exists in the taxonomy and
      the class gate (`file.pull` only) but no build path emits a stager. Add
      the stager output class to the .NET build unit: a minimal loader that
      fetches and runs a stage-2 artifact. _AC:_ building a stager yields a
      runnable stage-1 that pulls its stage-2 and enrols.

## Transports (architecture.md Sec 8)

- [ ] **DNS listener.** The listener abstraction is in place; add the DNS
      transport for egress-restricted targets. _AC:_ an implant checks in over
      DNS against a real listener entry.

## Tradecraft extension kit (architecture.md Sec 10.2, Sec 13)

The out-of-tree seams exist (config-listed server modules, compile-time implant
handlers); the kit makes them effortless. See
[extending/tradecraft.md](extending/tradecraft.md) for the current seams.

- [ ] **Out-of-tree implant handlers without a fork.** A configured extension
      directory whose sources the .NET build unit overlays onto the per-build
      staging tree, with a generated registrations file feeding
      `HandlerRegistry.Default`'s `additional` seam. _AC:_ dropping a handler
      source into the directory and building yields an artifact that runs it --
      no fork of the implant tree to maintain.
- [ ] **Advertise contract-only verbs on baked artifacts.** The handshake
      advertisement intersects compiled handlers with the baked class set, so
      `evasion.*`/`exploit.*` handlers never appear there (dispatch is
      unaffected). Bake the class set plus the registered contract-only verbs
      so the roster reflects reality. _AC:_ an artifact built with an
      out-of-tree evasion handler advertises the verb at handshake.

## Implant reach (architecture.md Sec 8, implant-contract.md)

The protocol is the product; the bar is that a from-scratch implant can be
written against `docs/implant-contract.md` alone.

- [ ] **Plain-HTTP envelope listener.** The recorded escape hatch, now
      scheduled for reach: the same rod.v1 Frames carried as
      varint-length-delimited sequences in ordinary HTTPS request/response
      bodies over the same client certificates -- one POST is one poll
      check-in (request body: handshake + results + exfil chunks; response
      body: handshake response + tasking). Drops the gRPC/HTTP-2 requirement
      so Tier 0 is reachable from any language with an HTTP and a protobuf
      codec. _AC:_ a from-scratch implant written from the contract doc
      alone, using no gRPC library, enrolls, checks in, and completes a task.
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
