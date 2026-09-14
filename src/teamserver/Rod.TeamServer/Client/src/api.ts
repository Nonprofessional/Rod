// Thin typed wrappers over the teamserver operator HTTP API
// (Rod.Transport/Endpoints). The browser UI talks to the same JSON endpoints
// the implant and operator layers do; the host serves this bundle from wwwroot
// so the calls are same-origin in production and proxied to :5080 in dev.

export interface RoeProfile {
  permittedVerbs: string[]
  permittedImplants: string[]
}

// The close-out state rides on timestamps: null while the engagement is open,
// frozenAt set once the close-out starts (no new tasking or deployments),
// retiredAt set when it completes -- terminal, the record sealed.
export interface Engagement {
  engagementId: string
  name: string
  description: string | null
  ownerId: string
  ownerHandle: string
  createdAt: string
  roe: RoeProfile
  frozenAt: string | null
  retiredAt: string | null
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
  // The artifact's time fuse as reported at enroll; null = open-ended (no
  // fuse -- the implant runs until retired).
  killDate: string | null
  createdAt: string
  isOnline: boolean
  retiredAt: string | null
  parentImplantId: string | null
  // The device identity the implant reported at enroll -- what the fleet
  // groups rows by. Null when the enrolling client predated the field.
  hostname: string | null
  os: string | null
  arch: string | null
  username: string | null
  // The listener whose socket carried the enrollment, when the transport
  // could attribute one -- what a listener-deletion warning counts against.
  enrolledViaListenerId: string | null
  // The durable heartbeat: when the teamserver last heard from this implant,
  // kept after the session is gone. Null when it never checked in past enroll.
  lastSeenAt: string | null
}

export interface Task {
  taskId: string
  engagementId: string
  implantId: string
  issuedBy: string
  verb: string
  arguments: string
  // Absent on the issue response -- a freshly issued task is queued by
  // construction, so the server does not say it -- and present on task
  // detail reads. Callers that need the state read getTask or track the
  // task themselves.
  status?: string
  output?: string | null
  outcome?: string | null
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
class SessionExpiredError extends Error {}

function notifySessionExpired(): void {
  window.dispatchEvent(new Event('rod-unauthorized'))
}

// A refused request with its status attached, so callers can branch on the
// server's verdict (the listener-delete guard's 409) instead of parsing the
// message text.
export class ApiError extends Error {
  readonly status: number
  constructor(message: string, status: number) {
    super(message)
    this.status = status
  }
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
    throw new ApiError(detail, response.status)
  }
  // A 204 (and any empty body) carries nothing to parse -- the delete routes
  // answer that way, and json() on an empty body would turn success into a
  // parse error. Callers expecting void read undefined.
  if (response.status === 204) {
    return undefined as T
  }
  const text = await response.text()
  return (text === '' ? undefined : JSON.parse(text)) as T
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
  description?: string
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

// The leak answer, above all for a credential baked into a deployed artifact:
// the id stops working at the next redeem or verify. Idempotent-refusing -- a
// second attempt 404s rather than reading as success.
export async function revokeStagerToken(engagementId: string, tokenId: string): Promise<void> {
  const response = await fetch(`engagements/${engagementId}/stager-tokens/${tokenId}:revoke`, {
    method: 'POST',
  })
  await jsonOrThrow<unknown>(response)
}

export async function listImplants(engagementId: string): Promise<Implant[]> {
  return jsonOrThrow(await fetch(`engagements/${engagementId}/implants`))
}

export interface IssueTaskInput {
  implantId: string
  verb: string
  arguments: string
  // The staged arm (file.push of a larger upload): base64 bytes the server
  // stores as a task-bound artifact and the implant pulls by hash. Absent for
  // every verb whose arguments carry everything.
  content?: string
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

// --- Engagement record management ------------------------------
//
// The working record (name + free-text description) is editable while the
// engagement operates and while it is frozen; retirement seals it. The
// close-out path (freeze -> export -> retire) is the engagement's exit from
// service -- the trail stays as the durable account, so "delete" here means
// retiring, not erasing. A mistaken freeze is walked back with unfreeze until
// retirement completes.

export async function editEngagement(
  engagementId: string,
  input: { name: string; description: string | null },
): Promise<Engagement> {
  const response = await fetch(`engagements/${engagementId}`, {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(input),
  })
  return jsonOrThrow(response)
}

export interface EngagementClosedResult {
  engagementId: string
  at: string
}

export async function freezeEngagement(engagementId: string): Promise<EngagementClosedResult> {
  const response = await fetch(`engagements/${engagementId}:freeze`, { method: 'POST' })
  return jsonOrThrow(response)
}

export interface EngagementReopenedResult {
  engagementId: string
  unfrozenAt: string
}

// Reverses a mistaken freeze; refused once the engagement is retired. The
// freeze and the unfreeze both stay in the audit trail.
export async function unfreezeEngagement(engagementId: string): Promise<EngagementReopenedResult> {
  const response = await fetch(`engagements/${engagementId}:unfreeze`, { method: 'POST' })
  return jsonOrThrow(response)
}

export async function retireEngagement(engagementId: string): Promise<EngagementClosedResult> {
  const response = await fetch(`engagements/${engagementId}:retire`, { method: 'POST' })
  return jsonOrThrow(response)
}

// The evidence package is a POST-only close-out action -- the export is an
// audited operator act, not a fetchable resource -- so the download posts and
// takes the returned ZIP as a blob.
export async function fetchEvidencePackageBlob(engagementId: string): Promise<Blob> {
  const response = await fetch(`engagements/${engagementId}:evidence-package`, { method: 'POST' })
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

// --- Cancel queued tasking ------------------------------------
//
// Takes a queued task back before the implant wakes: the operator's own
// tasking, retracted. Only a queued task can be cancelled -- one already
// claimed by a beacon stream belongs to the implant and answers with 409.

export interface CancelledTask {
  taskId: string
  engagementId: string
  cancelledBy: string
  cancelledAt: string
}

export async function cancelTask(
  engagementId: string,
  taskId: string,
): Promise<CancelledTask> {
  const response = await fetch(`engagements/${engagementId}/tasks/${taskId}:cancel`, {
    method: 'POST',
  })
  return jsonOrThrow(response)
}

// --- Streaming task input (architecture.md Sec 10.3) ------------------------
//
// A live channel task (shell.interact, tunnel.forward) takes operator input
// through its own route: one post is one line of typing or one chunk of
// tunnel traffic, and eof closes the channel's stdin (the shell exits and the
// task completes with the transcript as its record; the tunnel half-closes
// its send side). The transcript itself is the task's own output -- read it
// back with getTask while the channel runs.

export interface TaskDetail {
  taskId: string
  verb: string
  arguments: string
  status: string
  output: string | null
  outcome: string | null
}

export async function getTask(engagementId: string, taskId: string): Promise<TaskDetail> {
  const response = await fetch(`engagements/${engagementId}/tasks/${taskId}`)
  return jsonOrThrow(response)
}

export async function sendTaskInput(
  engagementId: string,
  taskId: string,
  text: string,
  eof = false,
): Promise<void> {
  const data = text.length > 0 ? toBase64Utf8(text) : undefined
  const response = await fetch(`engagements/${engagementId}/tasks/${taskId}/input`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ data, eof }),
  })
  await jsonOrThrow<unknown>(response)
}

// Base64 of the UTF-8 encoding: the route binds byte[] from base64, so the
// round-trip preserves arbitrary text exactly.
function toBase64Utf8(text: string): string {
  const bytes = new TextEncoder().encode(text)
  let binary = ''
  for (const b of bytes) binary += String.fromCharCode(b)
  return btoa(binary)
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
  | 'TaskCancelled'
  | 'ChannelOutput'
  | 'SessionClosed'

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
  onTaskCancelled?: (taskId: string, payload: string) => void
  onChannelOutput?: (taskId: string, chunk: string) => void
  onSessionOpened?: (implantId: string, payload: string) => void
  onSessionClosed?: (implantId: string, payload: string) => void
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
  source.addEventListener('TaskCancelled', (e) => {
    const payload = parse((e as MessageEvent).data)
    handlers.onTaskCancelled?.(payload?.taskId ?? '', payload?.payload ?? '')
  })
  source.addEventListener('ChannelOutput', (e) => {
    const payload = parse((e as MessageEvent).data)
    handlers.onChannelOutput?.(payload?.taskId ?? '', payload?.payload ?? '')
  })
  source.addEventListener('SessionOpened', (e) => {
    const payload = parse((e as MessageEvent).data)
    handlers.onSessionOpened?.(payload?.implantId ?? '', payload?.payload ?? '')
  })
  source.addEventListener('SessionClosed', (e) => {
    const payload = parse((e as MessageEvent).data)
    handlers.onSessionClosed?.(payload?.implantId ?? '', payload?.payload ?? '')
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

// --- Paged lists --------------------------------------------------
//
// Task, audit, and artifact listings are paged: each call returns one page --
// the newest window on the first call, one page older per cursor -- plus the
// cursor for the next older page (null at the beginning of history). A long
// engagement no longer grows any listing response without bound; the views walk
// pages with their "load older" controls.

export interface ListPage<T> {
  items: T[]
  nextCursor: string | null
}

// --- Engagement-wide task list  ------------------------------
//
// The task history for an engagement across every implant, one page per call,
// oldest first within the page. Reuses the per-implant task shape so both list
// views read identically to a client.

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

export async function listEngagementTasks(
  engagementId: string,
  cursor?: string,
): Promise<ListPage<EngagementTask>> {
  const query = cursor ? `?cursor=${encodeURIComponent(cursor)}` : ''
  return jsonOrThrow(await fetch(`engagements/${engagementId}/tasks${query}`))
}

// --- Per-implant task list  ---------------------------------
//
// The same history scoped to one implant (the session console's feed), same
// paging envelope as the engagement-wide list.

export async function listImplantTasks(
  engagementId: string,
  implantId: string,
  cursor?: string,
): Promise<ListPage<EngagementTask>> {
  const query = cursor ? `?cursor=${encodeURIComponent(cursor)}` : ''
  return jsonOrThrow(
    await fetch(`engagements/${engagementId}/implants/${implantId}/tasks${query}`),
  )
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
  // The resolved handle of the acting operator ("system" for unattributed
  // events), so the trail reads as who acted rather than a guid.
  operatorHandle: string
  implantId: string
  taskId: string
  payload: string
  output: string | null
  outcome: string
  at: string
}

export async function listAudit(
  engagementId: string,
  cursor?: string,
): Promise<ListPage<AuditEventEntry>> {
  const query = cursor ? `?cursor=${encodeURIComponent(cursor)}` : ''
  return jsonOrThrow(await fetch(`engagements/${engagementId}/audit${query}`))
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

export async function listArtifacts(
  engagementId: string,
  taskId: string,
  cursor?: string,
): Promise<ListPage<ArtifactSummary>> {
  const query = cursor ? `?cursor=${encodeURIComponent(cursor)}` : ''
  return jsonOrThrow(await fetch(`engagements/${engagementId}/tasks/${taskId}/artifacts${query}`))
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

// --- Implant notes --------------------------------------------
//
// The "whose beacon is this" memory: free-text, attributed notes on an
// implant. A note's only storage is the audit trail (ImplantNoteAdded events),
// so it survives a teamserver restart with the trail itself and reads back
// here on the implant view, oldest first.

export interface ImplantNote {
  noteId: string
  implantId: string
  author: string
  text: string
  at: string
}

export async function listImplantNotes(
  engagementId: string,
  implantId: string,
): Promise<ImplantNote[]> {
  return jsonOrThrow(
    await fetch(`engagements/${engagementId}/implants/${implantId}/notes`),
  )
}

export async function addImplantNote(
  engagementId: string,
  implantId: string,
  text: string,
): Promise<ImplantNote> {
  const response = await fetch(`engagements/${engagementId}/implants/${implantId}/notes`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ text }),
  })
  return jsonOrThrow(response)
}

// --- Listeners and redirector repoint  -----------------------
//
// Listeners are the engagement's own C2 ingress: created here against the
// engagement in the path, persisted so a restart rebinds them, and enforced at
// enrollment (a foreign engagement's token is refused whole on the socket).
// The bind address is the socket; the public endpoint is the redirector
// implants dial, decoupled so a repoint swaps a burned front without backend
// change. The operator front the UI rides is startup configuration, carries no
// implant ingress, and never appears here.

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

// The host's bindable interfaces (GET /network/interfaces): the read view the
// listener form's bind dropdown is built from. The wildcard all-interfaces
// entry is a client-side constant, not a reported interface.
export interface NetworkInterfaceSummary {
  name: string
  address: string
}

export async function listNetworkInterfaces(): Promise<NetworkInterfaceSummary[]> {
  return jsonOrThrow(await fetch('network/interfaces'))
}

export async function listListeners(engagementId: string): Promise<ListenerSummary[]> {
  return jsonOrThrow(await fetch(`engagements/${engagementId}/listeners`))
}

export interface CreateListenerInput {
  name: string
  transport: string
  bindAddress: string
  publicEndpoint: string
}

export async function createListener(
  engagementId: string,
  input: CreateListenerInput,
): Promise<ListenerSummary> {
  const response = await fetch(`engagements/${engagementId}/listeners`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(input),
  })
  return jsonOrThrow(response)
}

export async function repointListener(
  engagementId: string,
  listenerId: string,
  publicEndpoint: string,
): Promise<ListenerSummary> {
  return jsonOrThrow(
    await fetch(`engagements/${engagementId}/listeners/${listenerId}:repoint`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ publicEndpoint }),
    }),
  )
}

// Deletes the listener. A listener live implants enrolled through refuses
// with a 409 naming them; `force` is the explicit second confirmation that
// deletes anyway.
export async function deleteListener(
  engagementId: string,
  listenerId: string,
  force = false,
): Promise<void> {
  await jsonOrThrow<unknown>(
    await fetch(
      `engagements/${engagementId}/listeners/${listenerId}${force ? '?force=true' : ''}`,
      { method: 'DELETE' },
    ),
  )
}

// --- Online implant roster (presence) -------------------------
//
// The per-engagement online roster: an implant is online exactly while it
// holds an active session, so this is the live-channel projection -- session,
// capabilities, last seen -- beside the enrolled-implant record the implants
// tab holds. This is the crew's live situational-awareness view.

export interface PresenceRecord {
  sessionId: string
  implantId: string
  engagementId: string
  capabilities: string[]
  onlineAt: string
  lastSeenAt: string
}

export async function listOnline(engagementId: string): Promise<PresenceRecord[]> {
  return jsonOrThrow(await fetch(`engagements/${engagementId}/presence`))
}

// --- Payload build with OPSEC profile (//) --------------
//
// Builds an implant artifact, baking in the beacon profile (mode, sleep,
// jitter), the kill date, and the malleable transport profile (endpoint,
// fallbacks, URIs, headers, timing, envelope). These are baked at generation;
// a live implant's profile is read-only after enrollment, so OPSEC changes go
// through a rebuild + redeploy.

export interface BuildPayloadInput {
  language: string | null
  class: string | null
  targetOs: string | null
  targetArch: string | null
  // Naming the engagement's listener supplies the endpoint from its record;
  // the manual endpoint covers shapes with no listener yet. Mutually
  // exclusive on the wire.
  listenerId: string | null
  endpoint: string | null
  // The socket the check-in stream dials when it differs from the enroll
  // endpoint (the split-socket shape: enroll on a web front, the interactive
  // mTLS stream on its own listener). Named by listener or typed URL, and
  // optional everywhere -- a web front carries its check-ins itself over the
  // envelope POST cycle.
  beaconListenerId: string | null
  beaconEndpoint: string | null
  // A stager-class build only: the completed Stage-2 artifact the loader
  // fetches at run time. Null everywhere else.
  stage2PayloadId: string | null
  fallbackEndpoints: string[] | null
  enrollPath: string | null
  userAgent: string | null
  headers: Record<string, string> | null
  requestTimeoutSeconds: number | null
  // The enroll-body shape only. Check-in protection is its own knob below --
  // the two phases are independent.
  envelope: string | null
  // Whether the artifact's check-in bodies seal under the per-artifact key
  // minted at build. Null (the default) leaves it on; false is the explicit
  // lab-debug opt-out -- the plaintext frame body, unauthenticated over
  // cleartext and unencrypted over TLS.
  checkInProtection: boolean | null
  mode: string | null
  sleepSeconds: number | null
  jitterSeconds: number | null
  killDate: string | null
  // The baked enrollment credential's scope: how many implants the artifact's
  // token may enroll, and how long the mint stays redeemable. Absent values
  // default server-side to single use inside the artifact's kill window.
  tokenMaxUses: number | null
  tokenLifetimeSeconds: number | null
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
  // The id of the enrollment credential baked into the artifact, when the
  // build minted one -- enough to revoke it, never the secret itself.
  tokenId: string | null
}

// --- Background payload builds ---------------------------------
//
// A build invokes a real toolchain, so it runs as a server-side job: POST
// returns 202 with the queued job immediately, and the build finishes into
// the payload store and the audit trail whatever happens to the page that
// asked. The job list is the durable view -- a browser refresh reloads it and
// keeps showing progress, so a build is never lost to navigation.

export type BuildJobState = 'queued' | 'running' | 'completed' | 'failed'

export interface BuildJob {
  jobId: string
  engagementId: string
  state: BuildJobState
  requestedBy: string
  requestedAt: string
  startedAt: string | null
  completedAt: string | null
  class: string
  language: string
  target: string
  endpoint: string
  // The check-in socket on a split-socket build; null when the beacon rides
  // the enroll endpoint.
  beaconEndpoint: string | null
  mode: string
  error: string | null
  artifact: BuildPayloadResult | null
}

export async function enqueueBuildJob(
  engagementId: string,
  input: BuildPayloadInput,
): Promise<BuildJob> {
  const response = await fetch(`engagements/${engagementId}/payload-jobs`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(input),
  })
  return jsonOrThrow(response)
}

export async function listBuildJobs(engagementId: string): Promise<BuildJob[]> {
  return jsonOrThrow(await fetch(`engagements/${engagementId}/payload-jobs`))
}

// --- Payload library -----------------------------------------
//
// The payload store's own listing: the durable answer to the bounded,
// process-local build-job list. A payload built weeks ago stays here --
// downloadable, its baked credential revocable, and deletable (which also
// stops any stager fetching it) -- whatever happened to the Recent builds
// queue or the teamserver process in between.

export interface PayloadSummary {
  artifactId: string
  class: string
  language: string
  target: string | null
  endpoint: string | null
  beaconEndpoint: string | null
  contentType: string
  size: number
  fingerprint: string
  builtAt: string
  tokenId: string | null
  // The baked credential's live state, joined from the token store at list
  // time. Null fields with a tokenId present mean the token is no longer
  // stored -- spent-and-removed, revoked, or expired and swept -- which reads
  // as "no enrollments left."
  tokenMaxUses: number | null
  tokenRemainingUses: number | null
  tokenExpiresAt: string | null
  // The bake-time build parameters; null on payloads built before the
  // snapshot existed, null fields inside mean "the build's default".
  build: PayloadBuildProfile | null
}

export interface PayloadBuildProfile {
  mode: string | null
  sleepSeconds: number | null
  jitterSeconds: number | null
  killDate: string | null
  tokenMaxUses: number | null
  enrollPath: string | null
  userAgent: string | null
  requestTimeoutSeconds: number | null
  envelope: string | null
  checkInProtection: boolean | null
  fallbackEndpoints: string[] | null
}

export async function listPayloads(engagementId: string): Promise<PayloadSummary[]> {
  return jsonOrThrow(await fetch(`engagements/${engagementId}/payloads`))
}

// Removes the stored payload: the bytes and the library row are gone, and a
// stager fetching it 404s from now on. The deletion is audited server-side.
export async function deletePayload(engagementId: string, artifactId: string): Promise<void> {
  await jsonOrThrow<unknown>(
    await fetch(`engagements/${engagementId}/payloads/${artifactId}`, { method: 'DELETE' }),
  )
}

// --- Runtime settings -----------------------------------------
//
// Operator-adjustable server settings. The session-presence pair (the
// staleness sweep's threshold and interval) applies live -- the sweeper reads
// the current values on every pass -- and the server persists changes so a
// restart keeps them.

export interface SessionSettings {
  thresholdMinutes: number
  sweepIntervalMinutes: number
}

export async function getSessionSettings(): Promise<SessionSettings> {
  return jsonOrThrow(await fetch('settings/sessions'))
}

export async function putSessionSettings(input: SessionSettings): Promise<SessionSettings> {
  return jsonOrThrow(
    await fetch('settings/sessions', {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(input),
    }),
  )
}
