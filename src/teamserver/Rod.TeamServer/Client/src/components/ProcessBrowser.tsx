import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { getTask, issueTask } from '../api'
import { StatusBadge } from './StatusBadge'

// The process browser: the classic operator pane. One recon.ps task lists
// the host's processes; each row offers proc.kill behind a confirm. The
// listing is a snapshot (the implant says so too), so the pane keeps a
// refresh and never pretends to be live.

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
  const [rows, setRows] = useState<ProcessRow[] | null>(null)
  const [raw, setRaw] = useState<string | null>(null)
  const [status, setStatus] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [filter, setFilter] = useState('')
  const [killIssued, setKillIssued] = useState<Set<number>>(new Set())
  const [busy, setBusy] = useState(false)
  // Set by the unmount cleanup so the polling loop stops instead of setting
  // state on a gone pane.
  const closedRef = useRef(false)

  const list = useCallback(async () => {
    if (busy) return
    setBusy(true)
    setError(null)
    setRows(null)
    setRaw(null)
    setStatus('Queued')
    try {
      const task = await issueTask(engagementId, {
        implantId,
        verb: 'recon.ps',
        arguments: '',
      })
      // The task completes when the implant wakes and answers; poll the
      // task's own record like the channel pane does.
      for (;;) {
        if (closedRef.current) return
        const detail = await getTask(engagementId, task.taskId)
        setStatus(detail.status)
        if (detail.status !== 'Queued' && detail.status !== 'Dispatched') {
          if (detail.outcome === 'Succeeded' && detail.output) {
            const parsed = parseListing(detail.output)
            setRows(parsed.length > 0 ? parsed : null)
            setRaw(detail.output)
          } else {
            setError(detail.output ?? `recon.ps ${detail.outcome ?? detail.status}`)
          }
          break
        }
        await new Promise((resolve) => setTimeout(resolve, 700))
      }
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }, [engagementId, implantId, busy])

  // Run the first listing once on open; refreshes are the pane's own button.
  useEffect(() => {
    void list()
    return () => {
      closedRef.current = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

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
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
          />
          <button className="ghost" onClick={() => void list()} disabled={busy}>
            Refresh
          </button>
          <button className="ghost" onClick={onClose}>
            Close
          </button>
        </div>
        <p className="muted">
          A snapshot from <code>recon.ps</code>{' '}
          {status && (
            <>
              · <StatusBadge status={status} />
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
        {rows === null && !error && (
          <div className="empty">
            <span className="spinner" />
            {busy ? 'Listing processes…' : 'Waiting for the implant to answer…'}
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
        {rows === null && raw && <pre className="output long">{raw}</pre>}
        {error && <p className="error">{error}</p>}
      </div>
    </div>
  )
}
