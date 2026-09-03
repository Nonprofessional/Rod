import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  type BuildJob,
  type ListenerSummary,
  type PayloadSummary,
  deletePayload,
  enqueueBuildJob,
  listBuildJobs,
  listListeners,
  listPayloads,
  revokeStagerToken,
} from '../api'
import { Icon } from '../components/Icons'
import { StatusBadge } from '../components/StatusBadge'

// The payload-build panel. The main path is the mainstream shape (Cobalt
// Strike's package dialog, Sliver's generate): name the listener the implant
// dials and the target it runs on, leave everything else at its default, and
// build -- the artifact is a self-contained executable with its enrollment
// credential baked in, so "drop it on the target and run" needs no arguments.
// Everything an operator sets rarely -- the malleable wire knobs (fallbacks,
// paths, headers-adjacent fields, envelope), the manual endpoint, and the
// credential window -- folds into the Advanced disclosure, defaulted server
// side, so the form never makes an operator read a knob they will not touch.
//
// The form offers only what the pipeline actually delivers: the in-tree .NET
// unit (no language picker for units that are not registered), the Stage2 and
// Stager classes (the deployable shapes; the reduced classes compile the same
// beacon with a gutted verb set and stay API-only), this engagement's
// HTTP-shaped listeners (the ones implants can enroll through -- DNS/SMB/TCP
// listeners appear greyed out), and the arch set the toolchain bundles a
// runtime for (x86 only pairs with Windows). A stager names the completed
// Stage-2 artifact it fetches, so that select lists the finished Stage2 builds
// below.
//
// The build runs as a server-side job: submitting queues it and returns
// immediately; the recent-builds list below is the durable view (fetched on
// mount, polled while anything runs), so leaving the page or refreshing never
// loses a build -- the finished artifact waits with its download link.

// The transports an implant can enroll through; the listener select offers
// these and greyes everything else out.
const HTTP_INGRESS = new Set(['http', 'mtls', 'https-envelope'])

// The arch set per OS that the .NET toolchain bundles a runtime for: x86
// exists only as a Windows target.
const ARCHS: Record<string, string[]> = {
  linux: ['amd64', 'arm64'],
  windows: ['amd64', 'x86', 'arm64'],
  osx: ['amd64', 'arm64'],
}

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
  const [klass, setKlass] = useState('Stage2')
  const [stage2PayloadId, setStage2PayloadId] = useState('')
  const [targetOs, setTargetOs] = useState('linux')
  const [targetArch, setTargetArch] = useState('amd64')
  const [listenerId, setListenerId] = useState('')
  const [listeners, setListeners] = useState<ListenerSummary[]>([])
  const [mode, setMode] = useState('stream')
  const [sleepSeconds, setSleepSeconds] = useState('30')
  const [jitterSeconds, setJitterSeconds] = useState('10')
  const [killDate, setKillDate] = useState('')
  const [tokenMaxUses, setTokenMaxUses] = useState('1')
  const [revoking, setRevoking] = useState<string | null>(null)
  const [jobs, setJobs] = useState<BuildJob[]>([])
  const [payloads, setPayloads] = useState<PayloadSummary[]>([])
  const [payloadFilter, setPayloadFilter] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  // The Advanced disclosure's fields; every one defaults server side, so they
  // ride empty unless the operator opens the section and fills them.
  const [endpoint, setEndpoint] = useState('')
  const [fallbackEndpoints, setFallbackEndpoints] = useState('')
  const [enrollPath, setEnrollPath] = useState('')
  const [userAgent, setUserAgent] = useState('')
  const [requestTimeoutSeconds, setRequestTimeoutSeconds] = useState('')
  const [envelope, setEnvelope] = useState('None')
  const [tokenHours, setTokenHours] = useState('')

  const isStager = klass === 'Stager'

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

  // The durable library: the payload store's own listing, unlike the job
  // queue above. Re-read whenever work settles (mount, and the moment a
  // running job finishes) so a fresh build appears without a manual refresh.
  const refreshPayloads = useCallback(async () => {
    try {
      setPayloads(await listPayloads(engagementId))
    } catch {
      // Keep the last known library; the next settle retries.
    }
  }, [engagementId])

  const active = jobs.some((j) => j.state === 'queued' || j.state === 'running')
  useEffect(() => {
    if (!active) void refreshPayloads()
  }, [active, refreshPayloads])

  // This engagement's own listeners, the ingress a build can name. Loaded on
  // mount; the engagement's listeners panel is where they are created.
  useEffect(() => {
    void (async () => {
      try {
        setListeners(await listListeners(engagementId))
      } catch {
        // The form still offers the manual endpoint field on a failed load.
      }
    })()
  }, [engagementId])

  // One pickable listener preselects itself: with exactly one front there is
  // nothing to choose between, and the main path stays two clicks long.
  const pickable = useMemo(
    () => listeners.filter((l) => HTTP_INGRESS.has(l.transport)),
    [listeners],
  )
  useEffect(() => {
    if (!listenerId && pickable.length === 1) setListenerId(pickable[0].id)
  }, [pickable, listenerId])

  // The finished Stage2 builds a stager can fetch -- the artifact ids the
  // request resolves against the payload store.
  const stage2Artifacts = jobs.filter(
    (j) => j.state === 'completed' && j.class === 'Stage2' && j.artifact,
  )

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

  const onRevokeToken = async (tokenId: string) => {
    if (!window.confirm(`Revoke baked token ${tokenId.slice(0, 8)}? The credential stops working immediately; a deployed artifact that has not enrolled yet will not be able to.`))
      return
    setRevoking(tokenId)
    try {
      await revokeStagerToken(engagementId, tokenId)
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setRevoking(null)
    }
  }

  const onDeletePayload = async (p: PayloadSummary) => {
    if (
      !window.confirm(
        `Delete payload ${p.fingerprint.slice(0, 12)} (${p.class}${p.target ? ' ' + p.target : ''})? ` +
          'The stored bytes are gone and any stager fetching it stops working. The deletion is audited.',
      )
    )
      return
    try {
      await deletePayload(engagementId, p.artifactId)
      setError(null)
      await refreshPayloads()
    } catch (e) {
      setError(String(e))
    }
  }

  const onBuild = async (event: React.FormEvent) => {
    event.preventDefault()
    if (!listenerId && !endpoint.trim()) {
      setError('Pick a listener (or fill the endpoint under Advanced).')
      return
    }
    if (isStager && !stage2PayloadId) {
      setError('A stager fetches a Stage-2 payload -- build one first and pick it.')
      return
    }
    setSubmitting(true)
    try {
      await enqueueBuildJob(engagementId, {
        // Language rides empty: the server defaults to the in-tree .NET unit,
        // and no other unit is registered to pick.
        language: null,
        class: klass,
        targetOs,
        targetArch,
        listenerId: listenerId || null,
        endpoint: !listenerId && endpoint ? endpoint : null,
        stage2PayloadId: isStager ? stage2PayloadId : null,
        fallbackEndpoints: fallbacks(fallbackEndpoints),
        enrollPath: enrollPath || null,
        userAgent: userAgent || null,
        headers: null,
        requestTimeoutSeconds: num(requestTimeoutSeconds),
        envelope: envelope !== 'None' ? envelope : null,
        mode: mode !== 'stream' ? mode : null,
        sleepSeconds: num(sleepSeconds),
        jitterSeconds: num(jitterSeconds),
        killDate: killDate ? new Date(killDate).toISOString() : null,
        tokenMaxUses: num(tokenMaxUses),
        tokenLifetimeSeconds: num(tokenHours) !== null ? num(tokenHours)! * 3600 : null,
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
        Pick the listener the implant dials and the target it runs on, leave the rest, and build:
        the artifact is a self-contained executable (Rod.Implant.exe on Windows, ~no runtime
        needed on the target) with its enrollment credential baked in -- drop it and run. Copies
        of one artifact share that credential, so Max hosts caps how many machines may enroll
        with it. Every knob is baked at generation; changing the profile later means rebuilding.
      </p>
      <form className="build-form" onSubmit={onBuild}>
        <fieldset>
          <legend>Target</legend>
          <label>
            Listener
            <select
              value={listenerId}
              onChange={(e) => setListenerId(e.target.value)}
              title="The listener's public endpoint is what the artifact dials. Only HTTP-shaped listeners serve enrollment; DNS/SMB/TCP fronts are reached by other means."
            >
              <option value="">-- pick a listener --</option>
              {listeners.map((l) =>
                HTTP_INGRESS.has(l.transport) ? (
                  <option key={l.id} value={l.id}>
                    {l.name} ({l.transport} → {l.publicEndpoint})
                  </option>
                ) : (
                  <option key={l.id} disabled>
                    {l.name} ({l.transport} — not HTTP ingress)
                  </option>
                ),
              )}
            </select>
          </label>
          <label>
            Class
            <select value={klass} onChange={(e) => setKlass(e.target.value)}>
              <option value="Stage2">Stage2 — full implant</option>
              <option value="Stager">Stager — small loader, fetches a Stage2</option>
            </select>
          </label>
          {isStager && (
            <label>
              Stage-2 payload
              <select
                value={stage2PayloadId}
                onChange={(e) => setStage2PayloadId(e.target.value)}
                title="The finished Stage2 build the loader fetches and runs at launch."
              >
                <option value="">-- pick the Stage2 it fetches --</option>
                {stage2Artifacts.map((j) => (
                  <option key={j.artifact!.artifactId} value={j.artifact!.artifactId}>
                    {j.artifact!.fingerprint.slice(0, 12)} · {j.target} ·{' '}
                    {new Date(j.completedAt ?? j.requestedAt).toLocaleString()}
                  </option>
                ))}
              </select>
            </label>
          )}
          <label>
            OS
            {/* The build unit maps these onto a runtime identifier and refuses
                anything outside the supported set, so the form offers exactly
                that set instead of free text a typo can waste a build on. */}
            <select
              value={targetOs}
              onChange={(e) => {
                setTargetOs(e.target.value)
                if (!ARCHS[e.target.value].includes(targetArch)) setTargetArch('amd64')
              }}
            >
              <option value="linux">Linux</option>
              <option value="windows">Windows</option>
              <option value="osx">macOS</option>
            </select>
          </label>
          <label>
            Arch
            {/* x86 exists only as a Windows target -- the toolchain bundles no
                linux-x86/osx-x86 runtime -- so the arch list follows the OS. */}
            <select value={targetArch} onChange={(e) => setTargetArch(e.target.value)}>
              {ARCHS[targetOs].map((a) => (
                <option key={a} value={a}>
                  {a === 'amd64' ? 'amd64 / x64' : a}
                </option>
              ))}
            </select>
          </label>
        </fieldset>
        <fieldset disabled={isStager}>
          <legend>Beacon profile</legend>
          <label>
            Mode
            <select value={mode} onChange={(e) => setMode(e.target.value)}>
              <option value="stream">stream — persistent (interactive)</option>
              <option value="poll">poll — check in and sleep</option>
            </select>
          </label>
          <label>
            Check-in every (s)
            <input
              value={sleepSeconds}
              onChange={(e) => setSleepSeconds(e.target.value)}
              title="How often the implant calls home. Default 30."
            />
          </label>
          <label>
            Randomize ± (s)
            <input
              value={jitterSeconds}
              onChange={(e) => setJitterSeconds(e.target.value)}
              title="Random slack added to every interval so check-ins are not clockwork. Default 10."
            />
          </label>
          <label>
            Expiry date
            <input
              type="date"
              value={killDate}
              onChange={(e) => setKillDate(e.target.value)}
              title="Past this date the executable stops being usable: a leftover copy refuses to run, and a live implant terminates at its next check-in. Empty = 30 days from the build; it also bounds the baked credential's window."
            />
          </label>
          <label>
            Max uses
            <input
              value={tokenMaxUses}
              onChange={(e) => setTokenMaxUses(e.target.value)}
              title="How many times this artifact's baked credential may enroll — one spend per host, so one copy per machine. Default 1. Revoke it in the payload library to kill a leaked artifact's credential."
            />
          </label>
          <p className="muted" style={{ gridColumn: '1 / -1', margin: 0 }}>
            The implant calls home every <em>check-in</em> seconds, randomized by ±<em>randomize</em>
            . Past the <em>expiry date</em> the executable stops working — a leftover copy refuses
            to run, and a live implant terminates at its next check-in. <em>Max uses</em> caps how
            many hosts one artifact may enroll — copies share the credential, one spend each.
          </p>
          {isStager && (
            <p className="muted" style={{ gridColumn: '1 / -1', margin: 0 }}>
              The stager bakes only its kill date; beacon timing belongs to the Stage2 it
              fetches.
            </p>
          )}
        </fieldset>
        <details className="build-advanced">
          <summary>Advanced — wire shape and credential window</summary>
          <div className="grid">
            <label>
              Endpoint (manual)
              <input
                value={endpoint}
                onChange={(e) => setEndpoint(e.target.value)}
                placeholder="https://redirect.example.test"
                disabled={!!listenerId}
                title="Only without a listener: the absolute URL baked as the dial address. With a listener picked, its public endpoint is used."
              />
            </label>
            <label>
              Fallback endpoints
              <input
                value={fallbackEndpoints}
                onChange={(e) => setFallbackEndpoints(e.target.value)}
                placeholder="https://alt1.example.test, https://alt2.example.test"
                title="Walked in order when the primary burns; empty bakes the single-endpoint shape."
              />
            </label>
            <label>
              Enroll path
              <input
                value={enrollPath}
                onChange={(e) => setEnrollPath(e.target.value)}
                placeholder="/implants/enroll"
              />
            </label>
            <label>
              User agent
              <input
                value={userAgent}
                onChange={(e) => setUserAgent(e.target.value)}
                placeholder="HTTP client default"
              />
            </label>
            <label>
              Request timeout (s)
              <input
                value={requestTimeoutSeconds}
                onChange={(e) => setRequestTimeoutSeconds(e.target.value)}
                placeholder="30"
              />
            </label>
            <label>
              Envelope
              <select value={envelope} onChange={(e) => setEnvelope(e.target.value)}>
                <option>None</option>
                <option>Base64</option>
              </select>
            </label>
            <label>
              Token window (h)
              <input
                value={tokenHours}
                onChange={(e) => setTokenHours(e.target.value)}
                placeholder="kill window"
                title="How long the baked credential stays redeemable; empty defaults to the artifact's kill window."
              />
            </label>
          </div>
        </details>
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
                    {job.artifact?.tokenId && (
                      <div className="muted" title={job.artifact.tokenId}>
                        baked token {job.artifact.tokenId.slice(0, 8)}{' '}
                        {revoking === job.artifact.tokenId ? (
                          '(revoking…)'
                        ) : (
                          <button
                            className="sm danger"
                            onClick={() => void onRevokeToken(job.artifact!.tokenId!)}
                            title="The leak answer: the baked credential stops working at the next enrollment attempt"
                          >
                            Revoke token
                          </button>
                        )}
                      </div>
                    )}
                  </td>
                  <td>
                    {job.artifact && (
                      <a
                        className="download-link"
                        href={`engagements/${engagementId}/payloads/${job.artifact.artifactId}`}
                        download
                      >
                        Download
                      </a>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <h3 className="jobs-head">Payload library</h3>
      <p className="muted">
        Every payload this engagement ever built, straight from the durable store — it survives
        restarts and outlives the build queue above. Download again, revoke the baked credential,
        or delete a payload (a stager fetching a deleted payload stops working). The filter
        matches class, language, target, endpoint, or fingerprint.
      </p>
      <div className="inline-form">
        <input
          className="filter-text"
          placeholder="Filter payloads (linux, Stage2, host…)"
          value={payloadFilter}
          onChange={(e) => setPayloadFilter(e.target.value)}
        />
        <button className="ghost" onClick={() => void refreshPayloads()}>
          <Icon name="refresh" />
          Refresh
        </button>
      </div>
      {payloads.length === 0 ? (
        <div className="empty">
          <Icon name="package" />
          No payloads stored yet -- the first build lands here.
        </div>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Built</th>
                <th>Class</th>
                <th>Target</th>
                <th>Endpoint</th>
                <th>Size</th>
                <th>Fingerprint</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {payloads
                .filter((p) => {
                  const q = payloadFilter.trim().toLowerCase()
                  if (!q) return true
                  return [p.class, p.language, p.target, p.endpoint, p.fingerprint].some((v) =>
                    v?.toLowerCase().includes(q),
                  )
                })
                .map((p) => (
                  <tr key={p.artifactId}>
                    <td>{new Date(p.builtAt).toLocaleString()}</td>
                    <td>
                      <code>
                        {p.language}:{p.class}
                      </code>
                    </td>
                    <td>{p.target ?? '—'}</td>
                    <td>
                      <code>{p.endpoint ?? '—'}</code>
                    </td>
                    <td>{p.size} bytes</td>
                    <td>
                      <code title={p.fingerprint}>{p.fingerprint.slice(0, 16)}</code>
                      {p.tokenId && (
                        <div className="muted" title={p.tokenId}>
                          baked token {p.tokenId.slice(0, 8)}{' '}
                          {revoking === p.tokenId ? (
                            '(revoking…)'
                          ) : (
                            <button
                              className="sm danger"
                              onClick={() => void onRevokeToken(p.tokenId!)}
                              title="The baked credential stops working at the next enrollment attempt"
                            >
                              Revoke token
                            </button>
                          )}
                        </div>
                      )}
                    </td>
                    <td>
                      <a
                        className="download-link"
                        href={`engagements/${engagementId}/payloads/${p.artifactId}`}
                        download
                      >
                        Download
                      </a>{' '}
                      <button className="sm danger" onClick={() => void onDeletePayload(p)}>
                        Delete
                      </button>
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
