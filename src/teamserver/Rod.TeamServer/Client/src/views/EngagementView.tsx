import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  type LiveOperator,
  type PresenceRecord,
  type SessionOperator,
  listImplants,
  listOnline,
  subscribeToEngagement,
} from '../api'
import type { TabId } from '../tabs'
import { LiveContext } from '../shell'
import { ArtifactsView } from './ArtifactsView'
import { AuditView } from './AuditView'
import { ImplantsView } from './ImplantsView'
import { InteractView } from './InteractView'
import { LaunchersView } from './LaunchersView'
import { ListenersView } from './ListenersView'
import { PayloadBuildView } from './PayloadBuildView'
import { PayloadsView } from './PayloadsView'
import { ReportView } from './ReportView'
import { ShellsView } from './ShellsView'
import { WebShellsView } from './WebShellsView'
import { TaskLogView } from './TaskLogView'

// The engagement detail body: the active view only -- navigation lives in the
// shell's sidebar and the live summary (connection state, fleet counts,
// operator presence) is published to the topbar through LiveContext. One SSE
// stream stays open so every connected operator sees tasking, results, and
// presence live; the stream's hello/leave events also drive the operator
// roster, and a monotonically increasing tick lets child views refresh
// without polling of their own. The online-implant roster (the presence
// query's projection: an implant is online exactly while its session is
// active) is fetched here and handed to the Implants view, on the live tick
// and a short reconciliation poll -- quiet heartbeats produce no SSE event,
// so the poll is what keeps a beaconing implant's last-seen moving.

export function EngagementView({
  engagementId,
  operator,
  tab,
  implantId,
}: {
  engagementId: string
  operator: SessionOperator
  tab: TabId
  // Set only on the session-console route (#/engagements/{id}/implants/{implantId}):
  // the implants tab then renders the per-implant console instead of the fleet
  // table. Undefined on every plain tab route.
  implantId?: string
}) {
  const [online, setOnline] = useState<LiveOperator[]>([])
  const [connected, setConnected] = useState(false)
  // A monotonically increasing tick the SSE handlers bump on any tasking or
  // presence change; child views read it to refresh without polling.
  const [tick, setTick] = useState(0)
  const [implantCount, setImplantCount] = useState(0)
  const [onlineImplants, setOnlineImplants] = useState<PresenceRecord[]>([])
  const [error, setError] = useState<string | null>(null)

  const refreshCount = useCallback(async () => {
    try {
      setImplantCount((await listImplants(engagementId)).length)
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }, [engagementId])

  useEffect(() => {
    void refreshCount()
  }, [refreshCount, tick])

  useEffect(() => {
    const close = subscribeToEngagement(engagementId, {
      onHello: (operators) => {
        setConnected(true)
        setOnline(operators)
      },
      onOperatorJoined: (id, handle) =>
        setOnline((current) =>
          current.some((o) => o.id === id) ? current : [...current, { id, handle, displayName: handle }],
        ),
      onOperatorLeft: (id) => setOnline((current) => current.filter((o) => o.id !== id)),
      onError: () => setConnected(false),
      onTaskIssued: () => setTick((t) => t + 1),
      onTaskCompleted: () => setTick((t) => t + 1),
      onTaskCancelled: () => setTick((t) => t + 1),
      onSessionOpened: () => setTick((t) => t + 1),
      onSessionClosed: () => setTick((t) => t + 1),
      onShellSessionOpened: () => setTick((t) => t + 1),
      onShellSessionEnded: () => setTick((t) => t + 1),
      // A launcher credential was spent by an actual download: the kept
      // rows' budgets and the audit trail both move on it.
      onPayloadFetched: () => setTick((t) => t + 1),
    })
    return close
  }, [engagementId])

  // A stale roster must never survive an engagement switch: clear it now and
  // let the fetch below repopulate for the new engagement.
  useEffect(() => {
    setOnlineImplants([])
  }, [engagementId])

  // The online-implant roster is refreshed on the live tick (SessionOpened
  // when an implant contacts, SessionClosed when its stream dies or is
  // swept) so the fleet counts move the moment the roster changes. A quiet
  // heartbeat -- an online implant simply beaconing on its cadence --
  // produces no SSE event, so the short poll below is also what keeps the
  // fleet's last-seen stamps moving between events, and it re-anchors the
  // roster to the server's view after a dropped SSE connection. One small
  // JSON read per operator every few seconds is nothing next to the SSE
  // stream already held open.
  useEffect(() => {
    let cancelled = false
    const refresh = async () => {
      try {
        const roster = await listOnline(engagementId)
        if (!cancelled) setOnlineImplants(roster)
      } catch {
        // Keep the last known roster; the next tick or poll retries.
      }
    }
    void refresh()
    const timer = window.setInterval(() => void refresh(), 5_000)
    return () => {
      cancelled = true
      window.clearInterval(timer)
    }
  }, [engagementId, tick])

  const live = useMemo(
    () => ({
      connected,
      operators: online,
      implantCount,
      onlineCount: onlineImplants.length,
    }),
    [connected, online, implantCount, onlineImplants.length],
  )

  return (
    <LiveContext.Provider value={live}>
      {error && <p className="error">{error}</p>}
      {tab === 'tasking' && <TaskLogView engagementId={engagementId} onlineTick={tick} />}
      {tab === 'implants' && implantId && (
        <InteractView
          engagementId={engagementId}
          implantId={implantId}
          operator={operator}
          onlineTick={tick}
          onlineImplants={onlineImplants}
        />
      )}
      {tab === 'implants' && !implantId && (
        <ImplantsView engagementId={engagementId} onlineTick={tick} onlineImplants={onlineImplants} />
      )}
      {tab === 'shells' && <ShellsView engagementId={engagementId} onlineTick={tick} />}
      {tab === 'webshells' && <WebShellsView engagementId={engagementId} onlineTick={tick} />}
      {tab === 'audit' && <AuditView engagementId={engagementId} onlineTick={tick} />}
      {tab === 'artifacts' && (
        <ArtifactsView engagementId={engagementId} onlineTick={tick} />
      )}
      {tab === 'report' && <ReportView engagementId={engagementId} />}
      {tab === 'listeners' && <ListenersView engagementId={engagementId} />}
      {tab === 'launchers' && <LaunchersView engagementId={engagementId} onlineTick={tick} />}
      {tab === 'build' && <PayloadBuildView engagementId={engagementId} />}
      {tab === 'payloads' && <PayloadsView engagementId={engagementId} />}
    </LiveContext.Provider>
  )
}
