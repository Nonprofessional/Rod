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
