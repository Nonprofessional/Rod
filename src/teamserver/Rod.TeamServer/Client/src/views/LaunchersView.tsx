import { Fragment, useCallback, useEffect, useMemo, useState } from 'react'
import {
  type LauncherRow,
  type ListenerSummary,
  type PayloadSummary,
  deleteLauncher,
  listLaunchers,
  listListeners,
  listPayloads,
  renderLaunchers,
} from '../api'
import { catchLaunchers } from '../catchOneLiners'
import { launcherHint } from '../launcherFamilies'
import { CopyButton } from '../components/CopyButton'
import { Icon } from '../components/Icons'
import { osIconFor } from '../osKind'
import { useNow } from '../when'

// The engagement's one-liner home: every command an operator copies out,
// in one place. Three surfaces live here. "Catch a shell" renders the
// paste-ready reverse-shell one-liners per shellcatch listener -- no
// credential is involved, the address is the listener's public endpoint,
// and the caught shell lands in the Shells roster. "Deliver a beacon"
// cuts a payload-fetch render: pick the payload, the server mints the
// download credential and answers with the downloader one-liner per shell
// family. "Kept launchers" is the list those renders land in -- every cut
// is kept, so the operator can come back to it: re-copy the command any
// time, watch the credential's budget, revoke it the moment it leaks, and
// delete the row when it is spent.

// The redeem budgets an operator realistically picks. Single-use is the
// default posture (one paste, one download); the wider budgets serve a
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
    // The wire class reads as noise for the only implant class there is;
    // the other kinds (a web-shell script) still stand out.
    p.class === 'Implant' ? null : p.class,
    target,
    // The library's own identifier, at the same width the Payloads tab's
    // Fingerprint column shows, so a picker entry matches its row there.
    p.fingerprint ? p.fingerprint.slice(0, 16) : null,
    new Date(p.builtAt).toLocaleString(),
  ]
    .filter(Boolean)
    .join(' · ')
}

export function LaunchersView({
  engagementId,
  onlineTick,
}: {
  engagementId: string
  onlineTick?: number
}) {
  const [payloads, setPayloads] = useState<PayloadSummary[]>([])
  const [listeners, setListeners] = useState<ListenerSummary[]>([])
  const [rows, setRows] = useState<LauncherRow[]>([])
  const [payloadId, setPayloadId] = useState('')
  const [listenerId, setListenerId] = useState('')
  const [maxUses, setMaxUses] = useState(1)
  const [lifetimeMinutes, setLifetimeMinutes] = useState(30)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [commandsFor, setCommandsFor] = useState<string | null>(null)

  // The quiet clock keeps expiry counts moving between interactions.
  const now = useNow(30_000)

  const refreshRows = useCallback(async () => {
    try {
      setRows(await listLaunchers(engagementId))
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }, [engagementId])

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

  // The live tick rides the SSE PayloadFetched frame: a target pulling a
  // credential spends it on the server, and the row's remaining budget
  // moves here without an operator pressing refresh.
  useEffect(() => {
    void refreshRows()
  }, [refreshRows, onlineTick])

  // A selection must not survive the engagement switch.
  useEffect(() => {
    setCommandsFor(null)
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
      await renderLaunchers(engagementId, {
        payloadId: payloadId || undefined,
        listenerId: listenerId || undefined,
        maxUses,
        lifetimeMinutes,
      })
      await refreshRows()
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }, [engagementId, payloadId, listenerId, maxUses, lifetimeMinutes, refreshRows])

  const onDelete = async (row: LauncherRow) => {
    if (!window.confirm('Delete this launcher? Its credential stops working at the next fetch (every pasted copy dies with it), and the row goes -- the mint\'s history stays on the audit trail.'))
      return
    try {
      await deleteLauncher(engagementId, row.launcherId)
      await refreshRows()
      setError(null)
    } catch (e) {
      setError(String(e))
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
    if (!listenerId) return 'auto front'
    const named = webListeners.find((l) => l.id === listenerId)
    return named ? named.name : 'named front'
  }, [listenerId, webListeners])

  return (
    <section className="view">
      <h2>Launchers</h2>
      <p className="muted">
        Every paste-ready one-liner this engagement can cut, in one place: reverse shells that land
        in the Shells roster, and payload fetches that grow a beacon in the Implants table. Every
        fetch render is kept below — re-copy it any time; Delete closes its lifecycle (the
        credential dies with the row, and the mint's history is the audit trail's to keep).
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
                      <CopyButton text={launcher.command} />
                    </div>
                  ))}
                </div>
              </details>
            )
          })}
        </div>
      )}

      {/* One card, one flow: the render form above, the kept rows below --
          the same single surface the Build tab gives its form and job strip,
          so cutting a launcher and coming back to it reads as one place
          instead of two boxed steps. */}
      <div className="card">
        <h3>Deliver a beacon</h3>
        {payloads.length === 0 || webListeners.length === 0 ? (
          <div className="empty">
            <Icon name="copy" />
            {payloads.length === 0 && webListeners.length === 0
              ? 'Delivery needs a payload (Build) and an HTTP(S) listener (Listeners) — create both first.'
              : payloads.length === 0
                ? 'No payload in this engagement yet — build one under Build first.'
                : 'No HTTP(S) listener in this engagement yet — create one under Listeners first.'}
          </div>
        ) : (
          <>
            <p className="muted">
              A one-liner that fetches the payload over the engagement's web front and runs
              it. Each served fetch spends one use of a freshly minted credential; the enrollment
              that follows rides the credential baked into the fetched artifact.
            </p>
            <div className="inline-form">
              <select
                value={payloadId}
                onChange={(e) => setPayloadId(e.target.value)}
                title="The payload the fetch delivers; the newest build stands in when unnamed"
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
                its own endpoints and enrollment credential; the fetch still
                needs a front to ride and a download credential to present, and
                the defaults -- the hardened front, single use, half an hour --
                are the right posture for the one-paste-one-download shape. */}
            <details className="build-advanced">
              <summary title="The fetch front and the minted credential's policy — the defaults fit the one-paste-one-download shape">
                Fetch front &amp; credential — {frontLabel} · {usesLabel.toLowerCase()} ·{' '}
                {lifetimeLabel.toLowerCase()}
              </summary>
              <div className="inline-form">
                <select
                  value={listenerId}
                  onChange={(e) => setListenerId(e.target.value)}
                  title="Every HTTP(S)/mTLS listener serves the payload fetch on its own public endpoint -- there is no separate file host, and the teamserver's own address never appears in a command. Name a listener when the fetch must cross a specific front (a redirector, say); unnamed prefers https, then mTLS, then the newest."
                >
                  <option value="">Auto — hardened front first</option>
                  {webListeners.map((l) => (
                    <option key={l.id} value={l.id}>
                      {l.name} · {l.transport} · {l.publicEndpoint}
                    </option>
                  ))}
                </select>
                <select
                  value={maxUses}
                  onChange={(e) => setMaxUses(Number(e.target.value))}
                  title="How many served fetches the minted credential allows"
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
          </>
        )}

        <h3 className="jobs-head">Kept launchers</h3>
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th></th>
                <th>Cut</th>
                <th>Payload</th>
                <th>Front</th>
                <th>Credential</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {rows.length === 0 && (
                <tr>
                  <td colSpan={6}>
                    <div className="empty">
                      <Icon name="copy" />
                      No launchers kept yet — every render above lands here, re-copyable until its
                      credential expires.
                    </div>
                  </td>
                </tr>
              )}
              {rows.map((row) => {
                const expired = new Date(row.expiresAt).getTime() <= now
                const revoked = row.revokedAt != null
                // A bounded credential whose token is no longer stored is out
                // of downloads no matter which end it met -- spent to zero,
                // revoked, or expired and swept; reading null as "alive" was
                // exactly the contradiction the old two-column display showed.
                const spent =
                  row.maxUses !== 0 &&
                  (row.tokenRemainingUses == null || row.tokenRemainingUses <= 0)
                const live = !revoked && !expired && !spent
                const status = revoked
                  ? `revoked ${new Date(row.revokedAt!).toLocaleTimeString()}`
                  : expired
                    ? `expired ${new Date(row.expiresAt).toLocaleTimeString()}`
                    : spent
                      ? 'no downloads left'
                      : row.maxUses === 0
                        ? `usable · unlimited downloads · until ${new Date(row.expiresAt).toLocaleTimeString()}`
                        : `usable · ${row.tokenRemainingUses} of ${row.maxUses} downloads left · until ${new Date(row.expiresAt).toLocaleTimeString()}`
                // The commands expand under their own row -- the shared
                // chevron-and-row-click affordance every detail table uses,
                // so the one-liners belong beside the row they were cut for,
                // not pooled below the table. Only while the credential
                // still serves fetches: a dead credential's command would
                // download nothing.
                const commandsOpen = commandsFor === row.launcherId
                return (
                  <Fragment key={row.launcherId}>
                    <tr
                      className={live ? 'console-row' : 'row-dim'}
                      onClick={
                        live
                          ? () =>
                              setCommandsFor((current) =>
                                current === row.launcherId ? null : row.launcherId,
                              )
                          : undefined
                      }
                    >
                      <td onClick={(e) => e.stopPropagation()}>
                        {live && (
                          <button
                            className={`ghost sm row-expand${commandsOpen ? ' open' : ''}`}
                            aria-label={commandsOpen ? 'Hide the commands' : 'Show the commands'}
                            title={
                              commandsOpen
                                ? 'Hide the one-liners'
                                : 'The one-liners this row was cut with'
                            }
                            onClick={() =>
                              setCommandsFor((current) =>
                                current === row.launcherId ? null : row.launcherId,
                              )
                            }
                          >
                            <Icon name={commandsOpen ? 'chevronDown' : 'chevronRight'} />
                          </button>
                        )}
                      </td>
                      <td title={`Cut by ${row.createdBy}`}>
                        {new Date(row.createdAt).toLocaleString()}
                      </td>
                      <td>
                        {/* The library's identifier for the delivered
                            payload, at the Payloads tab's own column width,
                            so a row matches its payload without the id
                            detour; a deleted payload falls back to the bare
                            artifact id. */}
                        {row.payloadFingerprint ? (
                          <code
                            title={`Fingerprint ${row.payloadFingerprint} · artifact ${row.payloadId} — match it on the Payloads tab`}
                          >
                            {row.payloadFingerprint.slice(0, 16)}
                          </code>
                        ) : (
                          <code
                            className="muted"
                            title={`Artifact ${row.payloadId} — no longer in the payload library`}
                          >
                            {row.payloadId.slice(0, 8)}
                          </code>
                        )}
                      </td>
                      <td title={row.frontEndpoint}>{row.frontName}</td>
                      <td
                        title={
                          live
                            ? 'The credential still serves fetches: copies of the command download until the budget or the window closes'
                            : 'This credential no longer serves fetches; the row stays until deleted (Delete also kills a live credential)'
                        }
                      >
                        {status}
                      </td>
                      <td onClick={(e) => e.stopPropagation()}>
                        <div className="row-actions">
                          <button className="ghost sm" onClick={() => void onDelete(row)}>
                            Delete
                          </button>
                        </div>
                      </td>
                    </tr>
                    {commandsOpen && (
                      <tr className="payload-detail-row">
                        <td colSpan={6}>
                          <div className="upgrade-panel">
                            <p>
                              Fetch URL <code>{row.url}</code> · credential{' '}
                              <code className="upgrade-command">{row.tokenSecret}</code>{' '}
                              <CopyButton text={row.tokenSecret} />
                            </p>
                            {row.launchers.map((launcher) => (
                              <div key={launcher.id} className="upgrade-launcher" title={launcherHint(launcher.id)}>
                                <span title={`For ${launcher.os} targets`}>
                                  <Icon name={osIconFor(launcher.os)} className="wire-icon" />
                                </span>
                                <code>{launcher.id}</code>
                                <code className="upgrade-command">{launcher.command}</code>
                                <CopyButton text={launcher.command} />
                              </div>
                            ))}
                          </div>
                        </td>
                      </tr>
                    )}
                  </Fragment>
                );
              })}
            </tbody>
          </table>
        </div>
        {rows.length > 0 && (
          <p className="muted">
            Commands re-render from each row's URL and credential, so an old row always copies in
            the current shape. Delete closes a row's whole lifecycle: the credential dies wherever
            a copy of the command carries it, the row goes, and the mint's history is the audit
            trail's to keep.
          </p>
        )}
      </div>
    </section>
  )
}
