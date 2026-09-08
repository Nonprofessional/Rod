import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  type EngagementTask,
  type Implant,
  cancelTask,
  listEngagementTasks,
  listImplantTasks,
  listImplants,
} from '../api'
import { Icon } from '../components/Icons'
import { InteractPane } from '../components/InteractPane'
import { StatusBadge } from '../components/StatusBadge'
import { CHANNEL_VERBS } from '../verbForms'

// The task log: the engagement's working record of issued tasking, live on
// the SSE tick. Issuing moved to the fleet's context menu and the session
// console; what belongs here is reading -- filter by implant (its console
// feed, engagement-wide when unfiltered), by verb, by status, by operator, or
// free text over verb/arguments/output, and watch new rows land as operators
// work. "Show me every shell command this engagement ran" is the shape this
// view exists for.
//
// A queued task still retracts from here (its Cancel), and channel tasks
// still open their pane (their Interact) -- the log is where an operator
// notices, not where they type.

const isChannelVerb = (verb: string): boolean => CHANNEL_VERBS.includes(verb)

export function TaskLogView({
  engagementId,
  onlineTick,
}: {
  engagementId: string
  onlineTick: number
}) {
  const [tasks, setTasks] = useState<EngagementTask[]>([])
  const [cursor, setCursor] = useState<string | null>(null)
  const [implants, setImplants] = useState<Implant[]>([])
  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  const [interactTask, setInteractTask] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [loadingMore, setLoadingMore] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // The filters. Implant "" means engagement-wide; the others are
  // client-side predicates over the loaded window.
  const [implantFilter, setImplantFilter] = useState('')
  const [verbFilter, setVerbFilter] = useState('')
  const [statusFilter, setStatusFilter] = useState('')
  const [operatorFilter, setOperatorFilter] = useState('')
  const [textFilter, setTextFilter] = useState('')

  const refresh = useCallback(async () => {
    try {
      const [implantList, page] = await Promise.all([
        listImplants(engagementId),
        implantFilter
          ? listImplantTasks(engagementId, implantFilter)
          : listEngagementTasks(engagementId),
      ])
      setImplants(implantList)
      setTasks(page.items)
      setCursor(page.nextCursor)
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setLoading(false)
    }
  }, [engagementId, implantFilter])

  useEffect(() => {
    void refresh()
  }, [refresh, onlineTick])

  // A filter switch must not carry an expanded row or an open pane across
  // sources; the rows themselves are different objects.
  useEffect(() => {
    setExpanded(new Set())
    setInteractTask(null)
  }, [implantFilter])

  const loadOlder = useCallback(async () => {
    if (!cursor) return
    setLoadingMore(true)
    try {
      const page = implantFilter
        ? await listImplantTasks(engagementId, implantFilter, cursor)
        : await listEngagementTasks(engagementId, cursor)
      setTasks((current) => [...current, ...page.items])
      setCursor(page.nextCursor)
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setLoadingMore(false)
    }
  }, [engagementId, implantFilter, cursor])

  // The verb/operator/status options come from the loaded window: the set of
  // things that actually happened, not a catalog of things that could.
  const verbs = useMemo(() => [...new Set(tasks.map((t) => t.verb))].sort(), [tasks])
  const statuses = useMemo(() => [...new Set(tasks.map((t) => t.status))].sort(), [tasks])
  const operators = useMemo(
    () => [...new Set(tasks.map((t) => t.issuedBy))].sort(),
    [tasks],
  )

  const filtered = useMemo(() => {
    const needle = textFilter.trim().toLowerCase()
    return tasks.filter((t) => {
      if (verbFilter && t.verb !== verbFilter) return false
      if (statusFilter && t.status !== statusFilter) return false
      if (operatorFilter && t.issuedBy !== operatorFilter) return false
      if (implantFilter && t.implantId !== implantFilter) return false
      if (needle !== '') {
        const haystack = `${t.verb} ${t.arguments} ${t.output ?? ''}`.toLowerCase()
        if (!haystack.includes(needle)) return false
      }
      return true
    })
  }, [tasks, verbFilter, statusFilter, operatorFilter, implantFilter, textFilter])

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

  const toggleExpanded = (taskId: string) => {
    setExpanded((current) => {
      const next = new Set(current)
      if (next.has(taskId)) next.delete(taskId)
      else next.add(taskId)
      return next
    })
  }

  if (loading) {
    return (
      <div className="card">
        <h3>Task log</h3>
        <div className="empty">
          <span className="spinner" />
          Loading the task log…
        </div>
      </div>
    )
  }

  return (
    <div className="card">
      <h3>Task log</h3>
      <p className="muted">
        Every task this engagement issued, newest first, live as operators work. Issue from
        the fleet's context menu or a session console; read it here.
      </p>
      <div className="inline-form">
        <select
          value={implantFilter}
          onChange={(e) => setImplantFilter(e.target.value)}
          title="Filter by implant (switches to that implant's own history)"
        >
          <option value="">All implants</option>
          {implants.map((i) => (
            <option key={i.implantId} value={i.implantId}>
              {i.hostname ? `${i.hostname} · ` : ''}
              {i.implantId.slice(0, 8)} ({i.class})
            </option>
          ))}
        </select>
        <select value={verbFilter} onChange={(e) => setVerbFilter(e.target.value)} title="Filter by verb">
          <option value="">All verbs</option>
          {verbs.map((v) => (
            <option key={v} value={v}>
              {v}
            </option>
          ))}
        </select>
        <select value={statusFilter} onChange={(e) => setStatusFilter(e.target.value)} title="Filter by status">
          <option value="">Any status</option>
          {statuses.map((s) => (
            <option key={s} value={s}>
              {s}
            </option>
          ))}
        </select>
        <select
          value={operatorFilter}
          onChange={(e) => setOperatorFilter(e.target.value)}
          title="Filter by the issuing operator"
        >
          <option value="">Any operator</option>
          {operators.map((o) => (
            <option key={o} value={o}>
              {o.slice(0, 8)}
            </option>
          ))}
        </select>
        <input
          className="filter-text"
          placeholder="Search verb, arguments, output…"
          value={textFilter}
          onChange={(e) => setTextFilter(e.target.value)}
        />
      </div>

      {interactTask && (
        <InteractPane
          engagementId={engagementId}
          taskId={interactTask}
          verb={tasks.find((t) => t.taskId === interactTask)?.verb ?? 'channel'}
          onClose={() => setInteractTask(null)}
        />
      )}

      {filtered.length === 0 ? (
        <div className="empty">
          <Icon name="inbox" />
          {tasks.length === 0 ? 'No tasks yet.' : 'No tasks match the filters.'}
        </div>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Verb</th>
                <th>Implant</th>
                <th>Status</th>
                <th>By</th>
                <th>At</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {[...filtered].reverse().map((t) => (
                <LogRow
                  key={t.taskId}
                  engagementId={engagementId}
                  task={t}
                  expanded={expanded.has(t.taskId)}
                  onToggle={() => toggleExpanded(t.taskId)}
                  onCancel={() => void onCancel(t.taskId, t.verb)}
                  onInteract={() =>
                    setInteractTask(interactTask === t.taskId ? null : t.taskId)
                  }
                  interactOpen={interactTask === t.taskId}
                />
              ))}
            </tbody>
          </table>
        </div>
      )}
      {cursor && (
        <div className="load-more">
          <button className="ghost" onClick={() => void loadOlder()} disabled={loadingMore}>
            {loadingMore ? 'Loading…' : 'Load older'}
          </button>
        </div>
      )}
      {error && <p className="error">{error}</p>}
    </div>
  )
}

function LogRow({
  engagementId,
  task,
  expanded,
  onToggle,
  onCancel,
  onInteract,
  interactOpen,
}: {
  engagementId: string
  task: EngagementTask
  expanded: boolean
  onToggle: () => void
  onCancel: () => void
  onInteract: () => void
  interactOpen: boolean
}) {
  return (
    <>
      <tr className="console-row" onClick={onToggle} title="Click to toggle output">
        <td>
          <code>{task.verb}</code>{' '}
          <span className="muted console-args">
            {task.arguments.length > 0 ? ellipsize(task.arguments) : ''}
          </span>
        </td>
        <td>
          <a
            className="button-link sm"
            href={`#/engagements/${engagementId}/implants/${task.implantId}`}
            title="Open the session console"
            onClick={(e) => e.stopPropagation()}
          >
            {task.implantId.slice(0, 8)}
          </a>
        </td>
        <td>
          <StatusBadge status={task.status} />
          {task.outcome === 'Failed' && <span className="error"> failed</span>}
        </td>
        <td>
          <code title={task.issuedBy}>{task.issuedBy.slice(0, 8)}</code>
        </td>
        <td>
          {task.completedAt
            ? new Date(task.completedAt).toLocaleTimeString()
            : new Date(task.createdAt).toLocaleTimeString()}
        </td>
        <td onClick={(e) => e.stopPropagation()}>
          <div className="row-actions">
            {task.status === 'Queued' && (
              <button className="danger sm" onClick={onCancel}>
                Cancel
              </button>
            )}
            {isChannelVerb(task.verb) && (
              <button className="sm" onClick={onInteract}>
                {interactOpen ? 'Hide' : 'Interact'}
              </button>
            )}
          </div>
        </td>
      </tr>
      {expanded && (
        <tr>
          <td colSpan={6}>
            <pre className="output long">{task.output ?? '—'}</pre>
          </td>
        </tr>
      )}
    </>
  )
}

function ellipsize(value: string, max = 72): string {
  const single = value.replace(/\s+/g, ' ')
  return single.length > max ? `${single.slice(0, max)}…` : single
}
