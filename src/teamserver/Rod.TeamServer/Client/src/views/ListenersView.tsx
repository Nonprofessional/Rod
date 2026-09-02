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

// The listeners / redirector panel: the bound C2 ingress, each with the socket
// it opens (bind) and the public endpoint implants dial (typically a
// redirector). Repointing swaps that public endpoint at runtime -- a burned
// redirector is replaced without backend change. Listeners can also be created
// and deleted here while the teamserver serves; a runtime listener is not
// written back to the startup configuration, so a restart rebinds exactly what
// the configuration names (delete is refused for configuration-bound
// listeners -- the configuration owns those). Global infrastructure, not
// engagement-scoped.

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

export function ListenersView() {
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
      setListeners(await listListeners())
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

  const onRepoint = async (id: string) => {
    const endpoint = newEndpoint[id]?.trim()
    if (!endpoint) return
    try {
      await repointListener(id, endpoint)
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
      await createListener({
        name,
        transport,
        bindAddress,
        publicEndpoint,
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
      await deleteListener(l.id)
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
        C2 ingress. <strong>Bind</strong> is the socket this server opens;{' '}
        <strong>public endpoint</strong> is the address baked payloads dial -- usually your
        redirector. The two are decoupled on purpose: repoint swaps a burned front without touching
        the backend. Runtime-created listeners are not persisted to the startup configuration -- a
        restart rebinds what the configuration names.
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
          placeholder="Public endpoint"
          value={publicEndpoint}
          onChange={(e) => setPublicEndpoint(e.target.value)}
          required
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
          No listeners registered.
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
                      <button
                        className="sm danger"
                        type="button"
                        title="Runtime-created listeners only; configuration-bound ones are deleted by editing the configuration"
                        onClick={() => void onDelete(l)}
                      >
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
