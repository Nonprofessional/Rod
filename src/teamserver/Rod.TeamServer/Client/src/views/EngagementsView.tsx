import { useCallback, useEffect, useState } from 'react'
import {
  type Engagement,
  editEngagement,
  createEngagement,
  fetchEvidencePackageBlob,
  freezeEngagement,
  listEngagements,
  retireEngagement,
  unfreezeEngagement,
} from '../api'
import { Icon } from '../components/Icons'

// The engagements list: enumerate every engagement the operator can reach,
// create a new one, edit an engagement's working record (name + description),
// and drive the close-out arc (freeze -> export evidence -> retire) from the
// row actions. A mistaken freeze is reversible until retirement (unfreeze);
// retire is the terminal step. Drilling into an engagement hands off to the
// detail view, which carries the full capability surface.
//
// "Delete" here is the close-out, not an erasure: the audit trail is the
// engagement's durable account, so retirement takes the engagement out of
// service while its evidence stays readable. A retired record is sealed --
// editing is refused server-side and hidden here.

function statusOf(e: Engagement): { label: string; tone: string } {
  if (e.retiredAt) return { label: 'retired', tone: 'status retired' }
  if (e.frozenAt) return { label: 'frozen', tone: 'status' }
  return { label: 'open', tone: 'status completed' }
}

export function EngagementsView() {
  const [items, setItems] = useState<Engagement[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [loading, setLoading] = useState(true)
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [editing, setEditing] = useState<Engagement | null>(null)
  const [editName, setEditName] = useState('')
  const [editDescription, setEditDescription] = useState('')

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

  const beginEdit = (e: Engagement) => {
    setEditing(e)
    setEditName(e.name)
    setEditDescription(e.description ?? '')
  }

  const onSaveEdit = async (event: React.FormEvent) => {
    event.preventDefault()
    if (!editing) return
    try {
      await editEngagement(editing.engagementId, {
        name: editName,
        description: editDescription || null,
      })
      setEditing(null)
      await refresh()
    } catch (err) {
      setError(String(err))
    }
  }

  const onFreeze = async (e: Engagement) => {
    if (!window.confirm(`Freeze "${e.name}"? It accepts no new tasking or deployments from now on.`))
      return
    try {
      await freezeEngagement(e.engagementId)
      await refresh()
    } catch (err) {
      setError(String(err))
    }
  }

  const onUnfreeze = async (e: Engagement) => {
    if (!window.confirm(`Unfreeze "${e.name}"? It reopens for tasking and deployments.`))
      return
    try {
      await unfreezeEngagement(e.engagementId)
      await refresh()
    } catch (err) {
      setError(String(err))
    }
  }

  // The export is a POST-only close-out action, so the download posts and
  // saves the returned ZIP itself -- a plain link would GET a POST-only route.
  const onDownloadEvidence = async (e: Engagement) => {
    try {
      const blob = await fetchEvidencePackageBlob(e.engagementId)
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = `rod-evidence-${e.engagementId}.zip`
      link.click()
      // Revoke after the click has been handed to the browser: revoking in the
      // same tick aborts the download in some browsers.
      setTimeout(() => URL.revokeObjectURL(url), 30_000)
    } catch (err) {
      setError(String(err))
    }
  }

  const onRetire = async (e: Engagement) => {
    if (
      !window.confirm(
        `Retire "${e.name}"? This completes the close-out and is terminal: the record is sealed and stays as evidence.`,
      )
    )
      return
    try {
      await retireEngagement(e.engagementId)
      await refresh()
    } catch (err) {
      setError(String(err))
    }
  }

  return (
    <section>
      <div className="page-head">
        <h2>Engagements</h2>
        <span className="muted">Scoped workspaces; everything else lives inside one.</span>
      </div>
      {error && <p className="error">{error}</p>}
      <form className="card create-form" onSubmit={onCreate}>
        <div className="create-form-row">
          <input
            placeholder="Engagement name"
            value={name}
            onChange={(e) => setName(e.target.value)}
            required
          />
          <button className="primary" type="submit" disabled={busy}>
            Create
          </button>
        </div>
        {/* A description is working notes: free text the crew keeps on the
            engagement, so it gets a roomy multi-line field, not one cramped
            line. */}
        <textarea
          rows={3}
          placeholder="Description (optional) -- scope, customer, target environment, working notes"
          value={description}
          onChange={(e) => setDescription(e.target.value)}
        />
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
                <th>Status</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {items.map((e) => {
                const status = statusOf(e)
                return (
                  <tr key={e.engagementId} className={e.retiredAt ? 'row-dim' : undefined}>
                    <td>
                      <a href={`#/engagements/${e.engagementId}`}>{e.name}</a>
                    </td>
                    <td className="muted">{e.description ?? ''}</td>
                    <td>{e.ownerHandle || e.ownerId.slice(0, 8)}</td>
                    <td>{new Date(e.createdAt).toLocaleString()}</td>
                    <td>
                      <span className={status.tone}>{status.label}</span>
                    </td>
                    <td className="row-actions">
                      {!e.retiredAt && (
                        <button className="sm ghost" onClick={() => beginEdit(e)}>
                          Edit
                        </button>
                      )}
                      {!e.frozenAt && !e.retiredAt && (
                        <button className="sm ghost" onClick={() => void onFreeze(e)}>
                          Freeze
                        </button>
                      )}
                      {e.frozenAt && !e.retiredAt && (
                        <>
                          <button className="sm ghost" onClick={() => void onUnfreeze(e)}>
                            Unfreeze
                          </button>
                          <button className="sm ghost" onClick={() => void onDownloadEvidence(e)}>
                            Evidence
                          </button>
                          <button className="sm danger" onClick={() => void onRetire(e)}>
                            Retire
                          </button>
                        </>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}

      {editing && (
        <div className="modal-backdrop" onClick={() => setEditing(null)}>
          <form
            className="card modal edit-engagement"
            onClick={(e) => e.stopPropagation()}
            onSubmit={onSaveEdit}
          >
            <h3>Edit engagement</h3>
            <label>
              Name
              <input value={editName} onChange={(e) => setEditName(e.target.value)} required />
            </label>
            <label>
              Description
              <textarea
                rows={5}
                placeholder="Scope, customer, target environment, working notes"
                value={editDescription}
                onChange={(e) => setEditDescription(e.target.value)}
              />
            </label>
            <div className="inline-form">
              <button className="primary" type="submit" disabled={busy}>
                Save
              </button>
              <button className="ghost" type="button" onClick={() => setEditing(null)}>
                Cancel
              </button>
            </div>
          </form>
        </div>
      )}
    </section>
  )
}
