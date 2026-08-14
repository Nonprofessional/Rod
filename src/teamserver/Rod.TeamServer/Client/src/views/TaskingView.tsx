import { useCallback, useEffect, useMemo, useState } from 'react'
import { type EngagementTask, type Implant, issueTask, listEngagementTasks, listImplants } from '../api'
import { loadCapabilityGroups, type CapabilityGroup } from '../capabilities'
import { OpsecBadges } from '../components/OpsecBadges'
import { StatusBadge } from '../components/StatusBadge'
import type { SessionOperator } from '../api'

// The tasking panel: issue any capability verb -- recon through
// exploit -- against any implant in the engagement, and watch the engagement-wide
// task history update. The capability picker is grouped by category and carries
// each verb's OPSEC attributes as risk badges (architecture.md Sec 7), driven by
// the registry (GET /capabilities) so the verb table is never hardcoded here.
// Sensitive categories (evasion, exploit) are surfaced as issuable verbs too;
// this surface holds only the contract, never concrete tradecraft.

export function TaskingView({
  engagementId,
  operator,
  onlineTick,
}: {
  engagementId: string
  operator: SessionOperator
  onlineTick: number
}) {
  const [groups, setGroups] = useState<CapabilityGroup[]>([])
  const [implants, setImplants] = useState<Implant[]>([])
  const [tasks, setTasks] = useState<EngagementTask[]>([])
  const [tasksCursor, setTasksCursor] = useState<string | null>(null)
  const [selectedImplant, setSelectedImplant] = useState('')
  const [verb, setVerb] = useState('shell.exec')
  const [args, setArgs] = useState('whoami')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [loadingMore, setLoadingMore] = useState(false)
  const [loading, setLoading] = useState(true)

  // Load the capability catalog once; the picker is static for the session.
  useEffect(() => {
    void loadCapabilityGroups()
      .then(setGroups)
      .catch((e) => setError(String(e)))
  }, [])

  const refresh = useCallback(async () => {
    try {
      // The newest window of the task history; older pages load on demand.
      const [implantList, taskPage] = await Promise.all([
        listImplants(engagementId),
        listEngagementTasks(engagementId),
      ])
      setImplants(implantList)
      setTasks(taskPage.items)
      setTasksCursor(taskPage.nextCursor)
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setLoading(false)
    }
  }, [engagementId])

  const loadOlderTasks = useCallback(async () => {
    if (!tasksCursor) return
    setLoadingMore(true)
    try {
      const page = await listEngagementTasks(engagementId, tasksCursor)
      setTasks((current) => [...current, ...page.items])
      setTasksCursor(page.nextCursor)
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setLoadingMore(false)
    }
  }, [engagementId, tasksCursor])

  useEffect(() => {
    void refresh()
  }, [refresh, onlineTick])

  // A stale implant selection must never carry into another engagement: the
  // component instance survives an engagement switch, so reset the picker when
  // the engagement changes instead of issuing against the wrong one.
  useEffect(() => {
    setSelectedImplant('')
  }, [engagementId])

  // Default the selected implant to the first non-retired one once implants load.
  useEffect(() => {
    if (selectedImplant) return
    const first = implants.find((i) => !i.retiredAt) ?? implants[0]
    if (first) setSelectedImplant(first.implantId)
  }, [implants, selectedImplant])

  // Descriptors indexed by verb so the picker can look up the OPSEC badges for
  // the currently selected verb.
  const descriptorByVerb = useMemo(() => {
    const map = new Map<string, { attributes: Record<string, string>; category: string }>()
    for (const group of groups) {
      for (const descriptor of group.descriptors) {
        map.set(descriptor.verb, { attributes: descriptor.attributes, category: descriptor.category })
      }
    }
    return map
  }, [groups])

  const activeImplant = implants.find((i) => i.implantId === selectedImplant)
  const activeRetired = activeImplant?.retiredAt != null

  const onIssue = async (event: React.FormEvent) => {
    event.preventDefault()
    if (!selectedImplant) return
    setBusy(true)
    try {
      await issueTask(engagementId, {
        implantId: selectedImplant,
        verb,
        arguments: args,
      })
      await refresh()
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  if (loading) {
    return (
      <div className="card">
        <h3>Tasking</h3>
        <p className="muted">Loading tasking…</p>
      </div>
    )
  }

  if (implants.length === 0) {
    return (
      <div className="card">
        <h3>Tasking</h3>
        <p className="muted">Enroll an implant first, then issue tasking against it here.</p>
        {error && <p className="error">{error}</p>}
      </div>
    )
  }

  return (
    <div className="card">
      <h3>Issue task</h3>
      <p className="muted">
        Pick a target implant, a capability verb, and the argument string. Verbs are grouped by
        category from the registry; badges flag OPSEC impact.
      </p>
      <form className="task-form" onSubmit={onIssue}>
        <select value={selectedImplant} onChange={(e) => setSelectedImplant(e.target.value)}>
          {implants.map((i) => (
            <option key={i.implantId} value={i.implantId}>
              {i.implantId.slice(0, 8)} ({i.class}){i.retiredAt ? ' [retired]' : ''}
            </option>
          ))}
        </select>
        <select value={verb} onChange={(e) => setVerb(e.target.value)}>
          {groups.map((group) => (
            <optgroup key={group.category} label={group.label}>
              {group.descriptors.map((d) => (
                <option key={d.verb} value={d.verb}>
                  {d.verb}
                </option>
              ))}
            </optgroup>
          ))}
        </select>
        <input
          className="wide"
          placeholder="arguments"
          value={args}
          onChange={(e) => setArgs(e.target.value)}
        />
        <button type="submit" disabled={busy || activeRetired}>
          Issue
        </button>
      </form>
      <div className="task-meta">
        {descriptorByVerb.has(verb) ? (
          <OpsecBadges attributes={descriptorByVerb.get(verb)!.attributes} />
        ) : (
          <span className="muted">verb not in registry (free-form)</span>
        )}
        {activeRetired && <span className="error"> that implant is retired</span>}
      </div>
      {error && <p className="error">{error}</p>}

      <h3>Task history &mdash; engagement-wide</h3>
      {tasks.length === 0 ? (
        <p className="muted">No tasks yet.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Verb</th>
              <th>Implant</th>
              <th>By</th>
              <th>Status</th>
              <th>Output</th>
              <th>At</th>
            </tr>
          </thead>
          <tbody>
            {[...tasks].reverse().map((t) => (
              <tr key={t.taskId}>
                <td>
                  <code>{t.verb}</code> {t.arguments}
                </td>
                <td>
                  <code>{t.implantId.slice(0, 8)}</code>
                </td>
                <td>
                  <code>{t.issuedBy === operator.operatorId ? 'you' : t.issuedBy.slice(0, 8)}</code>
                </td>
                <td>
                  <StatusBadge status={t.status} />
                </td>
                <td>
                  <pre className="output">{t.output ?? '\u2014'}</pre>
                </td>
                <td>
                  {t.completedAt
                    ? new Date(t.completedAt).toLocaleTimeString()
                    : new Date(t.createdAt).toLocaleTimeString()}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {tasksCursor && (
        <p>
          <button onClick={() => void loadOlderTasks()} disabled={loadingMore}>
            {loadingMore ? 'Loading…' : 'Load older'}
          </button>
        </p>
      )}
    </div>
  )
}
