import { useCallback, useEffect, useState } from 'react'
import { getTask, issueTask, listImplantTasks } from '../api'
import { InteractPane } from './InteractPane'

// One ended shell session as the dialog renders it: the task record's own
// facts plus the started-at stamp the detail endpoint does not carry.
interface ShellSession {
  taskId: string
  createdAt: string
  status: string
  outcome: string | null
  output: string | null
}

// The interactive shell as a dialog: one window per implant, not per channel
// task. Every shell.interact this implant ever ran stays visible as a session
// block (its transcript lives on the task record -- the record of a session
// is the session), oldest on top; the live session sits at the bottom with
// the typing line. A separator marks each break, because a shell does not
// survive its channel: when the operator closes stdin, the shell exits, or
// the idle window closes an abandoned session, the next shell starts fresh
// (a new process, in its home directory) -- the line says that instead of
// pretending one continuous terminal.
//
// Opening the dialog continues the live session when one is running and
// otherwise starts a new one under the history. The implant self-closes a
// shell nobody types into (default 10 minutes), so an abandoned dialog does
// not hold a live shell on the target.
export function ShellDialog({
  engagementId,
  implantId,
  hostLabel,
  onClose,
}: {
  engagementId: string
  implantId: string
  hostLabel: string
  onClose: () => void
}) {
  const [history, setHistory] = useState<ShellSession[]>([])
  const [liveTaskId, setLiveTaskId] = useState<string | null>(null)
  // The live session's started-at stamp, so folding its completion into the
  // history keeps the header the task list would have shown.
  const [liveStartedAt, setLiveStartedAt] = useState<string>(new Date().toISOString())
  const [starting, setStarting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const startSession = useCallback(async () => {
    setStarting(true)
    try {
      const task = await issueTask(engagementId, { implantId, verb: 'shell.interact', arguments: '' })
      setLiveStartedAt(new Date().toISOString())
      setLiveTaskId(task.taskId)
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setStarting(false)
    }
  }, [engagementId, implantId])

  // The cold open: the sessions this implant already ran become the history
  // (newest last), then either the running session continues or a fresh one
  // starts under it.
  const open = useCallback(async () => {
    try {
      const page = await listImplantTasks(engagementId, implantId)
      const sessions = page.items
        .filter((t) => t.verb === 'shell.interact')
        .sort((a, b) => a.createdAt.localeCompare(b.createdAt))
      setHistory(sessions.filter((t) => t.status !== 'Queued' && t.status !== 'Dispatched'))
      const running = [...sessions].reverse().find(
        (t) => t.status === 'Queued' || t.status === 'Dispatched',
      )
      if (running) {
        setLiveStartedAt(running.createdAt)
        setLiveTaskId(running.taskId)
      } else {
        await startSession()
      }
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }, [engagementId, implantId, startSession])

  useEffect(() => {
    void open()
  }, [open])

  // The live session's lifecycle: watch for it completing (shell exit, closed
  // stdin, idle self-close), then fold its final transcript into the history
  // and offer the next session -- the operator decides when a new shell
  // starts, not the dialog.
  useEffect(() => {
    if (!liveTaskId) return
    let stopped = false
    const timer = setInterval(() => {
      void (async () => {
        try {
          const t = await getTask(engagementId, liveTaskId)
          if (stopped) return
          if (t.status !== 'Queued' && t.status !== 'Dispatched') {
            const endedAt = liveStartedAt
            setHistory((current) => [
              ...current.filter((h) => h.taskId !== t.taskId),
              { taskId: t.taskId, createdAt: endedAt, status: t.status, outcome: t.outcome, output: t.output },
            ])
            setLiveTaskId(null)
          }
        } catch {
          // The next tick retries; the pane shows its own errors.
        }
      })()
    }, 1000)
    return () => {
      stopped = true
      clearInterval(timer)
    }
  }, [engagementId, liveTaskId, liveStartedAt])

  return (
    <div className="modal-backdrop" onClick={(e) => e.target === e.currentTarget && onClose()}>
      <div className="card modal wide shell-dialog">
        <div className="inline-form">
          <h3 style={{ marginRight: 'auto' }}>
            Interactive shell <span className="muted">· {hostLabel}</span>
          </h3>
          <button className="ghost" onClick={onClose}>
            Close
          </button>
        </div>
        {error && <p className="error">{error}</p>}
        <div className="shell-sessions">
          {history.map((session) => (
            <HistorySession key={session.taskId} session={session} />
          ))}
          {history.length > 0 && (
            <div className="shell-separator" title="The previous shell ended -- a new shell is a new process in its home directory">
              ──── previous shell ended · a new one starts fresh (home directory) ────
            </div>
          )}
          {liveTaskId ? (
            <InteractPane
              engagementId={engagementId}
              taskId={liveTaskId}
              verb="shell.interact"
              onClose={onClose}
              embedded
            />
          ) : (
            <div className="empty">
              {starting ? (
                <>
                  <span className="spinner" /> Starting a shell…
                </>
              ) : (
                <>
                  No live shell.
                  <button className="sm" style={{ marginLeft: 10 }} onClick={() => void startSession()}>
                    Start a new session
                  </button>
                </>
              )}
            </div>
          )}
        </div>
      </div>
    </div>
  )
}

// One ended session: header names when it ran and how it ended; the
// transcript folds by default (a long session should not bury the live one)
// and unfolds on click.
function HistorySession({ session }: { session: ShellSession }) {
  const [open, setOpen] = useState(false)
  const lines = (session.output ?? '').split('\n')
  return (
    <div className="shell-history">
      <button className="link shell-history-toggle" onClick={() => setOpen(!open)}>
        {open ? '▾' : '▸'} session {new Date(session.createdAt).toLocaleTimeString()} ·{' '}
        {lines.length} line{lines.length === 1 ? '' : 's'} ·{' '}
        {session.outcome === 'Succeeded' ? 'closed' : `ended (${session.outcome?.toLowerCase()})`}
      </button>
      {open && <pre className="interact-transcript shell-history-transcript">{session.output || '—'}</pre>}
    </div>
  )
}
