import { useEffect, useMemo, useRef, useState } from 'react'
import {
  ApiError,
  type ListenerSummary,
  type PayloadSummary,
  type ShellSession,
  type ShellUpgrade,
  closeShell,
  readShellOutput,
  sendShellInput,
  upgradeShell,
} from '../api'
import { launcherHint } from '../launcherFamilies'
import { deliverable, payloadLabel } from '../payloads'
import { parseTerminalOutput } from '../terminalText'
import { useClaim } from '../useClaim'
import { CopyButton } from './CopyButton'
import { StatusBadge } from './StatusBadge'

// The caught-shell console, styled as a terminal: the held connection's
// output read by cursor -- each read parks server-side until the next chunk,
// so one request stays in flight instead of a spinning poll -- with typing
// posted through the input route and the close route ending the shell as an
// operator action. The input half is claim-aware (architecture.md Sec 4.5):
// a shell another operator holds locks the input line and names the holder,
// and this console's typing takes the claim on first post. The upgrade
// button renders the paste-ready launchers that grow the shell into a real
// implant (naming the web front the fetch rides when several exist); the
// paste itself is the operator's, through this same input line.
export function ShellConsole({
  engagementId,
  shell,
  webListeners,
  payloads,
  onEnded,
}: {
  engagementId: string
  shell: ShellSession
  webListeners: ListenerSummary[]
  payloads: PayloadSummary[]
  onEnded: () => void
}) {
  const [transcript, setTranscript] = useState('')
  const [line, setLine] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [upgrade, setUpgrade] = useState<ShellUpgrade | null>(null)
  const [upgradeListenerId, setUpgradeListenerId] = useState('')
  const [upgradePayloadId, setUpgradePayloadId] = useState('')
  const transcriptRef = useRef<HTMLPreElement>(null)
  // A console's input is where the operator lands: focused when the console
  // opens and re-focused after every submission, so Enter never strands the
  // cursor outside the line.
  const inputRef = useRef<HTMLInputElement>(null)
  // The output cursor survives re-renders; the console never re-reads what
  // it already holds.
  const cursorRef = useRef(0)
  const { locked, holderHandle } = useClaim(engagementId, 'shell', shell.sessionId)

  useEffect(() => {
    setTranscript('')
    cursorRef.current = 0
    inputRef.current?.focus()
  }, [shell.sessionId])

  // The output loop: one long poll in flight at a time; each answer's
  // chunks append to the transcript and advance the cursor. A 410 answer
  // means the shell ended server-side -- the registry holds the record and
  // the console stops reading.
  useEffect(() => {
    let stopped = false
    const pump = async () => {
      while (!stopped) {
        try {
          const output = await readShellOutput(engagementId, shell.sessionId, cursorRef.current)
          if (stopped) return
          if (output.chunks.length > 0) {
            cursorRef.current = output.latestSequence
            setTranscript((current) => current + output.chunks.map((c) => c.text).join(''))
            setError(null)
          }
        } catch (e) {
          if (stopped) return
          if (e instanceof ApiError && e.status === 410) {
            onEnded()
            return
          }
          setError(String(e))
          await new Promise((resolve) => setTimeout(resolve, 1000))
        }
      }
    }
    void pump()
    return () => {
      stopped = true
    }
  }, [engagementId, shell.sessionId, onEnded])

  // Keep the newest output in view as the transcript grows.
  useEffect(() => {
    const pre = transcriptRef.current
    if (pre) pre.scrollTop = pre.scrollHeight
  }, [transcript])

  // The raw stream reduced to styled lines: a shell under a pseudo-terminal
  // speaks terminal control data (colors, title sets, shell-integration
  // markers), and the console renders what is visible rather than dumping
  // the control bytes.
  const lines = useMemo(() => parseTerminalOutput(transcript), [transcript])

  const done = shell.status !== 'live'

  // The upgrade grows the shell into an enrolled implant, so it needs a
  // payload whose baked enrollment credential still lives -- the same gate
  // the Launchers picker applies, said up front instead of discovered by
  // the paste.
  const upgradeBlockedReason = payloads.length === 0
    ? 'No payload in this engagement yet -- build one under Build first'
    : payloads.every((p) => !deliverable(p))
      ? 'Every built payload\'s enrollment credential is dead (revoked, spent, or expired) -- build a fresh one'
      : null

  const onSend = async (event: React.FormEvent) => {
    event.preventDefault()
    if (!line || busy || done) return
    setBusy(true)
    try {
      await sendShellInput(engagementId, shell.sessionId, line + '\n')
      setLine('')
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
      inputRef.current?.focus()
    }
  }

  // The interrupt byte the way a real terminal sends it; on a dumb shell it
  // is a byte the shell's foreground program reads or ignores -- the button
  // carries no PTY promise a caught shell cannot keep.
  const onInterrupt = async () => {
    setBusy(true)
    try {
      await sendShellInput(engagementId, shell.sessionId, '\x03')
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
      inputRef.current?.focus()
    }
  }

  const onCloseShell = async () => {
    setBusy(true)
    try {
      await closeShell(engagementId, shell.sessionId)
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  const onUpgrade = async () => {
    setBusy(true)
    try {
      setUpgrade(
        await upgradeShell(
          engagementId,
          shell.sessionId,
          upgradePayloadId || undefined,
          upgradeListenerId || undefined,
        ),
      )
      setError(null)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="terminal">
      <div className="terminal-header">
        <code>shell</code>
        <span>·</span>
        <code>{shell.sessionId.slice(0, 8)}</code>
        <span>·</span>
        <code>{shell.remoteAddress}</code>
        <StatusBadge status={shell.status} />
        {locked && (
          <span className="muted" title="Another operator holds this shell's input; it releases when they dismiss their console or disconnect">
            held by {holderHandle}
          </span>
        )}
        <span className="spacer">
          <button
            className="ghost sm"
            onClick={() => void onUpgrade()}
            disabled={busy || upgradeBlockedReason !== null}
            title={upgradeBlockedReason ?? 'Render the paste-ready launchers that grow this shell into an enrolled implant'}
          >
            Upgrade
          </button>
          <button
            className="ghost sm"
            onClick={() => void onCloseShell()}
            disabled={busy || done}
            title="End this shell (the target-side one-liner exits with the socket)"
          >
            Close
          </button>
        </span>
      </div>
      <pre className="interact-transcript" ref={transcriptRef}>
        {lines.length === 0 || (lines.length === 1 && lines[0].spans.length === 0)
          ? '—'
          : lines.map((line, i) => (
              <div className="t-line" key={i}>
                {line.spans.length === 0
                  ? '\u200b'
                  : line.spans.map((span, j) => (
                      <span
                        key={j}
                        style={{
                          color: span.color,
                          fontWeight: span.bold ? 600 : undefined,
                          opacity: span.dim ? 0.55 : undefined,
                          textDecoration: span.underline ? 'underline' : undefined,
                        }}
                      >
                        {span.text}
                      </span>
                    ))}
              </div>
            ))}
      </pre>
      <form className="task-form" onSubmit={onSend}>
        <span className="prompt" aria-hidden="true">
          ›
        </span>
        <input
          className="wide"
          ref={inputRef}
          placeholder={done ? 'shell ended' : locked ? `held by ${holderHandle}` : 'type a command'}
          value={line}
          disabled={done || busy || locked}
          onChange={(e) => setLine(e.target.value)}
        />
        <button className="primary sm" type="submit" disabled={busy || done || locked || !line}>
          Send
        </button>
        <button className="ghost sm" type="button" onClick={() => void onInterrupt()} disabled={busy || done || locked}>
          ^C
        </button>
      </form>
      {upgrade && (
        <div className="upgrade-panel">
          <p>
            Paste one of these into the shell to grow it into an enrolled implant. The credential is
            single-use and expires <time>{new Date(upgrade.tokenExpiresAt).toLocaleTimeString()}</time>.
          </p>
          {/* The same picks the Launchers render form offers: which build the
              fetch delivers, and which front serves it. */}
          <div className="upgrade-launcher">
            <code>deliver</code>
            <select
              value={upgradePayloadId}
              onChange={(e) => setUpgradePayloadId(e.target.value)}
              title="The payload the fetch delivers; the newest build stands in when unnamed. A dead credential cannot enroll the beacon it downloads, so those entries refuse."
            >
              <option value="">Newest build</option>
              {payloads.map((p) => {
                const dead = !deliverable(p)
                return (
                  <option key={p.artifactId} value={p.artifactId} disabled={dead}>
                    {payloadLabel(p)}
                    {dead ? ' — credential dead' : ''}
                  </option>
                )
              })}
            </select>
          </div>
          {webListeners.length > 1 && (
            <div className="upgrade-launcher">
              <code>fetch via</code>
              <select
                value={upgradeListenerId}
                onChange={(e) => setUpgradeListenerId(e.target.value)}
                title="Which web listener's front the payload fetch rides -- every HTTP(S)/mTLS listener serves it on its public endpoint; there is no separate file host. Auto prefers https over mTLS over cleartext; name one when the fetch must cross a specific redirector."
              >
                <option value="">auto — hardened front preferred</option>
                {webListeners.map((l) => (
                  <option key={l.id} value={l.id}>
                    {l.name} ({l.transport} → {l.publicEndpoint})
                  </option>
                ))}
              </select>
            </div>
          )}
          {upgrade.launchers.map((launcher) => (
            <div key={launcher.id} className="upgrade-launcher" title={launcherHint(launcher.id)}>
              <code>{launcher.id}</code>
              <code className="upgrade-command">{launcher.command}</code>
              <CopyButton text={launcher.command} />
            </div>
          ))}
        </div>
      )}
      {error && <p className="error terminal-error">{error}</p>}
    </div>
  )
}
