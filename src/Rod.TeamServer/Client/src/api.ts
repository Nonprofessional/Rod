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

async function jsonOrThrow<T>(response: Response): Promise<T> {
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
  return (await response.json()) as T
}

export interface CreateEngagementInput {
  ownerId: string
  ownerHandle: string
  ownerDisplayName: string
  name: string
}

export async function listEngagements(): Promise<Engagement[]> {
  return jsonOrThrow(await fetch('engagements'))
}

export async function createEngagement(input: CreateEngagementInput): Promise<Engagement> {
  const response = await fetch('engagements', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      ownerId: input.ownerId,
      ownerHandle: input.ownerHandle,
      ownerDisplayName: input.ownerDisplayName,
      name: input.name,
    }),
  })
  return jsonOrThrow(response)
}

export async function mintStagerToken(engagementId: string): Promise<StagerToken> {
  return jsonOrThrow(await fetch(`engagements/${engagementId}/stager-tokens`, { method: 'POST' }))
}

export async function listImplants(engagementId: string): Promise<Implant[]> {
  return jsonOrThrow(await fetch(`engagements/${engagementId}/implants`))
}

export async function listTasks(engagementId: string, implantId: string): Promise<Task[]> {
  return jsonOrThrow<Task[]>(
    await fetch(`engagements/${engagementId}/implants/${implantId}/tasks`),
  ).catch(
    () => [] as Task[],
  )
}

export interface IssueTaskInput {
  implantId: string
  issuedBy: string
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

export async function getTask(engagementId: string, taskId: string): Promise<Task> {
  return jsonOrThrow(await fetch(`engagements/${engagementId}/tasks/${taskId}`))
}

// --- Live event stream (roadmap M2.4) ---------------------------------------
//
// Server-Sent Events keep each connected operator session live on an engagement.
// The bus fans task-issued / task-completed / operator-joined / operator-left
// events out to every subscriber, so two operators see each other's actions in
// real time without polling. Identity is supplied by query parameters in this
// milestone; real operator auth arrives later and replaces only how the identity
// is established, not this stream.

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

// Opens an SSE stream for an engagement with the operator's session identity.
// Returns a close() that tears the stream down; the caller invokes it on
// unmount. The EventSource reconnects automatically on a dropped connection.
export function subscribeToEngagement(
  engagementId: string,
  identity: { operatorId: string; handle: string; displayName: string },
  handlers: EngagementStreamHandlers,
): () => void {
  const params = new URLSearchParams({
    operatorId: identity.operatorId,
    handle: identity.handle,
    displayName: identity.displayName,
  })
  const source = new EventSource(`engagements/${engagementId}/events?${params.toString()}`)

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
