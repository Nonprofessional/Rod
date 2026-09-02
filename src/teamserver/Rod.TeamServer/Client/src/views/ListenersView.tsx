import { useCallback, useEffect, useState } from 'react'
import {
  type Engagement,
  type ListenerSummary,
  createListener,
  deleteListener,
  listEngagements,
  listListeners,
  repointListener,
} from '../api'
import { Icon } from '../components/Icons'
import { StatusBadge } from '../components/StatusBadge'

// The listeners / redirector panel: the bound C2 ingress, each with the socket
// it opens (bind) and the public endpoint implants dial (typically a
// redirector). A listener created here belongs to one engagement -- it is that
// engagement's private ingress, and enrollment through it accepts only that
// engagement's tokens. The definition is persisted, so a restart rebinds it
// with the same id; repointing swaps the public endpoint at runtime and the
// definition follows. The startup-configuration tier (the operator front and
// any deliberately shared ingress) shows no engagement and cannot be deleted
// here -- the configuration owns those.

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
  const [engagements, setEngagements] = useState<Engagement[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [newEndpoint, setNewEndpoint] = useState<Record<string, string>>({})

  // The create form's working state; the transport select drives the bind
  // placeholder so the accepted shape is visible where it is entered.
  const [name, setName] = useState('')
  const [transport, setTransport] = useState('http')
  const [bindAddress, setBindAddress] = useState('')
  const [publicEndpoint, setPublicEndpoint] = useState('')
  const [engagementId, setEngagementId] = useState('')
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

  // The create form's engagement picker: the open engagements a listener may
  // belong to. Loaded beside the roster so a fresh engagement is one refresh
  // away.
  useEffect(() => {
    void (async () => {
      try {
        const all = await listEngagements()
        setEngagements(all.filter((e) => !e.retiredAt))
      } catch {
        // The roster still renders; the picker stays empty until a retry.
      }
    })()
  }, [listeners])

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
        engagementId,
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
        the backend. A listener created here belongs to one engagement and is persisted -- a restart
        rebinds it; enrollment through it accepts only that engagement's tokens. Listeners without
        an engagement are the startup configuration's shared tier.
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
        <select
          value={engagementId}
          onChange={(e) => setEngagementId(e.target.value)}
          aria-label="Engagement"
          title="The engagement this listener answers for"
          required
        >
          <option value="" disabled>
            engagement…
          </option>
          {engagements.map((e) => (
            <option key={e.engagementId} value={e.engagementId}>
              {e.name}
            </option>
          ))}
        </select>
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
                <th>Engagement</th>
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
                    {l.engagementId ? (
                      <code title={l.engagementId}>
                        {engagements.find((e) => e.engagementId === l.engagementId)?.name ??
                          l.engagementId.slice(0, 8)}
                      </code>
                    ) : (
                      <span className="muted">shared</span>
                    )}
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
