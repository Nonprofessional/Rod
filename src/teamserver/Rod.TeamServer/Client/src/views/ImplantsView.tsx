import { Fragment, useCallback, useEffect, useState } from 'react'
import {
  type Implant,
  type ImplantNote,
  type PresenceRecord,
  type StagerToken,
  addImplantNote,
  listImplantNotes,
  listImplants,
  mintStagerToken,
  retireImplant,
} from '../api'
import { Icon } from '../components/Icons'
import { StatusBadge } from '../components/StatusBadge'

// The implants panel: the enrolled sessions for an engagement,
// each with its class, online state, kill date, and parentage, plus the live
// roster (which implants hold active sessions right now, handed down from the
// engagement view's presence query). An operator can mint a stager token (to
// enroll a new implant), retire (burn) a live implant -- the OPSEC control
// that takes an implant out of operation (refused at handshake and untaskable
// afterwards) -- and keep free-text notes on an implant: the "whose beacon is
// this" memory, attributed per author and durable in the audit trail, so it
// survives a teamserver restart.

export function ImplantsView({
  engagementId,
  onlineTick,
  onlineImplants,
}: {
  engagementId: string
  // Bumped by the parent whenever a live event suggests a change, so the list
  // refreshes without per-view polling.
  onlineTick: number
  // The presence query's projection, refreshed by the parent on the live tick.
  onlineImplants: PresenceRecord[]
}) {
  const [implants, setImplants] = useState<Implant[]>([])
  const [minted, setMinted] = useState<StagerToken | null>(null)
  const [copied, setCopied] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [notesFor, setNotesFor] = useState<string | null>(null)
  const [notes, setNotes] = useState<ImplantNote[]>([])
  const [noteDraft, setNoteDraft] = useState('')
  const [noteBusy, setNoteBusy] = useState(false)
  // The mint scope: a batch of N implants takes one token with N uses -- each
  // enroll spends one -- inside the chosen window, so the credential is handed
  // to the crew once instead of minted per deployment.
  const [mintUses, setMintUses] = useState(1)
  const [mintHours, setMintHours] = useState(1)

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

  // A stale open notes panel must never survive an engagement switch.
  useEffect(() => {
    setNotesFor(null)
    setNotes([])
  }, [engagementId])

  const onMint = async () => {
    try {
      const scope =
        mintUses > 1 || mintHours !== 1
          ? { maxUses: mintUses, lifetimeSeconds: mintHours * 3600 }
          : undefined
      setMinted(await mintStagerToken(engagementId, scope))
      setCopied(false)
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }

  // The secret is shown exactly once, so the copy affordance is the difference
  // between transcribing it correctly and not.
  const onCopySecret = async () => {
    if (!minted) return
    try {
      await navigator.clipboard.writeText(minted.secret)
      setCopied(true)
      window.setTimeout(() => setCopied(false), 1500)
    } catch {
      // Clipboard access can be refused (permissions, non-secure origin);
      // the secret stays selectable for manual copy.
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

  const onToggleNotes = async (implantId: string) => {
    if (notesFor === implantId) {
      setNotesFor(null)
      return
    }
    setNotesFor(implantId)
    setNoteDraft('')
    try {
      setNotes(await listImplantNotes(engagementId, implantId))
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }

  const onAddNote = async (event: React.FormEvent) => {
    event.preventDefault()
    if (!notesFor || !noteDraft.trim() || noteBusy) return
    setNoteBusy(true)
    try {
      await addImplantNote(engagementId, notesFor, noteDraft.trim())
      setNoteDraft('')
      setNotes(await listImplantNotes(engagementId, notesFor))
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setNoteBusy(false)
    }
  }

  return (
    <>
      <div className="card">
        <h3>Live sessions</h3>
        <p className="muted">
          Implants holding an active session right now. Opens and closes arrive as live events,
          so this roster moves the moment the fleet changes.
        </p>
        {onlineImplants.length === 0 ? (
          <div className="empty">
            <Icon name="radio" />
            No implants online.
          </div>
        ) : (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Implant</th>
                  <th>Online since</th>
                  <th>Last seen</th>
                  <th>Capabilities</th>
                </tr>
              </thead>
              <tbody>
                {onlineImplants.map((p) => (
                  <tr key={p.sessionId}>
                    <td>
                      <span className="dot online" title="online" />{' '}
                      <code>{p.implantId.slice(0, 8)}</code>
                    </td>
                    <td>{new Date(p.onlineAt).toLocaleTimeString()}</td>
                    <td>{new Date(p.lastSeenAt).toLocaleTimeString()}</td>
                    <td>
                      <span className="muted" title={p.capabilities.join(', ')}>
                        {p.capabilities.length} capabilit{p.capabilities.length === 1 ? 'y' : 'ies'}
                      </span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <div className="card">
        <h3>Fleet</h3>
        {implants.length === 0 ? (
          <div className="empty">
            <Icon name="cpu" />
            No implants enrolled yet.
          </div>
        ) : (
          <div className="table-wrap">
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
                  <Fragment key={i.implantId}>
                    <tr>
                      <td>
                        <code>{i.implantId.slice(0, 8)}</code>
                      </td>
                      <td>{i.class}</td>
                      <td>
                        <StatusBadge
                          status={i.retiredAt ? 'retired' : i.isOnline ? 'online' : 'offline'}
                        />
                      </td>
                      <td>{new Date(i.killDate).toLocaleDateString()}</td>
                      <td>
                        {i.parentImplantId ? (
                          <code>{i.parentImplantId.slice(0, 8)}</code>
                        ) : (
                          <span className="muted">&mdash;</span>
                        )}
                      </td>
                      <td>
                        <div className="row-actions">
                          <button className="sm" onClick={() => void onToggleNotes(i.implantId)}>
                            {notesFor === i.implantId ? 'Hide notes' : 'Notes'}
                          </button>
                          {!i.retiredAt && (
                            <button className="danger sm" onClick={() => onRetire(i.implantId)}>
                              Retire
                            </button>
                          )}
                        </div>
                      </td>
                    </tr>
                    {notesFor === i.implantId && (
                      <tr>
                        <td colSpan={6}>
                          <div className="notes-panel">
                            <ul className="notes-list">
                              {notes.length === 0 ? (
                                <li className="muted">No notes on this implant yet.</li>
                              ) : (
                                notes.map((n) => (
                                  <li key={n.noteId}>
                                    <span className="notes-meta">
                                      <code>{n.author.slice(0, 8)}</code>{' '}
                                      {new Date(n.at).toLocaleString()}
                                    </span>
                                    {n.text}
                                  </li>
                                ))
                              )}
                            </ul>
                            <form className="task-form" onSubmit={onAddNote}>
                              <input
                                className="wide"
                                placeholder="whose beacon is this?"
                                value={noteDraft}
                                onChange={(e) => setNoteDraft(e.target.value)}
                              />
                              <button className="sm" type="submit" disabled={noteBusy || !noteDraft.trim()}>
                                Add note
                              </button>
                            </form>
                          </div>
                        </td>
                      </tr>
                    )}
                  </Fragment>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {error && <p className="error">{error}</p>}
      </div>

      <div className="card">
        <h3>Stager token</h3>
        <p className="muted">
          The deployment credential: a secret you hand to a payload so it can enroll into
          this engagement -- run the built implant (or its stager) with{' '}
          <code>-enroll-url &lt;endpoint&gt; -token &lt;secret&gt;</code>. Nothing joins the
          engagement without one, and the secret is shown exactly once at mint. A batch
          mints one token with several uses instead of one secret per deployment.
        </p>
        <div className="create-form-row">
          <label className="muted" htmlFor="mint-uses">
            uses
          </label>
          <input
            id="mint-uses"
            type="number"
            min={1}
            max={10000}
            style={{ width: '5.5rem' }}
            value={mintUses}
            onChange={(e) => setMintUses(Math.min(10000, Math.max(1, Number(e.target.value) || 1)))}
          />
          <label className="muted" htmlFor="mint-window">
            window
          </label>
          <select
            id="mint-window"
            value={mintHours}
            onChange={(e) => setMintHours(Number(e.target.value))}
          >
            <option value={1}>1 hour</option>
            <option value={8}>8 hours</option>
            <option value={24}>24 hours</option>
            <option value={168}>7 days</option>
          </select>
          <button className="primary" onClick={onMint} disabled={busy}>
            Mint stager token
          </button>
        </div>
        {minted && (
          <>
            <div className="secret-row">
              <code className="secret">{minted.secret}</code>
              <button className="sm" onClick={() => void onCopySecret()}>
                {copied ? 'Copied' : 'Copy'}
              </button>
            </div>
            <dl className="kv">
              <dt>Expires</dt>
              <dd>{new Date(minted.expiresAt).toLocaleString()}</dd>
              <dt>Max uses</dt>
              <dd>{minted.maxUses}</dd>
            </dl>
          </>
        )}
      </div>
    </>
  )
}
