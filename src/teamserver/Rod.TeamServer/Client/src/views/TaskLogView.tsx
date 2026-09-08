import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  type ArtifactSummary,
  type EngagementTask,
  type Implant,
  fetchArtifactBlob,
  listArtifacts,
  listEngagementTasks,
  listImplantTasks,
  listImplants,
} from '../api'
import { Icon } from '../components/Icons'
import { InteractPane } from '../components/InteractPane'
import { StatusBadge } from '../components/StatusBadge'
import { CHANNEL_VERBS } from '../verbForms'

// The task log: the engagement's working record of issued tasking, live on
// the SSE tick. Issuing moved to the implants menu and the session console;
// what belongs here is reading -- filter by implant (its console feed,
// engagement-wide when unfiltered), by verb, by status, by operator, or
// free text over verb/arguments/output, and watch new rows land as operators
// work. "Show me every shell command this engagement ran" is the shape this
// view exists for. Read-only by design: retracting a queued task is an
// operational action and lives in the session console, next to the implant
// it targets; channel transcripts still open from their rows.

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
        Every task this engagement issued, newest first, live as operators work -- read-only;
        cancel queued tasking from the implant's session console.
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
                <th></th>
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
  onInteract,
  interactOpen,
}: {
  engagementId: string
  task: EngagementTask
  expanded: boolean
  onToggle: () => void
  onInteract: () => void
  interactOpen: boolean
}) {
  return (
    <>
      <tr className="console-row" onClick={onToggle}>
        <td onClick={(e) => e.stopPropagation()}>
          <button
            className={`ghost sm row-expand${expanded ? ' open' : ''}`}
            onClick={onToggle}
            title={expanded ? 'Collapse the output' : 'Expand the full output'}
          >
            <Icon name={expanded ? 'chevronDown' : 'chevronRight'} />
          </button>
        </td>
        <td>
          <div>
            <code>{task.verb}</code>{' '}
            <span className="muted console-args">
              {task.arguments.length > 0 ? ellipsize(task.arguments) : ''}
            </span>
          </div>
          {/* A one-line preview of the answer under the command: the log's
              rows stay scannable while still showing that data came back --
              the chevron unfolds the whole thing. */}
          {!expanded && task.output && (
            <div className="muted console-args output-preview">{ellipsize(task.output, 96)}</div>
          )}
        </td>
        <td>
          <a
            className="button-link sm"
            href={`#/engagements/${engagementId}/implants/${task.implantId}`}
            title="Open the session console -- queued tasks cancel there"
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
          <td colSpan={7}>
            <div className="task-detail">
              {task.arguments.length > 0 && (
                <div className="task-detail-line">
                  <span className="muted">arguments</span> <code>{task.arguments}</code>
                </div>
              )}
              <pre className="output long">{task.output ?? '—'}</pre>
              <div className="task-detail-line muted">
                created {new Date(task.createdAt).toLocaleString()}
                {task.completedAt && <> · completed {new Date(task.completedAt).toLocaleString()}</>}
                {' '}· by <code title={task.issuedBy}>{task.issuedBy.slice(0, 8)}</code>
              </div>
              <TaskArtifacts engagementId={engagementId} taskId={task.taskId} />
            </div>
          </td>
        </tr>
      )}
    </>
  )
}

// The artifacts a task produced or consumed, lazily fetched on expand: a
// staged upload binds its bytes to the task, a large pull streams into the
// store -- the expanded row carries the download instead of making the
// operator cross to the Artifacts tab.
function TaskArtifacts({ engagementId, taskId }: { engagementId: string; taskId: string }) {
  const [artifacts, setArtifacts] = useState<ArtifactSummary[] | null>(null)

  useEffect(() => {
    let stopped = false
    void listArtifacts(engagementId, taskId)
      .then((page) => {
        if (!stopped) setArtifacts(page.items)
      })
      .catch(() => {
        if (!stopped) setArtifacts([])
      })
    return () => {
      stopped = true
    }
  }, [engagementId, taskId])

  if (!artifacts || artifacts.length === 0) return null

  const onDownload = async (artifact: ArtifactSummary) => {
    try {
      const blob = await fetchArtifactBlob(engagementId, artifact.artifactId)
      const url = URL.createObjectURL(blob)
      const anchor = document.createElement('a')
      anchor.href = url
      anchor.download = artifact.name
      anchor.click()
      window.setTimeout(() => URL.revokeObjectURL(url), 30_000)
    } catch {
      // The row stays; a failed fetch surfaces on retry.
    }
  }

  return (
    <div className="task-detail-artifacts">
      <span className="muted">artifacts</span>
      {artifacts.map((a) => (
        <span key={a.artifactId} className="task-detail-artifact">
          <code>{a.name}</code> <span className="muted">({a.size} bytes)</span>{' '}
          <button className="sm" onClick={() => void onDownload(a)}>
            Download
          </button>
        </span>
      ))}
    </div>
  )
}

function ellipsize(value: string, max = 72): string {
  const single = value.replace(/\s+/g, ' ')
  return single.length > max ? `${single.slice(0, max)}…` : single
}
