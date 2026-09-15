import { useEffect, useRef, useState } from 'react'
import {
  ApiError,
  type ShellSession,
  type ShellUpgrade,
  closeShell,
  readShellOutput,
  sendShellInput,
  upgradeShell,
} from '../api'
import { StatusBadge } from './StatusBadge'

// The caught-shell console, styled as a terminal: the held connection's
// output read by cursor -- each read parks server-side until the next chunk,
// so one request stays in flight instead of a spinning poll -- with typing
// posted through the input route and the close route ending the shell as an
// operator action. The upgrade button renders the paste-ready launchers
// that grow the shell into a real implant; the paste itself is the
// operator's, through this same input line.
export function ShellConsole({
  engagementId,
  shell,
  onEnded,
}: {
  engagementId: string
  shell: ShellSession
  onEnded: () => void
}) {
  const [transcript, setTranscript] = useState('')
  const [line, setLine] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [upgrade, setUpgrade] = useState<ShellUpgrade | null>(null)
  const [copied, setCopied] = useState<string | null>(null)
  const transcriptRef = useRef<HTMLPreElement>(null)
  // The output cursor survives re-renders; the console never re-reads what
  // it already holds.
  const cursorRef = useRef(0)

  useEffect(() => {
    setTranscript('')
    cursorRef.current = 0
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

  const done = shell.status !== 'live'

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
      setUpgrade(await upgradeShell(engagementId, shell.sessionId))
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

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

  return (
    <div className="terminal">
      <div className="terminal-header">
        <code>shell</code>
        <span>·</span>
        <code>{shell.sessionId.slice(0, 8)}</code>
        <span>·</span>
        <code>{shell.remoteAddress}</code>
        <StatusBadge status={shell.status} />
        <span className="spacer">
          <button className="ghost sm" onClick={() => void onUpgrade()} disabled={busy}>
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
        {transcript || '—'}
      </pre>
      <form className="task-form" onSubmit={onSend}>
        <span className="prompt" aria-hidden="true">
          ›
        </span>
        <input
          className="wide"
          placeholder={done ? 'shell ended' : 'type a command'}
          value={line}
          disabled={done || busy}
          onChange={(e) => setLine(e.target.value)}
        />
        <button className="primary sm" type="submit" disabled={busy || done || !line}>
          Send
        </button>
        <button className="ghost sm" type="button" onClick={() => void onInterrupt()} disabled={busy || done}>
          ^C
        </button>
      </form>
      {upgrade && (
        <div className="upgrade-panel">
          <p>
            Paste one of these into the shell to grow it into an enrolled implant. The credential is
            single-use and expires <time>{new Date(upgrade.tokenExpiresAt).toLocaleTimeString()}</time>.
          </p>
          {upgrade.launchers.map((launcher) => (
            <div key={launcher.id} className="upgrade-launcher">
              <code>{launcher.id}</code>
              <code className="upgrade-command">{launcher.command}</code>
              <button className="ghost sm" onClick={() => void copy(launcher.id, launcher.command)}>
                {copied === launcher.id ? 'Copied' : 'Copy'}
              </button>
            </div>
          ))}
        </div>
      )}
      {error && <p className="error terminal-error">{error}</p>}
    </div>
  )
}
