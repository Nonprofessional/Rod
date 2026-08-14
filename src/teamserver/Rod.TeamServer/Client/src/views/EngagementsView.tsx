import { useCallback, useEffect, useState } from 'react'
import { type Engagement, createEngagement, listEngagements } from '../api'

// The engagements list (roadmap M1.5): enumerate every engagement the operator
// can reach and create a new one. Drilling into an engagement hands off to the
// engagement detail view, which carries the full capability surface (M11.1).
// The engagement's owner is the authenticated operator, resolved server-side,
// so this view carries no identity of its own.

export function EngagementsView() {
  const [items, setItems] = useState<Engagement[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [loading, setLoading] = useState(true)
  const [name, setName] = useState('')

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
      await createEngagement({ name })
      setName('')
      await refresh()
    } catch (e) {
      setError(String(e))
    }
  }

  return (
    <section>
      <h2>Engagements</h2>
      {error && <p className="error">{error}</p>}
      <form className="inline-form" onSubmit={onCreate}>
        <input
          placeholder="Engagement name"
          value={name}
          onChange={(e) => setName(e.target.value)}
          required
        />
        <button type="submit" disabled={busy}>
          Create
        </button>
      </form>

      {loading ? (
        <p className="muted">Loading engagements…</p>
      ) : items.length === 0 ? (
        <p className="muted">No engagements yet.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Name</th>
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
                <td>{e.ownerHandle || e.ownerId.slice(0, 8)}</td>
                <td>{new Date(e.createdAt).toLocaleString()}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  )
}
