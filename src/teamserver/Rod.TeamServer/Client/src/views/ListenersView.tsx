import { useCallback, useEffect, useRef, useState } from 'react'
import {
  type ListenerSummary,
  type NetworkInterfaceSummary,
  ApiError,
  createListener,
  deleteListener,
  listListeners,
  listNetworkInterfaces,
  repointListener,
} from '../api'
import { Icon } from '../components/Icons'
import { StatusBadge } from '../components/StatusBadge'

// The engagement's listeners: the C2 ingress this one engagement owns, each
// with the socket it opens (bind) and the public endpoint implants dial
// (typically a redirector). A listener created here belongs to the engagement
// in context -- no picker, no sharing -- and its definition is persisted, so a
// restart rebinds it with the same id; repointing swaps the public endpoint at
// runtime and the definition follows. The operator front the UI rides is
// startup configuration: it carries no implant ingress and never appears
// here.
//
// The bind is picked, not typed: the host reports its interfaces
// (GET /network/interfaces), the form offers them beside the all-interfaces
// wildcard and a custom escape hatch, and the port is its own field defaulted
// per transport. The public endpoint stays free text -- it names the redirector
// implants dial, which is a fact about the target network, not this host.

// The transports the create form offers, with the default port each one takes
// and the wire it rides named in the label. The server validates for real;
// this list only keeps the form from offering shapes the server would refuse
// (the retired https-envelope transport is deliberately absent -- envelope
// check-ins ride the HTTPS listener now). SMB is the odd one out: its bind is
// a bare pipe name, not interface + port.
// Each label names what the transport carries, in the fixed vocabulary:
// TLS-terminated fronts carry every behavior (enroll, check-in, interactive);
// cleartext HTTP carries enroll + check-in over the envelope POST cycle but
// no interactive stream; DNS/SMB/TCP are alternate reach and pivot links,
// not the payload ingress the Build form picks from.
const TRANSPORTS = [
  { value: 'https', label: 'HTTPS — one port: enroll + check-in + interactive', port: '443' },
  { value: 'mtls', label: 'mTLS — client certs: enroll + check-in + interactive', port: '5443' },
  { value: 'http', label: 'HTTP — cleartext: enroll + check-in (poll); no interactive', port: '5090' },
  { value: 'dns', label: 'DNS — TXT over UDP: alternate reach, not payload ingress', port: '53' },
  { value: 'smb', label: 'SMB — named pipe: pivot link, not payload ingress', port: '' },
  { value: 'tcp', label: 'Raw TCP — framed: pivot link, not payload ingress', port: '4444' },
  { value: 'quic', label: 'QUIC — UDP stream: check-in + interactive beacon; no enroll', port: '443' },
]

// Select values that are not reported addresses: the wildcard bind and the
// custom-host escape hatch.
const ALL_INTERFACES = '0.0.0.0'
const CUSTOM = 'custom'

// Composes the wire's host:port bind from the two form fields, bracketing an
// IPv6 literal so the port reads unambiguously.
function hostPort(host: string, port: string): string {
  const bracketed = host.includes(':') && !host.startsWith('[') ? `[${host}]` : host
  return `${bracketed}:${port}`
}

export function ListenersView({ engagementId }: { engagementId: string }) {
  const [listeners, setListeners] = useState<ListenerSummary[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  // The endpoint being repointed, one row at a time: the listener id plus the
  // draft (seeded from the current endpoint so an edit is a tweak, not a
  // retype). Null when no row is editing.
  const [editing, setEditing] = useState<{ id: string; draft: string } | null>(null)

  // The create form's working state. The transport select drives the default
  // port; the interface select is built from the host's reported interfaces
  // with the wildcard and a custom entry riding along.
  const [name, setName] = useState('')
  const [transport, setTransport] = useState('http')
  const [interfaces, setInterfaces] = useState<NetworkInterfaceSummary[]>([])
  const [bindInterface, setBindInterface] = useState('')
  const [customHost, setCustomHost] = useState('')
  const [bindPort, setBindPort] = useState('5090')
  const [pipeName, setPipeName] = useState('')
  const [publicEndpoint, setPublicEndpoint] = useState('')
  const isSmb = transport === 'smb'

  const refresh = useCallback(async () => {
    setBusy(true)
    try {
      setListeners(await listListeners(engagementId))
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }, [engagementId])

  useEffect(() => {
    void refresh()
  }, [refresh])

  // The host's bindable interfaces for the bind dropdown. A failed load keeps
  // the dropdown useful: wildcard, loopback, and custom remain.
  useEffect(() => {
    void (async () => {
      try {
        setInterfaces(await listNetworkInterfaces())
      } catch {
        // Wildcard and custom still cover a host whose list did not load.
      }
    })()
  }, [])

  // Pick the default bind: the first non-loopback address (a dialable NIC, so
  // an empty public endpoint can derive from it), else loopback.
  useEffect(() => {
    if (bindInterface !== '' || interfaces.length === 0) return
    const lan = interfaces.find((i) => !i.address.startsWith('127.') && !i.address.includes(':'))
    const chosen = lan?.address ?? interfaces[0].address
    setBindInterface(interfaces.some((i) => i.address === chosen) ? chosen : ALL_INTERFACES)
  }, [interfaces, bindInterface])

  // The loopback option rides even when the host list did not load.
  const loopbackMissing = !interfaces.some((i) => i.address === '127.0.0.1')

  const onRepoint = async () => {
    if (!editing) return
    const endpoint = editing.draft.trim()
    if (!endpoint || endpoint === listeners.find((l) => l.id === editing.id)?.publicEndpoint) {
      setEditing(null)
      return
    }
    try {
      await repointListener(engagementId, editing.id, endpoint)
      setEditing(null)
      await refresh()
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }

  const onCreate = async (event: React.FormEvent) => {
    event.preventDefault()
    // An unresolved interface (the list never loaded, or no default picked
    // yet) reads as the wildcard the dropdown already shows as selected.
    const iface = bindInterface === '' ? ALL_INTERFACES : bindInterface
    const bindAddress = isSmb
      ? pipeName.trim()
      : iface === CUSTOM
        ? hostPort(customHost.trim(), bindPort.trim())
        : hostPort(iface, bindPort.trim())
    try {
      await createListener(engagementId, {
        name,
        transport,
        bindAddress,
        // An empty field means "derive it from the bind"; the server completes
        // blanks and bare hostnames, so the form never blocks on typing a
        // fully-qualified URL.
        publicEndpoint: publicEndpoint.trim(),
      })
      setName('')
      setPipeName('')
      setPublicEndpoint('')
      setError(null)
      await refresh()
    } catch (e) {
      setError(String(e))
    }
  }

  // The delete guard, in two layers. The button itself arms: the first click
  // turns it into a "Confirm delete" that auto-reverts after a few seconds,
  // so an accidental click never deletes anything. The armed click runs the
  // delete; when live implants enrolled through the listener, the server
  // refuses with a 409 naming them, and that message is the second
  // confirmation -- only its explicit accept forces the delete.
  const [armed, setArmed] = useState<string | null>(null)
  const disarmTimer = useRef<number | null>(null)
  useEffect(
    () => () => {
      if (disarmTimer.current !== null) window.clearTimeout(disarmTimer.current)
    },
    [],
  )

  const onArmDelete = (l: ListenerSummary) => {
    if (armed === l.id) {
      if (disarmTimer.current !== null) window.clearTimeout(disarmTimer.current)
      setArmed(null)
      void onDelete(l)
      return
    }
    setArmed(l.id)
    disarmTimer.current = window.setTimeout(() => setArmed(null), 4000)
  }

  const onDelete = async (l: ListenerSummary) => {
    try {
      await deleteListener(engagementId, l.id)
      setError(null)
      await refresh()
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) {
        if (!window.confirm(`${e.message}\n\nDelete anyway?`)) return
        try {
          await deleteListener(engagementId, l.id, true)
          setError(null)
          await refresh()
          return
        } catch (forced) {
          setError(String(forced))
          return
        }
      }
      setError(String(e))
    }
  }

  return (
    <div className="card">
      <h3>Listeners</h3>
      <p className="muted" title="Bind is the socket this server opens; the public endpoint is what implants dial. Hover the fields for specifics; the full guide is docs/operations/operator-ui.md.">
        This engagement's C2 ingress — bind is the socket here, public endpoint is what implants
        dial. Cleartext HTTP carries enroll + check-in over the sealed envelope POST; only the
        interactive stream needs TLS, so an HTTPS/mTLS listener alone covers everything.
      </p>

      {/* The same labeled-grid shape as the Build form: every field carries
          its name above it, placeholders stay as hints only. */}
      <form className="listener-form" onSubmit={onCreate}>
        <label>
          Name
          <input
            placeholder="front"
            value={name}
            onChange={(e) => setName(e.target.value)}
            required
          />
        </label>
        <label>
          Transport
          <select
            value={transport}
            onChange={(e) => {
              setTransport(e.target.value)
              const port = TRANSPORTS.find((t) => t.value === e.target.value)?.port ?? ''
              if (port !== '') setBindPort(port)
            }}
              title="The wire this listener speaks. HTTPS/mTLS carry every behavior (enroll, check-in, interactive) on one TLS socket; cleartext HTTP carries enroll + check-in via the sealed envelope POST but no interactive stream — a build on it polls unless it names an mTLS listener for interactive."
          >
            {TRANSPORTS.map((t) => (
              <option key={t.value} value={t.value}>
                {t.label}
              </option>
            ))}
          </select>
        </label>
        {isSmb ? (
          <label>
            Pipe name
            <input
              placeholder="rod-pipe"
              value={pipeName}
              onChange={(e) => setPipeName(e.target.value)}
              title="SMB has no interface or port — its bind is the named pipe implants open."
              required
            />
          </label>
        ) : (
          <>
            <label>
              Bind interface
              <select
                value={bindInterface}
                onChange={(e) => setBindInterface(e.target.value)}
                title="The interface this listener opens its socket on"
              >
                <option value={ALL_INTERFACES}>All interfaces (0.0.0.0)</option>
                {interfaces.map((i) => (
                  <option key={`${i.name}-${i.address}`} value={i.address}>
                    {i.name} ({i.address})
                  </option>
                ))}
                {loopbackMissing && <option value="127.0.0.1">Loopback (127.0.0.1)</option>}
                <option value={CUSTOM}>Custom address…</option>
              </select>
            </label>
            <label>
              Custom host
              {/* Always rendered, disabled unless Custom is picked, so the
                  grid never reshuffles when the custom entry comes and goes;
                  the disabled value mirrors the selected interface, so the
                  host about to be bound stays readable. */}
              <input
                className="bind-host"
                placeholder="192.168.1.5"
                value={
                  bindInterface === CUSTOM
                    ? customHost
                    : bindInterface === ''
                      ? ALL_INTERFACES
                      : bindInterface
                }
                onChange={(e) => setCustomHost(e.target.value)}
                disabled={bindInterface !== CUSTOM}
                title="The address to bind. Pick 'Custom address…' to type one — a NIC the host has not reported, or an address that is not up yet. IPv6 literals are bracketed automatically."
                required={bindInterface === CUSTOM}
              />
            </label>
            <label>
              Bind port
              <input
                className="bind-port"
                placeholder="5090"
                value={bindPort}
                onChange={(e) => setBindPort(e.target.value)}
                title="The port this listener opens"
                required
              />
            </label>
          </>
        )}
        <label className="endpoint-label">
          Public endpoint
          <input
            className="endpoint-input"
            placeholder="host, host:port, or URL — empty = the bind"
            title="The address baked into payloads — what deployed implants enroll and check in on (your redirector in production). Type just the hostname and the transport's scheme and this listener's port are added; a full URL or host:port is completed with the scheme; empty derives it from the bind. A wildcard bind cannot derive — type the hostname implants should reach."
            value={publicEndpoint}
            onChange={(e) => setPublicEndpoint(e.target.value)}
          />
        </label>
        <div className="listener-form-actions">
          <button className="primary" type="submit" disabled={busy}>
            Create
          </button>
        </div>
      </form>

      <div className="inline-form">
        <button className="ghost" onClick={() => void refresh()} disabled={busy}>
          <Icon name="refresh" />
          Refresh
        </button>
      </div>
      {error && <p className="error">{error}</p>}
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Name</th>
              <th>Transport</th>
              <th>Bind</th>
              <th>Public endpoint</th>
              <th>State</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {listeners.length === 0 && (
              <tr>
                <td colSpan={6}>
                  <div className="empty">
                    <Icon name="radio" />
                    No listeners for this engagement yet -- create the ingress its implants will
                    dial.
                  </div>
                </td>
              </tr>
            )}
            {listeners.map((l) => (
                <tr key={l.id}>
                  <td>{l.name}</td>
                  <td>{l.transport}</td>
                  <td>
                    <code>{l.bindAddress}</code>
                  </td>
                  <td>
                    {editing?.id === l.id ? (
                      <form
                        className="endpoint-edit"
                        onSubmit={(e) => {
                          e.preventDefault()
                          void onRepoint()
                        }}
                      >
                        <input
                          value={editing.draft}
                          onChange={(e) =>
                            setEditing({ id: l.id, draft: e.target.value })
                          }
                          autoFocus
                          title="The address deployed implants should dial — a burned redirector's replacement."
                        />
                        <button className="sm" type="submit" title="Save the new endpoint">
                          <Icon name="check" />
                        </button>
                        <button
                          className="ghost sm"
                          type="button"
                          title="Cancel"
                          onClick={() => setEditing(null)}
                        >
                          <Icon name="x" />
                        </button>
                      </form>
                    ) : (
                      <span className="endpoint-cell">
                        <code>{l.publicEndpoint}</code>
                        {l.repointedAt && <span className="muted"> (repointed)</span>}
                        <button
                          className="ghost sm menu-trigger"
                          title="Repoint this endpoint"
                          onClick={() => setEditing({ id: l.id, draft: l.publicEndpoint })}
                        >
                          <Icon name="edit" />
                        </button>
                      </span>
                    )}
                  </td>
                  <td>
                    <StatusBadge status={l.state} />
                  </td>
                  <td>
                    <button
                      className={`sm danger${armed === l.id ? ' armed' : ''}`}
                      onClick={() => onArmDelete(l)}
                      title={
                        armed === l.id
                          ? 'Click again to delete — the button reverts on its own after a few seconds'
                          : 'Delete this listener (two clicks: the first arms, the second deletes). A listener live implants enrolled through asks once more.'
                      }
                    >
                      {armed === l.id ? 'Confirm delete' : 'Delete'}
                    </button>
                  </td>
                </tr>
              ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}
