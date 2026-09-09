import { useEffect, useMemo, useState } from 'react'
import { issueTask } from '../api'
import { browseInFlight, ensureBrowse, useBrowseEntry } from '../browserCache'
import { ago, useNow } from '../when'
import { StatusBadge } from './StatusBadge'

// The process browser: the classic operator pane. One recon.ps task lists
// the host's processes; each row offers proc.kill behind a confirm. The
// listing is a snapshot (the implant says so too), so the pane keeps a
// refresh and never pretends to be live.
//
// The listing rides the browse-result cache: reopening shows the last
// snapshot instantly (with its age), an in-flight listing is attached to
// rather than re-issued, and a listing that completes while the pane is
// closed lands in the cache anyway -- the background poller owns the wait.
// Refresh cancels the queued listing it replaces and issues a fresh one.

interface ProcessRow {
  pid: number
  ppid: number
  user: string
  image: string
}

// The reference implant's recon.ps line: "pid=<n> ppid=<n> user=<name>
// image=<path>" -- stable by contract, so the pane parses it outright and
// falls back to showing the raw output when a line does not match.
const ROW = /^pid=(\d+) ppid=(-?\d+) user=(\S+) image=(.*)$/

function parseListing(output: string): ProcessRow[] {
  return output
    .split('\n')
    .map((line) => ROW.exec(line.trim()))
    .filter((match): match is RegExpExecArray => match !== null)
    .map((match) => ({
      pid: Number(match[1]),
      ppid: Number(match[2]),
      user: match[3],
      image: match[4],
    }))
}

export function ProcessBrowser({
  engagementId,
  implantId,
  onClose,
}: {
  engagementId: string
  implantId: string
  onClose: () => void
}) {
  const [error, setError] = useState<string | null>(null)
  // The filter commits on Enter or the Search button, like every text search
  // in the operator UI.
  const [filterDraft, setFilterDraft] = useState('')
  const [filter, setFilter] = useState('')
  const [killIssued, setKillIssued] = useState<Set<number>>(new Set())
  const now = useNow(30_000)

  useEffect(() => {
    // Cached, in-flight, or cold -- the cache decides; the pane renders
    // whatever state comes back.
    void ensureBrowse(engagementId, implantId, 'recon.ps', '').catch((e) => setError(String(e)))
  }, [engagementId, implantId])

  const entry = useBrowseEntry(engagementId, implantId, 'recon.ps', '')
  const busy = browseInFlight(entry)

  const rows = useMemo(() => {
    if (!entry || entry.outcome !== 'Succeeded' || !entry.output) return null
    const parsed = parseListing(entry.output)
    return parsed.length > 0 ? parsed : null
  }, [entry])

  const taskError =
    entry && !busy && entry.outcome && entry.outcome !== 'Succeeded'
      ? (entry.output ?? `recon.ps ${entry.outcome}`)
      : null

  // The file browser's old-build translation applies here too: verbs are
  // baked at build time, so a binary fielded before recon.ps answers
  // "unknown verb" and the fix is a rebuild, not a retry.
  const unknownVerb =
    entry?.outcome === 'Failed' && !busy
      ? (entry.output ?? '').match(/unknown verb:\s*(\S+)/)?.[1]
      : undefined

  const filtered = useMemo(() => {
    const needle = filter.trim().toLowerCase()
    if (!rows || needle === '') return rows ?? []
    return rows.filter(
      (r) => r.image.toLowerCase().includes(needle) || r.user.toLowerCase().includes(needle),
    )
  }, [rows, filter])

  const onKill = async (pid: number) => {
    if (!window.confirm(`Terminate process ${pid}? This is observable and irreversible on the target.`)) {
      return
    }
    try {
      await issueTask(engagementId, { implantId, verb: 'proc.kill', arguments: String(pid) })
      setKillIssued((current) => new Set(current).add(pid))
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }

  return (
    <div className="modal-backdrop" onClick={(e) => e.target === e.currentTarget && onClose()}>
      <div className="card modal wide">
        <div className="inline-form">
          <h3 style={{ marginRight: 'auto' }}>Processes</h3>
          <input
            className="filter-text"
            placeholder="Filter image or user…"
            value={filterDraft}
            onChange={(e) => setFilterDraft(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') setFilter(filterDraft)
            }}
            title="Free text across process image and user. Enter or the Search button applies."
          />
          <button
            className="ghost"
            onClick={() => setFilter(filterDraft)}
            title="Apply the filter (Enter works too)"
          >
            Search
          </button>
          <button
            className="ghost"
            onClick={() => void ensureBrowse(engagementId, implantId, 'recon.ps', '', { force: true }).catch((e) => setError(String(e)))}
            disabled={busy}
            title="Cancel the queued listing (if any) and issue a fresh recon.ps"
          >
            Refresh
          </button>
          <button className="ghost" onClick={onClose}>
            Close
          </button>
        </div>
        <p className="muted">
          A snapshot from <code>recon.ps</code>
          {entry && (
            <>
              {' '}
              · <StatusBadge status={entry.status} />
              {entry.completedAt && (
                <span title={new Date(entry.completedAt).toLocaleString()}>
                  {' '}
                  listed {ago(entry.completedAt, now)}
                </span>
              )}
            </>
          )}
          {killIssued.size > 0 && (
            <>
              {' '}
              · kill issued for {killIssued.size} pid{killIssued.size === 1 ? '' : 's'} — refresh
              to confirm
            </>
          )}
        </p>
        {rows === null && !taskError && !error && (
          <div className="empty">
            <span className="spinner" />
            {busy
              ? 'Listing processes…'
              : entry && entry.completedAt
                ? 'The listing did not parse — showing raw output below.'
                : 'Waiting for the implant to answer…'}
          </div>
        )}
        {rows !== null && (
          <div className="table-wrap process-table">
            <table>
              <thead>
                <tr>
                  <th>PID</th>
                  <th>PPID</th>
                  <th>User</th>
                  <th>Image</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                {filtered.map((row) => (
                  <tr key={row.pid} className={killIssued.has(row.pid) ? 'row-dim' : undefined}>
                    <td>{row.pid}</td>
                    <td>{row.ppid}</td>
                    <td>{row.user}</td>
                    <td>
                      <code>{row.image}</code>
                    </td>
                    <td>
                      <button
                        className="danger sm"
                        onClick={() => void onKill(row.pid)}
                        disabled={killIssued.has(row.pid)}
                      >
                        {killIssued.has(row.pid) ? 'Kill issued' : 'Kill'}
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {rows === null && entry?.output && <pre className="output long">{entry.output}</pre>}
        {(taskError || error) && <p className="error">{taskError ?? error}</p>}
        {unknownVerb && (
          <p className="muted" style={{ marginTop: 4 }}>
            This implant's build predates <code>{unknownVerb}</code> -- verbs are baked into the
            artifact at build time. Rebuild the payload on the Build tab and redeploy to list
            processes.
          </p>
        )}
      </div>
    </div>
  )
}
