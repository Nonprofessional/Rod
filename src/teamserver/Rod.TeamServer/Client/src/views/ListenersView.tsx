import { useCallback, useEffect, useState } from 'react'
import { type ListenerSummary, listListeners, repointListener } from '../api'
import { Icon } from '../components/Icons'
import { StatusBadge } from '../components/StatusBadge'

// The listeners / redirector panel: the bound C2 ingress, each
// with the socket it opens (bind) and the public endpoint implants dial
// (typically a redirector). Repointing swaps that public endpoint at runtime --
// a burned redirector is replaced without backend change. Global infrastructure,
// not engagement-scoped.

export function ListenersView() {
  const [listeners, setListeners] = useState<ListenerSummary[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [newEndpoint, setNewEndpoint] = useState<Record<string, string>>({})

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

  return (
    <div className="card">
      <h3>Listeners</h3>
      <p className="muted">
        C2 ingress. <strong>Name</strong> is the label you gave the entry in the{' '}
        <code>Listeners</code> configuration. <strong>Bind</strong> is the socket this server
        opens; <strong>public endpoint</strong> is the address baked payloads dial -- usually your
        redirector. The two are decoupled on purpose: repoint swaps a burned front without
        touching the backend.
      </p>
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
