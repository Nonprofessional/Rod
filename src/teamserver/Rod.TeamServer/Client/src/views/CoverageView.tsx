import { useEffect, useMemo, useState } from 'react'
import { type CapabilityCoverage, getCapabilityCoverage } from '../api'
import { HelpHint } from '../components/HelpHint'
import { Icon } from '../components/Icons'
import { STATUS_META, carrierSummary, filterCoverage, isGapRow } from '../coverageMatrix'

// The coverage matrix (ADR-007): technique x platform x implementation status
// over the whole deployment -- what this teamserver *can* do, with each
// technique naming the verb, module, and implant classes that carry it. The
// engagement report's Navigator layer is the other face of the same mapping
// data: what an engagement *did*. The gap rows are the work queue -- a
// declared technique no implementation carries yet.

export function CoverageView() {
  const [coverage, setCoverage] = useState<CapabilityCoverage | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [tactic, setTactic] = useState<string | null>(null)
  const [platform, setPlatform] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    getCapabilityCoverage()
      .then((matrix) => {
        if (!cancelled) setCoverage(matrix)
      })
      .catch((e) => {
        if (!cancelled) setError(String(e))
      })
    return () => {
      cancelled = true
    }
  }, [])

  const rows = useMemo(
    () => (coverage ? filterCoverage(coverage.techniques, tactic, platform) : []),
    [coverage, tactic, platform],
  )
  const gapCount = useMemo(() => rows.filter(isGapRow).length, [rows])

  return (
    <section className="view">
      <h2>
        Coverage
        <HelpHint>
          The ATT&CK coverage matrix: every technique this deployment can task, the verb, module,
          and implant classes carrying it, and the gaps that read as the work queue. The
          engagement report carries the same mapping as an ATT&CK Navigator layer — what was
          done, from what can be.
        </HelpHint>
      </h2>
      {error && <p className="error">{error}</p>}

      <div className="card">
        <div className="card-head">
          <h3>Techniques{coverage ? ` (ATT&CK v${coverage.attackVersion})` : ''}</h3>
          <div className="table-toolbar">
            <select
              aria-label="Filter by tactic"
              value={tactic ?? ''}
              onChange={(e) => setTactic(e.target.value || null)}
            >
              <option value="">All tactics</option>
              {(coverage?.tactics ?? []).map((t) => (
                <option key={t} value={t}>
                  {t}
                </option>
              ))}
            </select>
            <select
              aria-label="Filter by platform"
              value={platform ?? ''}
              onChange={(e) => setPlatform(e.target.value || null)}
            >
              <option value="">All platforms</option>
              {(coverage?.platforms ?? []).map((p) => (
                <option key={p} value={p}>
                  {p}
                </option>
              ))}
            </select>
          </div>
        </div>

        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Technique</th>
                <th>Tactics</th>
                <th>Carried by</th>
                <th>Platforms</th>
              </tr>
            </thead>
            <tbody>
              {rows.length === 0 && (
                <tr>
                  <td colSpan={4}>
                    <div className="empty">
                      <Icon name="target" />
                      {coverage
                        ? 'No technique under this filter.'
                        : 'Loading the coverage matrix…'}
                    </div>
                  </td>
                </tr>
              )}
              {rows.map((row) => (
                <tr key={row.techniqueId}>
                  <td>
                    <strong title={row.name}>{row.techniqueId}</strong>
                    <div className="muted">{row.name}</div>
                  </td>
                  <td>{row.tactics.join(', ')}</td>
                  <td>
                    {row.carriers.map((carrier) => (
                      <div key={carrier.verb} title={carrierSummary(carrier)}>
                        <span className={STATUS_META[carrier.status].pill}>
                          {STATUS_META[carrier.status].label}
                        </span>{' '}
                        {carrier.verb}
                        {carrier.module ? <span className="muted"> · {carrier.module}</span> : null}
                        <span className="muted"> · {carrier.implantClasses.join(', ')}</span>
                      </div>
                    ))}
                  </td>
                  <td>{row.platforms.length > 0 ? row.platforms.join(', ') : 'per deployment'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {coverage && rows.length > 0 && (
          <p className="muted">
            {rows.length} technique{rows.length === 1 ? '' : 's'}
            {gapCount > 0 ? ` — ${gapCount} read${gapCount === 1 ? 's' : ''} as the work queue (gap rows)` : ''}
          </p>
        )}
      </div>
    </section>
  )
}
