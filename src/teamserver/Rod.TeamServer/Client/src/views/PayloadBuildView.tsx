import { useState } from 'react'
import { type BuildPayloadResult, buildPayload } from '../api'

// The payload-build panel (//): builds an implant artifact,
// baking in the beacon profile (sleep/jitter), the kill date (self-termination),
// and the malleable transport profile (endpoint, URIs, headers, timing, envelope).
// These are baked at generation -- a live implant's profile is read-only after
// enrollment -- so OPSEC changes go through a rebuild and redeploy.

export function PayloadBuildView({
  engagementId,
}: {
  engagementId: string
}) {
  const [language, setLanguage] = useState('Go')
  const [klass, setKlass] = useState('Stage2')
  const [targetOs, setTargetOs] = useState('linux')
  const [targetArch, setTargetArch] = useState('amd64')
  const [endpoint, setEndpoint] = useState('')
  const [uriPath, setUriPath] = useState('')
  const [enrollPath, setEnrollPath] = useState('')
  const [userAgent, setUserAgent] = useState('')
  const [requestTimeoutSeconds, setRequestTimeoutSeconds] = useState('')
  const [envelope, setEnvelope] = useState('None')
  const [sleepSeconds, setSleepSeconds] = useState('30')
  const [jitterSeconds, setJitterSeconds] = useState('10')
  const [killDate, setKillDate] = useState('')
  const [result, setResult] = useState<BuildPayloadResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const num = (value: string): number | null => {
    const trimmed = value.trim()
    if (trimmed === '') return null
    const parsed = Number(trimmed)
    return Number.isFinite(parsed) ? parsed : null
  }

  const onBuild = async (event: React.FormEvent) => {
    event.preventDefault()
    setBusy(true)
    try {
      const built = await buildPayload(engagementId, {
        language: language || null,
        class: klass || null,
        targetOs: targetOs || null,
        targetArch: targetArch || null,
        endpoint: endpoint || null,
        uriPath: uriPath || null,
        enrollPath: enrollPath || null,
        userAgent: userAgent || null,
        headers: null,
        requestTimeoutSeconds: num(requestTimeoutSeconds),
        envelope: envelope !== 'None' ? envelope : null,
        sleepSeconds: num(sleepSeconds),
        jitterSeconds: num(jitterSeconds),
        killDate: killDate ? new Date(killDate).toISOString() : null,
      })
      setResult(built)
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="card">
      <h3>Build payload</h3>
      <p className="muted">
        Bake an implant with its beacon profile (sleep/jitter), kill date, and malleable transport
        profile. These are baked at generation; rebuild and redeploy to change an implant's OPSEC
        profile.
      </p>
      <form className="build-form" onSubmit={onBuild}>
        <fieldset>
          <legend>Target</legend>
          <label>
            Language
            <select value={language} onChange={(e) => setLanguage(e.target.value)}>
              <option>Go</option>
              <option>DotNet</option>
            </select>
          </label>
          <label>
            Class
            <select value={klass} onChange={(e) => setKlass(e.target.value)}>
              <option>Stage2</option>
              <option>Stager</option>
              <option>WebShell</option>
              <option>Ephemeral</option>
              <option>Pivot</option>
            </select>
          </label>
          <label>
            OS
            <input value={targetOs} onChange={(e) => setTargetOs(e.target.value)} />
          </label>
          <label>
            Arch
            <input value={targetArch} onChange={(e) => setTargetArch(e.target.value)} />
          </label>
        </fieldset>
        <fieldset>
          <legend>Beacon profile</legend>
          <label>
            Sleep (s)
            <input value={sleepSeconds} onChange={(e) => setSleepSeconds(e.target.value)} />
          </label>
          <label>
            Jitter (s)
            <input value={jitterSeconds} onChange={(e) => setJitterSeconds(e.target.value)} />
          </label>
          <label>
            Kill date
            <input type="date" value={killDate} onChange={(e) => setKillDate(e.target.value)} />
          </label>
        </fieldset>
        <fieldset>
          <legend>Malleable transport profile</legend>
          <label>
            Endpoint
            <input value={endpoint} onChange={(e) => setEndpoint(e.target.value)} placeholder="https://redirect.example.test" />
          </label>
          <label>
            URI path
            <input value={uriPath} onChange={(e) => setUriPath(e.target.value)} />
          </label>
          <label>
            Enroll path
            <input value={enrollPath} onChange={(e) => setEnrollPath(e.target.value)} />
          </label>
          <label>
            User agent
            <input value={userAgent} onChange={(e) => setUserAgent(e.target.value)} />
          </label>
          <label>
            Request timeout (s)
            <input value={requestTimeoutSeconds} onChange={(e) => setRequestTimeoutSeconds(e.target.value)} />
          </label>
          <label>
            Envelope
            <select value={envelope} onChange={(e) => setEnvelope(e.target.value)}>
              <option>None</option>
              <option>Base64</option>
            </select>
          </label>
        </fieldset>
        <button type="submit" disabled={busy}>
          Build
        </button>
      </form>
      {error && <p className="error">{error}</p>}
      {result && (
        <dl className="kv">
          <dt>Artifact</dt>
          <dd>
            <code>{result.artifactId.slice(0, 12)}</code>
          </dd>
          <dt>Fingerprint</dt>
          <dd>
            <code>{result.fingerprint.slice(0, 16)}</code>
          </dd>
          <dt>Size</dt>
          <dd>{result.size} bytes</dd>
          <dt>Built at</dt>
          <dd>{new Date(result.builtAt).toLocaleString()}</dd>
          <dt>Download</dt>
          <dd>
            <a href={`engagements/${engagementId}/payloads/${result.artifactId}`} download>
              Retrieve artifact
            </a>
          </dd>
        </dl>
      )}
    </div>
  )
}
