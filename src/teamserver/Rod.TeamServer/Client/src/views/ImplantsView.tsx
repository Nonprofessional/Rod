import { Fragment, useCallback, useEffect, useMemo, useState } from 'react'
import {
  type Implant,
  type ImplantNote,
  type PresenceRecord,
  addImplantNote,
  issueTask,
  listImplantNotes,
  listImplants,
  retireImplant,
} from '../api'
import { loadCapabilityGroups, type CapabilityGroup } from '../capabilities'
import { osIconFor } from '../osKind'
import { ContextMenu } from '../components/ContextMenu'
import { useContextMenu } from '../contextMenuState'
import { FileBrowser } from '../components/FileBrowser'
import { Icon } from '../components/Icons'
import { ProcessBrowser } from '../components/ProcessBrowser'
import { StatusBadge } from '../components/StatusBadge'
import { TaskDialog } from '../components/TaskDialog'
import { VERB_FORMS } from '../verbForms'
import { implantMenuEntries } from './implantMenu'

// The implants panel: the fleet as one table, three identity layers deep. The
// device is the group header (the host each implant reported at enroll --
// hostname, os/arch, how many implants live there), the implant is the row
// (its class, kill date, parentage, lifecycle), and the session is the status
// dot plus the last-seen column (the presence query's projection, handed down
// from the engagement view's live tick). One row per implant is enough because
// the session registry holds at most one active session per implant; a
// re-check-in refreshes it rather than adding rows.
//
// Implants that predate host reporting (or a test client) group under
// "unknown host", one group per implant -- the fallback keeps the grouping
// honest without inventing a shared device.
//
// An operator can retire (burn) a live implant -- the OPSEC control that
// takes an implant out of operation (refused at handshake and untaskable
// afterwards) -- and keep free-text notes on an implant: the "whose beacon is
// this" memory, attributed per author and durable in the audit trail, so it
// survives a teamserver restart.

// One device's slice of the fleet: the grouping key and the implants that
// reported it. A null hostname groups alone (the unknown-host fallback).
interface DeviceGroup {
  key: string
  hostname: string | null
  os: string | null
  arch: string | null
  implants: Implant[]
  online: number
}

function groupByDevice(implants: Implant[]): DeviceGroup[] {
  const groups = new Map<string, DeviceGroup>()
  for (const implant of implants) {
    // Unknown hosts never merge: each unreported implant is its own group, so
    // two pre-field implants are not silently presented as one device.
    const key = implant.hostname ?? `unknown:${implant.implantId}`
    const group = groups.get(key) ?? {
      key,
      hostname: implant.hostname,
      os: implant.os,
      arch: implant.arch,
      implants: [],
      online: 0,
    }
    group.implants.push(implant)
    if (implant.isOnline && !implant.retiredAt) group.online += 1
    groups.set(key, group)
  }
  return [...groups.values()].sort((a, b) => {
    // Devices with something online first, then by hostname; unknown hosts
    // sink to the end of both bands.
    if (a.online !== b.online) return b.online - a.online
    return (a.hostname ?? '\uffff').localeCompare(b.hostname ?? '\uffff')
  })
}

// Compact "how long ago" for the session column: seconds just now, then
// minutes, then hours -- the clock an operator actually reads.
function ago(iso: string): string {
  const seconds = Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 1000))
  if (seconds < 10) return 'now'
  if (seconds < 60) return `${seconds}s ago`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`
  return `${Math.floor(seconds / 86400)}d ago`
}

export function ImplantsView({
  engagementId,
  onlineTick,
  onlineImplants,
}: {
  engagementId: string
  // Bumped by the parent whenever a live event suggests a change, so the list
  // refreshes without per-view polling.
  onlineTick: number
  // The presence query's projection, refreshed by the parent on the live tick.
  onlineImplants: PresenceRecord[]
}) {
  const [implants, setImplants] = useState<Implant[]>([])
  const [error, setError] = useState<string | null>(null)
  const [notesFor, setNotesFor] = useState<string | null>(null)
  const [notes, setNotes] = useState<ImplantNote[]>([])
  const [noteDraft, setNoteDraft] = useState('')
  const [noteBusy, setNoteBusy] = useState(false)
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set())
  // The per-verb dialog and process browser targets: an implant plus the verb
  // (or just the implant for the browser). One at a time -- the operator acts
  // on one row at a time.
  const [dialog, setDialog] = useState<{ implantId: string; verb: string } | null>(null)
  const [processesFor, setProcessesFor] = useState<string | null>(null)
  const [filesFor, setFilesFor] = useState<string | null>(null)
  const [capabilityGroups, setCapabilityGroups] = useState<CapabilityGroup[]>([])
  // Which implant the open context menu acts on (a row right-click or its
  // three-dot button); null while no menu is open.
  const [menuFor, setMenuFor] = useState<string | null>(null)
  const menu = useContextMenu()

  useEffect(() => {
    void loadCapabilityGroups()
      .then(setCapabilityGroups)
      .catch(() => {
        // The catalog only feeds dialog badges; a failed load leaves them empty.
      })
  }, [])

  const attributesByVerb = useMemo(() => {
    const map = new Map<string, Record<string, string>>()
    for (const group of capabilityGroups) {
      for (const descriptor of group.descriptors) {
        map.set(descriptor.verb, descriptor.attributes)
      }
    }
    return map
  }, [capabilityGroups])

  const refresh = useCallback(async () => {
    try {
      setImplants(await listImplants(engagementId))
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }, [engagementId])

  useEffect(() => {
    void refresh()
  }, [refresh, onlineTick])

  // A stale open notes panel must never survive an engagement switch.
  useEffect(() => {
    setNotesFor(null)
    setNotes([])
    setCollapsed(new Set())
  }, [engagementId])

  // The session projection by implant: last-seen and online-since fold into
  // the implant's row, so no separate live-sessions table is needed.
  const presenceByImplant = useMemo(() => {
    const map = new Map<string, PresenceRecord>()
    for (const record of onlineImplants) map.set(record.implantId, record)
    return map
  }, [onlineImplants])

  const groups = useMemo(() => groupByDevice(implants), [implants])
  const onlineCount = implants.filter((i) => i.isOnline && !i.retiredAt).length

  const toggleGroup = (key: string) => {
    setCollapsed((current) => {
      const next = new Set(current)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })
  }

  const onRetire = async (implantId: string) => {
    if (!window.confirm(`Retire (burn) implant ${implantId.slice(0, 8)}? It will be refused at handshake and untaskable.`)) {
      return
    }
    try {
      await retireImplant(engagementId, implantId)
      await refresh()
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }

  const onToggleNotes = async (implantId: string) => {
    if (notesFor === implantId) {
      setNotesFor(null)
      return
    }
    setNotesFor(implantId)
    setNoteDraft('')
    try {
      setNotes(await listImplantNotes(engagementId, implantId))
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }

  const onAddNote = async (event: React.FormEvent) => {
    event.preventDefault()
    if (!notesFor || !noteDraft.trim() || noteBusy) return
    setNoteBusy(true)
    try {
      await addImplantNote(engagementId, notesFor, noteDraft.trim())
      setNoteDraft('')
      setNotes(await listImplantNotes(engagementId, notesFor))
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setNoteBusy(false)
    }
  }

  // The context menu for one implant: the shared builder over this view's
  // actions (console navigation, dialogs, the process browser, notes,
  // retire).
  const menuEntriesFor = (implantId: string) => {
    const target = implants.find((i) => i.implantId === implantId)
    if (!target) return []
    return implantMenuEntries(target, {
      onInteract: () => {
        window.location.hash = `#/engagements/${engagementId}/implants/${implantId}`
      },
      onIssue: (verb) => {
        void (async () => {
          try {
            await issueTask(engagementId, { implantId, verb, arguments: '' })
            await refresh()
          } catch (e) {
            setError(String(e))
          }
        })()
      },
        onDialog: (verb) => setDialog({ implantId, verb }),
        onProcesses: () => setProcessesFor(implantId),
        onFiles: () => setFilesFor(implantId),
        onNotes: () => void onToggleNotes(implantId),
        onRetire: () => onRetire(implantId),
      })
    }

  if (implants.length === 0 && !error) {
    return (
      <div className="card">
        <h3>Implants</h3>
        <div className="empty">
          <Icon name="cpu" />
          No implants enrolled yet.
        </div>
      </div>
    )
  }

  return (
    <div className="card">
      <h3>Implants</h3>
      <p className="muted">
        {groups.length} device{groups.length === 1 ? '' : 's'} · {implants.length} implant{implants.length === 1 ? '' : 's'} ·{' '}
        {onlineCount} online. Grouped by the host reported at enroll; the dot is the live session.
      </p>
      <div className="table-wrap">
        <table>
            <thead>
              <tr>
                <th>Implant</th>
                <th>Status</th>
                <th>Last seen</th>
                <th>Kill date</th>
                <th>Parent</th>
                <th></th>
              </tr>
            </thead>
          <tbody>
            {groups.map((group) => (
              <Fragment key={group.key}>
                <tr
                  className={`device-row${collapsed.has(group.key) ? ' collapsed' : ''}`}
                  onClick={() => toggleGroup(group.key)}
                >
                  <td colSpan={6}>
                    <Icon name={osIconFor(group.os)} className="wire-icon device-os" />
                    <strong>{group.hostname ?? 'unknown host'}</strong>
                    <span className="device-meta">
                      {group.os || group.arch
                        ? [group.os, group.arch].filter(Boolean).join(' · ')
                        : 'no host facts reported'}
                    </span>
                    <span className="device-meta">
                      {group.implants.length} implant{group.implants.length === 1 ? '' : 's'}
                      {group.online > 0 ? ` · ${group.online} online` : ''}
                    </span>
                    <Icon
                      name={collapsed.has(group.key) ? 'chevronRight' : 'chevronDown'}
                      className="device-caret"
                    />
                  </td>
                </tr>
                {!collapsed.has(group.key) &&
                  group.implants.map((implant) => {
                    const presence = presenceByImplant.get(implant.implantId)
                    // The fresher of the two stamps: the presence roster while
                    // a session lives, the implant row's durable heartbeat
                    // after it is gone.
                    const lastSeen = presence?.lastSeenAt ?? implant.lastSeenAt
                    const retired = !!implant.retiredAt
                    return (
                      <Fragment key={implant.implantId}>
                        <tr
                          className={implant.isOnline || retired ? undefined : 'row-dim'}
                          onContextMenu={(e) => {
                            e.preventDefault()
                            setMenuFor(implant.implantId)
                            menu.openAt(e)
                          }}
                        >
                          <td>
                            <span
                              className={`dot ${!retired && implant.isOnline ? 'online' : 'offline'}`}
                              title={
                                retired
                                  ? 'Retired: refused at handshake, untaskable'
                                  : implant.isOnline
                                    ? `Live session, online since ${new Date(presence?.onlineAt ?? implant.createdAt).toLocaleString()}`
                                    : lastSeen
                                      ? `No active session; last heard ${new Date(lastSeen).toLocaleString()}`
                                      : 'No active session'
                              }
                            />{' '}
                            <code>{implant.implantId.slice(0, 8)}</code>{' '}
                            <span className="muted">{implant.class}</span>
                            {implant.username && (
                              <span className="muted" title="The account the implant runs under">
                                {' '}
                                as {implant.username}
                              </span>
                            )}
                          </td>
                          <td>
                            <StatusBadge
                              status={retired ? 'retired' : implant.isOnline ? 'online' : 'offline'}
                            />
                          </td>
                          <td>
                            {lastSeen ? (
                              <span
                                title={new Date(lastSeen).toLocaleString()}
                              >
                                {ago(lastSeen)}
                              </span>
                            ) : (
                              <span className="muted">&mdash;</span>
                            )}
                          </td>
                          <td>
                            <span title={new Date(implant.killDate).toLocaleString()}>
                              {new Date(implant.killDate).toLocaleDateString()}
                            </span>
                          </td>
                          <td>
                            {implant.parentImplantId ? (
                              <code>{implant.parentImplantId.slice(0, 8)}</code>
                            ) : (
                              <span className="muted">&mdash;</span>
                            )}
                          </td>
                          <td>
                            <div className="row-actions">
                              <a
                                className="button-link sm"
                                href={`#/engagements/${engagementId}/implants/${implant.implantId}`}
                                title="Open the session console"
                              >
                                Interact
                              </a>
                              <button className="sm" onClick={() => void onToggleNotes(implant.implantId)}>
                                {notesFor === implant.implantId ? 'Hide notes' : 'Notes'}
                              </button>
                              <button
                                className="ghost sm menu-trigger"
                                title="Implant actions"
                                onClick={(e) => {
                                  const rect = (e.currentTarget as HTMLButtonElement).getBoundingClientRect()
                                  setMenuFor(implant.implantId)
                                  menu.openAt({ x: rect.left, y: rect.bottom + 4 })
                                }}
                                onContextMenu={(e) => e.stopPropagation()}
                              >
                                <Icon name="more" />
                              </button>
                            </div>
                          </td>
                        </tr>
                        {notesFor === implant.implantId && (
                          <tr className="notes-row">
                            <td colSpan={6}>
                              <div className="notes-panel">
                                <ul className="notes-list">
                                  {notes.length === 0 ? (
                                    <li className="muted">No notes on this implant yet.</li>
                                  ) : (
                                    notes.map((n) => (
                                      <li key={n.noteId}>
                                        <span className="notes-meta">
                                          <code>{n.author.slice(0, 8)}</code>{' '}
                                          {new Date(n.at).toLocaleString()}
                                        </span>
                                        {n.text}
                                      </li>
                                    ))
                                  )}
                                </ul>
                                <form className="task-form" onSubmit={onAddNote}>
                                  <input
                                    className="wide"
                                    placeholder="whose beacon is this?"
                                    value={noteDraft}
                                    onChange={(e) => setNoteDraft(e.target.value)}
                                  />
                                  <button className="sm" type="submit" disabled={noteBusy || !noteDraft.trim()}>
                                    Add note
                                  </button>
                                </form>
                              </div>
                            </td>
                          </tr>
                        )}
                      </Fragment>
                    )
                  })}
              </Fragment>
            ))}
          </tbody>
        </table>
      </div>
      {error && <p className="error">{error}</p>}
      {menu.menu && menuFor && (
        <ContextMenu
          x={menu.menu.x}
          y={menu.menu.y}
          entries={menuEntriesFor(menuFor)}
          onClose={() => {
            menu.close()
            setMenuFor(null)
          }}
        />
      )}
      {dialog && (
        <TaskDialog
          engagementId={engagementId}
          implantId={dialog.implantId}
          verb={dialog.verb}
          form={
            VERB_FORMS[dialog.verb] ?? {
              title: 'Issue task',
              fields: [{ key: 'args', label: 'Arguments', type: 'wide', placeholder: 'the argument string' }],
              build: (values) => ({ arguments: (values.args ?? '').trim() }),
            }
          }
          attributes={attributesByVerb.get(dialog.verb) ?? {}}
          onClose={() => setDialog(null)}
          onIssued={() => void refresh()}
        />
      )}
      {processesFor && (
        <ProcessBrowser
          engagementId={engagementId}
          implantId={processesFor}
          onClose={() => setProcessesFor(null)}
        />
      )}
      {filesFor && (
        <FileBrowser
          engagementId={engagementId}
          implantId={filesFor}
          osHint={implants.find((i) => i.implantId === filesFor)?.os ?? null}
          onClose={() => setFilesFor(null)}
        />
      )}
    </div>
  )
}
