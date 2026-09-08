import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  type EngagementTask,
  type Implant,
  type PresenceRecord,
  type SessionOperator,
  cancelTask,
  issueTask,
  listImplantTasks,
  listImplants,
} from '../api'
import { loadCapabilityGroups, type CapabilityGroup } from '../capabilities'
import { ContextMenu } from '../components/ContextMenu'
import { useContextMenu } from '../contextMenuState'
import { Icon } from '../components/Icons'
import { InteractPane } from '../components/InteractPane'
import { ProcessBrowser } from '../components/ProcessBrowser'
import { StatusBadge } from '../components/StatusBadge'
import { TaskDialog } from '../components/TaskDialog'
import { CHANNEL_VERBS, VERB_FORMS } from '../verbForms'
import { implantMenuEntries } from './implantMenu'

// The session console: one implant, operator-first. The header names the
// device and the identity; the feed is this implant's own task history (live
// on the SSE tick), rows expanding to their output; the command bar at the
// bottom is the operator's keyboard path -- a plain line runs as a shell
// command, a handful of prefixed words map to the common verbs. Channel tasks
// (interactive shell, tunnels) get their terminal pane here. Everything the
// command bar cannot express (an upload's file picker, the full verb table)
// stays one menu away in the header's three-dot menu -- the same menu the
// fleet rows open.

const isChannelVerb = (verb: string): boolean => CHANNEL_VERBS.includes(verb)

interface QuickCommand {
  word: string
  usage: string
  note: string
}

const QUICK_HELP: readonly QuickCommand[] = [
  { word: '', usage: '<anything>', note: 'runs as a shell.exec command' },
  { word: 'interact', usage: 'interact', note: 'open the interactive shell channel' },
  { word: 'ps', usage: 'ps', note: 'list processes (the browser pane is in the menu)' },
  { word: 'kill', usage: 'kill <pid>', note: 'terminate a process' },
  { word: 'screenshot', usage: 'screenshot', note: 'capture the display' },
  { word: 'hostenum', usage: 'hostenum', note: 'local host facts' },
  { word: 'portscan', usage: 'portscan <host> <start-end>', note: 'scan a host' },
  { word: 'services', usage: 'services <host> <ports>', note: 'probe services' },
  { word: 'download', usage: 'download <path>', note: 'pull a file back' },
  { word: 'raw', usage: 'raw <verb> [args…]', note: 'issue any verb directly' },
]

export function InteractView({
  engagementId,
  implantId,
  operator,
  onlineTick,
  onlineImplants,
}: {
  engagementId: string
  implantId: string
  operator: SessionOperator
  onlineTick: number
  onlineImplants: PresenceRecord[]
}) {
  const [implant, setImplant] = useState<Implant | null>(null)
  const [missing, setMissing] = useState(false)
  const [tasks, setTasks] = useState<EngagementTask[]>([])
  const [cursor, setCursor] = useState<string | null>(null)
  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  const [interactTask, setInteractTask] = useState<string | null>(null)
  const [dialogVerb, setDialogVerb] = useState<string | null>(null)
  const [processes, setProcesses] = useState(false)
  const [groups, setGroups] = useState<CapabilityGroup[]>([])
  const [line, setLine] = useState('')
  const [hint, setHint] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const menu = useContextMenu()

  useEffect(() => {
    void loadCapabilityGroups()
      .then(setGroups)
      .catch((e) => setError(String(e)))
  }, [])

  const refresh = useCallback(async () => {
    try {
      const [implants, page] = await Promise.all([
        listImplants(engagementId),
        listImplantTasks(engagementId, implantId),
      ])
      setImplant(implants.find((i) => i.implantId === implantId) ?? null)
      setMissing(implants.every((i) => i.implantId !== implantId))
      setTasks(page.items)
      setCursor(page.nextCursor)
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }, [engagementId, implantId])

  useEffect(() => {
    void refresh()
  }, [refresh, onlineTick])

  const loadOlder = useCallback(async () => {
    if (!cursor) return
    try {
      const page = await listImplantTasks(engagementId, implantId, cursor)
      setTasks((current) => [...current, ...page.items])
      setCursor(page.nextCursor)
    } catch (e) {
      setError(String(e))
    }
  }, [engagementId, implantId, cursor])

  const descriptorByVerb = useMemo(() => {
    const map = new Map<string, Record<string, string>>()
    for (const group of groups) {
      for (const descriptor of group.descriptors) {
        map.set(descriptor.verb, descriptor.attributes)
      }
    }
    return map
  }, [groups])

  const presence = onlineImplants.find((p) => p.implantId === implantId)

  const issue = useCallback(
    async (verb: string, args: string) => {
      setBusy(true)
      try {
        const task = await issueTask(engagementId, { implantId, verb, arguments: args })
        setHint(null)
        await refresh()
        return task
      } catch (e) {
        setError(String(e))
        throw e
      } finally {
        setBusy(false)
      }
    },
    [engagementId, implantId, refresh],
  )

  const onQuick = async (event: React.FormEvent) => {
    event.preventDefault()
    const trimmed = line.trim()
    if (!trimmed || busy) return
    setLine('')
    const space = trimmed.indexOf(' ')
    const head = space === -1 ? trimmed : trimmed.slice(0, space)
    const rest = space === -1 ? '' : trimmed.slice(space + 1).trim()
    try {
      switch (head) {
        case 'help':
          setHint(
            QUICK_HELP.map((c) =>
              [c.usage || '‹command line›', '--', c.note].join(' '),
            ).join('   ·   '),
          )
          return
        case 'interact': {
          const task = await issue('shell.interact', '')
          setInteractTask(task.taskId)
          return
        }
        case 'shell':
          await issue('shell.exec', rest)
          return
        case 'ps':
          await issue('recon.ps', '')
          return
        case 'kill':
          await issue('proc.kill', rest.split(/\s+/)[0] ?? '')
          return
        case 'screenshot':
          await issue('collect.screenshot', '')
          return
        case 'hostenum':
          await issue('recon.hostenum', '')
          return
        case 'portscan':
          await issue('recon.portscan', rest)
          return
        case 'services':
          await issue('recon.service', rest)
          return
        case 'download':
          await issue('file.pull', rest)
          return
        case 'upload':
          setHint('Uploads need a file picker -- use Upload… in the menu (top right).')
          return
        case 'raw': {
          const verb = rest.split(/\s+/)[0] ?? ''
          if (!verb) {
            setHint('raw <verb> [args…]')
            return
          }
          await issue(verb, rest.slice(verb.length).trim())
          return
        }
        default:
          await issue('shell.exec', trimmed)
      }
    } catch {
      // issue() already surfaced the error.
    }
  }

  const onCancel = async (taskId: string) => {
    if (!window.confirm(`Cancel queued task ${taskId.slice(0, 8)}? It will never be dispatched.`)) {
      return
    }
    try {
      await cancelTask(engagementId, taskId)
      await refresh()
    } catch (e) {
      setError(String(e))
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

  if (missing) {
    return (
      <div className="card">
        <h3>Session</h3>
        <div className="empty">
          <Icon name="cpu" />
          Implant {implantId.slice(0, 8)} is not enrolled in this engagement.{' '}
          <a href={`#/engagements/${engagementId}/implants`}>Back to the fleet</a>.
        </div>
      </div>
    )
  }

  const menuEntries = implant
    ? implantMenuEntries(implant, {
        onInteract: async () => {
          const task = await issue('shell.interact', '').catch(() => null)
          if (task) setInteractTask(task.taskId)
        },
        onIssue: (verb) => {
          void issue(verb, '')
        },
        onDialog: (verb) => setDialogVerb(verb),
        onProcesses: () => setProcesses(true),
      })
    : []

  return (
    <>
      <div className="card">
        <div className="console-head">
          <div>
            <a className="back-link" href={`#/engagements/${engagementId}/implants`}>
              ← Fleet
            </a>
            <h3>
              {implant?.hostname ?? 'unknown host'}{' '}
              <span className="muted">
                {implant ? [implant.os, implant.arch].filter(Boolean).join(' · ') : ''}
              </span>
            </h3>
            <p className="muted">
              <code>{implantId.slice(0, 8)}</code> {implant?.class} ·{' '}
              {implant?.username ? `as ${implant.username} · ` : ''}
              {implant?.parentImplantId ? `parent ${implant.parentImplantId.slice(0, 8)} · ` : ''}
              kill {implant ? new Date(implant.killDate).toLocaleDateString() : '—'}
            </p>
          </div>
          <div className="console-status">
            {implant && (
              <StatusBadge
                status={implant.retiredAt ? 'retired' : implant.isOnline ? 'online' : 'offline'}
              />
            )}
            {presence && !implant?.retiredAt && (
              <span className="muted" title={`Online since ${new Date(presence.onlineAt).toLocaleString()}`}>
                last seen {new Date(presence.lastSeenAt).toLocaleTimeString()}
              </span>
            )}
            <button
              className="ghost sm menu-trigger"
              title="Implant actions"
              onClick={(e) => {
                const rect = (e.currentTarget as HTMLButtonElement).getBoundingClientRect()
                menu.openAt({ x: rect.left, y: rect.bottom + 4 })
              }}
            >
              <Icon name="more" />
            </button>
          </div>
        </div>
        {error && <p className="error">{error}</p>}
      </div>

      <div className="card">
        <h3>Task feed</h3>
        {tasks.length === 0 ? (
          <div className="empty">
            <Icon name="inbox" />
            No tasks on this implant yet -- type below or use the menu.
          </div>
        ) : (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Verb</th>
                  <th>Status</th>
                  <th>By</th>
                  <th>At</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                {[...tasks].reverse().map((task) => (
                  <ConsoleRow
                    key={task.taskId}
                    task={task}
                    operatorId={operator.operatorId}
                    expanded={expanded.has(task.taskId)}
                    onToggle={() => toggleExpanded(task.taskId)}
                    onCancel={() => void onCancel(task.taskId)}
                    onInteract={() =>
                      setInteractTask(interactTask === task.taskId ? null : task.taskId)
                    }
                    interactOpen={interactTask === task.taskId}
                  />
                ))}
              </tbody>
            </table>
          </div>
        )}
        {cursor && (
          <div className="load-more">
            <button className="ghost" onClick={() => void loadOlder()}>
              Load older
            </button>
          </div>
        )}
      </div>

      {interactTask && (
        <InteractPane
          engagementId={engagementId}
          taskId={interactTask}
          verb={tasks.find((t) => t.taskId === interactTask)?.verb ?? 'channel'}
          onClose={() => setInteractTask(null)}
        />
      )}

      <div className="terminal console-bar">
        <form className="task-form" onSubmit={onQuick}>
          <span className="prompt" aria-hidden="true">
            ›
          </span>
          <input
            className="wide"
            placeholder="type a command and press Enter -- 'help' for the shortcuts"
            value={line}
            onChange={(e) => setLine(e.target.value)}
            disabled={busy || !!implant?.retiredAt}
          />
          <button className="primary sm" type="submit" disabled={busy || !line.trim()}>
            Run
          </button>
        </form>
        {hint && <p className="muted console-hint">{hint}</p>}
      </div>

      <details className="build-advanced">
        <summary>Advanced — raw task against this implant</summary>
        <RawTaskForm
          groups={groups}
          disabled={!!implant?.retiredAt}
          onSubmit={(verb, args) => void issue(verb, args)}
        />
      </details>

      {menu.menu && (
        <ContextMenu x={menu.menu.x} y={menu.menu.y} entries={menuEntries} onClose={menu.close} />
      )}
      {processes && implant && (
        <ProcessBrowser engagementId={engagementId} implantId={implantId} onClose={() => setProcesses(false)} />
      )}
      {dialogVerb && implant && (
        <TaskDialog
          engagementId={engagementId}
          implantId={implantId}
          verb={dialogVerb}
          form={VERB_FORMS[dialogVerb] ?? {
            title: 'Issue task',
            fields: [
              { key: 'args', label: 'Arguments', type: 'wide', placeholder: 'the argument string' },
            ],
            build: (values) => ({ arguments: (values.args ?? '').trim() }),
          }}
          attributes={descriptorByVerb.get(dialogVerb) ?? {}}
          onClose={() => setDialogVerb(null)}
          onIssued={() => void refresh()}
        />
      )}
    </>
  )
}

// One feed row: verb and arguments, status, attribution, time; the output
// hides behind the row until the operator expands it (console transcripts are
// long, and the newest command's result is what the eye is hunting).
function ConsoleRow({
  task,
  operatorId,
  expanded,
  onToggle,
  onCancel,
  onInteract,
  interactOpen,
}: {
  task: EngagementTask
  operatorId: string
  expanded: boolean
  onToggle: () => void
  onCancel: () => void
  onInteract: () => void
  interactOpen: boolean
}) {
  const long = (task.output ?? '').length > 0
  return (
    <>
      <tr className="console-row" onClick={onToggle} title={long ? 'Click to toggle output' : undefined}>
        <td>
          <code>{task.verb}</code>{' '}
          <span className="muted console-args">{task.arguments.length > 0 ? ellipsize(task.arguments) : ''}</span>
        </td>
        <td>
          <StatusBadge status={task.status} />
          {task.outcome === 'Failed' && <span className="error"> failed</span>}
        </td>
        <td>
          <code>{task.issuedBy === operatorId ? 'you' : task.issuedBy.slice(0, 8)}</code>
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
          <td colSpan={5}>
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

// The raw-task escape hatch: any verb in the registry, free-form arguments --
// the old tasking form minus the implant picker, pinned to this console's
// implant.
function RawTaskForm({
  groups,
  disabled,
  onSubmit,
}: {
  groups: CapabilityGroup[]
  disabled: boolean
  onSubmit: (verb: string, args: string) => void
}) {
  const [verb, setVerb] = useState('shell.exec')
  const [args, setArgs] = useState('')
  return (
    <form
      className="task-form"
      onSubmit={(e) => {
        e.preventDefault()
        onSubmit(verb, args)
        setArgs('')
      }}
    >
      <select value={verb} onChange={(e) => setVerb(e.target.value)} title="Capability verb">
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
      <button className="primary" type="submit" disabled={disabled}>
        Issue
      </button>
    </form>
  )
}
