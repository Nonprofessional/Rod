import { useCallback, useEffect, useState } from 'react'
import { type Engagement, createEngagement, listEngagements } from '../api'
import { Icon } from '../components/Icons'

// The engagements list: enumerate every engagement the operator
// can reach and create a new one. Drilling into an engagement hands off to the
// engagement detail view, which carries the full capability surface.
// The engagement's owner is the authenticated operator, resolved server-side,
// so this view carries no identity of its own.

export function EngagementsView() {
  const [items, setItems] = useState<Engagement[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [loading, setLoading] = useState(true)
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')

  const refresh = useCallback(async () => {
    setBusy(true)
    try {
      setItems(await listEngagements())
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const onCreate = async (event: React.FormEvent) => {
    event.preventDefault()
    try {
      await createEngagement({ name, description: description || undefined })
      setName('')
      setDescription('')
      await refresh()
    } catch (e) {
      setError(String(e))
    }
  }

  return (
    <section>
      <div className="page-head">
        <h2>Engagements</h2>
        <span className="muted">Scoped workspaces; everything else lives inside one.</span>
      </div>
      {error && <p className="error">{error}</p>}
      <form className="card inline-form" onSubmit={onCreate}>
        <input
          placeholder="Engagement name"
          value={name}
          onChange={(e) => setName(e.target.value)}
          required
        />
        <input
          className="wide"
          placeholder="Description (optional)"
          value={description}
          onChange={(e) => setDescription(e.target.value)}
        />
        <button className="primary" type="submit" disabled={busy}>
          Create
        </button>
      </form>

      {loading ? (
        <div className="empty">
          <span className="spinner" />
          Loading engagements…
        </div>
      ) : items.length === 0 ? (
        <div className="empty">
          <Icon name="globe" />
          No engagements yet -- create one to begin.
        </div>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Name</th>
                <th>Description</th>
                <th>Owner</th>
                <th>Created</th>
              </tr>
            </thead>
            <tbody>
              {items.map((e) => (
                <tr key={e.engagementId}>
                  <td>
                    <a href={`#/engagements/${e.engagementId}`}>{e.name}</a>
                  </td>
                  <td className="muted">{e.description ?? ''}</td>
                  <td>{e.ownerHandle || e.ownerId.slice(0, 8)}</td>
                  <td>{new Date(e.createdAt).toLocaleString()}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}
