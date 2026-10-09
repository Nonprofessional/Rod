# MCP server (agent tooling over the operator surface)

The operator front exposes an [MCP](https://modelcontextprotocol.io/)
endpoint so an operator can drive Rod's read side from their own agent
tooling -- any MCP client -- instead of a hand-switched console
(architecture.md Sec 4, the operator layer's agent surface).

## Endpoint and authentication

- **URL**: `http://<operator-front>/mcp` (Streamable HTTP, stateless
  sessions -- each call is standalone, no server-to-client requests).
- **Authentication**: the existing operator authentication. An MCP client
  presents an **operator API token** as its bearer credential and receives
  the same principal the console's session carries -- scope rules apply
  unchanged, and revoking the token cuts the agent off at its next call.

Mint a token from the console's operator routes (the secret is shown once):

```
POST /operators/{operatorId}/tokens        # -> { tokenId, token, createdAt }
```

Point any MCP client at the endpoint with that token as the bearer. The
client must accept both `application/json` and `text/event-stream` response
media types (standard Streamable HTTP negotiation).

## The toolset (read-only)

| Tool | Reads |
|------|-------|
| `list_engagements` | Every engagement: id, name, state, creation. |
| `list_implants` | An engagement's implant roster: class, host, liveness, parentage. |
| `list_sessions` | An engagement's active sessions -- who is alive. |
| `list_tasks` | One page of an engagement's task history, newest first. |
| `get_task` | One task in full: verb, arguments, status, outcome, output transcript. |
| `list_audit` | One page of an engagement's audit trail, newest first. |

Every tool takes an engagement id and reads through the engagement-rooted
repository methods, so engagement scoping holds by construction; a foreign
engagement's id answers not-found. Tool results are compact JSON strings
carrying the same field names the operator API's responses use.

## OPSEC posture

- The toolset is **read-only by construction**: no tool writes, issues
  tasking, or mutates engagement state, so an agent client can only observe.
  Task-issuing tools are a separate future item with their own explicit gate
  (below) -- they do not arrive here by drift.
- Reads are not audited, the digest's posture: a read of the evidence is
  not an act on the engagement.
- The endpoint rides the operator front: whoever can reach the console can
  reach the MCP endpoint, and nobody else should be able to reach either
  (the operator front is loopback by default; front it deliberately).

## Evolution notes

- **Write tools behind an explicit gate**: task-issuing tools (`issue_task`,
  snippet replay) would let a directed agent act on an engagement. That is
  a deliberate design of its own -- an authorization boundary (a separate
  operator scope or an opt-in gate), automation-safety interplay
  (architecture.md Sec 10.4's unattended-execution posture), and OPSEC
  review -- not a widening that rides along.
- The LLM triage client ([llm.md](llm.md)) is the in-platform sibling of
  this surface: same reads, different consumer.
