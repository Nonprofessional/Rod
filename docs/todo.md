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

- **Server-side automation: triggers and scheduled tasking** (serves
  architecture.md Sec 10.3; design lands as a new subsection there before
  any code). What an engagement cannot do without it: act on a cadence or
  on a return while no operator watches -- the overnight screenshot every
  30 minutes, the triage batch on first check-in, the chain that reads a
  result and tasks the follow-up. Shape: an engine beside the operator
  layer subscribing to the live event bus (Sec 4.1, layer 4) for event
  triggers and running a scheduler for time triggers; engagement-scoped
  declarative rules (trigger, condition, action) persisted with the
  engagement; every firing issues through `TaskService` so the class,
  carrier, ROE, and closed-engagement gates hold unchanged, attributes to
  a synthetic automation operator, and lands in the audit trail. Guards:
  firing caps, cooldowns, chain depth, no sensitive verbs. Time triggers
  are durable (persisted next-fire stamps); event triggers inherit the
  bus's best-effort posture and say so. A sandboxed script front-end
  (JS/Lua) is a possible follow-on evaluator, not the first one --
  declarative rules are auditable and testable and cover the
  engagement-shown needs.
  _AC:_ a rule that runs a recon verb on one implant every 30 minutes
  survives a teamserver restart, shows automation attribution in the audit
  trail, and is cancelable from the operator API.

- **MCP server over the operator surface** (serves architecture.md Sec 4,
  the operator layer). What an engagement cannot do without it: let an
  operator drive Rod from their own agent tooling (any MCP client) instead
  of a hand-switched console -- the same roster, task, and audit reads the
  operator UI makes, discovered and called as standard tools. Shape: an
  MCP endpoint (Streamable HTTP) on the operator front behind the existing
  operator token auth, engagement-scoped by construction; read-only
  toolset first (engagements, implants, sessions, tasks and transcripts,
  audit reads); task-issuing tools are a separate later item with their
  own explicit gate, not part of this one.
  _AC:_ an external MCP client lists an engagement's implants and reads a
  completed task's output through the operator front's auth, and no write
  tool is exposed yet.

- **OpenAI-compatible LLM client for triage and reporting** (serves
  architecture.md Sec 11). What an engagement cannot do without it:
  compress operator attention -- summarize a task's captured output,
  triage a recon sweep, draft report sections from the attributed trail.
  Shape: an opt-in chat-completions client behind
  `Microsoft.Extensions.AI`'s `IChatClient` with a configurable
  OpenAI-compatible base URL and model (cloud or local runtime -- the
  format is the compatibility contract, not the vendor), disabled by
  default; every request is engagement-scoped and recorded in the audit
  trail; the egress decision (which endpoint, local or not) stays the
  operator's and is documented in the operations runbook.
  _AC:_ with the integration enabled, an operator generates a summary of a
  completed task's output from the task read, and the request appears in
  the engagement's audit trail.
