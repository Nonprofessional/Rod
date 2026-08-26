import { useCallback, useEffect, useState } from 'react'
import {
  type LiveOperator,
  type PresenceRecord,
  type SessionOperator,
  listImplants,
  listOnline,
  subscribeToEngagement,
} from '../api'
import { Tabs } from '../components/Tabs'
import { ArtifactsView } from './ArtifactsView'
import { AuditView } from './AuditView'
import { ImplantsView } from './ImplantsView'
import { ListenersView } from './ListenersView'
import { PayloadBuildView } from './PayloadBuildView'
import { ReportView } from './ReportView'
import { TaskingView } from './TaskingView'
import { TimelineView } from './TimelineView'

// The engagement detail shell: the full capability surface under
// one set of tabs -- tasking (recon through exploit), implants (with retire), the
// M6 evidence views (audit, artifacts, timeline, report), and the M4 OPSEC
// controls (listeners/redirectors, payload build). One SSE stream stays open so
// every connected operator sees tasking, results, and presence live . The
// roster card under the operators card is the presence query -- the online
// implants for this engagement -- refreshed on the same live tick plus a slow
// poll, because a session opening is not a live event (a close is).

type TabId = 'tasking' | 'implants' | 'audit' | 'artifacts' | 'timeline' | 'report' | 'listeners' | 'build'

const TABS = [
  { id: 'tasking', label: 'Tasking' },
  { id: 'implants', label: 'Implants' },
  { id: 'audit', label: 'Audit' },
  { id: 'artifacts', label: 'Artifacts' },
  { id: 'timeline', label: 'Timeline' },
  { id: 'report', label: 'Report' },
  { id: 'listeners', label: 'Listeners' },
  { id: 'build', label: 'Build' },
] as const

export function EngagementView({
  engagementId,
  operator,
  tab,
  onTab,
}: {
  engagementId: string
  operator: SessionOperator
  tab: string
  onTab: (id: string) => void
}) {
  const [online, setOnline] = useState<LiveOperator[]>([])
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
      onHello: (operators) => setOnline(operators),
      onOperatorJoined: (id, handle) =>
        setOnline((current) =>
          current.some((o) => o.id === id) ? current : [...current, { id, handle, displayName: handle }],
        ),
      onOperatorLeft: (id) => setOnline((current) => current.filter((o) => o.id !== id)),
      onTaskIssued: () => setTick((t) => t + 1),
      onTaskCompleted: () => setTick((t) => t + 1),
      onTaskCancelled: () => setTick((t) => t + 1),
      onSessionClosed: () => setTick((t) => t + 1),
    })
    return close
  }, [engagementId])

  // A stale roster must never survive an engagement switch: clear it now and
  // let the fetch below repopulate for the new engagement.
  useEffect(() => {
    setOnlineImplants([])
  }, [engagementId])

  // The online-implant roster is the presence query's projection: an implant
  // is online exactly while its session is active. A session opening is not
  // a live event (a close fires SessionClosed), so besides the live-event
  // tick the roster refreshes on a slow poll -- without it a fresh implant
  // stays invisible until someone acts.
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
    const timer = window.setInterval(() => void refresh(), 10_000)
    return () => {
      cancelled = true
      window.clearInterval(timer)
    }
  }, [engagementId, tick])

  const activeTab = (TABS.find((t) => t.id === tab)?.id ?? 'tasking') as TabId

  return (
    <section>
      <h2>Engagement</h2>
      <p className="muted">
        <code>{engagementId}</code> &middot; {implantCount} implant{implantCount === 1 ? '' : 's'}
      </p>
      {error && <p className="error">{error}</p>}

      <div className="card">
        <h3>Operators online</h3>
        {online.length === 0 ? (
          <p className="muted">No other operators connected.</p>
        ) : (
          <ul className="sessions">
            {online.map((o) => (
              <li key={o.id}>
                <span className="dot online" title="online" />
                <code>{o.handle || o.id.slice(0, 8)}</code>
                {o.id === operator.operatorId && <span className="muted"> (you)</span>}
              </li>
            ))}
          </ul>
        )}
      </div>

      <div className="card">
        <h3>Implants online</h3>
        {onlineImplants.length === 0 ? (
          <p className="muted">No implants online.</p>
        ) : (
          <table>
            <thead>
              <tr>
                <th>Implant</th>
                <th>Online since</th>
                <th>Last seen</th>
                <th>Capabilities</th>
              </tr>
            </thead>
            <tbody>
              {onlineImplants.map((p) => (
                <tr key={p.sessionId}>
                  <td>
                    <span className="dot online" title="online" />{' '}
                    <code>{p.implantId.slice(0, 8)}</code>
                  </td>
                  <td>{new Date(p.onlineAt).toLocaleTimeString()}</td>
                  <td>{new Date(p.lastSeenAt).toLocaleTimeString()}</td>
                  <td>
                    <span className="muted" title={p.capabilities.join(', ')}>
                      {p.capabilities.length} capabilit{p.capabilities.length === 1 ? 'y' : 'ies'}
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      <Tabs tabs={TABS} active={activeTab} onSelect={onTab} />

      {activeTab === 'tasking' && (
        <TaskingView engagementId={engagementId} operator={operator} onlineTick={tick} />
      )}
      {activeTab === 'implants' && (
        <ImplantsView engagementId={engagementId} onlineTick={tick} />
      )}
      {activeTab === 'audit' && <AuditView engagementId={engagementId} onlineTick={tick} />}
      {activeTab === 'artifacts' && (
        <ArtifactsView engagementId={engagementId} onlineTick={tick} />
      )}
      {activeTab === 'timeline' && <TimelineView engagementId={engagementId} />}
      {activeTab === 'report' && <ReportView engagementId={engagementId} />}
      {activeTab === 'listeners' && <ListenersView />}
      {activeTab === 'build' && <PayloadBuildView engagementId={engagementId} />}
    </section>
  )
}
