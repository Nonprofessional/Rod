import { useState } from 'react'
import { issueTask, type Task } from '../api'
import { readSelectedFile, type SelectedFile, type VerbForm } from '../verbForms'
import { OpsecBadges } from './OpsecBadges'

// The per-verb task dialog: labeled fields instead of one raw argument
// string. The verb's OPSEC attributes ride along from the registry so a
// risky issuance says so before the button, not after. Submitting builds the
// arguments (and the staged content, for a large upload) and issues the
// task; the caller decides what feedback follows (the task log will show it
// either way -- it is engagement-wide and live).

export function TaskDialog({
  engagementId,
  implantId,
  verb,
  form,
  attributes,
  onIssued,
  onClose,
}: {
  engagementId: string
  implantId: string
  verb: string
  form: VerbForm
  attributes: Record<string, string>
  onIssued?: (task: Task) => void
  onClose: () => void
}) {
  const [values, setValues] = useState<Record<string, string>>({})
  const [files, setFiles] = useState<Record<string, SelectedFile>>({})
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const onFilePicked = async (key: string, file: globalThis.File | undefined) => {
    if (!file) return
    try {
      setFiles((current) => ({ ...current, [key]: { name: file.name, base64: '', bytes: 0 } }))
      const selected = await readSelectedFile(file)
      setFiles((current) => ({ ...current, [key]: selected }))
    } catch (e) {
      setError(String(e))
    }
  }

  const onSubmit = async (event: React.FormEvent) => {
    event.preventDefault()
    if (busy) return
    setBusy(true)
    try {
      const built = await form.build(values, files)
      const task = await issueTask(engagementId, {
        implantId,
        verb,
        arguments: built.arguments,
        content: built.content,
      })
      onIssued?.(task)
      onClose()
    } catch (e) {
      setError(String(e))
      setBusy(false)
    }
  }

  const missing = form.fields.some(
    (f) => f.required && f.type === 'file' && !files[f.key],
  ) || form.fields.some(
    (f) => f.required && f.type !== 'file' && (values[f.key] ?? '').trim() === '',
  )

  return (
    <div className="modal-backdrop" onClick={(e) => e.target === e.currentTarget && onClose()}>
      <form className="card modal" onSubmit={onSubmit}>
        <h3>
          {form.title} <code>{verb}</code>
        </h3>
        {form.fields.map((field) => (
          <label key={field.key}>
            {field.label}
            {field.type === 'file' ? (
              <input
                type="file"
                onChange={(e) => void onFilePicked(field.key, e.target.files?.[0])}
              />
            ) : (
              <input
                type={field.type === 'number' ? 'number' : 'text'}
                className={field.type === 'wide' ? 'wide' : undefined}
                placeholder={field.placeholder}
                value={values[field.key] ?? ''}
                onChange={(e) =>
                  setValues((current) => ({ ...current, [field.key]: e.target.value }))
                }
              />
            )}
            {field.help && <span className="field-help">{field.help}</span>}
          </label>
        ))}
        <div className="task-meta">
          <OpsecBadges attributes={attributes} />
        </div>
        {error && <p className="error">{error}</p>}
        <div className="inline-form">
          <button className="ghost" type="button" onClick={onClose}>
            Cancel
          </button>
          <button className="primary" type="submit" disabled={busy || missing}>
            {busy ? 'Issuing…' : 'Issue task'}
          </button>
        </div>
      </form>
    </div>
  )
}
