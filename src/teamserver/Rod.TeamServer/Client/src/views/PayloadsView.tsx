import { useCallback, useEffect, useState } from 'react'
import {
  type ListenerSummary,
  type PayloadSummary,
  deletePayload,
  listListeners,
  listPayloads,
  revokeStagerToken,
} from '../api'
import { frontFor } from '../fronts'
import { Icon } from '../components/Icons'

// The engagement's payload library: the payload store's durable listing, the
// view that outlives the Build page's bounded, process-local job queue. A
// payload built weeks ago is still here -- downloadable again, its baked
// credential's use budget readable (how many enrolls it has left, read live
// off the token store), its credential revocable, and deletable (which also
// stops any stager fetching it) -- whatever happened to the build queue or
// the teamserver process. This is the record; the Build page's recent-builds
// strip is only the queue's status.
// The long-form explanation lives in docs/operations/operator-ui.md; the
// filter field and actions carry their own hover text.
export function PayloadsView({ engagementId }: { engagementId: string }) {
  const [payloads, setPayloads] = useState<PayloadSummary[]>([])
  const [listeners, setListeners] = useState<ListenerSummary[]>([])
  const [filter, setFilter] = useState('')
  const [revoking, setRevoking] = useState<string | null>(null)
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
        column reads the baked token's use budget live.
      </p>
      <div className="inline-form">
        <input
          className="filter-text"
          placeholder="Filter (linux, Stage2, front, fingerprint…)"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          title="Free text across class, language, target, dial front, and fingerprint."
        />
        <button className="ghost" onClick={() => void refresh()}>
          <Icon name="refresh" />
          Refresh
        </button>
      </div>
      {error && <p className="error">{error}</p>}
      {payloads.length === 0 ? (
        <div className="empty">
          <Icon name="package" />
          No payloads stored yet -- build one on the Build tab.
        </div>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Built</th>
                <th>Class</th>
                <th>Target</th>
                <th>Front</th>
                <th>Credential</th>
                <th>Size</th>
                <th>Fingerprint</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
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
                  return (
                    <tr key={p.artifactId}>
                      <td>{new Date(p.builtAt).toLocaleString()}</td>
                      <td>
                        <code>
                          {p.language}:{p.class}
                        </code>
                      </td>
                      <td>{p.target ?? '—'}</td>
                      <td>
                        {front && (
                          <div
                            className="muted"
                            title={`The ${front.transport} listener this artifact dials`}
                          >
                            via {front.name} ({front.transport})
                          </div>
                        )}
                        <code>{p.endpoint ?? '—'}</code>
                        {p.beaconEndpoint && (
                          <div
                            className="muted"
                            title="The socket the check-in stream dials (split-socket build)"
                          >
                            beacon <code>{p.beaconEndpoint}</code>
                          </div>
                        )}
                      </td>
                      <td>
                        {p.tokenId ? (
                          p.tokenMaxUses !== null && p.tokenRemainingUses !== null && used !== null ? (
                            <span
                              title={`Baked token ${p.tokenId}${
                                p.tokenExpiresAt
                                  ? ` · expires ${new Date(p.tokenExpiresAt).toLocaleString()}`
                                  : ''
                              } -- one spend per enrolled host`}
                            >
                              {used}/{p.tokenMaxUses} enrolls spent · {p.tokenRemainingUses} left
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
                      <td>{p.size} bytes</td>
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
                  )
                })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
