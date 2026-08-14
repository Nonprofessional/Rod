// Thin typed wrappers over the teamserver operator HTTP API
// (Rod.Transport/Endpoints). The browser UI talks to the same JSON endpoints
// the implant and operator layers do; the host serves this bundle from wwwroot
// so the calls are same-origin in production and proxied to :5080 in dev.

export interface Engagement {
  engagementId: string
  name: string
  ownerId: string
  ownerHandle: string
  createdAt: string
}

export interface StagerToken {
  stagerTokenId: string
  engagementId: string
  secret: string
  issuedBy: string
  issuedAt: string
  expiresAt: string
  maxUses: number
}

export interface Implant {
  implantId: string
  engagementId: string
  class: string
  killDate: string
  createdAt: string
  isOnline: boolean
  retiredAt: string | null
  parentImplantId: string | null
}

export interface Task {
  taskId: string
  engagementId: string
  implantId: string
  issuedBy: string
  verb: string
  arguments: string
  status: string
  output: string | null
  outcome: string | null
  createdAt: string
  dispatchedAt: string | null
  completedAt: string | null
}

export interface Problem {
  error: string
}

// The session cookie expired or was revoked mid-use. The shell listens for the
// unauthorized event and returns to the login view; a view's inline error text
// is not enough when every subsequent call will 401.
export class SessionExpiredError extends Error {}

function notifySessionExpired(): void {
  window.dispatchEvent(new Event('rod-unauthorized'))
}

async function jsonOrThrow<T>(response: Response): Promise<T> {
  if (!response.ok) {
    let detail = `${response.status} ${response.statusText}`
    try {
      const body = (await response.json()) as Problem
      if (body?.error) detail = body.error
    } catch {
      // Non-JSON error body; keep the status text.
    }
    if (response.status === 401) {
      notifySessionExpired()
      throw new SessionExpiredError(detail)
    }
    throw new Error(detail)
  }
  return (await response.json()) as T
}

// --- Operator session (architecture.md Sec 4) --------------------------------
//
// The browser session is established by verified credentials, not a client-
// generated id: POST /operators/login sets the auth cookie, GET /operators/me
// reads the server-recorded operator back, and POST /operators/logout clears it.
// Every other call in this module relies on that cookie; an unauthenticated
// browser gets 401 and the UI shows the login view. This replaces the walking
// skeleton's self-assigned identity.

export interface SessionOperator {
  operatorId: string
  handle: string
  displayName: string
}

export interface LoginInput {
  handle: string
  password: string
}

export async function login(input: LoginInput): Promise<void> {
  const response = await fetch('operators/login', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(input),
  })
  if (!response.ok) {
    throw new Error(`${response.status} ${response.statusText}`)
  }
}

export async function logout(): Promise<void> {
  await fetch('operators/logout', { method: 'POST' })
}

// Resolves the authenticated operator off the session cookie. Throws on 401 so
// the route guard can fall back to the login view.
export async function getSessionOperator(): Promise<SessionOperator> {
  const response = await fetch('operators/me')
  if (!response.ok) {
    throw new Error(`${response.status} ${response.statusText}`)
  }
  const body = (await response.json()) as { id: string; handle: string; displayName: string }
  return { operatorId: body.id, handle: body.handle, displayName: body.displayName }
}

export interface CreateEngagementInput {
  name: string
}

export async function listEngagements(): Promise<Engagement[]> {
  return jsonOrThrow(await fetch('engagements'))
}

export async function createEngagement(input: CreateEngagementInput): Promise<Engagement> {
  const response = await fetch('engagements', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(input),
  })
  return jsonOrThrow(response)
}

export async function mintStagerToken(engagementId: string): Promise<StagerToken> {
  return jsonOrThrow(await fetch(`engagements/${engagementId}/stager-tokens`, { method: 'POST' }))
}

export async function listImplants(engagementId: string): Promise<Implant[]> {
  return jsonOrThrow(await fetch(`engagements/${engagementId}/implants`))
}

export interface IssueTaskInput {
  implantId: string
  verb: string
  arguments: string
}

export async function issueTask(
  engagementId: string,
  input: IssueTaskInput,
): Promise<Task> {
  const response = await fetch(`engagements/${engagementId}/tasks`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(input),
  })
  return jsonOrThrow(response)
}

// --- Live event stream  ---------------------------------------
//
// Server-Sent Events keep each connected operator session live on an engagement.
// The bus fans task-issued / task-completed / operator-joined / operator-left
// events out to every subscriber, so two operators see each other's actions in
// real time without polling. The operator's identity is read off the session
// cookie server-side, so this stream carries no identity of its own.

export type LiveEventName =
  | 'hello'
  | 'OperatorJoined'
  | 'OperatorLeft'
  | 'TaskIssued'
  | 'TaskCompleted'

export interface LiveOperator {
  id: string
  handle: string
  displayName: string
}

export interface LiveEventPayload {
  kind?: string
  engagementId?: string
  operatorId?: string
  implantId?: string | null
  taskId?: string | null
  payload?: string
  at?: string
  operators?: LiveOperator[]
}

export interface EngagementStreamHandlers {
  onHello?: (operators: LiveOperator[]) => void
  onOperatorJoined?: (operatorId: string, handle: string) => void
  onOperatorLeft?: (operatorId: string, handle: string) => void
  onTaskIssued?: (taskId: string, payload: string) => void
  onTaskCompleted?: (taskId: string, payload: string) => void
  onError?: (event: Event) => void
}

// Opens an SSE stream for an engagement. The auth cookie identifies the operator
// server-side, so no identity travels in the URL. Returns a close that tears
// the stream down; the caller invokes it on unmount. The EventSource reconnects
// automatically on a dropped connection.
export function subscribeToEngagement(
  engagementId: string,
  handlers: EngagementStreamHandlers,
): () => void {
  const source = new EventSource(`engagements/${engagementId}/events`)

  const parse = (data: string): LiveEventPayload | null => {
    try {
      return JSON.parse(data) as LiveEventPayload
    } catch {
      return null
    }
  }

  source.addEventListener('hello', (e) => {
    const payload = parse((e as MessageEvent).data)
    handlers.onHello?.(payload?.operators ?? [])
  })
  source.addEventListener('OperatorJoined', (e) => {
    const payload = parse((e as MessageEvent).data)
    handlers.onOperatorJoined?.(payload?.operatorId ?? '', payload?.payload ?? '')
  })
  source.addEventListener('OperatorLeft', (e) => {
    const payload = parse((e as MessageEvent).data)
    handlers.onOperatorLeft?.(payload?.operatorId ?? '', payload?.payload ?? '')
  })
  source.addEventListener('TaskIssued', (e) => {
    const payload = parse((e as MessageEvent).data)
    handlers.onTaskIssued?.(payload?.taskId ?? '', payload?.payload ?? '')
  })
  source.addEventListener('TaskCompleted', (e) => {
    const payload = parse((e as MessageEvent).data)
    handlers.onTaskCompleted?.(payload?.taskId ?? '', payload?.payload ?? '')
  })
  source.onerror = (e) => handlers.onError?.(e)

  return () => source.close()
}

// --- Capability catalog  -------------------------------------
//
// The verb table is data-driven from the registry (GET /capabilities) so the UI
// surfaces every capability category as tasking without hardcoding the verbs.
// Each descriptor carries its category (for grouping) and OPSEC attributes (for
// risk badges). Sensitive categories -- evasion and exploit -- are listed too;
// this surface holds only the contract, never concrete tradecraft.

export interface CapabilityDescriptor {
  verb: string
  category: string
  version: string
  attributes: Record<string, string>
}

export async function listCapabilities(): Promise<CapabilityDescriptor[]> {
  return jsonOrThrow(await fetch('capabilities'))
}

// --- Engagement-wide task list  ------------------------------
//
// The whole task history for an engagement across every implant, oldest first.
// Reuses the per-implant task shape so both list views read identically to a
// client.

export interface EngagementTask {
  taskId: string
  implantId: string
  issuedBy: string
  verb: string
  arguments: string
  status: string
  output: string | null
  outcome: string | null
  createdAt: string
  completedAt: string | null
}

export async function listEngagementTasks(engagementId: string): Promise<EngagementTask[]> {
  return jsonOrThrow(await fetch(`engagements/${engagementId}/tasks`))
}

// --- Audit trail  ---------------------------------------------
//
// The per-engagement, append-only, hash-chained event stream. Every action that
// changes engagement state or binds an identity produces an immutable, attributed
// event; this reads that trail oldest-first so the engagement timeline reads in
// causal order. Distinct from the live SSE stream (transient fan-out).

export interface AuditEventEntry {
  eventId: string
  kind: string
  verb: string
  operatorId: string
  implantId: string
  taskId: string
  payload: string
  output: string | null
  outcome: string
  at: string
}

export async function listAudit(engagementId: string): Promise<AuditEventEntry[]> {
  return jsonOrThrow(await fetch(`engagements/${engagementId}/audit`))
}

// --- Artifacts  -----------------------------------------------
//
// First-class evidence objects attached to tasks. Attach (base64 body), list per
// task (metadata only), and retrieve a single artifact's bytes as a file
// download. Scoped by engagement so cross-engagement access is impossible.

export interface ArtifactSummary {
  artifactId: string
  taskId: string
  name: string
  contentType: string
  operatorId: string | null
  size: number
  storedAt: string
}

export async function listArtifacts(engagementId: string, taskId: string): Promise<ArtifactSummary[]> {
  return jsonOrThrow(await fetch(`engagements/${engagementId}/tasks/${taskId}/artifacts`))
}

export async function attachArtifact(
  engagementId: string,
  taskId: string,
  input: { name: string; contentType: string | null; content: string },
): Promise<ArtifactSummary> {
  const response = await fetch(`engagements/${engagementId}/tasks/${taskId}/artifacts`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(input),
  })
  return jsonOrThrow(response)
}

// Returns the artifact's bytes as a blob the caller can download or preview.
export async function fetchArtifactBlob(engagementId: string, artifactId: string): Promise<Blob> {
  const response = await fetch(`engagements/${engagementId}/artifacts/${artifactId}`)
  if (!response.ok) {
    let detail = `${response.status} ${response.statusText}`
    try {
      const body = (await response.json()) as Problem
      if (body?.error) detail = body.error
    } catch {
      // Non-JSON error body; keep the status text.
    }
    throw new Error(detail)
  }
  return response.blob()
}

// --- Timeline and report export  ------------------------------
//
// Built-in consumers of the event + task + artifact store. Both export as JSON
// by default, or Markdown when format='markdown' (returned as text). Each
// carries a content hash so two exports of identical state match.

export interface TimelineActor {
  operatorId: string
  handle: string
}
export interface TimelineSubject {
  implantId: string
  class: string
}
export interface TimelineTaskRef {
  taskId: string
  verb: string | null
  outcome: string | null
}
export interface TimelineEntry {
  eventId: string
  at: string
  kind: string
  verb: string
  operator: TimelineActor | null
  implant: TimelineSubject | null
  task: TimelineTaskRef | null
  payload: string
  output: string | null
  outcome: string
  hash: string
}
export interface TimelineReport {
  engagementId: string
  engagementName: string
  generatedAt: string
  contentHash: string
  entries: TimelineEntry[]
}

export async function getTimeline(engagementId: string): Promise<TimelineReport> {
  return jsonOrThrow(await fetch(`engagements/${engagementId}/timeline`))
}

export async function getTimelineMarkdown(engagementId: string): Promise<string> {
  const response = await fetch(`engagements/${engagementId}/timeline?format=markdown`)
  if (!response.ok) throw new Error(`${response.status} ${response.statusText}`)
  return response.text()
}

export interface ReportTask {
  taskId: string
  verb: string
  arguments: string
  status: string
  outcome: string | null
  issuedBy: string
  issuedByHandle: string
  implantId: string
  createdAt: string
  dispatchedAt: string | null
  completedAt: string | null
  output: string | null
  artifacts: string[]
}
export interface EngagementReport {
  engagement: {
    engagementId: string
    name: string
    ownerId: string
    ownerHandle: string
    createdAt: string
  }
  generatedAt: string
  contentHash: string
  operators: { operatorId: string; handle: string }[]
  implants: { implantId: string; class: string; parentImplantId: string | null; retiredAt: string | null }[]
  tasks: ReportTask[]
  artifacts: { artifactId: string; taskId: string; name: string; contentType: string; size: number }[]
  timeline: TimelineEntry[]
}

export async function getReport(engagementId: string): Promise<EngagementReport> {
  return jsonOrThrow(await fetch(`engagements/${engagementId}/report`))
}

export async function getReportMarkdown(engagementId: string): Promise<string> {
  const response = await fetch(`engagements/${engagementId}/report?format=markdown`)
  if (!response.ok) throw new Error(`${response.status} ${response.statusText}`)
  return response.text()
}

// --- Implant retire / burn  -----------------------------------
//
// Takes an implant out of operation: a retired implant is refused at handshake
// and untaskable. Idempotent; reflects retirement in the listing. The retiring
// operator is the authenticated operator, so no body is sent.

export interface RetireImplantResult {
  implantId: string
  engagementId: string
  retiredBy: string
  retiredAt: string
  justRetired: boolean
  closedSession: string | null
}

export async function retireImplant(
  engagementId: string,
  implantId: string,
): Promise<RetireImplantResult> {
  return jsonOrThrow(
    await fetch(`engagements/${engagementId}/implants/${implantId}:retire`, {
      method: 'POST',
    }),
  )
}

// --- Listeners and redirector repoint  -----------------------
//
// Listeners are the bound C2 ingress; their public endpoint is the redirector
// implants dial, decoupled from the bind address. Repointing swaps that endpoint
// at runtime -- a burned redirector is replaced without backend change. Global
// infrastructure, not engagement-scoped.

export interface ListenerSummary {
  id: string
  name: string
  transport: string
  bindAddress: string
  publicEndpoint: string
  state: string
  createdAt: string
  repointedAt: string | null
}

export async function listListeners(): Promise<ListenerSummary[]> {
  return jsonOrThrow(await fetch('listeners'))
}

export async function repointListener(listenerId: string, publicEndpoint: string): Promise<ListenerSummary> {
  return jsonOrThrow(
    await fetch(`listeners/${listenerId}:repoint`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ publicEndpoint }),
    }),
  )
}

// --- Payload build with OPSEC profile (//) --------------
//
// Builds an implant artifact, baking in the beacon profile (sleep/jitter), the
// kill date, and the malleable transport profile (endpoint, URIs, headers,
// timing, envelope). These are baked at generation; a live implant's profile is
// read-only after enrollment, so OPSEC changes go through a rebuild + redeploy.

export interface BuildPayloadInput {
  language: string | null
  class: string | null
  targetOs: string | null
  targetArch: string | null
  endpoint: string | null
  uriPath: string | null
  enrollPath: string | null
  userAgent: string | null
  headers: Record<string, string> | null
  requestTimeoutSeconds: number | null
  envelope: string | null
  sleepSeconds: number | null
  jitterSeconds: number | null
  killDate: string | null
}

export interface BuildPayloadResult {
  artifactId: string
  engagementId: string
  class: string
  language: string
  contentType: string
  fingerprint: string
  size: number
  builtAt: string
}

export async function buildPayload(engagementId: string, input: BuildPayloadInput): Promise<BuildPayloadResult> {
  return jsonOrThrow(
    await fetch(`engagements/${engagementId}/payloads`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(input),
    }),
  )
}
