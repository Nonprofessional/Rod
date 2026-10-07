import { useMemo, useState } from 'react'
import { type EngagementTask, createTaskSnippet } from '../api'

// The snippet save dialog: the console's "save what I just typed" flow. The
// implant's recent transcript reads as a checklist -- newest on top, because
// the lines just issued are the ones being saved -- and the checked lines
// become the snippet's steps in issue order, whichever order they were
// checked in. A snippet carries no target: it runs against whichever
// implant the operator picks at run time, so one saved sequence serves the
// whole fleet.

const SHOWN = 30

export function SnippetSaveDialog({
  engagementId,
  tasks,
  onClose,
}: {
  engagementId: string
  // The console's loaded transcript; the dialog reads the most recent
  // tasks off it and orders steps chronologically whatever their display
  // order.
  tasks: EngagementTask[]
  onClose: () => void
}) {
  const recent = useMemo(
    () => [...tasks].sort((a, b) => a.createdAt.localeCompare(b.createdAt)).slice(-SHOWN),
    [tasks],
  )
  const [name, setName] = useState('')
  const [checked, setChecked] = useState<Set<string>>(new Set())
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const toggle = (taskId: string) => {
    setChecked((current) => {
      const next = new Set(current)
      if (next.has(taskId)) next.delete(taskId)
      else next.add(taskId)
      return next
    })
  }

  const checkedCount = checked.size

  const onSave = async () => {
    const trimmed = name.trim()
    if (!trimmed || checkedCount === 0 || busy) return
    // Steps in issue order (chronological), arguments verbatim.
    const steps = recent
      .filter((t) => checked.has(t.taskId))
      .map((t) => ({ verb: t.verb, arguments: t.arguments }))
    setBusy(true)
    try {
      await createTaskSnippet(engagementId, trimmed, steps)
      onClose()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="modal-backdrop" onClick={(e) => e.target === e.currentTarget && onClose()}>
      <div className="card modal">
        <div className="inline-form">
          <h3 style={{ marginRight: 'auto' }}>Save task snippet</h3>
          <button className="ghost" onClick={onClose}>
            Close
          </button>
        </div>
        <p className="muted">
          Pick the transcript lines to save as one named sequence. The snippet carries no target --
          it runs against whichever implant you pick, from the palette (Ctrl+K) or a console.
        </p>
        <label>
          Name
          <input
            className="wide"
            placeholder="triage-sweep"
            value={name}
            onChange={(e) => setName(e.target.value)}
            autoFocus
          />
        </label>
        <div className="snippet-pick-list">
          {recent
            .slice()
            .reverse()
            .map((task) => (
              <label key={task.taskId} className="snippet-pick-row">
                <input
                  type="checkbox"
                  checked={checked.has(task.taskId)}
                  onChange={() => toggle(task.taskId)}
                />
                <span className="t">{new Date(task.createdAt).toLocaleTimeString()}</span>
                <code>{task.verb}</code>
                <span className="args">{task.arguments || '—'}</span>
              </label>
            ))}
          {recent.length === 0 && (
            <p className="muted">No tasks on this implant yet -- issue the sequence first, then save it.</p>
          )}
        </div>
        {error && <p className="error">{error}</p>}
        <div className="inline-form">
          <span className="muted" style={{ marginRight: 'auto' }}>
            {checkedCount} step{checkedCount === 1 ? '' : 's'} selected, saved in issue order
          </span>
          <button
            className="primary"
            disabled={!name.trim() || checkedCount === 0 || busy}
            onClick={() => void onSave()}
          >
            Save
          </button>
        </div>
      </div>
    </div>
  )
}
