import { useCallback, useEffect, useState } from 'react'
import {
  type ListenerSummary,
  createListener,
  deleteListener,
  listListeners,
  repointListener,
} from '../api'
import { Icon } from '../components/Icons'
import { StatusBadge } from '../components/StatusBadge'

// The engagement's listeners: the C2 ingress this one engagement owns, each
// with the socket it opens (bind) and the public endpoint implants dial
// (typically a redirector). A listener created here belongs to the engagement
// in context -- no picker, no sharing -- and its definition is persisted, so a
// restart rebinds it with the same id; repointing swaps the public endpoint at
// runtime and the definition follows. The operator front the UI rides is
// startup configuration: it carries no implant ingress and never appears
// here.

// The transports the create form offers, with the bind-address shape each
// one takes. The server validates for real; this list only keeps the form
// from offering shapes the server would refuse.
const TRANSPORTS = [
  { value: 'http', label: 'HTTP (plain, loopback dev posture)', bindHint: '127.0.0.1:5090' },
  { value: 'mtls', label: 'mTLS (gRPC beacon)', bindHint: '0.0.0.0:5443' },
  { value: 'https-envelope', label: 'HTTPS envelope (POST check-ins)', bindHint: '0.0.0.0:8443' },
  { value: 'dns', label: 'DNS (TXT check-ins)', bindHint: '0.0.0.0:53' },
  { value: 'smb', label: 'SMB named pipe', bindHint: 'rod-pipe' },
  { value: 'tcp', label: 'Raw TCP', bindHint: '0.0.0.0:4444' },
]

export function ListenersView({ engagementId }: { engagementId: string }) {
  const [listeners, setListeners] = useState<ListenerSummary[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [newEndpoint, setNewEndpoint] = useState<Record<string, string>>({})

  // The create form's working state; the transport select drives the bind
  // placeholder so the accepted shape is visible where it is entered.
  const [name, setName] = useState('')
  const [transport, setTransport] = useState('http')
  const [bindAddress, setBindAddress] = useState('')
  const [publicEndpoint, setPublicEndpoint] = useState('')
  const bindHint = TRANSPORTS.find((t) => t.value === transport)?.bindHint ?? ''

  const refresh = useCallback(async () => {
    setBusy(true)
    try {
      setListeners(await listListeners(engagementId))
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

  const onRepoint = async (id: string) => {
    const endpoint = newEndpoint[id]?.trim()
    if (!endpoint) return
    try {
      await repointListener(engagementId, id, endpoint)
      setNewEndpoint((m) => ({ ...m, [id]: '' }))
      await refresh()
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }

  const onCreate = async (event: React.FormEvent) => {
    event.preventDefault()
    try {
      await createListener(engagementId, {
        name,
        transport,
        bindAddress,
        // An empty field means "derive it from the bind"; the server completes
        // blanks and bare hostnames, so the form never blocks on typing a
        // fully-qualified URL.
        publicEndpoint: publicEndpoint.trim(),
      })
      setName('')
      setBindAddress('')
      setPublicEndpoint('')
      setError(null)
      await refresh()
    } catch (e) {
      setError(String(e))
    }
  }

  const onDelete = async (l: ListenerSummary) => {
    if (!window.confirm(`Delete listener "${l.name}" (${l.bindAddress})? Its socket is unbound.`))
      return
    try {
      await deleteListener(engagementId, l.id)
      setError(null)
      await refresh()
    } catch (e) {
      setError(String(e))
    }
  }

  return (
    <div className="card">
      <h3>Listeners</h3>
      <p className="muted">
        This engagement's C2 ingress. <strong>Bind</strong> is the socket this server opens;{' '}
        <strong>public endpoint</strong> is the address baked into payloads — what implants
        actually dial, usually your redirector in production. Leave it empty and the server derives
        it from the bind: bind 10.1.2.3:8443 on https-envelope becomes{' '}
        <code>https://10.1.2.3:8443</code>. A bare hostname (<code>redirect.example</code>) takes
        the transport's scheme and this listener's port; a wildcard bind (0.0.0.0) names no
        address implants can dial, so it needs a hostname. DNS, SMB, and TCP cannot derive — spell
        their endpoint out. Every listener is persisted — a restart rebinds it — and enrollment
        through it accepts only this engagement's tokens.
      </p>

      <form className="inline-form listener-create" onSubmit={onCreate}>
        <input
          placeholder="Name"
          value={name}
          onChange={(e) => setName(e.target.value)}
          required
        />
        <select
          value={transport}
          onChange={(e) => setTransport(e.target.value)}
          aria-label="Transport"
          title="Transport"
        >
          {TRANSPORTS.map((t) => (
            <option key={t.value} value={t.value}>
              {t.label}
            </option>
          ))}
        </select>
        <input
          placeholder={`Bind (${bindHint})`}
          value={bindAddress}
          onChange={(e) => setBindAddress(e.target.value)}
          required
        />
        <input
          placeholder="Public endpoint (optional)"
          title="Leave empty and the server derives it from the bind (the transport's scheme + the bind host:port). A bare hostname gets the scheme and this listener's port. A wildcard bind like 0.0.0.0 cannot derive — type the hostname implants should dial."
          value={publicEndpoint}
          onChange={(e) => setPublicEndpoint(e.target.value)}
        />
        <button className="primary" type="submit" disabled={busy}>
          Create
        </button>
      </form>

      <div className="inline-form">
        <button className="ghost" onClick={() => void refresh()} disabled={busy}>
          <Icon name="refresh" />
          Refresh
        </button>
      </div>
      {error && <p className="error">{error}</p>}
      {listeners.length === 0 ? (
        <div className="empty">
          <Icon name="radio" />
          No listeners for this engagement yet -- create the ingress its implants will dial.
        </div>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Name</th>
                <th>Transport</th>
                <th>Bind</th>
                <th>Public endpoint</th>
                <th>State</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {listeners.map((l) => (
                <tr key={l.id}>
                  <td>{l.name}</td>
                  <td>{l.transport}</td>
                  <td>
                    <code>{l.bindAddress}</code>
                  </td>
                  <td>
                    <code>{l.publicEndpoint}</code>
                    {l.repointedAt && <span className="muted"> (repointed)</span>}
                  </td>
                  <td>
                    <StatusBadge status={l.state} />
                  </td>
                  <td>
                    <form
                      className="repoint-form"
                      onSubmit={(e) => {
                        e.preventDefault()
                        void onRepoint(l.id)
                      }}
                    >
                      <input
                        placeholder="new endpoint"
                        value={newEndpoint[l.id] ?? ''}
                        onChange={(e) => setNewEndpoint((m) => ({ ...m, [l.id]: e.target.value }))}
                      />
                      <button className="sm" type="submit">
                        Repoint
                      </button>
                      <button className="sm danger" type="button" onClick={() => void onDelete(l)}>
                        Delete
                      </button>
                    </form>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
