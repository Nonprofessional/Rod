import { useEffect, useReducer } from 'react'
import { cancelTask, getTask, issueTask } from './api'

// The browse-result cache: the memory behind the process and file browsers.
//
// Browses (recon.ps, fs.list) are tasks like any other -- they sit queued
// until the implant wakes, then answer. A pane that closed while the answer
// was in flight used to lose it and re-issue on reopen, which both wasted a
// check-in cycle and stacked duplicate commands. This module keeps the last
// browse per (implant, verb, arguments) at module scope -- outside React, so
// it survives pane close -- and polls in-flight tasks on its own timer, so
// the answer lands in the cache whether or not anyone is looking. Reopening
// shows the cached snapshot immediately; refresh re-issues deliberately.
//
// One browse per key at a time: an in-flight browse is attached to, never
// duplicated, and a forced refresh first cancels the queued task it replaces
// (a dispatched one is already in the implant's hands and cannot be called
// back -- the refresh rides the next issue).
//
// The cache is engagement-keyed and session-lived; it is a working
// convenience, not evidence -- the task log and audit trail remain the
// record.

export interface BrowseEntry {
  engagementId: string
  implantId: string
  verb: string
  arguments: string
  taskId: string
  status: string
  outcome: string | null
  output: string | null
  createdAt: string
  completedAt: string | null
}

const IN_FLIGHT = new Set(['Queued', 'Dispatched'])

const entries = new Map<string, BrowseEntry>()
const lastPaths = new Map<string, string>()
const listeners = new Set<() => void>()
let pollTimer: number | null = null

const keyOf = (engagementId: string, implantId: string, verb: string, args: string) =>
  `${engagementId}|${implantId}|${verb}|${args}`

function notify() {
  for (const listener of listeners) listener()
}

// The background poller: runs only while something is in flight. It is the
// whole point of the cache -- completion is recorded even with every pane
// closed.
function ensurePolling() {
  if (pollTimer !== null) return
  pollTimer = window.setInterval(() => void pollInFlight(), 800)
}

async function pollInFlight() {
  const inFlight = [...entries.values()].filter((e) => IN_FLIGHT.has(e.status))
  if (inFlight.length === 0) {
    if (pollTimer !== null) {
      window.clearInterval(pollTimer)
      pollTimer = null
    }
    return
  }
  let changed = false
  for (const entry of inFlight) {
    try {
      const detail = await getTask(entry.engagementId, entry.taskId)
      if (
        detail.status !== entry.status ||
        detail.outcome !== entry.outcome ||
        detail.output !== entry.output
      ) {
        const key = keyOf(entry.engagementId, entry.implantId, entry.verb, entry.arguments)
        entries.set(key, {
          ...entry,
          status: detail.status,
          outcome: detail.outcome,
          output: detail.output,
          completedAt: IN_FLIGHT.has(detail.status) ? null : new Date().toISOString(),
        })
        changed = true
      }
    } catch {
      // A lost race (engagement switched, task evicted): the next tick
      // retries, or the entry leaves the in-flight set on its own terminal
      // answer. Never let one bad probe stop the others.
    }
  }
  if (changed) notify()
}

export function browseInFlight(entry: BrowseEntry | undefined): boolean {
  return !!entry && IN_FLIGHT.has(entry.status)
}

// The browsers' issue path. Without force: a cached answer returns as-is, an
// in-flight browse is attached to (no duplicate), and only a cold key issues.
// With force: the queued task this replaces is cancelled first, then a fresh
// browse issues.
export async function ensureBrowse(
  engagementId: string,
  implantId: string,
  verb: string,
  args: string,
  opts: { force?: boolean } = {},
): Promise<BrowseEntry> {
  const key = keyOf(engagementId, implantId, verb, args)
  const current = entries.get(key)
  if (current && browseInFlight(current)) return current
  if (current && !opts.force) return current
  if (current && current.status === 'Queued') {
    try {
      await cancelTask(engagementId, current.taskId)
    } catch {
      // It dispatched between the check and the cancel: it answers into the
      // cache and is superseded by the fresh issue below anyway.
    }
  }
  const issued = await issueTask(engagementId, { implantId, verb, arguments: args })
  const entry: BrowseEntry = {
    engagementId,
    implantId,
    verb,
    arguments: args,
    taskId: issued.taskId,
    // The issue response carries no status (a fresh task is queued by
    // construction); the poller's first read replaces this with the real
    // one.
    status: issued.status ?? 'Queued',
    outcome: issued.outcome ?? null,
    output: issued.output ?? null,
    createdAt: issued.createdAt,
    completedAt: null,
  }
  entries.set(key, entry)
  notify()
  ensurePolling()
  return entry
}

// The file browser's last visited directory per implant, so reopening lands
// where the operator left rather than at the root.
export function lastBrowsedPath(engagementId: string, implantId: string): string | null {
  return lastPaths.get(`${engagementId}|${implantId}`) ?? null
}

export function rememberBrowsedPath(engagementId: string, implantId: string, path: string) {
  lastPaths.set(`${engagementId}|${implantId}`, path)
}

// Re-renders the calling component whenever a tracked browse changes, and
// hands back the entry for the key the component is showing.
export function useBrowseEntry(
  engagementId: string,
  implantId: string,
  verb: string,
  args: string,
): BrowseEntry | undefined {
  const [, bump] = useReducer((n: number) => n + 1, 0)
  useEffect(() => {
    listeners.add(bump)
    return () => {
      listeners.delete(bump)
    }
  }, [bump])
  return entries.get(keyOf(engagementId, implantId, verb, args))
}
