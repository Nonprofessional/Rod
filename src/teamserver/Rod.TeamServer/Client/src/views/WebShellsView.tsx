import { useCallback, useEffect, useState } from 'react'
import {
  type RegisteredWebShell,
  type WebShell,
  ApiError,
  executeWebShell,
  listWebShells,
  probeWebShell,
  registerWebShell,
  removeWebShell,
} from '../api'
import { StatusBadge } from '../components/StatusBadge'

// The engagement's web-shell endpoints: scripts placed in targets' web
// roots, bound to the engagement by registration. The roster reads the
// profile (url, adapter, probe stamps); registering an endpoint answers
// with the one-liner to place, so the operator can drop the script and
// connect in one flow. The console is line-oriented -- each submitted line
// is one synchronous execution whose output appends to the transcript --
// because a web-shell has no live stream; the task log carries the durable
// history of every command.
export function WebShellsView({
  engagementId,
  onlineTick,
}: {
  engagementId: string
  onlineTick: number
}) {
  const [shells, setShells] = useState<WebShell[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [registerUrl, setRegisterUrl] = useState('')
  const [registerPassword, setRegisterPassword] = useState('')
  const [registering, setRegistering] = useState(false)
  const [placed, setPlaced] = useState<RegisteredWebShell | null>(null)
  const [copied, setCopied] = useState(false)

  const refresh = useCallback(async () => {
    try {
      setShells(await listWebShells(engagementId))
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }, [engagementId])

  useEffect(() => {
    void refresh()
  }, [refresh, onlineTick])

  const onRegister = async (event: React.FormEvent) => {
    event.preventDefault()
    if (!registerUrl || registering) return
    setRegistering(true)
    try {
      const registered = await registerWebShell(engagementId, {
        url: registerUrl,
        password: registerPassword || undefined,
      })
      setPlaced(registered)
      setRegisterUrl('')
      setRegisterPassword('')
      setError(null)
      await refresh()
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally {
      setRegistering(false)
    }
  }

  const onRemove = async (implantId: string) => {
    try {
      await removeWebShell(engagementId, implantId)
      if (selectedId === implantId) setSelectedId(null)
      setError(null)
      await refresh()
    } catch (e) {
      setError(String(e))
    }
  }

  const onProbe = async (implantId: string) => {
    try {
      await probeWebShell(engagementId, implantId)
      setError(null)
      await refresh()
    } catch (e) {
      setError(String(e))
    }
  }

  const copyScript = async () => {
    if (!placed) return
    try {
      await navigator.clipboard.writeText(placed.script)
      setCopied(true)
      window.setTimeout(() => setCopied(false), 1500)
    } catch {
      // Clipboard permission denied: the script stays selectable.
    }
  }

  const selected = shells.find((s) => s.implantId === selectedId) ?? null

  return (
    <section className="view">
      <h2>Web shells</h2>
      <p className="muted">
        Scripts placed in targets' web roots, bound to this engagement by registration. A
        web-shell never enrolls; execution is synchronous and lands on the task log like any
        other tasking. The in-tree protocol is the open AntSword eval family.
      </p>
      {error && <p className="error">{error}</p>}

      <form className="task-form" onSubmit={onRegister}>
        <input
          className="wide"
          placeholder="https://target.example.test/uploads/cmd.php"
          value={registerUrl}
          onChange={(e) => setRegisterUrl(e.target.value)}
        />
        <input
          placeholder="connection password (optional)"
          value={registerPassword}
          onChange={(e) => setRegisterPassword(e.target.value)}
        />
        <button className="primary sm" type="submit" disabled={registering || !registerUrl}>
          Register
        </button>
      </form>

      {placed && (
        <div className="upgrade-panel">
          <p>
            Place this one-liner in the target's web root (the connection password is{' '}
            <code>{placed.password}</code>), then register the reachable URL if you have not
            already.
          </p>
          <div className="upgrade-launcher">
            <code className="upgrade-command">{placed.script}</code>
            <button className="ghost sm" onClick={() => void copyScript()}>
              {copied ? 'Copied' : 'Copy'}
            </button>
          </div>
        </div>
      )}

      {shells.length === 0 ? (
        <p className="muted">No web-shell endpoints registered yet.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>URL</th>
              <th>Adapter</th>
              <th>Password</th>
              <th>Last probe</th>
              <th>Status</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {shells.map((shell) => (
              <tr
                key={shell.implantId}
                className={shell.implantId === selectedId ? 'selected' : undefined}
              >
                <td>
                  <code>{shell.url}</code>
                </td>
                <td>
                  <code>{shell.adapterId}</code>
                </td>
                <td>
                  <code>{shell.password}</code>
                </td>
                <td>
                  {shell.lastProbeAt
                    ? `${shell.lastProbeOk ? 'ok' : 'failed'} · ${new Date(shell.lastProbeAt).toLocaleTimeString()}`
                    : 'never'}
                </td>
                <td>
                  <StatusBadge status={shell.retired ? 'retired' : 'registered'} />
                </td>
                <td>
                  <div className="row-actions">
                    <button className="ghost sm" onClick={() => void onProbe(shell.implantId)}>
                      Test
                    </button>
                    <button
                      className={shell.implantId === selectedId ? 'ghost sm' : 'primary sm'}
                      onClick={() =>
                        setSelectedId((current) =>
                          current === shell.implantId ? null : shell.implantId,
                        )
                      }
                    >
                      {shell.implantId === selectedId ? 'Hide console' : 'Console'}
                    </button>
                    <button className="ghost sm" onClick={() => void onRemove(shell.implantId)}>
                      Remove
                    </button>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {selected && (
        <WebShellConsole engagementId={engagementId} shell={selected} onRan={() => void refresh()} />
      )}
    </section>
  )
}

// The line-oriented console: each submitted line is one synchronous
// execution; the transcript is local working state, the task log is the
// record.
function WebShellConsole({
  engagementId,
  shell,
  onRan,
}: {
  engagementId: string
  shell: WebShell
  onRan: () => void
}) {
  const [transcript, setTranscript] = useState('')
  const [line, setLine] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    setTranscript('')
  }, [shell.implantId])

  const onSend = async (event: React.FormEvent) => {
    event.preventDefault()
    if (!line || busy) return
    const command = line
    setBusy(true)
    setTranscript((current) => current + `$ ${command}\n`)
    setLine('')
    try {
      const result = await executeWebShell(engagementId, shell.implantId, command)
      setTranscript((current) => current + `${result.output}\n`)
      setError(null)
      onRan()
    } catch (e) {
      const message = e instanceof ApiError ? e.message : String(e)
      setTranscript((current) => current + `! ${message}\n`)
      setError(message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="terminal">
      <div className="terminal-header">
        <code>webshell</code>
        <span>·</span>
        <code>{shell.url}</code>
        <StatusBadge status={shell.retired ? 'retired' : 'registered'} />
      </div>
      <pre className="interact-transcript">{transcript || '—'}</pre>
      <form className="task-form" onSubmit={onSend}>
        <span className="prompt" aria-hidden="true">
          ›
        </span>
        <input
          className="wide"
          placeholder={busy ? 'running…' : 'type a command'}
          value={line}
          disabled={busy || shell.retired}
          onChange={(e) => setLine(e.target.value)}
          autoFocus
        />
        <button className="primary sm" type="submit" disabled={busy || !line || shell.retired}>
          Run
        </button>
      </form>
      {error && <p className="error terminal-error">{error}</p>}
    </div>
  )
}
