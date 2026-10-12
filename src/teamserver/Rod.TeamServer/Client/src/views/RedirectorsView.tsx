import { useCallback, useEffect, useState } from 'react'
import {
  type MintedRedirectorCredential,
  type RedirectorCredentialSummary,
  type RedirectorNodeSummary,
  listRedirectorCredentials,
  listRedirectorNodes,
  mintRedirectorCredential,
  retireRedirectorNode,
  revokeRedirectorCredential,
} from '../api'
import { Icon } from '../components/Icons'
import { CopyButton } from '../components/CopyButton'
import { ago } from '../when'
import { useArmedDelete } from '../useArmedDelete'

// The redirector fleet (architecture.md Sec 8): the deployment's forwarding
// capacity, deployment-scoped like the topbar it rides under. Nodes dial
// home over the control channel and receive their forwarding rules when a
// listener attaches a front to them (that half lives in each engagement's
// Listeners tab); this page is the roster, the enrollment credentials that
// pre-provision new nodes, and retirement -- a retired node's reconnect is
// refused by name and its live session closed.

// The control URL a new node should dial, derived from the address this
// console is being viewed at: the operator front is the shared tier the
// control channel rides (architecture.md Sec 8), so wherever the browser
// reached the teamserver is exactly where a node can. The scheme follows
// the console's own TLS posture -- wss behind an HTTPS front, ws on the
// plain-HTTP lab socket (docs/operations/redirectors.md).
function controlUrl(): string {
  const scheme = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
  return `${scheme}//${window.location.host}/redirectors/control`
}

// The full node invocation a minted secret completes: the control URL, the
// one-time credential, and the state path from the runbook -- without it a
// restart loses the identity the spent credential minted, and the node can
// never enroll again.
function nodeCommand(secret: string): string {
  return `rod-redirector -control ${controlUrl()} -credential ${secret} -state /var/lib/rod/redirector.state.json`
}

export function RedirectorsView() {
  const [nodes, setNodes] = useState<RedirectorNodeSummary[]>([])
  const [credentials, setCredentials] = useState<RedirectorCredentialSummary[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [name, setName] = useState('')
  const [ttl, setTtl] = useState('')
  const [minted, setMinted] = useState<MintedRedirectorCredential | null>(null)

  const [armed, arm] = useArmedDelete()

  const refresh = useCallback(async () => {
    setBusy(true)
    try {
      const [nodeRows, credentialRows] = await Promise.all([
        listRedirectorNodes(),
        listRedirectorCredentials(),
      ])
      setNodes(nodeRows)
      setCredentials(credentialRows)
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const onMint = async (event: React.FormEvent) => {
    event.preventDefault()
    try {
      const ttlHours = ttl.trim() === '' ? undefined : Number(ttl.trim())
      setMinted(await mintRedirectorCredential(name.trim(), ttlHours))
      setName('')
      setTtl('')
      setError(null)
      await refresh()
    } catch (e) {
      setError(String(e))
    }
  }

  const onRetire = (n: RedirectorNodeSummary) =>
    arm(n.id, async () => {
      try {
        await retireRedirectorNode(n.id)
        setError(null)
        await refresh()
      } catch (e) {
        setError(String(e))
      }
    })

  const onRevoke = (c: RedirectorCredentialSummary) =>
    arm(c.id, async () => {
      try {
        await revokeRedirectorCredential(c.id)
        setError(null)
        await refresh()
      } catch (e) {
        setError(String(e))
      }
    })

  return (
    <div className="card">
      <h3>Redirectors</h3>
      <p className="muted">
        The deployment's forwarding fleet. Mint a credential below and the console renders the full
        node invocation to paste on the machine — the node enrolls once, then receives and serves
        the fronts this teamserver's listeners attach to it. The control URL it bakes follows this
        console's own address and TLS posture (wss behind HTTPS, ws otherwise). Presence is the
        control connection itself; retirement refuses the next reconnect by name.
      </p>
      <div className="inline-form">
        <button className="ghost" onClick={() => void refresh()} disabled={busy}>
          <Icon name="refresh" />
          Refresh
        </button>
      </div>
      {error && <p className="error">{error}</p>}

      <h4>Fleet</h4>
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Name</th>
              <th>Status</th>
              <th>Build</th>
              <th>Last seen</th>
              <th>Address</th>
              <th>Enrolled</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {nodes.length === 0 && (
              <tr>
                <td colSpan={7}>
                  <div className="empty">
                    <Icon name="radio" />
                    No redirectors enrolled — mint a credential below and hand it to the binary's
                    -credential flag.
                  </div>
                </td>
              </tr>
            )}
            {nodes.map((n) => (
              <tr key={n.id} className={n.retiredAt ? 'row-dim' : ''}>
                <td>{n.name}</td>
                <td>
                  {n.retiredAt ? (
                    <span className="status failed" title={`Retired ${ago(n.retiredAt)} ago`}>
                      retired
                    </span>
                  ) : n.online ? (
                    <span className="status completed" title="Control channel connected">
                      online
                    </span>
                  ) : (
                    <span className="status queued" title={`Last contact ${ago(n.lastSeenAt)} ago`}>
                      offline
                    </span>
                  )}
                </td>
                <td>
                  {n.reportedVersion ? (
                    <span className="muted" title={`commit ${n.reportedCommit ?? '?'}`}>
                      {n.reportedVersion}
                    </span>
                  ) : (
                    <span className="muted">—</span>
                  )}
                </td>
                <td title={new Date(n.lastSeenAt).toLocaleString()}>{ago(n.lastSeenAt)}</td>
                <td>
                  <span className="muted">{n.lastAddress ?? '—'}</span>
                </td>
                <td title={new Date(n.enrolledAt).toLocaleString()}>{ago(n.enrolledAt)}</td>
                <td>
                  {!n.retiredAt && (
                    <button
                      className={`sm danger${armed === n.id ? ' armed' : ''}`}
                      onClick={() => void onRetire(n)}
                      title={
                        armed === n.id
                          ? 'Click again — the button reverts on its own after a few seconds'
                          : 'Retire this node: its live control session closes and the next reconnect is refused by name. The two-click guard applies.'
                      }
                    >
                      {armed === n.id ? 'Confirm retire' : 'Retire'}
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <h4>Enrollment credentials</h4>
      <form className="inline-form" onSubmit={onMint}>
        <label>
          Node name
          <input
            placeholder="edge-fra-1"
            value={name}
            onChange={(e) => setName(e.target.value)}
            title="The credential's name becomes the enrolled node's roster name."
            required
          />
        </label>
        <label>
          Expires in (hours)
          <input
            className="bind-port"
            placeholder="24"
            value={ttl}
            onChange={(e) => setTtl(e.target.value)}
            title="Hard lifetime of the credential (default 24h — one use, then spent)."
          />
        </label>
        <button className="primary" type="submit" disabled={busy}>
          Mint credential
        </button>
      </form>

      {minted && (
        <div className="minted-secret" title="The secret is shown once, here, and never again.">
          <p>
            <strong>{minted.name}</strong> — paste this on the node's machine:
          </p>
          <code>{nodeCommand(minted.secret)}</code>
          <div className="table-toolbar">
            <CopyButton text={nodeCommand(minted.secret)} label="Copy command" />
            <CopyButton text={minted.secret} label="Copy secret" />
            <span className="muted">
              single-use, expires {new Date(minted.expiresAt).toLocaleString()}; the -state path
              persists the identity the enrollment mints, so a restart reconnects instead of
              dead-ending on the spent credential.
            </span>
          </div>
        </div>
      )}

      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Name</th>
              <th>State</th>
              <th>Issued</th>
              <th>Expires</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {credentials.length === 0 && (
              <tr>
                <td colSpan={5}>
                  <div className="empty">No credentials minted.</div>
                </td>
              </tr>
            )}
            {credentials.map((c) => (
              <tr key={c.id}>
                <td>{c.name}</td>
                <td>
                  {c.revokedAt ? (
                    <span className="status failed">revoked</span>
                  ) : c.enrolledNodeId ? (
                    <span className="status completed" title={`Enrolled node ${c.enrolledNodeId}`}>
                      enrolled
                    </span>
                  ) : new Date(c.expiresAt) < new Date() ? (
                    <span className="status failed">expired</span>
                  ) : (
                    <span className="status queued">unused</span>
                  )}
                </td>
                <td title={new Date(c.issuedAt).toLocaleString()}>{ago(c.issuedAt)}</td>
                <td title={new Date(c.expiresAt).toLocaleString()}>{ago(c.expiresAt)}</td>
                <td>
                  {!c.revokedAt && !c.enrolledNodeId && (
                    <button
                      className={`sm danger${armed === c.id ? ' armed' : ''}`}
                      onClick={() => void onRevoke(c)}
                      title={
                        armed === c.id
                          ? 'Click again — the button reverts on its own after a few seconds'
                          : 'Revoke this credential: it can never enroll. Idempotent.'
                      }
                    >
                      {armed === c.id ? 'Confirm revoke' : 'Revoke'}
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}
