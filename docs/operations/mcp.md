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

## The toolset

The read set (always present):

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

The write set (behind the gate below): `issue_task` and `replay_snippet`.

## OPSEC posture

- The toolset is **read-only by default**: no tool writes, issues tasking,
  or mutates engagement state, so an agent client can only observe. The
  task-issuing half exists behind its own explicit gate (below) and is
  absent from the wire until a deployment opens it.
- Reads are not audited, the digest's posture: a read of the evidence is
  not an act on the engagement. Writes are -- an issued task rides the
  ordinary `TaskIssued` fact, attributed to the token's operator and
  indistinguishable in the trail from an operator-typed issuance.
- The endpoint rides the operator front: whoever can reach the console can
  reach the MCP endpoint, and nobody else should be able to reach either
  (the operator front is loopback by default; front it deliberately).

## The write gate (task-issuing tools)

`issue_task` and `replay_snippet` let a directed agent act on an
engagement, so they ship behind `Mcp:AllowWriteTools: true` (default
closed). The gate is registration-time by design: closed, the write tools
do not exist on the wire -- `tools/list` names no way to act -- rather
than existing and refusing, so an agent client cannot even discover an
action surface the deployment never opened.

Open, the tools issue through the same `TaskService` path the console's
task dialog drives, so every gate holds exactly as if an operator typed
the command: per-class verb sets, ROE guardrails (a refusal is audited
`TaskRoeRefused` like any other), carrier claims, closed engagements, and
attribution to the token's operator. The engagement bar is the write bar:
`issue_task` requires the token's operator to hold writer (or owner)
standing on the engagement -- a reader's token reads "no such engagement,"
the same concealment the operator API keeps -- and a snippet replays only
within its own engagement, every step issued in order to the named
implant.

Opening the gate is a deployment's automation decision
(architecture.md Sec 10.4's unattended-execution posture applies): review
what your agent may task before you let it.
- The LLM triage client ([llm.md](llm.md)) is the in-platform sibling of
  this surface: same reads, different consumer.
