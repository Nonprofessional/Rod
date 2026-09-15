import { useCallback, useEffect, useState } from 'react'
import { type ShellSession, listShells } from '../api'
import { ShellConsole } from '../components/ShellConsole'
import { StatusBadge } from '../components/StatusBadge'

// The engagement's caught shells: the roster of connections a shellcatch
// listener holds, each speaking no Rod protocol -- the anonymous arrivals
// scoped by the listener they landed on. The roster refreshes on the live
// tick (a shell joining or leaving the roster bumps it) with a slow poll as
// reconciliation; selecting a shell opens the console under the table.
export function ShellsView({
  engagementId,
  onlineTick,
}: {
  engagementId: string
  onlineTick: number
}) {
  const [shells, setShells] = useState<ShellSession[]>([])
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
      {shells.length === 0 ? (
        <p className="muted">
          No shells caught yet. Create a shellcatch listener under Listeners and run a reverse-shell
          one-liner that dials it.
        </p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Status</th>
              <th>Remote</th>
              <th>Shell</th>
              <th>Opened</th>
              <th>Last output</th>
              <th>Grew into</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
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
                  {shell.upgradedImplantId ? (
                    <code>{shell.upgradedImplantId.slice(0, 8)}</code>
                  ) : (
                    '—'
                  )}
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
      )}
      {selected && (
        <ShellConsole engagementId={engagementId} shell={selected} onEnded={onEnded} />
      )}
    </section>
  )
}
