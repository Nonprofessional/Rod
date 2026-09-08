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
import { InteractPane } from '../components/InteractPane'
import { ProcessBrowser } from '../components/ProcessBrowser'
import { StatusBadge } from '../components/StatusBadge'
import { TaskDialog } from '../components/TaskDialog'
import { VERB_FORMS } from '../verbForms'
import { ago, useNow } from '../when'
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
// The table rides a toolbar -- text search, a state filter, a class filter,
// column sorting -- and paginates by device group when the fleet outgrows one
// screen. The header row is always laid down (an empty fleet reads as a table
// with a message, not as a card with nothing in it), and relative last-seen
// stamps re-render on a quiet clock so "2m ago" keeps moving without a live
// event to bump it.
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

// Column sorting: null is the default order (online-first devices, server
// order within), otherwise the key with a direction multiplier over the
// ascending comparison. Each column names the direction its first click
// should land on -- newest last-seen first, soonest kill date first.
type SortKey = 'id' | 'seen' | 'kill'
interface Sort {
  key: SortKey
  dir: 1 | -1
}

const SORT_DEFAULTS: Record<SortKey, 1 | -1> = { id: 1, seen: -1, kill: 1 }

function timeOf(iso: string | null): number {
  return iso ? new Date(iso).getTime() : 0
}

// How many device groups one page holds. The fleet view is scanned, not
// scrolled through: past this the pager takes over and the standfirst keeps
// the whole-fleet counts honest.
const DEVICES_PER_PAGE = 10

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
  // The live interactive-shell channel opened from the fleet (the menu's
  // "Interactive shell"): the channel task id plus the implant it runs on.
  const [shell, setShell] = useState<{ implantId: string; taskId: string } | null>(null)
  const [processesFor, setProcessesFor] = useState<string | null>(null)
  const [filesFor, setFilesFor] = useState<string | null>(null)
  const [capabilityGroups, setCapabilityGroups] = useState<CapabilityGroup[]>([])
  // Which implant the open context menu acts on (a row right-click or its
  // three-dot button); null while no menu is open.
  const [menuFor, setMenuFor] = useState<string | null>(null)
  const menu = useContextMenu()

  // The toolbar: free text across the identity fields, the session/lifecycle
  // state, and the implant class. All client-side -- the fleet is one query's
  // worth of rows, and filters must feel instant, not round-trip.
  const [search, setSearch] = useState('')
  const [stateFilter, setStateFilter] = useState('')
  const [classFilter, setClassFilter] = useState('')
  const [sort, setSort] = useState<Sort | null>(null)
  const [page, setPage] = useState(0)

  // The quiet clock for relative stamps: bumps every 30s so "2m ago" keeps
  // moving between live events.
  const now = useNow(30_000)

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
    setSearch('')
    setStateFilter('')
    setClassFilter('')
    setSort(null)
    setPage(0)
  }, [engagementId])

  // Any narrowed view restarts at the first page -- page five of a filter
  // that now matches three rows is a dead end.
  useEffect(() => {
    setPage(0)
  }, [search, stateFilter, classFilter])

  // The session projection by implant: last-seen and online-since fold into
  // the implant's row, so no separate live-sessions table is needed.
  const presenceByImplant = useMemo(() => {
    const map = new Map<string, PresenceRecord>()
    for (const record of onlineImplants) map.set(record.implantId, record)
    return map
  }, [onlineImplants])

  // The fresher of the two stamps: the presence roster while a session
  // lives, the implant row's durable heartbeat after it is gone.
  const seenOf = useCallback(
    (implant: Implant) =>
      presenceByImplant.get(implant.implantId)?.lastSeenAt ?? implant.lastSeenAt,
    [presenceByImplant],
  )

  const classes = useMemo(
    () => [...new Set(implants.map((i) => i.class))].sort(),
    [implants],
  )

  const filtered = useMemo(() => {
    const needle = search.trim().toLowerCase()
    return implants.filter((implant) => {
      if (classFilter !== '' && implant.class !== classFilter) return false
      const retired = !!implant.retiredAt
      if (stateFilter === 'online' && !(implant.isOnline && !retired)) return false
      if (stateFilter === 'offline' && !(!implant.isOnline && !retired)) return false
      if (stateFilter === 'retired' && !retired) return false
      if (needle !== '') {
        const haystack = [
          implant.hostname,
          implant.implantId,
          implant.class,
          implant.username,
          implant.os,
          implant.arch,
        ]
          .filter((v): v is string => !!v)
          .join(' ')
          .toLowerCase()
        if (!haystack.includes(needle)) return false
      }
      return true
    })
  }, [implants, search, stateFilter, classFilter])

  const compareUnderSort = useCallback(
    (a: Implant, b: Implant): number => {
      if (!sort) return 0
      switch (sort.key) {
        case 'id':
          return a.implantId.localeCompare(b.implantId) * sort.dir
        case 'seen':
          return (timeOf(seenOf(a)) - timeOf(seenOf(b))) * sort.dir
        case 'kill':
          return (new Date(a.killDate).getTime() - new Date(b.killDate).getTime()) * sort.dir
      }
    },
    [sort, seenOf],
  )

  // Grouping over the filtered rows, then the sort: within a group by the
  // chosen column, and -- when a sort is active -- the groups themselves by
  // their best row, so a "last seen" sort reads newest-first down the whole
  // table, not just inside each device.
  const sortedGroups = useMemo(() => {
    const grouped = groupByDevice(filtered)
    if (!sort) return grouped
    const withSortedRows = grouped.map((group) => ({
      ...group,
      implants: [...group.implants].sort(compareUnderSort),
    }))
    return withSortedRows.sort((a, b) => compareUnderSort(a.implants[0], b.implants[0]))
  }, [filtered, sort, compareUnderSort])

  const totalPages = Math.max(1, Math.ceil(sortedGroups.length / DEVICES_PER_PAGE))
  const currentPage = Math.min(page, totalPages - 1)
  const pageGroups = sortedGroups.slice(
    currentPage * DEVICES_PER_PAGE,
    (currentPage + 1) * DEVICES_PER_PAGE,
  )

  const filtering = search.trim() !== '' || stateFilter !== '' || classFilter !== ''
  const onlineCount = filtered.filter((i) => i.isOnline && !i.retiredAt).length

  const toggleGroup = (key: string) => {
    setCollapsed((current) => {
      const next = new Set(current)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })
  }

  // A header that sorts: first click lands on the column's natural direction,
  // the second flips it, the third returns to the default fleet order.
  const sortHeader = (key: SortKey, label: string) => (
    <button
      type="button"
      className={`th-sort${sort?.key === key ? ' active' : ''}`}
      onClick={() =>
        setSort((current) =>
          current?.key === key
            ? current.dir === SORT_DEFAULTS[key]
              ? { key, dir: current.dir === 1 ? -1 : 1 }
              : null
            : { key, dir: SORT_DEFAULTS[key] },
        )
      }
    >
      {label}
      {sort?.key === key && <span className="sort-arrow">{sort.dir === 1 ? '↑' : '↓'}</span>}
    </button>
  )

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
      onConsole: () => {
        window.location.hash = `#/engagements/${engagementId}/implants/${implantId}`
      },
      onShell: () => {
        void (async () => {
          try {
            const task = await issueTask(engagementId, { implantId, verb: 'shell.interact', arguments: '' })
            setShell({ implantId, taskId: task.taskId })
            setError(null)
          } catch (e) {
            setError(String(e))
          }
        })()
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

  return (
    <div className="card">
      <h3>Implants</h3>
      <p className="muted">
        {sortedGroups.length} device{sortedGroups.length === 1 ? '' : 's'} ·{' '}
        {filtered.length} implant{filtered.length === 1 ? '' : 's'} · {onlineCount} online
        {filtering && implants.length !== filtered.length && (
          <span title="Clear the toolbar's search and filters to see the whole fleet">
            {' '}
            (of {implants.length})
          </span>
        )}
        . Grouped by the host reported at enroll; the dot is the live session.
      </p>
      <div className="table-toolbar">
        <input
          className="toolbar-search"
          placeholder="Search host, id, user, class…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        <select
          value={stateFilter}
          onChange={(e) => setStateFilter(e.target.value)}
          aria-label="State filter"
          title="The session/lifecycle state: online (live session), offline (no session), retired (burned)"
        >
          <option value="">All states</option>
          <option value="online">Online</option>
          <option value="offline">Offline</option>
          <option value="retired">Retired</option>
        </select>
        <select
          value={classFilter}
          onChange={(e) => setClassFilter(e.target.value)}
          aria-label="Class filter"
          title="The implant class -- the capability set its build baked"
        >
          <option value="">All classes</option>
          {classes.map((c) => (
            <option key={c} value={c}>
              {c}
            </option>
          ))}
        </select>
      </div>
      <div className="table-wrap">
        <table>
            <thead>
              <tr>
                <th>{sortHeader('id', 'Implant')}</th>
                <th>Status</th>
                <th>{sortHeader('seen', 'Last seen')}</th>
                <th>{sortHeader('kill', 'Kill date')}</th>
                <th>Parent</th>
                <th></th>
              </tr>
            </thead>
          <tbody>
            {pageGroups.length === 0 && (
              <tr>
                <td colSpan={6}>
                  <div className="empty">
                    <Icon name="cpu" />
                    {implants.length === 0
                      ? 'No implants enrolled yet.'
                      : 'No implants match — clear the search or filters.'}
                  </div>
                </td>
              </tr>
            )}
            {pageGroups.map((group) => (
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
                    const lastSeen = seenOf(implant)
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
                                {ago(lastSeen, now)}
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
      {totalPages > 1 && (
        <div className="table-pager">
          <button
            className="ghost sm"
            disabled={currentPage === 0}
            onClick={() => setPage(currentPage - 1)}
          >
            ‹ Prev
          </button>
          <span className="muted">
            page {currentPage + 1} / {totalPages}
          </span>
          <button
            className="ghost sm"
            disabled={currentPage >= totalPages - 1}
            onClick={() => setPage(currentPage + 1)}
          >
            Next ›
          </button>
        </div>
      )}
      {error && <p className="error">{error}</p>}
      {shell && (
        <InteractPane
          engagementId={engagementId}
          taskId={shell.taskId}
          verb="shell.interact"
          onClose={() => setShell(null)}
        />
      )}
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
