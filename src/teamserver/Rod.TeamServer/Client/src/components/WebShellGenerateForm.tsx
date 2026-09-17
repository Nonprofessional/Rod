import { useState } from 'react'
import { type GeneratedWebShellScript, ApiError, generateWebShellScript } from '../api'

// The web-shell half of the Build tab: render a placement script with its
// credential baked in, decoupled from any endpoint -- prepare the artifact
// here, drop it in the target's web root, register the reachable URL under
// Web shells when it exists. The picks compose: a language, and the
// encryption the channel carries -- the Rod family's AES-256-GCM seal
// under a baked 256-bit key, or the universal one-liner (the classic
// eval shape every manager drives, base64 on the wire, password-gated;
// PHP, ASPX, and classic ASP).
const LANGUAGES = [
  { id: 'php', label: 'PHP' },
  { id: 'jsp', label: 'JSP' },
  { id: 'aspx', label: 'ASPX' },
  { id: 'asp', label: 'ASP' },
]

// The languages each encryption renders today: the sealed Rod family is
// PHP and JSP; the universal one-liner is PHP, ASPX, and classic ASP.
const ONELINER_LANGUAGES = ['php', 'aspx', 'asp']

function adapterFor(language: string, encryption: string): string {
  if (encryption === 'oneliner') {
    if (language === 'aspx') return 'eval-aspx'
    if (language === 'asp') return 'eval-asp'
    return 'eval-php'
  }
  return language === 'jsp' ? 'rod-jsp' : 'rod-php'
}

const ENCRYPTIONS = [
  {
    id: 'sealed',
    label: 'AES-256-GCM sealed — own protocol, 256-bit key baked at generation',
    credentialLabel: 'Connection key',
    credentialPlaceholder: 'leave empty — a 256-bit key is generated',
  },
  {
    id: 'oneliner',
    label: 'One-liner — universal eval shape, base64 wire, password-gated',
    credentialLabel: 'Connection password',
    credentialPlaceholder: 'connection password (optional)',
  },
]

export function WebShellGenerateForm({ engagementId }: { engagementId: string }) {
  const [language, setLanguage] = useState('php')
  const [encryption, setEncryption] = useState('sealed')
  const [credential, setCredential] = useState('')
  const [generating, setGenerating] = useState(false)
  const [generated, setGenerated] = useState<GeneratedWebShellScript | null>(null)
  const [copied, setCopied] = useState<'script' | 'credential' | null>(null)
  const [error, setError] = useState<string | null>(null)

  const shape = ENCRYPTIONS.find((e) => e.id === encryption) ?? ENCRYPTIONS[0]
  const sealed = shape.id === 'sealed'

  const onGenerate = async (event: React.FormEvent) => {
    event.preventDefault()
    if (generating) return
    setGenerating(true)
    try {
      const script = await generateWebShellScript(
        engagementId,
        adapterFor(language, encryption),
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
            Language
            <select
              value={language}
              onChange={(e) => {
                setLanguage(e.target.value)
                // The one-liner family renders PHP, ASPX, and classic ASP
                // today; picking another language falls back to the sealed
                // shape.
                if (!ONELINER_LANGUAGES.includes(e.target.value) && encryption === 'oneliner')
                  setEncryption('sealed')
              }}
              title="The language the placed script is written in. The wire protocol is the same shape in every language this list grows."
            >
              {LANGUAGES.map((l) => (
                <option key={l.id} value={l.id}>
                  {l.label}
                </option>
              ))}
            </select>
          </label>
          <label>
            Encryption
            <select
              value={encryption}
              onChange={(e) => setEncryption(e.target.value)}
              title="The channel's protection. Sealed is this tool's own protocol: every request and answer wrapped as AES-256-GCM under a 256-bit key baked into the script, so nothing on the wire names the command or the output (PHP needs the openssl extension; JSP needs javax.crypto, which every container ships). The one-liner is the universal eval shape -- the placed script any manager drives, base64 on the wire, gated by the connection password."
            >
              {ENCRYPTIONS.map((e) => (
                <option
                  key={e.id}
                  value={e.id}
                  disabled={e.id === 'oneliner' && !ONELINER_LANGUAGES.includes(language)}
                >
                  {e.label}
                  {e.id === 'oneliner' && !ONELINER_LANGUAGES.includes(language)
                    ? ' (PHP / ASPX / ASP)'
                    : ''}
                </option>
              ))}
            </select>
          </label>
          <label>
            {shape.credentialLabel}
            <input
              value={credential}
              onChange={(e) => setCredential(e.target.value)}
              placeholder={shape.credentialPlaceholder}
              title={
                sealed
                  ? 'The base64 of a 256-bit key, baked into the script and sealing both directions as AES-256-GCM. Leave empty and one is generated -- registration under Web shells needs it, and the Payloads detail shows it later.'
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
            <code>{sealed ? 'key' : 'password'}</code>
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
