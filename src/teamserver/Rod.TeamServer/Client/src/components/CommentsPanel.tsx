import { useEffect, useState } from 'react'
import {
  type CommentRow,
  type CommentSubjectKind,
  createComment,
  deleteComment,
  editComment,
  listComments,
} from '../api'

// Renders @task-<id> and $cred-<id> tokens as chips so a reference reads as
// a link, not as a wall of guid. The server resolved the reference at
// publish -- nothing here needs to look anything up.
function CommentBodyText({ body }: { body: string }) {
  const parts = body.split(/(@task-[\da-fA-F-]+|\$cred-[\da-fA-F-]+)/g)
  return (
    <>
      {parts.map((part, i) =>
        part.startsWith('@task-') || part.startsWith('$cred-') ? (
          <code key={i} className="ref-token" title={part}>
            {part.startsWith('@task-') ? 'task' : 'cred'}·{part.slice(part.length - 4)}
          </code>
        ) : (
          <span key={i}>{part}</span>
        ),
      )}
    </>
  )
}

// The mutable process-knowledge panel every entity detail carries
// (architecture.md Sec 15.3, ADR-005): comments on one subject, editable by
// their author within the window, riding the timeline but never the audit
// chain. The `tick` prop is the engagement's live-stream beat -- a peer's
// comment lands here the moment it lands.
export function CommentsPanel({
  engagementId,
  subjectKind,
  subjectId,
  tick,
}: {
  engagementId: string
  subjectKind: CommentSubjectKind
  subjectId: string
  tick?: number
}) {
  const [comments, setComments] = useState<CommentRow[]>([])
  const [draft, setDraft] = useState('')
  const [editing, setEditing] = useState<{ id: string; body: string } | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    void (async () => {
      try {
        setComments(await listComments(engagementId, subjectKind, subjectId))
        setError(null)
      } catch (e) {
        setError(String(e))
      }
    })()
  }, [engagementId, subjectKind, subjectId, tick])

  const run = async (action: () => Promise<unknown>) => {
    if (busy) return
    setBusy(true)
    try {
      await action()
      setComments(await listComments(engagementId, subjectKind, subjectId))
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="notes-panel">
      <ul className="notes-list">
        {comments.length === 0 ? (
          <li className="muted">No comments yet -- process knowledge lives here, not in the trail.</li>
        ) : (
          comments.map((c) => (
            <li key={c.id}>
              <span className="notes-meta">
                <code>{c.authorId.slice(0, 8)}</code> {new Date(c.createdAt).toLocaleString()}
                {c.editedAt ? ' (edited)' : ''}
                {c.editable && (
                  <>
                    {' '}
                    <button
                      className="chip-x"
                      title="Edit within the window"
                      onClick={() => setEditing({ id: c.id, body: c.body })}
                    >
                      edit
                    </button>
                    <button
                      className="chip-x"
                      title="Delete within the window"
                      onClick={() =>
                        void run(async () => {
                          await deleteComment(engagementId, c.id)
                        })
                      }
                    >
                      delete
                    </button>
                  </>
                )}
              </span>
              {editing?.id === c.id ? (
                <form
                  className="task-form"
                  onSubmit={(e) => {
                    e.preventDefault()
                    const body = editing.body.trim()
                    if (!body) return
                    void run(async () => {
                      await editComment(engagementId, editing.id, body)
                      setEditing(null)
                    })
                  }}
                >
                  <input
                    className="wide"
                    value={editing.body}
                    onChange={(e) => setEditing({ ...editing, body: e.target.value })}
                  />
                  <button className="sm" type="submit" disabled={busy || !editing.body.trim()}>
                    Save
                  </button>
                  <button className="sm" type="button" onClick={() => setEditing(null)}>
                    Cancel
                  </button>
                </form>
              ) : (
                <CommentBodyText body={c.body} />
              )}
            </li>
          ))
        )}
      </ul>
      <form
        className="task-form"
        onSubmit={(e) => {
          e.preventDefault()
          const body = draft.trim()
          if (!body) return
          void run(async () => {
            await createComment(engagementId, subjectKind, subjectId, body)
            setDraft('')
          })
        }}
      >
        <input
          className="wide"
          placeholder="comment (@task-… / $cred-… resolve on save)"
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
        />
        <button className="sm" type="submit" disabled={busy || !draft.trim()}>
          Comment
        </button>
      </form>
      {error && <div className="error">{error}</div>}
    </div>
  )
}
