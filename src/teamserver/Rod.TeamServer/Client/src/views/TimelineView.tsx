import { useCallback, useEffect, useState } from 'react'
import { type TimelineReport, getTimeline, getTimelineMarkdown } from '../api'

// The engagement timeline (roadmap M6.3): a reproducible, content-hashed
// projection of the audit trail enriched with operator/implant/task context.
// Toggles between a rendered table (JSON) and the raw Markdown export for
// cut-paste into a report.

export function TimelineView({ engagementId }: { engagementId: string }) {
  const [report, setReport] = useState<TimelineReport | null>(null)
  const [markdown, setMarkdown] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [view, setView] = useState<'table' | 'markdown'>('table')

  const refresh = useCallback(async () => {
    setBusy(true)
    try {
      setReport(await getTimeline(engagementId))
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }, [engagementId])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const showMarkdown = async () => {
    try {
      setMarkdown(await getTimelineMarkdown(engagementId))
      setView('markdown')
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }

  return (
    <div className="card">
      <h3>Timeline</h3>
      <div className="inline-form">
        <button onClick={() => void refresh()} disabled={busy}>
          Refresh
        </button>
        <button className={view === 'table' ? 'active' : ''} onClick={() => setView('table')}>
          Table
        </button>
        <button className={view === 'markdown' ? 'active' : ''} onClick={() => void showMarkdown()}>
          Markdown
        </button>
        {report && (
          <span className="muted">
            hash <code>{report.contentHash.slice(0, 12)}</code>
          </span>
        )}
      </div>
      {error && <p className="error">{error}</p>}

      {view === 'markdown' ? (
        <pre className="output long">{markdown ?? 'loading\u2026'}</pre>
      ) : report && report.entries.length > 0 ? (
        <table>
          <thead>
            <tr>
              <th>At</th>
              <th>Kind</th>
              <th>Verb</th>
              <th>Operator</th>
              <th>Implant</th>
              <th>Outcome</th>
            </tr>
          </thead>
          <tbody>
            {[...report.entries].reverse().map((e) => (
              <tr key={e.eventId}>
                <td>{new Date(e.at).toLocaleString()}</td>
                <td>{e.kind}</td>
                <td>
                  <code>{e.verb}</code>
                </td>
                <td>{e.operator?.handle ?? '\u2014'}</td>
                <td>
                  {e.implant ? <code>{e.implant.implantId.slice(0, 8)}</code> : '\u2014'}
                </td>
                <td>{e.outcome}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : (
        <p className="muted">No timeline entries.</p>
      )}
    </div>
  )
}
