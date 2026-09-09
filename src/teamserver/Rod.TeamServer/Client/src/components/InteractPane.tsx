import { useEffect, useRef, useState } from 'react'
import { getTask, sendTaskInput } from '../api'
import { StatusBadge } from './StatusBadge'

// The channel pane, styled as a terminal: a live channel task's transcript
// with an input line and stdin close. The transcript is the task's own output
// server-side (the record of the session is the session), so the pane polls it
// while the channel runs instead of holding a second event stream; typing
// posts through the input route and Close stdin sends the eof that ends (or
// half-closes, for a tunnel) the channel.
//
// Shared by the task log (a channel row's Interact action), the session
// console (inline channel panes), and the shell dialog (the live session at
// the bottom). Inside the dialog the pane is embedded: same terminal, no
// duplicate Hide button -- the dialog's Close is the one affordance.
export function InteractPane({
  engagementId,
  taskId,
  verb,
  onClose,
  embedded = false,
}: {
  engagementId: string
  taskId: string
  verb: string
  onClose: () => void
  embedded?: boolean
}) {
  const [transcript, setTranscript] = useState('')
  const [status, setStatus] = useState('Dispatched')
  const [line, setLine] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const transcriptRef = useRef<HTMLPreElement>(null)
  const doneRef = useRef(false)
  doneRef.current = status !== 'Dispatched'

  useEffect(() => {
    let stopped = false
    const poll = async () => {
      try {
        const t = await getTask(engagementId, taskId)
        if (stopped) return
        setTranscript(t.output ?? '')
        setStatus(t.status)
        setError(null)
      } catch (e) {
        if (!stopped) setError(String(e))
      }
    }
    void poll()
    const timer = setInterval(() => {
      if (!stopped && !doneRef.current) void poll()
    }, 500)
    return () => {
      stopped = true
      clearInterval(timer)
    }
  }, [engagementId, taskId])

  // Keep the newest output in view as the transcript grows.
  useEffect(() => {
    const pre = transcriptRef.current
    if (pre) pre.scrollTop = pre.scrollHeight
  }, [transcript])

  const done = status !== 'Dispatched'

  const onSend = async (event: React.FormEvent) => {
    event.preventDefault()
    if (!line || busy || done) return
    setBusy(true)
    try {
      await sendTaskInput(engagementId, taskId, line + '\n')
      setLine('')
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  const onCloseStdin = async () => {
    setBusy(true)
    try {
      await sendTaskInput(engagementId, taskId, '', true)
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  // The interrupt: sends the ETX byte (0x03) the way a real terminal does.
  // On a PTY-backed channel (the Unix interactive shell) the line discipline
  // turns it into SIGINT for the foreground program; on a pipes channel it
  // is a byte the shell ignores -- harmless, but the button only matters
  // where it works, so its tooltip says so.
  const onInterrupt = async () => {
    setBusy(true)
    try {
      await sendTaskInput(engagementId, taskId, '\x03')
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="terminal">
      <div className="terminal-header">
        <code>{verb}</code>
        <span>·</span>
        <code>{taskId.slice(0, 8)}</code>
        <StatusBadge status={status} />
        {!embedded && (
          <span className="spacer">
            <button className="ghost sm" onClick={onClose}>
              Hide
            </button>
          </span>
        )}
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
          placeholder={done ? 'channel closed' : 'type a command'}
          value={line}
          disabled={done || busy}
          onChange={(e) => setLine(e.target.value)}
        />
        <button className="primary sm" type="submit" disabled={busy || done || !line}>
          Send
        </button>
        <button
          className="ghost sm"
          type="button"
          onClick={() => void onInterrupt()}
          disabled={busy || done}
          title="Send the interrupt byte (Ctrl+C) -- SIGINT to the foreground program on a PTY-backed shell"
        >
          ^C
        </button>
        <button className="ghost sm" type="button" onClick={() => void onCloseStdin()} disabled={busy || done}>
          Close stdin
        </button>
      </form>
      {error && <p className="error terminal-error">{error}</p>}
    </div>
  )
}
