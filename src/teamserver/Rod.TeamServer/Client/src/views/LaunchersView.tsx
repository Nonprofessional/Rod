import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  type LauncherRender,
  type ListenerSummary,
  type PayloadSummary,
  listListeners,
  listPayloads,
  renderLaunchers,
} from '../api'
import { catchLaunchers } from '../catchOneLiners'
import { Icon } from '../components/Icons'
import { osIconFor } from '../osKind'

// The engagement's one-liner home: every command an operator copies out,
// in one place. Two surfaces live here. "Catch a shell" renders the
// paste-ready reverse-shell one-liners per shellcatch listener -- no
// credential is involved, the address is the listener's public endpoint,
// and the caught shell lands in the Shells roster. "Deliver a beacon"
// drives the launcher render endpoint: pick the stage-2 payload, and the
// server mints the fetch's deployment credential and answers with the
// downloader one-liner per shell family; the credential shows exactly once
// and never lands anywhere else.

// The redeem budgets an operator realistically picks. Single-use is the
// default posture (one paste, one beacon); the wider budgets serve a
// many-host deployment from one render.
const USE_OPTIONS: { value: number; label: string }[] = [
  { value: 1, label: 'Single use' },
  { value: 0, label: 'Unlimited (until expiry)' },
  { value: 5, label: '5 uses' },
  { value: 25, label: '25 uses' },
]

const LIFETIME_OPTIONS: { value: number; label: string }[] = [
  { value: 30, label: '30 minutes' },
  { value: 120, label: '2 hours' },
  { value: 720, label: '12 hours' },
  { value: 1440, label: '24 hours' },
]

function payloadLabel(p: PayloadSummary): string {
  const target = p.target ?? p.language
  return [
    p.class,
    target,
    p.fingerprint ? p.fingerprint.slice(0, 12) : null,
    new Date(p.builtAt).toLocaleString(),
  ]
    .filter(Boolean)
    .join(' · ')
}

export function LaunchersView({ engagementId }: { engagementId: string }) {
  const [payloads, setPayloads] = useState<PayloadSummary[]>([])
  const [listeners, setListeners] = useState<ListenerSummary[]>([])
  const [payloadId, setPayloadId] = useState('')
  const [listenerId, setListenerId] = useState('')
  const [maxUses, setMaxUses] = useState(1)
  const [lifetimeMinutes, setLifetimeMinutes] = useState(30)
  const [rendered, setRendered] = useState<LauncherRender | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [copied, setCopied] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    void (async () => {
      try {
        const [allPayloads, allListeners] = await Promise.all([
          listPayloads(engagementId),
          listListeners(engagementId),
        ])
        if (cancelled) return
        setPayloads(allPayloads)
        setListeners(allListeners)
        setError(null)
      } catch (e) {
        if (!cancelled) setError(String(e))
      }
    })()
    return () => {
      cancelled = true
    }
  }, [engagementId])

  // A render belongs to the engagement it was cut for; a stale panel must
  // never survive the switch.
  useEffect(() => {
    setRendered(null)
  }, [engagementId])

  const webListeners = useMemo(
    () => listeners.filter((l) => ['http', 'https', 'mtls'].includes(l.transport)),
    [listeners],
  )
  const catchers = useMemo(
    () => listeners.filter((l) => l.transport === 'shellcatch'),
    [listeners],
  )

  const onRender = useCallback(async () => {
    setBusy(true)
    try {
      setRendered(
        await renderLaunchers(engagementId, {
          payloadId: payloadId || undefined,
          listenerId: listenerId || undefined,
          maxUses,
          lifetimeMinutes,
        }),
      )
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }, [engagementId, payloadId, listenerId, maxUses, lifetimeMinutes])

  const copy = async (id: string, text: string) => {
    try {
      await navigator.clipboard.writeText(text)
      setCopied(id)
      window.setTimeout(() => setCopied(null), 1500)
    } catch {
      // Clipboard permission denied: the command stays selectable to copy
      // by hand.
    }
  }

  const usesLabel = useMemo(
    () => USE_OPTIONS.find((o) => o.value === maxUses)?.label ?? `${maxUses} uses`,
    [maxUses],
  )
  const lifetimeLabel = useMemo(
    () => LIFETIME_OPTIONS.find((o) => o.value === lifetimeMinutes)?.label ?? `${lifetimeMinutes} min`,
    [lifetimeMinutes],
  )
  const frontLabel = useMemo(() => {
    if (!listenerId) return 'preferred front'
    const named = webListeners.find((l) => l.id === listenerId)
    return named ? named.name : 'named front'
  }, [listenerId, webListeners])

  return (
    <section className="view">
      <h2>Launchers</h2>
      <p className="muted">
        Every paste-ready one-liner this engagement can cut, in one place: reverse shells that land
        in the Shells roster, and stage-2 fetches that grow a beacon in the Implants table.
      </p>
      {error && <p className="error">{error}</p>}

      {catchers.length > 0 && (
        <div className="card">
          <h3>Catch a shell</h3>
          <p className="muted">
            Reverse-shell one-liners per shellcatch listener. No credential is involved — the
            address is the listener's public endpoint; paste one on the target and the shell lands
            in the Shells roster.
          </p>
          {catchers.map((listener) => {
            const launchers = catchLaunchers(listener.publicEndpoint)
            if (launchers.length === 0) return null
            return (
              <details key={listener.id} className="catch-details">
                <summary title="The paste-ready reverse-shell one-liners for this listener's public endpoint — expand to copy one">
                  Catch on <code>{listener.name}</code> · <code>{listener.publicEndpoint}</code>
                  <span className="muted"> — {launchers.length} one-liners</span>
                </summary>
                <div className="upgrade-panel">
                  {launchers.map((launcher) => (
                    <div key={launcher.id} className="upgrade-launcher">
                      <span title={`For ${launcher.os} targets`}>
                        <Icon name={osIconFor(launcher.os)} className="wire-icon" />
                      </span>
                      <code>{launcher.id}</code>
                      <code className="upgrade-command">{launcher.command}</code>
                      <button
                        className="ghost sm"
                        onClick={() => void copy(`catch:${listener.id}:${launcher.id}`, launcher.command)}
                      >
                        {copied === `catch:${listener.id}:${launcher.id}` ? 'Copied' : 'Copy'}
                      </button>
                    </div>
                  ))}
                </div>
              </details>
            )
          })}
        </div>
      )}

      {payloads.length === 0 || webListeners.length === 0 ? (
        <div className="card">
          <h3>Deliver a beacon</h3>
          <div className="empty">
            <Icon name="copy" />
            {payloads.length === 0 && webListeners.length === 0
              ? 'Delivery needs a stage-2 payload (Build) and an HTTP(S) listener (Listeners) — create both first.'
              : payloads.length === 0
                ? 'No stage-2 payload in this engagement yet — build one under Build first.'
                : 'No HTTP(S) listener in this engagement yet — create one under Listeners first.'}
          </div>
        </div>
      ) : (
        <div className="card">
          <h3>Deliver a beacon</h3>
          <p className="muted">
            A one-liner that fetches the stage-2 payload over the engagement's web front and runs
            it. The fetch presents a freshly minted credential; the enrollment that follows spends
            it.
          </p>
          <div className="inline-form">
            <select
              value={payloadId}
              onChange={(e) => setPayloadId(e.target.value)}
              title="The stage-2 payload the fetch delivers; the newest build stands in when unnamed"
            >
              <option value="">Newest build</option>
              {payloads.map((p) => (
                <option key={p.artifactId} value={p.artifactId}>
                  {payloadLabel(p)}
                </option>
              ))}
            </select>
            <button className="primary" onClick={() => void onRender()} disabled={busy}>
              {busy ? 'Rendering…' : 'Render'}
            </button>
          </div>
          {/* The choices almost every render leaves alone. The payload bakes
              its own endpoints for after it runs; the fetch still needs a
              front to ride and a credential to present, and the defaults --
              the hardened front, single-use, half an hour -- are the right
              posture for the one-paste-one-beacon shape. */}
          <details className="build-advanced">
            <summary title="The fetch front and the minted credential's policy — the defaults fit the one-paste-one-beacon shape">
              Fetch front &amp; credential — {frontLabel} · {usesLabel.toLowerCase()} ·{' '}
              {lifetimeLabel.toLowerCase()}
            </summary>
            <div className="inline-form">
              <select
                value={listenerId}
                onChange={(e) => setListenerId(e.target.value)}
                title="The web front the fetch rides; the hardened members are preferred when unnamed"
              >
                <option value="">Preferred HTTP(S) front</option>
                {webListeners.map((l) => (
                  <option key={l.id} value={l.id}>
                    {l.name} · {l.transport} · {l.publicEndpoint}
                  </option>
                ))}
              </select>
              <select
                value={maxUses}
                onChange={(e) => setMaxUses(Number(e.target.value))}
                title="How many redeems the minted credential allows before it is spent"
              >
                {USE_OPTIONS.map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </select>
              <select
                value={lifetimeMinutes}
                onChange={(e) => setLifetimeMinutes(Number(e.target.value))}
                title="How long the minted credential lives"
              >
                {LIFETIME_OPTIONS.map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </select>
            </div>
          </details>

          {rendered && (
            <div className="upgrade-panel">
              <p>
                Fetch URL <code>{rendered.url}</code> · credential{' '}
                <code className="upgrade-command">{rendered.tokenSecret}</code>{' '}
                <button
                  className="ghost sm"
                  onClick={() => void copy('token', rendered.tokenSecret)}
                >
                  {copied === 'token' ? 'Copied' : 'Copy'}
                </button>{' '}
                · {usesLabel.toLowerCase()}, expires{' '}
                {new Date(rendered.tokenExpiresAt).toLocaleTimeString()}. Paste one of these on
                the target; the beacon lands in the Implants table.
              </p>
              {rendered.launchers.map((launcher) => (
                <div key={launcher.id} className="upgrade-launcher">
                  <span title={`For ${launcher.os} targets`}>
                    <Icon name={osIconFor(launcher.os)} className="wire-icon" />
                  </span>
                  <code>{launcher.id}</code>
                  <code className="upgrade-command">{launcher.command}</code>
                  <button
                    className="ghost sm"
                    onClick={() => void copy(launcher.id, launcher.command)}
                  >
                    {copied === launcher.id ? 'Copied' : 'Copy'}
                  </button>
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </section>
  )
}
