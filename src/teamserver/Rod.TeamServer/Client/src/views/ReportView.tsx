import { useCallback, useEffect, useState } from 'react'
import { type EngagementReport, getReport, getReportMarkdown } from '../api'

// The engagement report (roadmap M6.3): the full evidence bundle -- engagement,
// operators, implants, tasks, artifacts, and the timeline -- in one reproducible,
// content-hashed export. Toggles between a structured summary (JSON) and the raw
// Markdown export.

export function ReportView({ engagementId }: { engagementId: string }) {
  const [report, setReport] = useState<EngagementReport | null>(null)
  const [markdown, setMarkdown] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [view, setView] = useState<'summary' | 'markdown'>('summary')

  const refresh = useCallback(async () => {
    setBusy(true)
    try {
      setReport(await getReport(engagementId))
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
      setMarkdown(await getReportMarkdown(engagementId))
      setView('markdown')
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }

  return (
    <div className="card">
      <h3>Engagement report</h3>
      <div className="inline-form">
        <button onClick={() => void refresh()} disabled={busy}>
          Refresh
        </button>
        <button className={view === 'summary' ? 'active' : ''} onClick={() => setView('summary')}>
          Summary
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
      ) : report ? (
        <>
          <dl className="kv">
            <dt>Engagement</dt>
            <dd>{report.engagement.name}</dd>
            <dt>Owner</dt>
            <dd>{report.engagement.ownerHandle}</dd>
            <dt>Generated</dt>
            <dd>{new Date(report.generatedAt).toLocaleString()}</dd>
          </dl>
          <div className="report-cols">
            <div>
              <h4>Operators ({report.operators.length})</h4>
              <ul className="sessions">
                {report.operators.map((o) => (
                  <li key={o.operatorId}>
                    <code>{o.handle}</code> <span className="muted">{o.role}</span>
                  </li>
                ))}
              </ul>
            </div>
            <div>
              <h4>Implants ({report.implants.length})</h4>
              <ul className="sessions">
                {report.implants.map((i) => (
                  <li key={i.implantId}>
                    <code>{i.implantId.slice(0, 8)}</code>{' '}
                    <span className="muted">
                      {i.class}
                      {i.retiredAt ? ' (retired)' : ''}
                    </span>
                  </li>
                ))}
              </ul>
            </div>
          </div>
          <h4>Tasks ({report.tasks.length})</h4>
          {report.tasks.length === 0 ? (
            <p className="muted">No tasks.</p>
          ) : (
            <table>
              <thead>
                <tr>
                  <th>Verb</th>
                  <th>By</th>
                  <th>Status</th>
                  <th>Implant</th>
                  <th>Artifacts</th>
                </tr>
              </thead>
              <tbody>
                {report.tasks.map((t) => (
                  <tr key={t.taskId}>
                    <td>
                      <code>{t.verb}</code> {t.arguments}
                    </td>
                    <td>{t.issuedByHandle}</td>
                    <td>{t.status}</td>
                    <td>
                      <code>{t.implantId.slice(0, 8)}</code>
                    </td>
                    <td>{t.artifacts.length}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          <h4>Artifacts ({report.artifacts.length})</h4>
          {report.artifacts.length === 0 ? (
            <p className="muted">No artifacts.</p>
          ) : (
            <table>
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Task</th>
                  <th>Content type</th>
                  <th>Size</th>
                </tr>
              </thead>
              <tbody>
                {report.artifacts.map((a) => (
                  <tr key={a.artifactId}>
                    <td>{a.name}</td>
                    <td>
                      <code>{a.taskId.slice(0, 8)}</code>
                    </td>
                    <td>{a.contentType}</td>
                    <td>{a.size}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </>
      ) : (
        <p className="muted">Loading&hellip;</p>
      )}
    </div>
  )
}
