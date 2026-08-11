# ADR 0004 -- Offensive-tradecraft boundary: by technique kind, not category

- **Status:** Accepted
- **Date:** 2026-08-11
- **Supersedes:** the boundary statement in
  [ADR 0001 § Sensitive-capability boundary](0001-stack-and-architecture.md),
  [architecture.md § 13](../architecture.md), and [AGENTS.md § 7](../../AGENTS.md)
  as those sections originally read ("all concrete offensive tradecraft is
  out-of-tree"). Those sections have been rewritten to forward-reference this
  ADR.

## Context

Rod is authorized-use red-team tooling ([RESPONSIBLE-USE.md](../../RESPONSIBLE-USE.md)):
penetration tests under signed scope, internal red-team exercises, CTF and lab
work, and defensive research. The project's stated purpose (Sec 14, "Capability
bar") is to be a learning, research, and operational platform whose capability
substrate is on par with established offensive frameworks.

The original boundary -- drawn in ADR 0001 and restated in architecture.md § 13
and AGENTS.md § 7 -- held that **all** concrete offensive tradecraft lives
out-of-tree: the core ships only the contract, registration, and dispatch, and
the operator supplies every module. Recon and `lateral.move` were the only
sanctioned in-repo exceptions.

That boundary is more conservative than the field. Mainstream open-source C2
frameworks -- Metasploit (BSD-3-Clause), Sliver, Havoc, Empire, Mythic -- ship
concrete, documented offensive techniques in their public repositories: payload
encoders, token manipulation, standard persistence mechanisms, scheduled-task
and service-based remote execution, credential-store enumeration, file
collection, and C2 exfiltration. Their stance is that techniques which are
documented in OS vendor references, taught in offensive-security curricula, and
already present in peer tools are defensible to publish under an authorized-use
license, while reserving in-the-wild 0days, weaponized PoCs, and unpublished
evasion for out-of-tree modules. Keeping Rod stricter than its peers limits its
value as a learning and research substrate without meaningfully reducing risk,
because every technique in question is already publicly available elsewhere.

The open items in [todo.md](../todo.md) asked for implant-side handlers across
the lateral, persist, collect, and exfil categories. Implementing them under the
old boundary would have required reversing it verb-by-verb; this ADR reverses
the boundary once, by principle, so the per-verb decisions follow a stable rule.

## Decision

The boundary between in-repo and out-of-tree tradecraft is decided by **what
kind of technique it is**, not by which capability category it belongs to.

### In-repo: standard, mainstream, documented techniques

The reference implants implement techniques that meet **all** of:

- documented in OS vendor references (Win32 API / MSDN, systemd, cron, OpenSSH,
  ...),
- covered in offensive-security curricula and existing public tooling, and
- carrying a legitimate system-administration or defensive-research side.

The current in-repo offensive surface:

| Category | In-repo surface |
|---|---|
| Core | shell execution, file transfer plumbing, host enumeration |
| Recon | TCP port scan, host enumeration, service banner probe |
| Lateral | child-implant derivation; Windows access-token duplication; remote execution over documented admin channels (SCM, scheduled tasks, SSH) |
| Persist | Windows `Run` registry key, scheduled tasks, services; Linux cron, systemd user units |
| Collect | filesystem reads; standard credential-store *listings* (SSH key presence + fingerprints, AWS profile names, Windows `cmdkey /list` names) without dumping secret material |
| Exfil | transfer over the C2 channel into engagement-scoped artifact storage (Sec 11) |

### Out-of-tree: sensitive tradecraft

The following remain **pluggable capability contracts only** -- the core defines
their interface, registration, dispatch, and data model; concrete tradecraft is
supplied as separate, opt-in, out-of-tree modules:

- in-the-wild zero-day exploits and weaponized proof-of-concept code,
- novel or unpublished detection-evasion and defensive-product bypasses,
- LSASS memory dumping for credential theft (no benign-system-tool side; tightly
  coupled to active credential theft), and
- input capture (`collect.keylog`).

The `evasion` and `exploit` categories are contract-and-dispatch only in their
entirety (Sec 10.2), unchanged by this ADR.

### Default

When it is unclear which side a technique falls on, default to **out-of-tree**
and raise the question. Tightening the in-repo set later is cheap; loosening it
under pressure is how the boundary erodes.

## Rationale

- **Peer-framework parity.** Metasploit, Sliver, and Havoc publish the
  techniques listed under "in-repo" above. Matching that surface makes Rod a
  useful learning and research platform rather than a contract-only skeleton,
  and removes a self-imposed restriction that did not reduce risk the techniques
  in question are already public.
- **Drawn by kind, not category.** The original boundary drew by category
  (lateral/persist/collect/exfil all out-of-tree, recon in-repo), which is
  coarse: `lateral.token` (documented Win32 token duplication) and an LSASS
  dump are both "lateral/collect" but have nothing in common operationally.
  Drawing by technique kind places each technique where its defensibility
  actually sits.
- **Authorized-use framing already in place.** RESPONSIBLE-USE.md and
  SECURITY.md establish the authorized-use license and disclosure model. The
  in-repo techniques are defensible under that framing; the out-of-tree set is
  not.
- **Reverses a standing self-restriction, not a security control.** The old
  boundary was a project policy choice, not a mitigation of a Rod-specific
  threat: SECURITY.md and the wire-protocol/crypto design are the actual
  security controls, and they are untouched by this ADR.

## Consequences

- **Positive:** the reference implants become operationally useful for
  authorized red-team work and study out of the box; the per-verb decisions in
  the lateral/persist/collect/exfil categories follow a single stable rule
  instead of a category-wide ban; the boundary now matches peer frameworks, so
  contributors are not surprised by a stricter-than-field rule.
- **Negative:** the in-repo offensive surface grows, and so does the
  responsibility to keep handlers correct, scoped, and OPSEC-attributed. Each
  in-repo handler carries per-command OPSEC metadata in its capability
  descriptor (Sec 10.1) and writes to the engagement audit trail on execution;
  that discipline is now load-bearing across more code.
- **Risk:** a relaxed boundary can drift. Mitigation: the "default to
  out-of-tree" rule above, and every future addition that crosses the line
  should cite this ADR (or propose its amendment) in the commit body.

## Alternatives considered

- **Keep the original boundary; implement the todo items out-of-tree.** Rejected:
  it leaves the reference implants as a contract-only skeleton, contradicts the
  field, and means every operator rebuilds the same standard handlers
  independently.
- **Delete the boundary entirely; everything in-repo.** Rejected: in-the-wild
  0days, weaponized PoCs, and novel evasion have no business in a public
  authorized-use repository regardless of how the line is drawn, and publishing
  them creates real harm (working offensive code against unpatched targets) the
  in-repo standard techniques do not.
- **Draw the line per category (original rule), with more categories in-repo.**
  Rejected: category is the wrong axis. A category-wide "in" pulls in LSASS
  dumping with credential-store listings; a category-wide "out" pushes out
  documented token manipulation with novel evasion. Technique kind is the axis
  the decision actually turns on.
