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
import { WebShellGenerateForm } from '../components/WebShellGenerateForm'

// The payload-build panel -- the artifact factory's operator face. The main
// path is the mainstream shape (Cobalt Strike's package dialog, Sliver's
// generate): name the listener the implant dials and the target it runs on,
// leave everything else at its default, and build -- the artifact is a
// self-contained executable with its enrollment credential baked in, so
// "drop it on the target and run" needs no arguments. Everything an
// operator sets rarely -- the malleable wire knobs (fallbacks, paths,
// headers-adjacent fields, envelope) and the credential window -- folds
// into the Advanced disclosure, defaulted server side, so the form never
// makes an operator read a knob they will not touch. The tab's second
// artifact kind is the web-shell script: the same
// prepare-then-place flow as the classic managers, rendered by the
// WebShellGenerateForm beside the implant form under one toggle.
//
// The form offers only what the pipeline actually delivers: the in-tree Rust
// unit (no language picker for units that are not registered), the Stage2
// class (the one deployable shape left -- the stager class retired with the
// .NET trees, delivery rides the launcher one-liners), this engagement's
// family listeners (http/https/tcp/dns/doh all serve enrollment; only the
// shell catcher greys out), and the arch set the toolchain bundles a
// runtime for (x86 only pairs with Windows).
// Interactive needs no second listener in the common case: every front
// carries its own contacts -- the envelope POST cycle for poll, the
// WebSocket beacon for stream, the socket family's held session. The
// Contact carrier pick is the deliberate exception: it names a different
// front for steady-state contacts while enrollment keeps riding the picked
// listener (the split shape, single-point by design).
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

// The transports an implant can enroll through -- the HTTP-shaped fronts,
// the socket family (enrollment over the stream contact), and the DNS
// family (enrollment over DNS: the chunked TXT exchange a DNS-only target
// runs); the listener select offers these and greys everything else out.
const ENROLL_TRANSPORTS = new Set(['http', 'https', 'tcp', 'dns', 'doh'])

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
  // The tab's two artifact kinds share the card: the implant build form
  // (the pipeline's jobs) and the web-shell script generator (instant
  // render, no job). One toggle, one mental model -- this is where
  // artifacts are made.
  const [artifact, setArtifact] = useState<'implant' | 'webshell'>('implant')
  const [klass, setKlass] = useState('Stage2')
  const [targetOs, setTargetOs] = useState('linux')
  const [targetArch, setTargetArch] = useState('amd64')
  const [format, setFormat] = useState('exe')
  const [listenerId, setListenerId] = useState('')
  const [listeners, setListeners] = useState<ListenerSummary[]>([])
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

  // The Advanced disclosure's fields; every one defaults server side, so they
  // ride empty unless the operator opens the section and fills them.
  // Fallbacks come from the inventory, not free text: the ordered ids of the
  // engagement's same-family listeners picked as walked fallbacks, plus a
  // typed tail for fronts this teamserver does not serve.
  const [fallbackIds, setFallbackIds] = useState<string[]>([])
  const [manualFallbacks, setManualFallbacks] = useState('')
  const [enrollPath, setEnrollPath] = useState('')
  const [userAgent, setUserAgent] = useState('')
  const [requestTimeoutSeconds, setRequestTimeoutSeconds] = useState('')
  const [envelope, setEnvelope] = useState('AesGcm')
  const [contactProtection, setContactProtection] = useState(true)
  const [tokenHours, setTokenHours] = useState('')


  // Every web front carries its own contacts -- the envelope POST cycle
  // for poll, the WebSocket beacon for stream -- so one listener is always
  // the whole story and the form offers no split.
  const selectedListener = listeners.find((l) => l.id === listenerId)

  // The poll-only family: DNS/DoH carry no live stream to hold -- one
  // answer per poll -- so stream mode is incoherent on them and the form
  // keeps the mode honest (the server refuses the pairing with the same
  // fix). The socket family (TCP) bakes either mode: stream holds the
  // live session, poll cycles one connection per contact.
  const pollOnly =
    selectedListener?.transport === 'dns' || selectedListener?.transport === 'doh'

  useEffect(() => {
    if (pollOnly && mode === 'stream') setMode('poll')
  }, [pollOnly, mode])

  const num = (value: string): number | null => {
    const trimmed = value.trim()
    if (trimmed === '') return null
    const parsed = Number(trimmed)
    return Number.isFinite(parsed) ? parsed : null
  }

  // The typed fallback tail: comma-separated, appended after the picked
  // fronts in walk order.
  const fallbacks = (value: string): string[] | null => {
    const list = value.split(',').map((f) => f.trim()).filter((f) => f !== '')
    return list.length > 0 ? list : null
  }

  // The scheme families the egress walk serves -- the server's own rule:
  // the web pair, the DNS pair, and the raw socket. Fallbacks dial the
  // front's family only, so both the picker and the chips stay inside it.
  const familyOf = (transport: string): string =>
    transport === 'http' || transport === 'https' ? 'web'
      : transport === 'dns' || transport === 'doh' ? 'dns'
        : transport === 'tcp' ? 'tcp' : ''

  // The front's family: the picked listener's transport.
  const frontFamily = selectedListener ? familyOf(selectedListener.transport) : ''

  // The dial a picked fallback bakes -- the same normalization the server
  // applies when the listener itself is named: an absolute public endpoint
  // stands as typed, a bare one completes under the transport's scheme, and
  // the DNS family's dial names the listener's own bind as the resolver
  // with its public endpoint as the zone.
  const dialOf = (l: ListenerSummary): string => {
    const pub = l.publicEndpoint.trim()
    if (l.transport === 'dns' || l.transport === 'doh') {
      const zone = pub.replace(/\.+$/, '').toLowerCase()
      return `${l.transport}://${l.bindAddress.trim()}/${zone}`
    }
    if (/^[a-z][a-z0-9+.-]*:\/\//i.test(pub)) return pub
    return `${l.transport}://${pub}`
  }

  // A wildcard-bound DNS listener names no resolver an implant can dial --
  // the same refusal the named-listener path applies -- so it stays off the
  // fallback offer.
  const wildcardBound = (l: ListenerSummary): boolean =>
    (l.transport === 'dns' || l.transport === 'doh')
    && /^(0\.0\.0\.0:|\[::\]:|:::)/.test(l.bindAddress.trim())

  // The picker's offer and the picked chips, both held to the front's own
  // family; a changed front filters the picks for free (an id its family no
  // longer serves resolves to no chip and bakes nothing).
  const eligible = useMemo(
    () => listeners.filter((l) =>
      l.id !== listenerId && familyOf(l.transport) === frontFamily && !wildcardBound(l)),
    [listeners, listenerId, frontFamily],
  )
  const picked = useMemo(
    () => fallbackIds
      .map((id) => eligible.find((l) => l.id === id))
      .filter((l): l is ListenerSummary => l !== undefined),
    [fallbackIds, eligible],
  )

  const movePick = (id: string, delta: number) => {
    setFallbackIds((prev) => {
      const from = prev.indexOf(id)
      const to = from + delta
      if (from < 0 || to < 0 || to >= prev.length) return prev
      const next = [...prev]
      ;[next[from], next[to]] = [next[to], next[from]]
      return next
    })
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
        // Without the inventory the form has nothing to offer: the empty
        // select says so, and the next mount retries.
      }
    })()
  }, [engagementId])

  // One pickable listener preselects itself on first load: with exactly one
  // front there is nothing to choose between. Only once -- after the user
  // has chosen (or deliberately chosen none), the preselect never fights
  // them again by snapping a deselected listener back into place.
  const preselected = useRef(false)
  const pickable = useMemo(
    () => listeners.filter((l) => ENROLL_TRANSPORTS.has(l.transport)),
    [listeners],
  )
  // The steady-state pairing shape (architecture.md Sec 8): contacts ride
  // the named carrier while enrollment keeps riding the picked front -- the
  // priority inversion a fallback list cannot express (its entries serve
  // both exchanges together). Any listener a beacon may name serves: the
  // web family (the WebSocket stream or the envelope cycle by mode), the
  // socket family (either mode), and the DNS family (poll, the
  // egress-restricted TXT carrier). The catcher serves no contact at all.
  const carriers = useMemo(
    () => listeners.filter((l) => l.transport !== 'shellcatch'),
    [listeners],
  )
  const [carrierId, setCarrierId] = useState('')
  useEffect(() => {
    if (preselected.current || listeners.length === 0 || listenerId) return
    if (pickable.length === 1) setListenerId(pickable[0].id)
    preselected.current = true
  }, [pickable, listenerId, listeners.length])

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
    if (!listenerId) {
      setError('Pick a listener (create one in the listeners panel first).')
      return
    }
    setSubmitting(true)
    try {
      await enqueueBuildJob(engagementId, {
        // Language rides empty: the server defaults to the in-tree Rust unit,
        // and no other unit is registered to pick.
        language: null,
        class: klass,
        targetOs,
        targetArch,
        listenerId,
        endpoint: null,
        beaconListenerId: carrierId || null,
        beaconEndpoint: null,
        // The walked fallback list: the picked fronts' dials in walk order,
        // then any typed tail -- one list, family-checked server side.
        fallbackEndpoints: (() => {
          const manual = fallbacks(manualFallbacks) ?? []
          const all = [...picked.map(dialOf), ...manual]
          return all.length > 0 ? all : null
        })(),
        enrollPath: enrollPath || null,
        userAgent: userAgent || null,
        headers: null,
        requestTimeoutSeconds: num(requestTimeoutSeconds),
        envelope: envelope !== 'None' ? envelope : null,
        contactProtection: contactProtection ? null : false,
        mode: mode !== 'stream' ? mode : null,
        sleepSeconds: num(sleepSeconds),
        jitterSeconds: num(jitterSeconds),
        killDate: killDate ? new Date(killDate).toISOString() : null,
        tokenMaxUses: num(tokenMaxUses),
        tokenLifetimeSeconds: num(tokenHours) !== null ? num(tokenHours)! * 3600 : null,
        format: format !== 'exe' ? format : null,
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
      {/* The artifact-kind toggle: the implant pipeline and the web-shell
          generator share this card -- one place that makes drop-on-target
          artifacts. */}
      <div className="inline-form">
        <button
          className={artifact === 'implant' ? 'primary sm' : 'ghost sm'}
          onClick={() => setArtifact('implant')}
          title="A self-contained executable with its enrollment credential baked in, built by the server-side pipeline."
        >
          Implant
        </button>
        <button
          className={artifact === 'webshell' ? 'primary sm' : 'ghost sm'}
          onClick={() => setArtifact('webshell')}
          title="A placement script with its credential baked in, rendered instantly for a target's web root."
        >
          Webshell script
        </button>
      </div>
      {artifact === 'webshell' ? (
        <>
          <h3>Generate web-shell script</h3>
          <p className="muted">
            A placement script with its credential baked in — generate here, drop it in the
            target's web root, register the reachable URL under Web shells.
          </p>
          <WebShellGenerateForm engagementId={engagementId} />
        </>
      ) : (
        <>
          <h3>Build payload</h3>
          <p className="muted" title="A self-contained executable with its enrollment credential baked in — drop it and run. Every knob is baked at generation.">
            Pick a listener and a target, leave the rest at the defaults.
          </p>
          <form className="build-form" onSubmit={onBuild}>
        <fieldset>
          <legend>Target</legend>
          <label>
            Listener (enroll + contact)
            <select
              value={listenerId}
              onChange={(e) => setListenerId(e.target.value)}
              title="The listener whose public endpoint gets baked: the implant registers on it once (enroll) and contacts on it for the rest of its life -- interactive rides the same front, and the summary under the form spells out how. Every family serves enrollment (web, raw socket, DNS/DoH); only the shell catcher serves none."
            >
              <option value="" disabled>-- pick a listener --</option>
              {listeners.map((l) =>
                ENROLL_TRANSPORTS.has(l.transport) ? (
                  <option key={l.id} value={l.id}>
                    {l.name} ({l.transport} → {l.publicEndpoint})
                  </option>
                ) : (
                  <option key={l.id} disabled>
                    {l.name} ({l.transport} — not enroll ingress)
                  </option>
                ),
              )}
            </select>
          </label>
          {carriers.length > 0 && (
            <label>
              Contact carrier
              <select
                value={carrierId}
                onChange={(e) => setCarrierId(e.target.value)}
                title="Where contacts ride while enrollment keeps riding the front above -- the steady state's own front, independent of the enroll pick (a fallback list cannot express this: its entries serve both exchanges together). Empty: the same front as enrollment. A web/mTLS listener: its native session (stream holds it, poll cycles it). A TCP listener: the socket wire, either mode. A DNS/DoH listener: the egress-restricted TXT carrier (poll only -- presence, short tasking, chunked results, no interactive channels and no staged transfers; the implant dials the listener's own bind as its resolver)."
              >
                <option value="">-- same front as enrollment --</option>
                {carriers.map((l) => (
                  <option key={l.id} value={l.id}>
                    {l.name} ({l.transport} · {l.publicEndpoint})
                  </option>
                ))}
              </select>
            </label>
          )}
          <label>
            Class
            {/* The stager class retired with the .NET trees: delivery rides
                the launcher one-liners, which the Launchers tab renders per
                payload. */}
            <select value={klass} onChange={(e) => setKlass(e.target.value)}>
              <option value="Stage2">Stage2 — full implant</option>
            </select>
          </label>
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
          <label>
            Format
            {/* The artifact's form factor: every Rust artifact is a native
                executable, so the spellings differ only in posture -- 'exe'
                the default, 'aot' the one the memfd one-liner family keys
                on for in-memory delivery. */}
            <select value={format} onChange={(e) => setFormat(e.target.value)}>
              <option value="exe">exe — native executable</option>
              <option value="aot">aot — native, in-memory deliverable</option>
            </select>
          </label>
        </fieldset>
        <fieldset>
          <legend>Beacon profile</legend>
          <label>
            Mode
            <select
              value={mode}
              onChange={(e) => setMode(e.target.value)}
              disabled={pollOnly}
              title={pollOnly
                ? 'This front holds no live stream (one connection or one answer per contact), so poll is the only coherent mode -- the server refuses the stream pairing with the same fix.'
                : 'How the artifact contacts: stream holds one connection open with live server push; poll exchanges one contact per interval. Either way every verb rides -- the summary below spells out how.'}
            >
              <option value="stream" disabled={pollOnly}>stream — persistent connection</option>
              <option value="poll">poll — contact and sleep</option>
            </select>
          </label>
          <label>
            Contact every (s)
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
              title="Random slack added to every interval so contacts are not clockwork. Default 10."
            />
          </label>
          <label>
            Max uses
            <input
              value={tokenMaxUses}
              onChange={(e) => setTokenMaxUses(e.target.value)}
              title="How many hosts the baked credential may enroll -- one spend per host, so one artifact can seed several machines until the budget runs out. 0 = unlimited. Default 1. Revoke it in the payload library to kill a leaked artifact's credential."
            />
            <span className="field-help">0 = unlimited</span>
          </label>
          <p className="muted" style={{ gridColumn: '1 / -1', margin: 0 }}>
            Call-home cadence and the baked credential's host cap. The artifact's kill-date fuse
            and the credential's enroll window ride under Advanced — hover each field for
            specifics.
          </p>
        </fieldset>
        <details className="build-advanced">
          <summary>Advanced — wire shape and credential timing</summary>
          <div className="grid">
            <p className="muted" style={{ gridColumn: '1 / -1', margin: 0 }}>
              Wire shape and credential timing. The fallback fronts are picked from this
              engagement's same-family listeners, in walk order; manual entries stay for fronts
              this teamserver does not serve. The one path knob is registration's. The
              artifact's kill-date fuse and the credential's enroll window ride here too.
            </p>
            <div className="fallback-fronts">
              <span className="fallback-caption">Fallback fronts (walk order)</span>
              {picked.length === 0 ? (
                <p className="muted" style={{ margin: 0 }}>
                  None — the primary front is the whole walk.
                </p>
              ) : (
                <div className="fallback-chips">
                  {picked.map((l, i) => (
                    <span className="chip" key={l.id}>
                      <strong>{l.name}</strong>
                      {dialOf(l)}
                      <button
                        type="button"
                        className="link"
                        disabled={i === 0}
                        onClick={() => movePick(l.id, -1)}
                        title="Walk this front earlier"
                      >
                        ↑
                      </button>
                      <button
                        type="button"
                        className="link"
                        disabled={i === picked.length - 1}
                        onClick={() => movePick(l.id, 1)}
                        title="Walk this front later"
                      >
                        ↓
                      </button>
                      <button
                        type="button"
                        className="link"
                        onClick={() =>
                          setFallbackIds((prev) => prev.filter((x) => x !== l.id))
                        }
                        title="Drop this fallback"
                      >
                        ✕
                      </button>
                    </span>
                  ))}
                </div>
              )}
              <select
                value=""
                onChange={(e) => {
                  if (e.target.value) setFallbackIds((prev) => [...prev, e.target.value])
                }}
                title="Add one of this engagement's same-family listeners as a walked fallback. The baked entry is its public endpoint as of this build (the DNS family: its bind as the resolver plus its zone) — a later repoint does not update already-deployed artifacts, the walk only bridges until a rebuild."
              >
                <option value="">
                  {eligible.filter((l) => !fallbackIds.includes(l.id)).length === 0
                    ? 'No same-family listeners left to add'
                    : '-- add a fallback front --'}
                </option>
                {eligible.filter((l) => !fallbackIds.includes(l.id)).map((l) => (
                  <option key={l.id} value={l.id}>
                    {l.name} ({l.transport} → {dialOf(l)})
                  </option>
                ))}
              </select>
              <label>
                Manual fallback endpoints
                <input
                  value={manualFallbacks}
                  onChange={(e) => setManualFallbacks(e.target.value)}
                  placeholder="https://alt.example.test"
                  title="Typed fallbacks for fronts without a listener record (a redirector in front of the same bind), appended after the picked fronts in walk order. Same scheme family as the front — the server refuses the mix."
                />
              </label>
            </div>
            <label>
              Enroll path
              <input
                value={enrollPath}
                onChange={(e) => setEnrollPath(e.target.value)}
                placeholder="/implants/enroll"
                title="The URI path of the one-time registration POST (default /implants/enroll). The only path knob: contacts ride the fixed /implants/beacon route and the interactive stream rides the mTLS socket's own path. Change it only when a redirector rewrites to the real route."
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
                title="Shapes the ENROLL request body only. AES-GCM (the default) encrypts it under a per-artifact key minted at build — the same default posture contacts already carry; redundant on direct https, where TLS already encrypts the channel. None sends the raw JSON body, the lab-debug shape; Base64 wraps it as one string so it no longer reads as structured C2."
              >
                <option>None</option>
                <option>Base64</option>
                <option value="AesGcm">AES-GCM</option>
              </select>
            </label>
            <label
              className="checkbox-label"
              title="Seals every contact POST and its response as AES-256-GCM under a per-artifact key minted at build, covering a fresh counter — the authentication the web contacts use instead of a TLS client certificate, and the confidentiality that makes cleartext http carry encrypted content. Off is the lab-debug plaintext frame."
            >
              Protect contacts
              <span className="checkbox-row">
                <input
                  type="checkbox"
                  checked={contactProtection}
                  onChange={(e) => setContactProtection(e.target.checked)}
                />
              </span>
            </label>
            <label>
              Kill date
              <input
                type="date"
                value={killDate}
                onChange={(e) => setKillDate(e.target.value)}
                title="Past this date the executable stops being usable: a leftover copy refuses to run, and a live implant terminates at its next contact. Empty = no fuse -- the implant runs until retired (the long-haul default). A date also caps the baked credential's window unless 'Valid for' overrides it."
              />
            </label>
            <label>
              Valid for (h)
              <input
                value={tokenHours}
                onChange={(e) => setTokenHours(e.target.value)}
                placeholder="hours"
                title="How long the baked credential can enroll NEW implants. Pairs with Max uses: that caps how many enrolls, this caps for how long. Empty = until the kill date, or a 30-day drop window when there is none. Enrollment is permanent — an implant that already enrolled contacts for life; this only gates copies that have not enrolled yet."
              />
              <span className="field-help">empty = until the kill date, else 30 days</span>
            </label>
          </div>
        </details>
        <BuildSummary
          listener={selectedListener}
          carrier={carriers.find((l) => l.id === carrierId)}
          mode={mode}
          sleep={sleepSeconds}
          jitter={jitterSeconds}
        />
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
                            ? `The engagement's ${front.transport} listener: ${job.endpoint} (enroll + contact)`
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
      </>
      )}

      <p className="muted">
        Finished payloads live on in the <a href={`#/engagements/${engagementId}/payloads`}>Payloads</a> tab.
      </p>
    </div>
  )
}

// The traffic shape this build bakes, drawn from the current picks: every
// behavior the artifact dials -- enroll, contact, and (stream mode) the
// interactive WebSocket beacon -- rides the one named front. The form's
// words say what each field does; this says what the target will see
// moving.
// The baked shape, composed live from the picks above: what the artifact
// enrolls on, how it contacts, and how interactive rides -- read before the
// build commits, not discovered on target. The three keys are the fixed
// vocabulary's three behaviors; the values follow the front's transport,
// the contact carrier, and the mode.
function BuildSummary({
  listener,
  carrier,
  mode,
  sleep,
  jitter,
}: {
  listener?: ListenerSummary
  carrier?: ListenerSummary
  mode: string
  sleep: string
  jitter: string
}) {
  const front = listener?.publicEndpoint ?? '— pick a listener —'
  const via = listener ? `${listener.name} (${listener.transport})` : 'unpicked'
  const cadence = `every ${sleep.trim() || '30'}s ± ${jitter.trim() || '10'}s`
  // The socket family's dial shape.
  const socket = listener?.transport === 'tcp'
  // The DNS family as the enroll front itself (the DNS-only target's shape).
  const dnsFront = !socket && (listener?.transport === 'dns' || listener?.transport === 'doh')

  const contact = carrier
    ? `DNS TXT polls on ${carrier.bindAddress} · zone ${carrier.publicEndpoint} — short tasking + chunked results (enroll stays on the front above)`
    : socket
      ? `one socket connection per contact, ${cadence}, on ${front}`
      : dnsFront
        ? `DNS TXT polls, ${cadence}, on ${front} — the whole lifecycle on one carrier`
        : mode === 'poll'
          ? `sealed envelope POSTs ${cadence} on ${front}`
          : `WebSocket beacon held open on ${front}`

  const interactive = carrier
    ? 'store-and-forward over the DNS carrier — input on the TXT answers, output as chunked queries'
      : dnsFront
        ? 'store-and-forward over the DNS polls — input on the TXT answers, output as chunked queries (query-rate cadence; the slowest wire that carries it)'
        : mode === 'poll'
          ? 'store-and-forward over those contacts — input rides the next cycle (sleep 0 approaches live)'
          : 'live channel over the WebSocket beacon'

  return (
    <div className="build-summary" title="What this build bakes, composed from the picks above">
      <div>
        <span className="summary-key">enroll</span> once on <code>{front}</code> via {via}
      </div>
      <div>
        <span className="summary-key">contact</span> {contact}
      </div>
      <div>
        <span className="summary-key">interactive</span> {interactive}
      </div>
    </div>
  )
}
