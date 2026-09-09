import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  type BuildJob,
  type ListenerSummary,
  enqueueBuildJob,
  listBuildJobs,
  listListeners,
  listPayloads,
  revokeStagerToken,
} from '../api'
import { frontFor, hostPortOf } from '../fronts'
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
// immediately; the recent-builds list below is the in-process view of the
// queue (fetched on mount, polled while anything runs), so leaving the page
// or refreshing never loses a running build. The strip shows the last few
// jobs as the queue's status -- for the artifact record (fronts, credential
// budgets, every build that ever finished) the durable home is the Payloads
// tab, which is also why a deleted payload reads "deleted" here instead of
// offering a download the store can no longer serve.

// How many jobs the queue strip shows; the rest is history the Payloads tab
// owns better.
const RECENT_BUILDS_SHOWN = 5

// The transports an implant can enroll through; the listener select offers
// these and greys everything else out.
const HTTP_INGRESS = new Set(['http', 'https', 'mtls'])

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
  // The socket the check-in stream dials when the build takes the hardened
  // split: an mTLS front beside the callback front. Empty means the check-in
  // rides the callback front's own envelope cycle.
  const [beaconListenerId, setBeaconListenerId] = useState('')
  const [mode, setMode] = useState('stream')
  const [sleepSeconds, setSleepSeconds] = useState('30')
  const [jitterSeconds, setJitterSeconds] = useState('10')
  const [killDate, setKillDate] = useState('')
  const [tokenMaxUses, setTokenMaxUses] = useState('1')
  const [revoking, setRevoking] = useState<string | null>(null)
  const [jobs, setJobs] = useState<BuildJob[]>([])
  // The library's artifact ids, fetched beside the jobs so the strip knows
  // which finished artifacts still exist -- a deleted payload reads "deleted"
  // instead of offering a download that would 404.
  const [libraryIds, setLibraryIds] = useState<Set<string>>(new Set())
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  // The Advanced disclosure's open state is tracked so choosing the manual
  // endpoint can open the section the endpoint field lives in.
  const [advancedOpen, setAdvancedOpen] = useState(false)

  // The Advanced disclosure's fields; every one defaults server side, so they
  // ride empty unless the operator opens the section and fills them.
  const [endpoint, setEndpoint] = useState('')
  const [beaconEndpoint, setBeaconEndpoint] = useState('')
  const [fallbackEndpoints, setFallbackEndpoints] = useState('')
  const [enrollPath, setEnrollPath] = useState('')
  const [userAgent, setUserAgent] = useState('')
  const [requestTimeoutSeconds, setRequestTimeoutSeconds] = useState('')
  const [envelope, setEnvelope] = useState('None')
  const [checkInProtection, setCheckInProtection] = useState(true)
  const [tokenHours, setTokenHours] = useState('')

  const isStager = klass === 'Stager'

  // Every web front carries its own check-ins: the envelope POST cycle rides
  // the same socket enrollment does, so no build needs a beacon split. The
  // split-socket shape -- enroll on a web front, the interactive gRPC stream
  // on an mTLS listener -- stays available as the hardened option, offered on
  // the cleartext front where an operator most often wants it.
  const selectedListener = listeners.find((l) => l.id === listenerId)
  const enrollIsPlainHttp = selectedListener
    ? selectedListener.transport === 'http'
    : /^http:\/\//i.test(endpoint.trim())
  const offersBeaconSplit = !isStager && enrollIsPlainHttp
  const beaconCandidates = listeners.filter((l) => l.transport === 'mtls')

  // The interactive front in play, whichever way it was named: a picked mTLS
  // listener, the manual endpoint under Advanced, or none (check-ins ride the
  // callback front itself). This is what the traffic diagram draws and what
  // gates stream mode.
  const interactiveFront =
    !isStager && enrollIsPlainHttp
      ? listeners.find((l) => l.id === beaconListenerId)
        ? `${listeners.find((l) => l.id === beaconListenerId)!.name} (mTLS)`
        : beaconEndpoint.trim() || null
      : null

  // Stream mode needs an mTLS path to hold open: a TLS-terminated front, or
  // an explicit interactive front beside a cleartext one. A cleartext front
  // with no interactive front is poll-only, and the form says so instead of
  // offering a mode the build cannot honor.
  const streamAvailable = !enrollIsPlainHttp || interactiveFront !== null
  useEffect(() => {
    if (!streamAvailable && mode === 'stream') setMode('poll')
  }, [streamAvailable, mode])

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
      const [list, library] = await Promise.all([
        listBuildJobs(engagementId),
        // A completed job's artifact can be deleted from the library at any
        // time; the strip re-reads the ids with every refresh so the
        // deleted/readable split stays current. The library load failing
        // degrades to "download" -- the click itself reports the 404.
        listPayloads(engagementId).catch(() => []),
      ])
      setJobs(list)
      setLibraryIds(new Set(library.map((p) => p.artifactId)))
    } catch {
      // Keep the last known list; the next poll retries.
    }
  }, [engagementId])

  useEffect(() => {
    void refreshJobs()
  }, [refreshJobs])

  const active = jobs.some((j) => j.state === 'queued' || j.state === 'running')

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

  // One pickable listener preselects itself on first load: with exactly one
  // front there is nothing to choose between. Only once -- after the user
  // has chosen (or deliberately chosen none), the preselect never fights
  // them again by snapping a deselected listener back into place.
  const preselected = useRef(false)
  const pickable = useMemo(
    () => listeners.filter((l) => HTTP_INGRESS.has(l.transport)),
    [listeners],
  )
  useEffect(() => {
    if (preselected.current || listeners.length === 0 || listenerId) return
    if (pickable.length === 1) setListenerId(pickable[0].id)
    preselected.current = true
  }, [pickable, listenerId, listeners.length])

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
        beaconListenerId: !isStager && beaconListenerId ? beaconListenerId : null,
        beaconEndpoint: !isStager && !beaconListenerId && beaconEndpoint.trim()
          ? beaconEndpoint.trim()
          : null,
        fallbackEndpoints: fallbacks(fallbackEndpoints),
        enrollPath: enrollPath || null,
        userAgent: userAgent || null,
        headers: null,
        requestTimeoutSeconds: num(requestTimeoutSeconds),
        envelope: envelope !== 'None' ? envelope : null,
        checkInProtection: checkInProtection ? null : false,
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
      <p className="muted" title="A self-contained executable with its enrollment credential baked in — drop it and run. Every knob is baked at generation.">
        Pick a listener and a target, leave the rest at the defaults.
      </p>
      <form className="build-form" onSubmit={onBuild}>
        <fieldset>
          <legend>Target</legend>
          <label>
            Listener (enroll + check-in)
            <select
              value={listenerId}
              onChange={(e) => {
                setListenerId(e.target.value)
                // Choosing the manual option is choosing to type an endpoint:
                // open the section it lives in.
                if (e.target.value === '') setAdvancedOpen(true)
                // A TLS-terminated listener carries enroll and beacon on one
                // socket; a beacon picked for a previous cleartext front
                // would silently split a build that does not need it.
                const next = listeners.find((l) => l.id === e.target.value)
                if (next && next.transport !== 'http') setBeaconListenerId('')
              }}
              title="The listener whose public endpoint gets baked: the implant registers on it once (enroll) and checks in on it for the rest of its life, unless an interactive listener is picked beside it. Only HTTP-shaped listeners serve implants; DNS/SMB/TCP fronts are reached by other means."
            >
              <option value="">-- none: public endpoint under Advanced --</option>
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
          {/* Always mounted, disabled unless the enroll + check-in listener is
              cleartext -- the form's grid never reshuffles when a listener is
              picked. The empty option carries the default (poll the same
              front); the full split rationale lives in the hover text. */}
          <label>
            Interactive listener (mTLS)
            <select
              value={offersBeaconSplit ? beaconListenerId : ''}
              disabled={!offersBeaconSplit}
              onChange={(e) => setBeaconListenerId(e.target.value)}
              title={
                offersBeaconSplit
                  ? 'A cleartext front cannot carry the interactive stream. Leave empty and the implant polls the enroll + check-in listener over the envelope POST cycle; pick the mTLS listener for the hardened split-socket shape -- the interactive gRPC stream (live channels) on its own TLS socket.'
                  : 'A TLS-terminated listener carries the enroll + check-in traffic and the interactive stream on the same socket, so no interactive split applies. Pick a cleartext http front to offer one.'
              }
            >
              {offersBeaconSplit ? (
                <>
                  <option value="">-- none: check-ins poll the enroll + check-in listener --</option>
                  {beaconCandidates.map((l) => (
                    <option key={l.id} value={l.id}>
                      {l.name} ({l.transport} → {l.publicEndpoint})
                    </option>
                  ))}
                </>
              ) : (
                <option value="">-- same listener as enroll + check-in --</option>
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
          {/* The traffic picture reads after the fields it summarizes: one
              socket, or two when the interactive stream gets its own. */}
          <WireShape
            enroll={
              selectedListener
                ? `${selectedListener.name} (${selectedListener.transport})`
                : endpoint.trim() || 'manual endpoint'
            }
            interactive={interactiveFront}
          />
        </fieldset>
        <fieldset disabled={isStager}>
          <legend>Beacon profile</legend>
          <label>
            Mode
            <select
              value={mode}
              onChange={(e) => setMode(e.target.value)}
              title="How the artifact checks in. Poll posts one envelope per interval over the web front; stream holds the mTLS connection open for interactive channels and needs a TLS path -- a TLS-terminated front or a picked interactive front."
            >
              <option value="stream" disabled={!streamAvailable}>
                stream — persistent (interactive, mTLS)
              </option>
              <option value="poll">poll — check in and sleep</option>
            </select>
            {!streamAvailable && (
              <span className="field-help">
                Stream needs TLS: pick a TLS front or an interactive front above.
              </span>
            )}
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
            Call-home cadence, the artifact's expiry fuse, and the baked credential's use count —
            hover each field for specifics.
          </p>
          {isStager && (
            <p className="muted" style={{ gridColumn: '1 / -1', margin: 0 }}>
              The stager bakes only its kill date; beacon timing belongs to the Stage2 it
              fetches.
            </p>
          )}
        </fieldset>
        <details
          className="build-advanced"
          open={advancedOpen || undefined}
          onToggle={(e) => setAdvancedOpen((e.target as HTMLDetailsElement).open)}
        >
          <summary>Advanced — wire shape and credential window</summary>
          <div className="grid">
            <p className="muted" style={{ gridColumn: '1 / -1', margin: 0 }}>
              Manual overrides only, for builds without a picked listener: two addresses at most
              (the enroll + check-in public endpoint, and the interactive public endpoint for the
              hardened split), backup enroll + check-in endpoints, and the one path knob —
              registration's. Check-ins ride a fixed route and the interactive stream rides its
              own, so no other path exists to set.
            </p>
            <label>
              Public endpoint (enroll + check-in, manual)
              <input
                value={endpoint}
                onChange={(e) => setEndpoint(e.target.value)}
                placeholder="https://redirect.example.test"
                disabled={!!listenerId}
                title={
                  listenerId
                    ? 'An enroll + check-in listener is picked, so its public endpoint is used. Choose "-- none: public endpoint under Advanced --" above to type one manually.'
                    : 'The address the implant registers and checks in on — typed instead of picking a listener, for an address this teamserver does not serve (a redirector you control elsewhere).'
                }
              />
            </label>
            <label>
              Public endpoint (interactive, manual)
              <input
                value={beaconEndpoint}
                onChange={(e) => setBeaconEndpoint(e.target.value)}
                placeholder="https://mtls.example.test"
                disabled={!!beaconListenerId}
                title={
                  beaconListenerId
                    ? 'An interactive listener is picked, so its public endpoint is used.'
                    : 'The mTLS socket the interactive stream dials, typed instead of picking a listener. Leave empty and check-ins poll the enroll + check-in address itself over the envelope POST cycle; name it only for the split-socket shape.'
                }
              />
            </label>
            <label>
              Fallback public endpoints
              <input
                value={fallbackEndpoints}
                onChange={(e) => setFallbackEndpoints(e.target.value)}
                placeholder="https://alt1.example.test, https://alt2.example.test"
                title="Backup enroll + check-in addresses the implant walks, in order, when the primary is unreachable — full addresses like the primary; they share the enroll path and the fixed check-in route. Empty bakes the single-address shape."
              />
            </label>
            <label>
              Enroll path
              <input
                value={enrollPath}
                onChange={(e) => setEnrollPath(e.target.value)}
                placeholder="/implants/enroll"
                title="The URI path of the one-time registration POST (default /implants/enroll). The only path knob: check-ins ride the fixed /implants/beacon route and the interactive stream rides the mTLS socket's own path. Change it only when a redirector rewrites to the real route."
              />
            </label>
            <label>
              User agent
              <input
                value={userAgent}
                onChange={(e) => setUserAgent(e.target.value)}
                placeholder="HTTP client default"
                title="The User-Agent header the implant presents, so the traffic blends with a known-good client."
              />
            </label>
            <label>
              Request timeout (s)
              <input
                value={requestTimeoutSeconds}
                onChange={(e) => setRequestTimeoutSeconds(e.target.value)}
                placeholder="30"
                title="Per-request HTTP timeout. Default 30."
              />
            </label>
            <label>
              Enroll body
              <select
                value={envelope}
                onChange={(e) => setEnvelope(e.target.value)}
                title="Shapes the ENROLL request body only. None sends the raw JSON body; Base64 wraps it as one string so it no longer reads as structured C2; AES-GCM encrypts it under a per-artifact key minted at build — worth it on cleartext http or where a redirector terminates TLS early; redundant on direct https, where TLS already encrypts the channel."
              >
                <option>None</option>
                <option>Base64</option>
                <option value="AesGcm">AES-GCM</option>
              </select>
            </label>
            <label
              className="checkbox-label"
              title="Seals every check-in POST and its response as AES-256-GCM under a per-artifact key minted at build, covering a fresh counter — the authentication the web check-ins use instead of a TLS client certificate, and the confidentiality that makes cleartext http carry encrypted content. Off is the lab-debug plaintext frame."
            >
              <input
                type="checkbox"
                checked={checkInProtection}
                onChange={(e) => setCheckInProtection(e.target.checked)}
              />
              Protect check-ins
            </label>
            <label>
              Credential window (h)
              <input
                value={tokenHours}
                onChange={(e) => setTokenHours(e.target.value)}
                placeholder="expiry window"
                title="How long the baked credential stays redeemable; empty defaults to the artifact's expiry window."
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
                <th>Listener</th>
                <th>State</th>
                <th>Artifact</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {jobs.slice(0, RECENT_BUILDS_SHOWN).map((job) => {
                const front = frontFor(job.endpoint, listeners)
                const inLibrary =
                  !job.artifact || libraryIds.has(job.artifact.artifactId)
                return (
                  <tr key={job.jobId}>
                    <td title={job.jobId}>{new Date(job.requestedAt).toLocaleString()}</td>
                    <td>
                      <code>
                        {job.language}:{job.class} {job.target}
                      </code>
                    </td>
                    <td>
                      <span
                        title={
                          front
                            ? `The engagement's ${front.transport} listener: ${job.endpoint} (enroll + check-in)`
                            : `No listener serves this address (typed for a redirector): ${job.endpoint}`
                        }
                      >
                        {front ? (
                          <>
                            {front.name} <span className="muted">({front.transport})</span> ·{' '}
                            <code>{hostPortOf(job.endpoint)}</code>
                          </>
                        ) : (
                          <>
                            <code>{hostPortOf(job.endpoint)}</code>{' '}
                            <span className="muted">manual</span>
                          </>
                        )}
                      </span>
                      {job.beaconEndpoint && (
                        <div
                          className="muted"
                          title="The socket the interactive stream dials (split-socket build)"
                        >
                          interactive <code>{hostPortOf(job.beaconEndpoint)}</code>
                        </div>
                      )}
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
                      {job.artifact &&
                        (inLibrary ? (
                          <a
                            className="download-link"
                            href={`engagements/${engagementId}/payloads/${job.artifact.artifactId}`}
                            download
                          >
                            Download
                          </a>
                        ) : (
                          <span
                            className="muted"
                            title="The artifact was deleted from the payload library -- the bytes are gone and a stager fetching it 404s. The credential can still be revoked above."
                          >
                            deleted
                          </span>
                        ))}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
          {jobs.length > RECENT_BUILDS_SHOWN && (
            <p className="muted" style={{ padding: '6px 10px', margin: 0 }}>
              {jobs.length - RECENT_BUILDS_SHOWN} older job
              {jobs.length - RECENT_BUILDS_SHOWN === 1 ? '' : 's'} hidden -- every finished
              artifact stays in the{' '}
              <a href={`#/engagements/${engagementId}/payloads`}>Payloads</a> library.
            </p>
          )}
        </div>
      )}

      <p className="muted">
        Finished payloads live on in the <a href={`#/engagements/${engagementId}/payloads`}>Payloads</a> tab.
      </p>
    </div>
  )
}

// The traffic shape this build bakes, drawn from the current picks and named
// with the fixed vocabulary: the behaviors each socket carries -- enroll +
// check-in on the primary, interactive on its own mTLS socket when the build
// splits. The form's words say what each field does; this says what the
// target will see moving.
function WireShape({ enroll, interactive }: { enroll: string; interactive: string | null }) {
  return (
    <div className="wire-shape" title="The traffic shape this build bakes">
      <span className="wire-node">
        <Icon name="cpu" className="wire-icon" /> implant
      </span>
      <div className="wire-paths">
        <div className="wire-path">
          <span className="wire-label">{interactive ? 'enroll + check-in' : 'enroll + check-in + interactive'}</span>
          <span className="wire-arrow">→</span>
          <span className="wire-node">{enroll}</span>
        </div>
        {interactive && (
          <div className="wire-path">
            <span className="wire-label">interactive · mTLS</span>
            <span className="wire-arrow">⇉</span>
            <span className="wire-node">{interactive}</span>
          </div>
        )}
      </div>
    </div>
  )
}
