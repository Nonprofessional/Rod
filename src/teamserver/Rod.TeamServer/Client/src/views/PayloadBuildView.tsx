import { useCallback, useEffect, useRef, useState } from 'react'
import { type BuildJob, enqueueBuildJob, listBuildJobs } from '../api'
import { Icon } from '../components/Icons'
import { StatusBadge } from '../components/StatusBadge'

// The payload-build panel: builds an implant artifact, baking in the beacon
// profile (mode, sleep/jitter), the kill date (self-termination), and the
// malleable transport profile (endpoint, fallback endpoints walked when the
// primary burns, URIs, headers, timing, envelope). These are baked at
// generation -- a live implant's profile is read-only after enrollment -- so
// OPSEC changes go through a rebuild and redeploy.
//
// The build itself runs as a server-side job: a real toolchain takes the kind
// of time an open request and a browser refresh must not own. Submitting
// queues the job and returns immediately; the recent-builds list below is the
// durable view of every job (fetched on mount, polled while anything runs), so
// leaving the page, refreshing it, or losing the connection never loses a
// build -- the finished artifact waits in the list with its download link.

function elapsed(job: BuildJob): string {
  const start = new Date(job.startedAt ?? job.requestedAt).getTime()
  const end = job.completedAt ? new Date(job.completedAt).getTime() : Date.now()
  const seconds = Math.max(0, Math.round((end - start) / 1000))
  return seconds >= 60 ? `${Math.floor(seconds / 60)}m ${seconds % 60}s` : `${seconds}s`
}

export function PayloadBuildView({
  engagementId,
}: {
  engagementId: string
}) {
  const [language, setLanguage] = useState('DotNet')
  const [klass, setKlass] = useState('Stage2')
  const [targetOs, setTargetOs] = useState('linux')
  const [targetArch, setTargetArch] = useState('amd64')
  const [endpoint, setEndpoint] = useState('')
  const [fallbackEndpoints, setFallbackEndpoints] = useState('')
  const [uriPath, setUriPath] = useState('')
  const [enrollPath, setEnrollPath] = useState('')
  const [userAgent, setUserAgent] = useState('')
  const [requestTimeoutSeconds, setRequestTimeoutSeconds] = useState('')
  const [envelope, setEnvelope] = useState('None')
  const [mode, setMode] = useState('stream')
  const [sleepSeconds, setSleepSeconds] = useState('30')
  const [jitterSeconds, setJitterSeconds] = useState('10')
  const [killDate, setKillDate] = useState('')
  const [jobs, setJobs] = useState<BuildJob[]>([])
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const num = (value: string): number | null => {
    const trimmed = value.trim()
    if (trimmed === '') return null
    const parsed = Number(trimmed)
    return Number.isFinite(parsed) ? parsed : null
  }

  // The fallback list is entered comma-separated, in walk order.
  const fallbacks = (value: string): string[] | null => {
    const list = value.split(',').map((f) => f.trim()).filter((f) => f !== '')
    return list.length > 0 ? list : null
  }

  const refreshJobs = useCallback(async () => {
    try {
      setJobs(await listBuildJobs(engagementId))
    } catch {
      // Keep the last known list; the next poll retries.
    }
  }, [engagementId])

  useEffect(() => {
    void refreshJobs()
  }, [refreshJobs])

  const active = jobs.some((j) => j.state === 'queued' || j.state === 'running')

  // Poll only while a job is in flight -- the list is otherwise quiet, and a
  // completed build changes nothing until the next submit.
  const timer = useRef<number | null>(null)
  useEffect(() => {
    if (!active) {
      if (timer.current !== null) {
        window.clearInterval(timer.current)
        timer.current = null
      }
      return
    }
    timer.current = window.setInterval(() => void refreshJobs(), 2000)
    return () => {
      if (timer.current !== null) {
        window.clearInterval(timer.current)
        timer.current = null
      }
    }
  }, [active, refreshJobs])

  const onBuild = async (event: React.FormEvent) => {
    event.preventDefault()
    setSubmitting(true)
    try {
      await enqueueBuildJob(engagementId, {
        language: language || null,
        class: klass || null,
        targetOs: targetOs || null,
        targetArch: targetArch || null,
        endpoint: endpoint || null,
        fallbackEndpoints: fallbacks(fallbackEndpoints),
        uriPath: uriPath || null,
        enrollPath: enrollPath || null,
        userAgent: userAgent || null,
        headers: null,
        requestTimeoutSeconds: num(requestTimeoutSeconds),
        envelope: envelope !== 'None' ? envelope : null,
        mode: mode !== 'stream' ? mode : null,
        sleepSeconds: num(sleepSeconds),
        jitterSeconds: num(jitterSeconds),
        killDate: killDate ? new Date(killDate).toISOString() : null,
      })
      setError(null)
      await refreshJobs()
    } catch (e) {
      setError(String(e))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="card">
      <h3>Build payload</h3>
      <p className="muted">
        Bake an implant with its beacon profile (sleep/jitter), kill date, and malleable transport
        profile. These are baked at generation; rebuild and redeploy to change an implant's OPSEC
        profile. The build runs as a background job -- watch it finish under Recent builds.
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
            {/* The build unit maps these onto a runtime identifier and
                refuses anything outside the supported set, so the form
                offers exactly that set instead of free text a typo can
                waste a build on. */}
            <select value={targetOs} onChange={(e) => setTargetOs(e.target.value)}>
              <option value="linux">linux</option>
              <option value="windows">windows</option>
              <option value="osx">osx</option>
            </select>
          </label>
          <label>
            Arch
            <select value={targetArch} onChange={(e) => setTargetArch(e.target.value)}>
              <option value="amd64">amd64 / x64</option>
              <option value="x86">x86</option>
              <option value="arm64">arm64</option>
            </select>
          </label>
        </fieldset>
        <fieldset>
          <legend>Beacon profile</legend>
          <label>
            Mode
            <select value={mode} onChange={(e) => setMode(e.target.value)}>
              <option value="stream">stream (persistent)</option>
              <option value="poll">poll (low and slow)</option>
            </select>
          </label>
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
            Fallback endpoints
            <input
              value={fallbackEndpoints}
              onChange={(e) => setFallbackEndpoints(e.target.value)}
              placeholder="https://alt1.example.test, https://alt2.example.test"
            />
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
        <button className="primary" type="submit" disabled={submitting}>
          Build payload
        </button>
      </form>
      {error && <p className="error">{error}</p>}

      <h3 className="jobs-head">Recent builds</h3>
      {jobs.length === 0 ? (
        <div className="empty">
          <Icon name="package" />
          No builds yet -- the queue is empty.
        </div>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Requested</th>
                <th>Target</th>
                <th>Endpoint</th>
                <th>State</th>
                <th>Artifact</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {jobs.map((job) => (
                <tr key={job.jobId}>
                  <td title={job.jobId}>{new Date(job.requestedAt).toLocaleString()}</td>
                  <td>
                    <code>
                      {job.language}:{job.class} {job.target}
                    </code>
                  </td>
                  <td>
                    <code>{job.endpoint}</code>
                  </td>
                  <td>
                    {(job.state === 'queued' || job.state === 'running') && (
                      <span className="spinner inline-spinner" />
                    )}
                    <StatusBadge status={job.state} />
                    <span className="muted"> {elapsed(job)}</span>
                    {job.error && <div className="error">{job.error}</div>}
                  </td>
                  <td>
                    {job.artifact ? (
                      <span>
                        <code>{job.artifact.fingerprint.slice(0, 16)}</code>
                        <span className="muted"> ({job.artifact.size} bytes)</span>
                      </span>
                    ) : (
                      <span className="muted">—</span>
                    )}
                  </td>
                  <td>
                    {job.artifact && (
                      <a
                        className="download-link"
                        href={`engagements/${engagementId}/payloads/${job.artifact.artifactId}`}
                        download
                      >
                        Retrieve
                      </a>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
