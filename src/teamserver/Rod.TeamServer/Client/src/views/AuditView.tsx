import { useCallback, useEffect, useMemo, useState } from 'react'
import { type AuditEventEntry, listAudit } from '../api'
import { Icon } from '../components/Icons'

// The operational event log: the per-engagement, append-only,
// hash-chained audit trail, oldest-first in causal order. Every action that
// changes engagement state or binds an identity produces an immutable, attributed
// event. Where the Timeline tab renders the story, this is the raw ledger --
// the dense, paged, filterable table a forensic read wants: kind filter,
// free-text search across verb/payload/outcome, and "load older" walking back
// through history. The full set is the raw evidence feed the timeline/report
// exports consume.

const ALL_KINDS = '(all)'

// Events without an operator or implant carry the empty GUID on the wire (a
// non-nullable server field), not null -- render it as a dash, not as
// "00000000-...".
const EMPTY_GUID = '00000000-0000-0000-0000-000000000000'

function shortId(id: string): string {
  return id === EMPTY_GUID ? '\u2014' : id.slice(0, 8)
}

export function AuditView({
  engagementId,
  onlineTick,
}: {
  engagementId: string
  onlineTick: number
}) {
  const [events, setEvents] = useState<AuditEventEntry[]>([])
  const [eventsCursor, setEventsCursor] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [loading, setLoading] = useState(true)
  const [kind, setKind] = useState(ALL_KINDS)
  const [query, setQuery] = useState('')

  const refresh = useCallback(async () => {
    setBusy(true)
    try {
      // The newest window of the trail; older pages load on demand.
      const page = await listAudit(engagementId)
      setEvents(page.items)
      setEventsCursor(page.nextCursor)
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
      setLoading(false)
    }
  }, [engagementId])

  const loadOlder = useCallback(async () => {
    if (!eventsCursor) return
    setBusy(true)
    try {
      const page = await listAudit(engagementId, eventsCursor)
      setEvents((current) => [...current, ...page.items])
      setEventsCursor(page.nextCursor)
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }, [engagementId, eventsCursor])

  useEffect(() => {
    void refresh()
  }, [refresh, onlineTick])

  const kinds = useMemo(() => {
    const set = new Set(events.map((e) => e.kind))
    return [ALL_KINDS, ...[...set].sort()]
  }, [events])

  const filtered = useMemo(() => {
    const byKind = kind === ALL_KINDS ? events : events.filter((e) => e.kind === kind)
    const needle = query.trim().toLowerCase()
    if (needle === '') return byKind
    return byKind.filter(
      (e) =>
        e.verb.toLowerCase().includes(needle) ||
        e.payload.toLowerCase().includes(needle) ||
        (e.output ?? '').toLowerCase().includes(needle) ||
        e.outcome.toLowerCase().includes(needle) ||
        e.operatorHandle.toLowerCase().includes(needle),
    )
  }, [events, kind, query])

  return (
    <div className="card">
      <h3>Audit trail</h3>
      <p className="muted" title="Append-only and hash-chained: tampering with a stored event breaks the chain at the next link.">
        The engagement's append-only ledger — every recorded fact, paged and filterable.
      </p>
      <div className="inline-form">
        <select
          value={kind}
          onChange={(e) => setKind(e.target.value)}
          title="Event kind filter"
          aria-label="Event kind filter"
        >
          {kinds.map((k) => (
            <option key={k} value={k}>
              {k}
            </option>
          ))}
        </select>
        <input
          className="filter-text"
          placeholder="Search verb, payload, outcome, operator…"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
        <button className="ghost" onClick={() => void refresh()} disabled={busy}>
          <Icon name="refresh" />
          Refresh
        </button>
      </div>
      {error && <p className="error">{error}</p>}
      {loading ? (
        <div className="empty">
          <span className="spinner" />
          Loading audit trail…
        </div>
      ) : filtered.length === 0 ? (
        <div className="empty">
          <Icon name="list" />
          No events recorded yet.
        </div>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>At</th>
                <th>Kind</th>
                <th>Verb</th>
                <th>Operator</th>
                <th>Implant</th>
                <th>Payload</th>
                <th>Outcome</th>
              </tr>
            </thead>
            <tbody>
              {[...filtered].reverse().map((e) => (
                <tr key={e.eventId}>
                  <td>{new Date(e.at).toLocaleString()}</td>
                  <td>
                    <span className="status">{e.kind}</span>
                  </td>
                  <td>
                    <code>{e.verb}</code>
                  </td>
                  <td>
                    {/* The resolved handle, with the guid on hover for the rare
                        event whose operator record no longer resolves. */}
                    <code title={e.operatorId}>{e.operatorHandle}</code>
                  </td>
                  <td>
                    <code>{shortId(e.implantId)}</code>
                  </td>
                  <td>
                    <pre className="output">{e.payload || '\u2014'}</pre>
                  </td>
                  <td>{e.outcome || '\u2014'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {eventsCursor && (
        <div className="load-more">
          <button className="ghost" onClick={() => void loadOlder()} disabled={busy}>
            {busy ? 'Loading…' : 'Load older'}
          </button>
        </div>
      )}
    </div>
  )
}
