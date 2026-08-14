import { useCallback, useEffect, useMemo, useState } from 'react'
import { type AuditEventEntry, listAudit } from '../api'

// The operational event log: the per-engagement, append-only,
// hash-chained audit trail, oldest-first in causal order. Every action that
// changes engagement state or binds an identity produces an immutable, attributed
// event. A kind filter narrows the view (e.g. only TaskIssued); the full set is
// the raw evidence feed the timeline/report exports consume.

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
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [loading, setLoading] = useState(true)
  const [kind, setKind] = useState(ALL_KINDS)

  const refresh = useCallback(async () => {
    setBusy(true)
    try {
      setEvents(await listAudit(engagementId))
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
      setLoading(false)
    }
  }, [engagementId])

  useEffect(() => {
    void refresh()
  }, [refresh, onlineTick])

  const kinds = useMemo(() => {
    const set = new Set(events.map((e) => e.kind))
    return [ALL_KINDS, ...[...set].sort()]
  }, [events])

  const filtered = kind === ALL_KINDS ? events : events.filter((e) => e.kind === kind)

  return (
    <div className="card">
      <h3>Audit trail</h3>
      <p className="muted">
        The append-only, hash-chained event log for this engagement. Filter by kind to narrow the view.
      </p>
      <div className="inline-form">
        <select value={kind} onChange={(e) => setKind(e.target.value)}>
          {kinds.map((k) => (
            <option key={k} value={k}>
              {k}
            </option>
          ))}
        </select>
        <button onClick={() => void refresh()} disabled={busy}>
          Refresh
        </button>
      </div>
      {error && <p className="error">{error}</p>}
      {loading ? (
        <p className="muted">Loading audit trail…</p>
      ) : filtered.length === 0 ? (
        <p className="muted">No events recorded yet.</p>
      ) : (
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
                  <code>{shortId(e.operatorId)}</code>
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
      )}
    </div>
  )
}
