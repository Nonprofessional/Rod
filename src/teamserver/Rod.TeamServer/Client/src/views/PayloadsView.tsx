import { Fragment, useCallback, useEffect, useState } from 'react'
import {
  type ListenerSummary,
  type PayloadSummary,
  deletePayload,
  listListeners,
  listPayloads,
  revokeStagerToken,
} from '../api'
import { frontFor, hostPortOf } from '../fronts'
import { Icon } from '../components/Icons'

// The engagement's payload library: the payload store's durable listing, the
// view that outlives the Build page's bounded, process-local job queue. A
// payload built weeks ago is still here -- downloadable again, its baked
// credential's use budget readable (how many enrolls it has left, read live
// off the token store), its credential revocable, and deletable (which also
// stops any stager fetching it) -- whatever happened to the build queue or
// the teamserver process. This is the record; the Build page's recent-builds
// strip is only the queue's status.
//
// Each row expands into the build's own parameters -- the beacon profile,
// the wire knobs, the credential's minted shape -- snapshotted at bake time,
// so "what did I build" never depends on remembering the form. Rows are
// single-line; the detail carries what does not fit.
// The long-form explanation lives in docs/operations/operator-ui.md; the
// filter field and actions carry their own hover text.

function fmtBytes(size: number): string {
  if (size >= 1024 * 1024) return `${(size / (1024 * 1024)).toFixed(1)} MB`
  if (size >= 1024) return `${(size / 1024).toFixed(0)} KB`
  return `${size} B`
}

export function PayloadsView({ engagementId }: { engagementId: string }) {
  const [payloads, setPayloads] = useState<PayloadSummary[]>([])
  const [listeners, setListeners] = useState<ListenerSummary[]>([])
  // The filter commits on Enter or the Search button, like every text search
  // in the operator UI.
  const [filterDraft, setFilterDraft] = useState('')
  const [filter, setFilter] = useState('')
  const [revoking, setRevoking] = useState<string | null>(null)
  const [expanded, setExpanded] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    try {
      const [list, fronts] = await Promise.all([
        listPayloads(engagementId),
        listListeners(engagementId).catch(() => [] as ListenerSummary[]),
      ])
      setPayloads(list)
      setListeners(fronts)
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }, [engagementId])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const onRevokeToken = async (tokenId: string) => {
    if (
      !window.confirm(
        `Revoke baked token ${tokenId.slice(0, 8)}? The credential stops working immediately; a deployed artifact that has not enrolled yet will not be able to.`,
      )
    )
      return
    setRevoking(tokenId)
    try {
      await revokeStagerToken(engagementId, tokenId)
      setError(null)
      await refresh()
    } catch (e) {
      setError(String(e))
    } finally {
      setRevoking(null)
    }
  }

  const onDelete = async (p: PayloadSummary) => {
    if (
      !window.confirm(
        `Delete payload ${p.fingerprint.slice(0, 12)} (${p.class}${p.target ? ' ' + p.target : ''})? ` +
          'The stored bytes are gone and any stager fetching it stops working. The deletion is audited.',
      )
    )
      return
    try {
      await deletePayload(engagementId, p.artifactId)
      setError(null)
      await refresh()
    } catch (e) {
      setError(String(e))
    }
  }

  return (
    <div className="card">
      <h3>Payloads</h3>
      <p className="muted">
        Every payload this engagement built — durable, restart-safe, filterable. The credential
        column reads the baked token's use budget live; the chevron unfolds the build's parameters.
      </p>
      <div className="inline-form">
        <input
          className="filter-text"
          placeholder="Filter (linux, Stage2, listener, fingerprint…)"
          value={filterDraft}
          onChange={(e) => setFilterDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') setFilter(filterDraft)
          }}
          title="Free text across class, language, target, listener, and fingerprint. Enter or the Search button applies."
        />
        <button
          className="ghost"
          onClick={() => setFilter(filterDraft)}
          title="Apply the filter (Enter works too)"
        >
          Search
        </button>
        <button className="ghost" onClick={() => void refresh()}>
          <Icon name="refresh" />
          Refresh
        </button>
      </div>
      {error && <p className="error">{error}</p>}
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th></th>
              <th>Built</th>
              <th>Class</th>
              <th>Target</th>
              <th>Listener</th>
              <th>Credential</th>
              <th>Size</th>
              <th>Fingerprint</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {payloads.length === 0 && (
              <tr>
                <td colSpan={9}>
                  <div className="empty">
                    <Icon name="package" />
                    No payloads stored yet -- build one on the Build tab.
                  </div>
                </td>
              </tr>
            )}
            {payloads
                .filter((p) => {
                  const q = filter.trim().toLowerCase()
                  if (!q) return true
                  const front = frontFor(p.endpoint, listeners)
                  return [p.class, p.language, p.target, p.endpoint, front?.name, p.fingerprint].some(
                    (v) => v?.toLowerCase().includes(q),
                  )
                })
                .map((p) => {
                  const front = frontFor(p.endpoint, listeners)
                  const expired =
                    p.tokenExpiresAt !== null && new Date(p.tokenExpiresAt).getTime() < Date.now()
                  const used =
                    p.tokenMaxUses !== null && p.tokenRemainingUses !== null
                      ? p.tokenMaxUses - p.tokenRemainingUses
                      : null
                  const open = expanded === p.artifactId
                  return (
                    <Fragment key={p.artifactId}>
                      <tr>
                        <td>
                          <button
                            className={`ghost sm row-expand${open ? ' open' : ''}`}
                            aria-label={open ? 'Collapse build parameters' : 'Expand build parameters'}
                            title={open ? 'Hide the build parameters' : 'The parameters this payload was built with'}
                            onClick={() => setExpanded(open ? null : p.artifactId)}
                          >
                            <Icon name={open ? 'chevronDown' : 'chevronRight'} />
                          </button>
                        </td>
                        <td>{new Date(p.builtAt).toLocaleString()}</td>
                        <td>
                          <code>
                            {p.language}:{p.class}
                          </code>
                        </td>
                        <td>{p.target ?? '—'}</td>
                        <td>
                          <span
                            title={
                              front
                                ? `The engagement's ${front.transport} listener: ${p.endpoint} (enroll + check-in${p.beaconEndpoint ? ' of the split shape' : ''})`
                                : `No listener serves this address (typed for a redirector): ${p.endpoint}`
                            }
                          >
                            {front ? (
                              <>
                                {front.name} <span className="muted">({front.transport})</span> ·{' '}
                                <code>{hostPortOf(p.endpoint ?? '')}</code>
                              </>
                            ) : (
                              <>
                                <code>{hostPortOf(p.endpoint ?? '')}</code>{' '}
                                <span className="muted">manual</span>
                              </>
                            )}
                          </span>
                        </td>
                        <td>
                          {p.tokenId ? (
                            used !== null && p.tokenMaxUses !== null && p.tokenRemainingUses !== null ? (
                              <span
                                title={`Baked token ${p.tokenId}${
                                  p.tokenExpiresAt
                                    ? ` · expires ${new Date(p.tokenExpiresAt).toLocaleString()}`
                                    : ''
                                } -- one spend per enrolled host; a zero budget is unlimited`}
                              >
                                {p.tokenMaxUses === 0 ? (
                                  <>
                                    unlimited{used > 0 ? ` · ${used} spent` : ''}
                                  </>
                                ) : (
                                  <>
                                    {used}/{p.tokenMaxUses} spent · {p.tokenRemainingUses} left
                                  </>
                                )}
                                {expired ? ' · expired' : ''}
                                {revoking === p.tokenId ? (
                                  ' (revoking…)'
                                ) : (
                                  <>
                                    {' '}
                                    <button
                                      className="sm danger"
                                      onClick={() => void onRevokeToken(p.tokenId!)}
                                      title="The baked credential stops working at the next enrollment attempt"
                                    >
                                      Revoke
                                    </button>
                                  </>
                                )}
                              </span>
                            ) : (
                              <span
                                className="muted"
                                title={`Baked token ${p.tokenId} is no longer stored -- spent, revoked, or expired and swept. No more enrollments.`}
                              >
                                no enrolls left{' '}
                                {revoking === p.tokenId ? (
                                  '(revoking…)'
                                ) : (
                                  <button
                                    className="sm danger"
                                    onClick={() => void onRevokeToken(p.tokenId!)}
                                    title="The baked credential stops working at the next enrollment attempt"
                                  >
                                    Revoke
                                  </button>
                                )}
                              </span>
                            )
                          ) : (
                            <span className="muted">—</span>
                          )}
                        </td>
                        <td title={`${p.size} bytes`}>{fmtBytes(p.size)}</td>
                        <td>
                          <code title={p.fingerprint}>{p.fingerprint.slice(0, 16)}</code>
                        </td>
                        <td>
                          <a
                            className="download-link"
                            href={`engagements/${engagementId}/payloads/${p.artifactId}`}
                            download
                          >
                            Download
                          </a>{' '}
                          <button className="sm danger" onClick={() => void onDelete(p)}>
                            Delete
                          </button>
                        </td>
                      </tr>
                      {open && (
                        <tr className="payload-detail-row">
                          <td colSpan={9}>
                            <PayloadDetail payload={p} />
                          </td>
                        </tr>
                      )}
                    </Fragment>
                  )
                })}
          </tbody>
        </table>
      </div>
    </div>
  )
}

// The build's own parameters, snapshotted at bake time and read back from the
// library row: what the form carried when this artifact was generated. Null
// fields mean the build's default -- the snapshot records what was set, and
// the defaults live in the Build form's help text.
function PayloadDetail({ payload }: { payload: PayloadSummary }) {
  const b = payload.build
  const line = (label: string, value: React.ReactNode): [string, React.ReactNode] => [label, value]
  const lines: [string, React.ReactNode][] = [
    line('Fingerprint', <code>{payload.fingerprint}</code>),
    line('Built', new Date(payload.builtAt).toLocaleString()),
    line(
      'Credential',
      payload.tokenId ? (
        <>
          token <code>{payload.tokenId.slice(0, 8)}</code>
          {b?.tokenMaxUses != null
            ? b.tokenMaxUses === 0
              ? ' · unlimited uses'
              : ` · max ${b.tokenMaxUses} enroll${b.tokenMaxUses === 1 ? '' : 's'}`
            : ''}
          {payload.tokenExpiresAt
            ? ` · expires ${new Date(payload.tokenExpiresAt).toLocaleString()}`
            : ''}
          {payload.tokenMaxUses === 0
            ? ''
            : payload.tokenMaxUses != null && payload.tokenRemainingUses != null
              ? ` · ${payload.tokenRemainingUses} left`
              : ' · no enrolls left (spent, revoked, or swept)'}
        </>
      ) : payload.class === 'WebShell' && payload.credential ? (
        <>
          {payload.target?.startsWith('rod-') ? 'connection key' : 'connection password'}{' '}
          <code>{payload.credential}</code>
        </>
      ) : (
        'none baked'
      ),
    ),
  ]
  if (b) {
    lines.push(
      line('Mode', b.mode ?? 'stream (default)'),
      line(
        'Check-in',
        b.sleepSeconds != null
          ? `every ${b.sleepSeconds}s ± ${b.jitterSeconds ?? 0}s jitter`
          : 'defaults',
      ),
      line('Kill date', b.killDate ? new Date(b.killDate).toLocaleString() : 'none (open-ended)'),
    )
    if (b.enrollPath) lines.push(line('Enroll path', <code>{b.enrollPath}</code>))
    if (b.userAgent) lines.push(line('User agent', <code>{b.userAgent}</code>))
    if (b.requestTimeoutSeconds != null)
      lines.push(line('Request timeout', `${b.requestTimeoutSeconds}s`))
    if (b.envelope) lines.push(line('Enroll body', b.envelope))
    lines.push(
      line(
        'Check-in protection',
        b.checkInProtection == null ? 'on (default)' : b.checkInProtection ? 'on' : 'off',
      ),
    )
  }
  if (payload.beaconEndpoint)
    lines.push(line('Interactive endpoint', <code>{payload.beaconEndpoint}</code>))
  if (b?.fallbackEndpoints?.length)
    lines.push(
      line('Fallback fronts', b.fallbackEndpoints.map((f) => hostPortOf(f)).join(' · ')),
    )
  lines.push(line('Content type', <code>{payload.contentType}</code>))
  lines.push(line('Size', `${payload.size} bytes`))

  return (
    <div className="payload-detail">
      {lines.map(([label, value]) => (
        <div className="payload-detail-line" key={label}>
          <span className="muted">{label}</span>
          <span>{value}</span>
        </div>
      ))}
    </div>
  )
}
