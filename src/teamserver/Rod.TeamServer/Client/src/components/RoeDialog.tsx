import { useState } from 'react'
import { applyRoe, type RoeProfile } from '../api'

// The engagement's ROE editor (architecture.md Sec 9's guardrails): the
// three allow-lists as one-line-per-entry text areas, prefilled from the
// profile in force. Each list empty means unrestricted on that dimension --
// the dialog says so per field, because "empty is the widest scope" is the
// one reading an operator must not get backwards. Applying is audited
// server-side (RoeUpdated); the answer refreshes the caller's engagement
// copy, since every surface that displays scope reads it from there.
//
// Lives on the recon tab beside the scope note -- the recon workbench is the
// ROE-gated surface -- but the profile gates tasking everywhere, not scans
// alone.

const FIELDS: readonly { key: keyof RoeProfile; label: string; help: string; placeholder: string }[] = [
  {
    key: 'permittedVerbs',
    label: 'Permitted verbs',
    help: 'Exact verbs or namespace wildcards (recon.*, persist.install). Empty: every verb taskable.',
    placeholder: 'shell.exec\nrecon.*\nfile.push',
  },
  {
    key: 'permittedImplants',
    label: 'Permitted implants',
    help: 'Exact implant ids. Empty: every implant in the engagement taskable.',
    placeholder: 'one id per line',
  },
  {
    key: 'permittedTargets',
    label: 'Permitted targets',
    help: 'Hostnames, IP literals, or CIDR blocks (10.0.0.0/16) -- the recon workbench gate. Empty: any target addressable.',
    placeholder: 'web01.example.com\n10.0.0.0/16',
  },
]

export function RoeDialog({
  engagementId,
  profile,
  onApplied,
  onClose,
}: {
  engagementId: string
  profile: RoeProfile
  onApplied: (profile: RoeProfile) => void
  onClose: () => void
}) {
  const [verbs, setVerbs] = useState(profile.permittedVerbs.join('\n'))
  const [implants, setImplants] = useState(profile.permittedImplants.join('\n'))
  const [targets, setTargets] = useState(profile.permittedTargets.join('\n'))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const drafts: Record<keyof RoeProfile, string> = {
    permittedVerbs: verbs,
    permittedImplants: implants,
    permittedTargets: targets,
  }
  const setDrafts: Record<keyof RoeProfile, (value: string) => void> = {
    permittedVerbs: setVerbs,
    permittedImplants: setImplants,
    permittedTargets: setTargets,
  }
  const entries = (text: string) =>
    text
      .split('\n')
      .map((line) => line.trim())
      .filter(Boolean)

  const unrestricted =
    entries(verbs).length === 0 && entries(implants).length === 0 && entries(targets).length === 0

  const onSubmit = async (event: React.FormEvent) => {
    event.preventDefault()
    if (busy) return
    setBusy(true)
    try {
      const applied = await applyRoe(engagementId, {
        permittedVerbs: entries(verbs),
        permittedImplants: entries(implants),
        permittedTargets: entries(targets),
      })
      onApplied(applied)
      onClose()
    } catch (e) {
      setError(String(e))
      setBusy(false)
    }
  }

  return (
    <div className="modal-backdrop" onClick={(e) => e.target === e.currentTarget && onClose()}>
      <form className="card modal" onSubmit={onSubmit}>
        <h3>
          Engagement scope <code>ROE</code>
        </h3>
        {FIELDS.map((field) => (
          <label key={field.key}>
            {field.label}
            <textarea
              rows={3}
              spellCheck={false}
              placeholder={field.placeholder}
              value={drafts[field.key]}
              onChange={(e) => setDrafts[field.key](e.target.value)}
            />
            <span className="field-help">{field.help}</span>
          </label>
        ))}
        {unrestricted && (
          <p className="muted">
            All three lists empty: unrestricted -- every verb, every implant, any target. Applying
            this widens any previously narrowed scope; the change is audited.
          </p>
        )}
        {error && <p className="error">{error}</p>}
        <div className="inline-form">
          <button className="ghost" type="button" onClick={onClose}>
            Cancel
          </button>
          <button className="primary" type="submit" disabled={busy}>
            {busy ? 'Applying…' : 'Apply scope'}
          </button>
        </div>
      </form>
    </div>
  )
}
