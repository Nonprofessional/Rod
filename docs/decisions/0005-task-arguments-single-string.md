# ADR 0005 -- Task arguments stay a single opaque string

- **Status:** Accepted
- **Date:** 2026-08-11

## Context

A task in Rod is `verb + arguments + result` end to end. The question is what
type `arguments` is at the contract boundary -- the proto field, the core-state
record, the transport DTO, the tradecraft dispatch contract, and both reference
implants' dispatch entrypoints.

Today every one of those boundaries carries arguments as a single opaque
`string`. Verified layer by layer:

| Layer | Site | Type |
|-------|------|------|
| Wire | `src/Rod.Protocol/protos/rod.proto:162` | `string arguments = 3;` on `TaskRequest` |
| Wire result | `src/Rod.Protocol/protos/rod.proto:178` | `string output = 3;` on `TaskResult` |
| Core-state command | `src/Rod.CoreState/Application/TaskService.cs:253` | `string Arguments` on `IssueTaskCommand` |
| Core-state entity | `src/Rod.CoreState/Tasks/Task.cs:26` | `public string Arguments { get; }` |
| Tradecraft dispatch contract | `src/Rod.Tradecraft/Capabilities/CapabilityInvocation.cs:15-17` | `record CapabilityInvocation(string Verb, string Arguments)` |
| Transport request DTO | `src/Rod.Transport/Endpoints/TaskEndpoints.cs:179` | `string? Arguments` (coalesced to `string.Empty`) |
| Server -> wire | `src/Rod.Transport/Endpoints/BeaconEndpoint.cs:254` | `Arguments = dispatched.Arguments` (passthrough) |
| Go implant | `implant/internal/exec/runner.go:75` | `Dispatch(ctx, verb, arguments string)` |
| .NET implant | `implant-dotnet/Internal/Exec.cs:83` | `Dispatch(string verb, string arguments)` |

Parsing is per-handler, in-process, with ad-hoc token splitting. The grammars
in active use are deliberately diverse: whitespace-separated tokens
(`lateral.move`'s `<token> [<class>]`, `persist.install`'s
`<mechanism> <name> <payload>`), a hyphen-separated range (`recon.portscan`'s
`<host> <start-end>`), a comma-separated list (`recon.service`'s
`<host> <port,port2,...>`), a trailing-token-preserves-whitespace shape
(`lateral.exec_remote`'s `<host> <command...>`, `exfil.push`'s
`<name> <path>`), and an optional single-token filter (`persist.list`). There
is no shared parser; every verb rolls its own. Separators in use include
whitespace, `-`, and `,`. Field-count contracts range from 0 to >=3.

The wire proto's own preamble (`rod.proto:152-155`) states the one-shot shape
is the minimum and that "structured arguments arrive in later milestones", and
`CapabilityInvocation`'s own remarks
(`src/Rod.Tradecraft/Capabilities/CapabilityInvocation.cs:10-14`) say the same:
"Arguments stay a single string to match the task shape in core state ...
Structured arguments arrive when a verb needs them; the contract shape is
stable for it." This ADR records that decision where it lives, so it stops
being an inline comment and becomes a citable record.

## Decision

**Keep task arguments a single opaque `string` at every contract boundary.**
The verb is the typed discriminator; the string is the verb's grammar,
parsed by the handler that owns it. A verb may, internally, parse its string
into a richer shape (and most do), but that shape does not cross any interface
or wire boundary.

Structured arguments arrive only when a concrete verb's grammar grows complex
enough that a string is the wrong shape for it -- and when that happens the
right move is to give *that verb* a typed argument on the wire (a proto message
or `oneof` arm), not to retrofit the global `arguments` field. The contract
shape stays stable for it: `TaskRequest.arguments` is already an opaque string
the server does not parse, so adding a structured arm for one verb does not
break any other verb or any existing task.

## Rationale

- **The wire is language-neutral.** A `string` argument is the lowest-common-
  denominator shape every implant language (Go, .NET, C, Nim) can parse with
  its own stdlib. A typed proto message is heavier for a C implant to consume
  and couples every language's argument parser to a shared schema. The
  per-handler string grammar keeps each language free.
- **The grammar is per-verb, not per-system.** `recon.portscan`'s
  `<host> <start-end>` and `persist.install`'s
  `<mechanism> <name> <payload>` have nothing in common. A shared structured
  argument type would either be a bag of optional fields (worse than the
  string, because now the schema lies about what is required) or a `oneof` with
  one arm per verb (which moves the per-verb grammar into the proto without
  removing it). The string keeps the grammar where it is owned.
- **The server never parses arguments.** `TaskService` gates on the verb, the
  transport passes the string through unchanged, and the implant parses it. A
  structured shape on the wire would invite the server to validate fields it
  has no business knowing, blurring the layer boundary.
- **Per-handler parsing is already diverse and that is fine.** The diversity
  (whitespace, `-`, `,`, trailing-whitespace-preserved, optional filter) is a
  reflection of what each verb actually needs. Forcing a single shape would
  flatten real differences for no gain.
- **The escape hatch is per-verb, not global.** When a verb's argument outgrows
  a string (streaming input, binary blobs, deeply nested config), that verb can
  carry a typed proto arm without disturbing the rest of the system. The
  contract is already opaque at the boundary; the change is local.

## Consequences

- **Positive:** the contract boundary stays at one type across nine sites; a
  new verb lands by adding a handler with its own parser, not by extending a
  shared schema; every implant language parses arguments the same way (its own
  stdlib); the server stays out of the argument-shape business.
- **Negative:** argument grammars are undocumented at the type level. A verb's
  grammar lives in its parser and its docstring, not in a schema a tool can
  introspect. Mitigation: the `/capabilities` catalog (ADR 0006) carries each
  verb's descriptor; a future `usage` field on the descriptor can document the
  grammar without changing the wire shape.
- **Risk:** a verb whose grammar grows complex (nested flags, repeated fields)
  can outgrow the string. Mitigation: the per-verb typed-arm escape hatch
  above; the decision is "string by default, typed when needed," not "string
  forever."

## Alternatives considered

- **A typed proto message for arguments, with a `oneof` per verb.** Rejected:
  it moves the per-verb grammar into the proto (every new verb edits the
  schema), couples every implant language to the shared schema, and invites the
  server to validate fields it does not own. The grammar does not go away; it
  moves.
- **A JSON object as the argument string, decoded per-handler.** Rejected: it
  adds a JSON parse step for no contract gain -- the grammar is still per-verb,
  now behind a JSON shape instead of a token shape, and the C implant pays a
  JSON dependency it does not need today. Revisit if a verb's grammar genuinely
  outgrows tokens.
- **A bag-of-optional-fields message.** Rejected: the schema would lie about
  what is required (every field optional, every verb using a different subset),
  which is strictly worse than the string for validation and equally
  untyped in practice.
