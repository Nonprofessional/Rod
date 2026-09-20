import { useCallback, useEffect, useState } from 'react'
import { type ListenerSummary, type ShellSession, listListeners, listShells } from '../api'
import { Icon } from '../components/Icons'
import { ShellConsole } from '../components/ShellConsole'
import { StatusBadge } from '../components/StatusBadge'

// The engagement's caught shells: the roster of connections a shellcatch
// listener holds, each speaking no Rod protocol -- the anonymous arrivals
// scoped by the listener they landed on. The roster refreshes on the live
// tick (a shell joining or leaving the roster bumps it) with a slow poll as
// reconciliation; selecting a shell opens the console under the table.
//
// The paste-ready catch one-liners live one tab over, under Launchers --
// the one home for every command an operator copies out. This view is the
// roster they land in.

export function ShellsView({
  engagementId,
  onlineTick,
}: {
  engagementId: string
  onlineTick: number
}) {
  const [shells, setShells] = useState<ShellSession[]>([])
  const [catchers, setCatchers] = useState<ListenerSummary[]>([])
  const [webListeners, setWebListeners] = useState<ListenerSummary[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    try {
      setShells(await listShells(engagementId))
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }, [engagementId])

  useEffect(() => {
    void refresh()
  }, [refresh, onlineTick])

  // The shellcatch listeners (the catch one-liners' home) and the web
  // listeners (the console's upgrade fetch fronts): loaded with the roster's
  // tick rather than the slow poll -- listeners change rarely, and the
  // surfaces that read them only need to exist, not to reconcile.
  useEffect(() => {
    void (async () => {
      try {
        const all = await listListeners(engagementId)
        setCatchers(all.filter((l) => l.transport === 'shellcatch'))
        setWebListeners(all.filter((l) => ['http', 'https', 'mtls'].includes(l.transport)))
      } catch {
        // A failed load leaves the hint generic; the roster above still works.
      }
    })()
  }, [engagementId, onlineTick])

  // Reconciliation only: a dropped SSE connection loses the roster events,
  // and the slow poll re-anchors to the server's view.
  useEffect(() => {
    const timer = window.setInterval(() => void refresh(), 5000)
    return () => window.clearInterval(timer)
  }, [refresh])

  // The selected shell's console reads the live entity from the roster, so
  // the status pill moves with the roster refreshes rather than a stale
  // copy captured at selection.
  const selected = shells.find((s) => s.sessionId === selectedId) ?? null

  // A selection that ended stays readable until the operator dismisses it:
  // the transcript is the working view, the audit trail is the record.
  const onEnded = useCallback(() => void refresh(), [refresh])

  return (
    <section className="view">
      <h2>Shells</h2>
      <p className="muted">
        Reverse shells caught on this engagement's shellcatch listeners. A caught shell speaks no
        protocol and carries no identity — it is scoped by the listener it landed on — and grows
        into a real implant through the console's Upgrade launchers.
      </p>
      {error && <p className="error">{error}</p>}

      {catchers.length > 0 && (
        <p className="muted">
          Paste-ready catch one-liners live under{' '}
          <a href={`#/engagements/${engagementId}/launchers`}>Launchers</a> — copy one there,
          paste it on the target, and the shell lands in this roster.
        </p>
      )}

      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Status</th>
              <th>Remote</th>
              <th>Shell</th>
              <th>Opened</th>
              <th>Last output</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {shells.length === 0 && (
              <tr>
                <td colSpan={6}>
                  <div className="empty">
                    <Icon name="terminal" />
                    {catchers.length > 0
                      ? 'No shells caught yet -- copy a catch one-liner under Launchers and paste it on the target.'
                      : 'No shells caught yet -- create a shellcatch listener under Listeners first.'}
                  </div>
                </td>
              </tr>
            )}
            {shells.map((shell) => (
              <tr
                key={shell.sessionId}
                className={shell.sessionId === selectedId ? 'selected' : undefined}
              >
                <td>
                  <StatusBadge status={shell.status} />
                </td>
                <td>
                  <code>{shell.remoteAddress}</code>
                </td>
                <td>
                  <code>{shell.os}</code>
                </td>
                <td>{new Date(shell.openedAt).toLocaleTimeString()}</td>
                <td>
                  {shell.lastOutputAt ? new Date(shell.lastOutputAt).toLocaleTimeString() : '—'}
                </td>
                <td>
                  <button
                    className={shell.sessionId === selectedId ? 'ghost sm' : 'primary sm'}
                    onClick={() =>
                      setSelectedId((current) =>
                        current === shell.sessionId ? null : shell.sessionId,
                      )
                    }
                  >
                    {shell.sessionId === selectedId ? 'Hide console' : 'Console'}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {selected && (
        <ShellConsole
          engagementId={engagementId}
          shell={selected}
          webListeners={webListeners}
          onEnded={onEnded}
        />
      )}
    </section>
  )
}
