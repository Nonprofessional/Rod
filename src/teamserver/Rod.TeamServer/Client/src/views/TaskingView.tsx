import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  type EngagementTask,
  type Implant,
  cancelTask,
  getTask,
  issueTask,
  listEngagementTasks,
  listImplants,
  sendTaskInput,
} from '../api'
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
//
// A queued task that should not run is retracted with its row's Cancel action:
// the server drops it from the dispatch queue before the implant wakes and
// records the retraction in the audit trail (architecture.md Sec 10.3).
//
// Interactive shell tasks (shell.interact, architecture.md Sec 10.3) get an
// Interact row action that opens the live channel pane: the transcript is the
// task's own output, polled while the pane is open; typing posts through the
// input route and Close stdin ends the channel. Every channel verb gets the
// same pane -- tunnel.forward's channel carries the tunnel's bytes the same
// way the shell's carries its stdio.

// The verbs whose tasks run as live channels (the server's ChannelVerbs is the
// authority; the operator UI keeps this mirror so a channel task can offer its
// input pane). The input route refuses anything else server-side.
const CHANNEL_VERBS: readonly string[] = ['shell.interact', 'tunnel.forward']

const isChannelVerb = (verb: string): boolean => CHANNEL_VERBS.includes(verb)

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
  const [interactTask, setInteractTask] = useState<string | null>(null)
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

  // Take a queued task back before the implant wakes. Only offered on Queued
  // rows: a dispatched task belongs to the implant, and the server would
  // refuse the cancel with a 409 anyway.
  const onCancel = async (taskId: string, verb: string) => {
    if (!window.confirm(`Cancel queued ${verb} task ${taskId.slice(0, 8)}? It will never be dispatched.`)) {
      return
    }
    try {
      await cancelTask(engagementId, taskId)
      await refresh()
      setError(null)
    } catch (e) {
      setError(String(e))
      await refresh()
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
      {interactTask && (
        <InteractPane
          engagementId={engagementId}
          taskId={interactTask}
          verb={tasks.find((t) => t.taskId === interactTask)?.verb ?? 'channel'}
          onClose={() => setInteractTask(null)}
        />
      )}
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
              <th></th>
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
                <td>
                  {t.status === 'Queued' && (
                    <button className="danger" onClick={() => void onCancel(t.taskId, t.verb)}>
                      Cancel
                    </button>
                  )}{' '}
                  {isChannelVerb(t.verb) && (
                    <button
                      onClick={() => setInteractTask(interactTask === t.taskId ? null : t.taskId)}
                    >
                      {interactTask === t.taskId ? 'Hide' : 'Interact'}
                    </button>
                  )}
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

// The channel pane: a live channel task's transcript with an input line and
// stdin close. The transcript is the task's own output server-side (the record
// of the session is the session), so the pane polls it while the channel runs
// instead of holding a second event stream; typing posts through the input
// route and Close stdin sends the eof that ends (or half-closes, for a tunnel)
// the channel.
function InteractPane({
  engagementId,
  taskId,
  verb,
  onClose,
}: {
  engagementId: string
  taskId: string
  verb: string
  onClose: () => void
}) {
  const [transcript, setTranscript] = useState('')
  const [status, setStatus] = useState('Dispatched')
  const [line, setLine] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const transcriptRef = useRef<HTMLPreElement>(null)
  const doneRef = useRef(false)
  doneRef.current = status !== 'Dispatched'

  useEffect(() => {
    let stopped = false
    const poll = async () => {
      try {
        const t = await getTask(engagementId, taskId)
        if (stopped) return
        setTranscript(t.output ?? '')
        setStatus(t.status)
        setError(null)
      } catch (e) {
        if (!stopped) setError(String(e))
      }
    }
    void poll()
    const timer = setInterval(() => {
      if (!stopped && !doneRef.current) void poll()
    }, 500)
    return () => {
      stopped = true
      clearInterval(timer)
    }
  }, [engagementId, taskId])

  // Keep the newest output in view as the transcript grows.
  useEffect(() => {
    const pre = transcriptRef.current
    if (pre) pre.scrollTop = pre.scrollHeight
  }, [transcript])

  const done = status !== 'Dispatched'

  const onSend = async (event: React.FormEvent) => {
    event.preventDefault()
    if (!line || busy || done) return
    setBusy(true)
    try {
      await sendTaskInput(engagementId, taskId, line + '\n')
      setLine('')
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  const onCloseStdin = async () => {
    setBusy(true)
    try {
      await sendTaskInput(engagementId, taskId, '', true)
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="interact-pane">
      <h4>
        {verb} &mdash; <code>{taskId.slice(0, 8)}</code> <StatusBadge status={status} />
      </h4>
      <pre className="output interact-transcript" ref={transcriptRef}>
        {transcript || '\u2014'}
      </pre>
      <form className="task-form" onSubmit={onSend}>
        <input
          className="wide"
          placeholder={done ? 'channel closed' : 'type a command'}
          value={line}
          disabled={done || busy}
          onChange={(e) => setLine(e.target.value)}
        />
        <button type="submit" disabled={busy || done || !line}>
          Send
        </button>
        <button type="button" onClick={() => void onCloseStdin()} disabled={busy || done}>
          Close stdin
        </button>
        <button type="button" onClick={onClose}>
          Hide
        </button>
      </form>
      {error && <p className="error">{error}</p>}
    </div>
  )
}
