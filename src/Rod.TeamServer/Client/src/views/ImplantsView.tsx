import { useCallback, useEffect, useState } from 'react'
import { type Implant, type StagerToken, listImplants, mintStagerToken, retireImplant } from '../api'

// The implants panel (roadmap M11.1): the enrolled sessions for an engagement,
// each with its class, online state, kill date, and parentage. An operator can
// mint a stager token (to enroll a new implant) and retire (burn) a live implant
// -- the M4.4 OPSEC control that takes an implant out of operation (refused at
// handshake and untaskable afterwards).

export function ImplantsView({
  engagementId,
  onlineTick,
}: {
  engagementId: string
  // Bumped by the parent whenever a live event suggests a change, so the list
  // refreshes without per-view polling.
  onlineTick: number
}) {
  const [implants, setImplants] = useState<Implant[]>([])
  const [minted, setMinted] = useState<StagerToken | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const refresh = useCallback(async () => {
    setBusy(true)
    try {
      setImplants(await listImplants(engagementId))
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }, [engagementId])

  useEffect(() => {
    void refresh()
  }, [refresh, onlineTick])

  const onMint = async () => {
    try {
      setMinted(await mintStagerToken(engagementId))
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }

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

  return (
    <div className="card">
      <h3>Stager token</h3>
      <p className="muted">
        Mint a single-use token, then redeem it at <code>POST /implants/enroll</code> to enroll an
        implant. The secret is shown once.
      </p>
      <button onClick={onMint} disabled={busy}>
        Mint stager token
      </button>
      {minted && (
        <dl className="kv">
          <dt>Secret</dt>
          <dd>
            <code>{minted.secret}</code>
          </dd>
          <dt>Expires</dt>
          <dd>{new Date(minted.expiresAt).toLocaleString()}</dd>
          <dt>Max uses</dt>
          <dd>{minted.maxUses}</dd>
        </dl>
      )}

      <h3>Implants</h3>
      {implants.length === 0 ? (
        <p className="muted">No implants enrolled yet.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Implant</th>
              <th>Class</th>
              <th>Status</th>
              <th>Kill date</th>
              <th>Parent</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {implants.map((i) => (
              <tr key={i.implantId}>
                <td>
                  <code>{i.implantId.slice(0, 8)}</code>
                </td>
                <td>{i.class}</td>
                <td>
                  <span className={`dot ${i.isOnline ? 'online' : 'offline'}`} title={i.isOnline ? 'online' : 'offline'} />{' '}
                  {i.retiredAt ? 'retired' : i.isOnline ? 'online' : 'offline'}
                </td>
                <td>{new Date(i.killDate).toLocaleDateString()}</td>
                <td>{i.parentImplantId ? <code>{i.parentImplantId.slice(0, 8)}</code> : <span className="muted">&mdash;</span>}</td>
                <td>
                  {!i.retiredAt && (
                    <button className="danger" onClick={() => onRetire(i.implantId)}>
                      Retire
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {error && <p className="error">{error}</p>}
    </div>
  )
}
