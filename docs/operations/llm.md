# LLM integration (triage and reporting)

The teamserver carries an opt-in LLM client that compresses operator
attention (architecture.md Sec 11): summarize a completed task's captured
output from the task read. This runbook covers the configuration, the egress
decision an operator makes before enabling it, and what lands in the audit
trail.

The wire contract is the **OpenAI chat-completions shape**, not a vendor: any
compatible endpoint serves it -- a cloud service or a local runtime. The
integration sits behind `Microsoft.Extensions.AI`'s `IChatClient`, so the
call shape stays stable while endpoints move.

## The egress decision comes first

Every summarize request sends engagement content (a task's verb, arguments,
and captured output) off-platform to the configured endpoint. Enabling the
integration is an OPSEC decision the operator makes deliberately:

- **Which endpoint** -- cloud or local. A local runtime (an OpenAI-compatible
  server on the operator's own host or the teamserver host) keeps the content
  inside the engagement's infrastructure; a cloud endpoint does not.
- **Whose key** -- the API key authenticates the egress. Set it through the
  environment (`Llm__ApiKey`), never inline in a committed file.
- **What it may see** -- the client sends one task's record per request,
  truncated to `MaxInputChars`. Nothing else rides the request: no other
  engagement data, no key material, no trail.

The standing default is **disabled**. Until the section is configured and
enabled, the summarize route answers `503` naming the configuration section.

## Configuration

| Key | Default | Meaning |
|-----|---------|---------|
| `Llm:Enabled` | `false` | Master switch. |
| `Llm:BaseUrl` | -- | The compatible endpoint's base URL, e.g. `https://host/v4` or `http://127.0.0.1:11434/v1`. |
| `Llm:ApiKey` | -- | The bearer key the endpoint authenticates. Environment variable (`Llm__ApiKey`) or a secret store; never committed. |
| `Llm:Model` | -- | The model name the endpoint serves. |
| `Llm:RequestTimeoutSeconds` | `120` | Per-request budget. Reasoning models answer slowly; the default is generous. |
| `Llm:MaxInputChars` | `65536` | How much captured output one request may carry; longer transcripts truncate with a marker. |
| `Llm:MaxOutputTokens` | `4096` | The response budget. Reasoning models spend tokens before answering. |

Example (environment, bigmodel's OpenAI-compatible endpoint):

```
Llm__Enabled=true
Llm__BaseUrl=https://open.bigmodel.cn/api/paas/v4
Llm__ApiKey=<key>
Llm__Model=glm-5.3
```

Example (local runtime, e.g. an OpenAI-compatible server on the same host):

```
Llm__Enabled=true
Llm__BaseUrl=http://127.0.0.1:11434/v1
Llm__ApiKey=local
Llm__Model=<served model>
```

A key borrowed from another client (an agent CLI, say) works the same way --
paste it into `Llm__ApiKey` for the session. It is still a credential:
treat it like any other engagement secret.

## Use and audit

`POST /engagements/{engagementId}/tasks/{taskId}:summarize` (read scope):

- Only a **completed** task summarizes -- the final output is what a summary
  reads; anything earlier answers `422`.
- With the integration disabled the route answers `503`.
- On success the response carries `{ taskId, model, summary }`.

Every attempt -- succeeded or failed -- lands in the engagement's audit
trail as an `LlmSummaryGenerated` event: attributed to the requesting
operator, bound to the task, payload naming what was sent (verb, captured
size), output carrying the generated summary on success, outcome
`succeeded:{model}` or `failed:{reason}`. The endpoint and the key never
enter the trail; configuration names the endpoint and this runbook records
the decision.

## Evolution notes

- **Digest narration** (architecture.md Sec 11.1): an LLM narration over the
  shift handoff digest is the natural widening of this client -- the same
  `IChatClient`, one more read-side prompt.
- **Report drafting**: drafting report sections from the attributed trail is
  the same shape -- read-side prompts over existing projections, every
  request audited.
- **Agent-driven operation**: the LLM *acting* on the platform (issuing
  tasking under operator direction) is deliberately not this surface -- it
  arrives through the MCP server's write-tool gate, a separate design with
  its own explicit authorization boundary (see
  [mcp.md](mcp.md), Evolution notes).
