import type { ListenerSummary } from './api'

// Payload rows bake the dial endpoint into the artifact, but operators think
// in fronts: "which listener does this call?" This maps a baked endpoint back
// to the engagement listener that serves it, so a row can name the front
// instead of a bare URL. Manual endpoints (typed at build for a redirector
// this teamserver does not serve) match nothing and stay verbatim.
export function frontFor(
  endpoint: string | null | undefined,
  listeners: ListenerSummary[],
): ListenerSummary | null {
  if (!endpoint) return null
  return listeners.find((l) => l.publicEndpoint === endpoint) ?? null
}
