import { useState } from 'react'
import { type GeneratedWebShellScript, ApiError, generateWebShellScript } from '../api'

// The web-shell half of the Build tab: render a placement script with its
// credential baked in, decoupled from any endpoint -- prepare the artifact
// here, drop it in the target's web root, register the reachable URL under
// Web shells when it exists. The Rod family is the default: its own
// one-line script sealed end to end as AES-256-GCM under a 256-bit key
// baked at generation; the AntSword eval family stays selectable as
// interop for scripts placed for other managers.
const ADAPTERS = [
  {
    id: 'rod-php',
    label: 'Rod (PHP) — own protocol: one line, AES-256-GCM sealed under a baked 256-bit key',
    credentialLabel: 'Connection key',
    credentialPlaceholder: 'leave empty — a 256-bit key is generated',
  },
  {
    id: 'antsword-php',
    label: 'AntSword eval (PHP) — interop: base64 shape shells placed for other managers speak',
    credentialLabel: 'Connection password',
    credentialPlaceholder: 'connection password (optional)',
  },
]

export function WebShellGenerateForm({ engagementId }: { engagementId: string }) {
  const [adapterId, setAdapterId] = useState('rod-php')
  const [credential, setCredential] = useState('')
  const [generating, setGenerating] = useState(false)
  const [generated, setGenerated] = useState<GeneratedWebShellScript | null>(null)
  const [copied, setCopied] = useState<'script' | 'credential' | null>(null)
  const [error, setError] = useState<string | null>(null)

  const adapter = ADAPTERS.find((a) => a.id === adapterId) ?? ADAPTERS[0]
  const isRod = adapter.id.startsWith('rod-')

  const onGenerate = async (event: React.FormEvent) => {
    event.preventDefault()
    if (generating) return
    setGenerating(true)
    try {
      const script = await generateWebShellScript(
        engagementId,
        adapterId,
        credential || undefined,
      )
      setGenerated(script)
      setCredential('')
      setError(null)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally {
      setGenerating(false)
    }
  }

  const copy = async (what: 'script' | 'credential', text: string) => {
    try {
      await navigator.clipboard.writeText(text)
      setCopied(what)
      window.setTimeout(() => setCopied(null), 1500)
    } catch {
      // Clipboard permission denied: the text stays selectable.
    }
  }

  return (
    <>
      <form className="build-form" onSubmit={onGenerate}>
        <fieldset>
          <legend>Script</legend>
          <label>
            Adapter
            <select
              value={adapterId}
              onChange={(e) => setAdapterId(e.target.value)}
              title="The protocol family the script speaks. The Rod family is this tool's own: a one-line PHP script whose 256-bit key seals every request and answer as AES-256-GCM. The AntSword family is the classic eval shape, kept so a script placed for another manager still registers."
            >
              {ADAPTERS.map((a) => (
                <option key={a.id} value={a.id}>
                  {a.label}
                </option>
              ))}
            </select>
          </label>
          <label>
            {adapter.credentialLabel}
            <input
              value={credential}
              onChange={(e) => setCredential(e.target.value)}
              placeholder={adapter.credentialPlaceholder}
              title={
                isRod
                  ? 'The base64 of a 256-bit key, baked into the script and sealing both directions as AES-256-GCM. Leave empty and one is generated -- copy it when placing the script, registration needs it.'
                  : 'The POST parameter the eval one-liner answers to. Leave empty and one is generated.'
              }
            />
          </label>
        </fieldset>
        <button className="primary" type="submit" disabled={generating}>
          Generate script
        </button>
      </form>
      {error && <p className="error">{error}</p>}

      {generated && (
        <div className="upgrade-panel">
          <p>
            Generated and stored as payload <code>{generated.payloadId.slice(0, 8)}</code> (also
            under Payloads). Place the script in the target's web root, then register the reachable
            URL under Web shells with this credential:
          </p>
          <div className="upgrade-launcher">
            <code>{isRod ? 'key' : 'password'}</code>
            <code className="upgrade-command">{generated.password}</code>
            <button
              className="ghost sm"
              onClick={() => void copy('credential', generated.password)}
            >
              {copied === 'credential' ? 'Copied' : 'Copy'}
            </button>
          </div>
          <div className="upgrade-launcher">
            <code>script</code>
            <code className="upgrade-command">{generated.script}</code>
            <button className="ghost sm" onClick={() => void copy('script', generated.script)}>
              {copied === 'script' ? 'Copied' : 'Copy'}
            </button>
            <a
              className="download-link"
              href={`engagements/${engagementId}/payloads/${generated.payloadId}`}
              download
            >
              Download
            </a>
          </div>
        </div>
      )}
    </>
  )
}
