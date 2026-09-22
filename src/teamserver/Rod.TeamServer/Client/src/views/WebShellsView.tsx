import { useCallback, useEffect, useState } from 'react'
import {
  type PayloadSummary,
  type RegisteredWebShell,
  type WebShell,
  ApiError,
  executeWebShell,
  listPayloads,
  listWebShells,
  probeWebShell,
  registerWebShell,
  removeWebShell,
} from '../api'
import { CopyButton } from '../components/CopyButton'
import { StatusBadge } from '../components/StatusBadge'
import { Icon } from '../components/Icons'

// The engagement's web-shell endpoints: scripts placed in targets' web
// roots, bound to the engagement by registration. The scripts themselves
// generate under Build (the Webshell script half of that tab); this view is
// the operating side -- register the reachable URL with its credential,
// probe, run. The roster reads the profile (url, adapter, probe stamps);
// registering an endpoint answers with the one-liner to place, so the
// operator can drop the script and connect in one flow. The console is
// line-oriented -- each submitted line is one synchronous execution whose
// output appends to the transcript -- because a web-shell has no live
// stream; the task log carries the durable history of every command.
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
  const [registerAdapter, setRegisterAdapter] = useState('rod-php')
  const [registerPassword, setRegisterPassword] = useState('')
  const [claimId, setClaimId] = useState('')
  const [scripts, setScripts] = useState<PayloadSummary[]>([])
  const [registering, setRegistering] = useState(false)
  const [placed, setPlaced] = useState<RegisteredWebShell | null>(null)

  const claim = scripts.find((s) => s.artifactId === claimId) ?? null
  const isRod = (claim ? claim.target : registerAdapter)?.startsWith('rod-')

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

  // The generated web-shell scripts, for the register form's claim pick:
  // naming one supplies the family and the credential from the stored
  // script, so a baked key never travels through the operator's clipboard.
  useEffect(() => {
    void (async () => {
      try {
        setScripts((await listPayloads(engagementId)).filter((p) => p.class === 'WebShell'))
      } catch {
        // Claiming stays unavailable; pasting the credential still works.
      }
    })()
  }, [engagementId, onlineTick])

  const onRegister = async (event: React.FormEvent) => {
    event.preventDefault()
    if (!registerUrl || registering) return
    setRegistering(true)
    try {
      const registered = await registerWebShell(engagementId, {
        url: registerUrl,
        adapterId: claim ? undefined : registerAdapter,
        password: claim ? undefined : registerPassword || undefined,
        payloadId: claim?.artifactId,
      })
      setPlaced(registered)
      setRegisterUrl('')
      setRegisterPassword('')
      setClaimId('')
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

  const selected = shells.find((s) => s.implantId === selectedId) ?? null

  return (
    <section className="view">
      <h2>Web shells</h2>
      <p className="muted">
        Scripts placed in targets' web roots, bound to this engagement by registration. A
        web-shell never enrolls; execution is synchronous and lands on the task log like any
        other tasking. Generate the script under Build; register it here once placed.
      </p>
      {error && <p className="error">{error}</p>}

      <form className="task-form" onSubmit={onRegister}>
        <select
          value={claimId}
          onChange={(e) => setClaimId(e.target.value)}
          disabled={scripts.length === 0}
          title="Claim a generated script (under Build): the family and the credential come from the stored payload, no key copying. With none claimed, the family and credential are picked by hand below."
        >
          <option value="">
            {scripts.length === 0 ? 'no generated scripts yet' : 'paste credential (no claim)'}
          </option>
          {scripts.map((s) => (
            <option key={s.artifactId} value={s.artifactId}>
              claim {s.target} · {s.fingerprint.slice(0, 8)} ·{' '}
              {new Date(s.builtAt).toLocaleDateString()}
            </option>
          ))}
        </select>
        <input
          className="wide"
          placeholder="https://target.example.test/uploads/cmd.php"
          value={registerUrl}
          onChange={(e) => setRegisterUrl(e.target.value)}
        />
        {!claim && (
          <select
            value={registerAdapter}
            onChange={(e) => setRegisterAdapter(e.target.value)}
            title="The protocol the placed script speaks -- the sealed Rod family (AES-256-GCM under a baked key; PHP or JSP) or the universal one-liner (the classic eval shape any manager drives)."
          >
            <option value="rod-php">Sealed (Rod, PHP)</option>
            <option value="rod-jsp">Sealed (Rod, JSP)</option>
            <option value="eval-php">One-liner (PHP)</option>
            <option value="eval-aspx">One-liner (ASPX)</option>
            <option value="eval-asp">One-liner (ASP)</option>
          </select>
        )}
        {!claim && (
          <input
            placeholder={
              isRod ? 'connection key (from the generated script)' : 'connection password (optional)'
            }
            title={
              isRod
                ? 'The 256-bit key baked into the generated script (shown when it was generated under Build, and in the Payloads detail). Leave empty and registration mints a fresh script with a new key.'
                : 'The POST parameter the eval one-liner answers to. Leave empty and one is generated.'
            }
            value={registerPassword}
            onChange={(e) => setRegisterPassword(e.target.value)}
          />
        )}
        <button className="primary sm" type="submit" disabled={registering || !registerUrl}>
          Register
        </button>
      </form>

      {placed && (
        <div className="upgrade-panel">
          <p>
            Place this one-liner in the target's web root (the connection credential is{' '}
            <code>{placed.password}</code>), then register the reachable URL if you have not
            already.
          </p>
          <div className="upgrade-launcher">
            <code className="upgrade-command">{placed.script}</code>
            <CopyButton text={placed.script} />
          </div>
        </div>
      )}

      <div className="table-wrap">
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
            {shells.length === 0 && (
              <tr>
                <td colSpan={6}>
                  <div className="empty">
                    <Icon name="globe" />
                    No web-shell endpoints registered yet -- register a URL above or generate a
                    script to place.
                  </div>
                </td>
              </tr>
            )}
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
      </div>

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
