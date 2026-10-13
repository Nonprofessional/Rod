import { Fragment, useCallback, useEffect, useState } from 'react'
import {
  type ListenerSummary,
  type NetworkInterfaceSummary,
  type RedirectorNodeSummary,
  ApiError,
  attachListenerFront,
  createListener,
  deleteListener,
  detachListenerFront,
  listListeners,
  listNetworkInterfaces,
  listRedirectorNodes,
  promoteListenerFront,
  repointListener,
} from '../api'
import { Icon } from '../components/Icons'
import { StatusBadge } from '../components/StatusBadge'
import { useArmedDelete } from '../useArmedDelete'

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

// The transports the create form offers, grouped by role: payload ingress
// first (the group a build names), the DNS family behind the egress
// disclosure (the refresh carrier for egress that only lets DNS-shaped
// traffic leave), the catcher last. Each entry carries the default port it
// takes and names the wire it rides in the label -- every front carries
// every behavior, so the label names the wire's own properties (its
// encryption, its posture, its mode shape), not a capability list; how each
// behavior rides is the build's pick, spelled out by the Build form's
// summary.
interface TransportOption {
  value: string
  label: string
  port: string
}

const TRANSPORT_GROUPS: readonly {
  label: string
  transports: readonly TransportOption[]
}[] = [
  {
    label: 'Payload ingress',
    transports: [
      { value: 'https', label: 'HTTPS — TLS web front (recommended)', port: '443' },
      { value: 'http', label: 'HTTP — cleartext, app-layer sealed (lab)', port: '8080' },
      { value: 'tcp', label: 'Raw TCP — arbitrary sockets out, weak inspection', port: '443' },
    ],
  },
]

const EGRESS_GROUP = {
  label: 'DNS family (DNS-only egress)',
  transports: [
    { value: 'dns', label: 'DNS — TXT over UDP', port: '53' },
    { value: 'doh', label: 'DoH — the same grammar over HTTPS', port: '443' },
  ],
} satisfies { label: string; transports: readonly TransportOption[] }

// The attach form's default front bind per transport: the public wire's
// customary port on the redirector, matching the port the listener form
// itself pre-fills.
const FRONT_DEFAULT_LISTEN: Record<string, string> = {
  https: '0.0.0.0:443',
  http: '0.0.0.0:8080',
  tcp: '0.0.0.0:443',
  dns: '0.0.0.0:53',
  doh: '0.0.0.0:443',
}

const CATCHERS_GROUP = {
  label: 'Catchers',
  transports: [
    {
      value: 'shellcatch',
      label: 'Shellcatch — holds caught reverse shells; no implant ingress',
      port: '8080',
    },
  ],
} satisfies { label: string; transports: readonly TransportOption[] }

// The flat view of the groups, for the port default a transport change sets.
const TRANSPORTS = [...TRANSPORT_GROUPS, EGRESS_GROUP, CATCHERS_GROUP].flatMap((g) => g.transports)

const EGRESS_TRANSPORTS = new Set(EGRESS_GROUP.transports.map((t) => t.value))

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
  // The enrolled fleet, for the front form's node dropdown. A failed load
  // keeps the form usable with a typed node id (tests and scripted setups).
  const [nodes, setNodes] = useState<RedirectorNodeSummary[]>([])
  // The endpoint being repointed, one row at a time: the listener id plus the
  // draft (seeded from the current endpoint so an edit is a tweak, not a
  // retype). Null when no row is editing.
  const [editing, setEditing] = useState<{ id: string; draft: string } | null>(null)

  // The create form's working state. The transport select drives the default
  // port; the interface select is built from the host's reported interfaces
  // with the wildcard and a custom entry riding along.
  const [name, setName] = useState('')
  // The form opens on https: the one-port posture that carries every
  // behavior, so the untouched default is already the shape the guidance
  // recommends. Cleartext http stays a deliberate pick, not a default.
  const [transport, setTransport] = useState('https')
  // The egress disclosure: the alternate-reach family hides until asked
  // for, so the dropdown browses the two groups an operator wants daily.
  const [showEgress, setShowEgress] = useState(false)
  const [interfaces, setInterfaces] = useState<NetworkInterfaceSummary[]>([])
  const [bindInterface, setBindInterface] = useState('')
  const [customHost, setCustomHost] = useState('')
  const [bindPort, setBindPort] = useState('443')
  const [publicEndpoint, setPublicEndpoint] = useState('')
  // Whose certificate the front presents -- the fact builds inherit as
  // their TLS roots. Offered on the web pair alone: the DNS and socket
  // families carry no TLS posture, and mTLS's client-certificate handshake
  // must terminate at the teamserver.
  const [trust, setTrust] = useState('pinned')
  const isWebTransport = transport === 'http' || transport === 'https'

  // The endpoint field speaks each transport's own dial shape: the web
  // family completes scheme-less hosts and can derive from the bind, the
  // socket family wants the bare host:port, the DNS family the zone. One
  // placeholder/title per shape, so the field itself names what the
  // transport's validation will demand.
  const isDnsFamily = transport === 'dns' || transport === 'doh'
  const isBareDial = transport === 'tcp' || transport === 'shellcatch'
  const endpointPlaceholder = isDnsFamily
    ? 'c2.example.test — the zone this listener answers for'
    : isBareDial
      ? 'host:port implants dial (e.g. 203.0.113.10:8443)'
      : 'host, host:port, or URL — empty = the bind'
  const endpointTitle = isDnsFamily
    ? 'The DNS zone this listener answers TXT contacts under — the domain delegated to this host (its NS records point here). Required: a zone is a fact about the target network, not derivable from the bind.'
    : isBareDial
      ? `The host:port implants dial (your redirector in production). The ${transport} scheme is completed at bake time, so type no scheme here. Required: the bare dial cannot be left empty.`
      : "The address baked into payloads — what deployed implants enroll and contact on (your redirector in production). Type just the hostname and the transport's scheme and this listener's port are added; a full URL or host:port is completed with the scheme; empty derives it from the bind. A wildcard bind cannot derive — type the hostname implants should reach."

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

  // The fleet roster for the attach form's node picker.
  useEffect(() => {
    void (async () => {
      try {
        setNodes((await listRedirectorNodes()).filter((n) => !n.retiredAt))
      } catch {
        // The form falls back to a typed node id.
      }
    })()
  }, [])

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

  // Proposes the public endpoint from the current picks: the dialable host
  // (the chosen interface, or the host's first dialable NIC when the bind
  // is the wildcard), the bind port, and the transport's own scheme where
  // one applies. The socket-owning family (tcp, shellcatch) stores
  // the bare host:port -- the scheme is the bake's completion, not the
  // listener record's shape. A redirector replaces it later -- this fills
  // the common no-redirector shape so the field never blocks on typing.
  const onFillEndpoint = () => {
    if (transport === 'dns' || transport === 'doh') return
    const host =
      bindInterface === CUSTOM
        ? customHost.trim()
        : bindInterface === '' || bindInterface === ALL_INTERFACES
          ? (interfaces.find((i) => !i.address.startsWith('127.') && !i.address.includes(':'))
              ?.address ?? '127.0.0.1')
          : bindInterface
    if (!host || !bindPort.trim()) return
    const dial = hostPort(host, bindPort.trim())
    setPublicEndpoint(
      transport === 'tcp' || transport === 'shellcatch'
        ? dial
        : `${transport === 'http' ? 'http' : 'https'}://${dial}`,
    )
  }

  const onCreate = async (event: React.FormEvent) => {
    event.preventDefault()
    // An unresolved interface (the list never loaded, or no default picked
    // yet) reads as the wildcard the dropdown already shows as selected.
    const iface = bindInterface === '' ? ALL_INTERFACES : bindInterface
    const bindAddress = iface === CUSTOM
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
        // The posture pick rides the web pair alone; everything else keeps
        // the pinned default the server applies on its own.
        trust: isWebTransport && trust !== 'pinned' ? trust : undefined,
      })
      setName('')
      setPublicEndpoint('')
      setError(null)
      await refresh()
    } catch (e) {
      setError(String(e))
    }
  }

  // The delete guard, in two layers. The button itself arms (the shared
  // two-click pattern); the armed click runs the delete, and when live
  // implants enrolled through the listener the server refuses with a 409
  // naming them -- that message is the second confirmation, and only its
  // explicit accept forces the delete.
  const [armed, armDelete] = useArmedDelete()

  // --- Fronts (the enrolled-fleet half, architecture.md Sec 8) ---
  //
  // A front attaches an enrolled node: the node binds the listen endpoint
  // and splices it to this listener's bind, receiving the rule over the
  // control channel the moment the attach lands. The primary front is the
  // public endpoint builds bake -- attaching primary or promoting is the
  // rotation op that replaces repointing for enrolled fleets.
  const [frontListener, setFrontListener] = useState('')
  const [frontNode, setFrontNode] = useState('')
  const [frontListen, setFrontListen] = useState('')
  const [frontPublic, setFrontPublic] = useState('')
  const [frontPrimary, setFrontPrimary] = useState(false)

  // Every payload-ingress family is frontable: the web pair and the raw
  // socket ride the TCP splice, the DNS family rides the node's datagram
  // relay. The catcher fronts no implant dial.
  const frontable = listeners.filter((l) => l.transport !== 'shellcatch')

  // Picking a listener drafts the attach form: the front's bind pre-fills
  // with the transport's standard public port (the same number the
  // listener form pre-fills), and a listener with no fronts pre-checks
  // Primary -- until it has one, the endpoint builds bake is still the
  // bare bind. Both are drafts to correct, not choices made for the
  // operator: a hand-typed endpoint survives a re-pick.
  const onPickFrontListener = (id: string) => {
    setFrontListener(id)
    const picked = listeners.find((l) => l.id === id)
    if (!picked) return
    const defaultListen = FRONT_DEFAULT_LISTEN[picked.transport]
    if (defaultListen) {
      setFrontListen((current) => (current.trim() === '' ? defaultListen : current))
    }
    setFrontPrimary(picked.fronts.length === 0)
  }

  const onAttachFront = async (event: React.FormEvent) => {
    event.preventDefault()
    try {
      await attachListenerFront(engagementId, frontListener, {
        nodeId: frontNode.trim(),
        listenEndpoint: frontListen.trim(),
        publicEndpoint: frontPublic.trim() || undefined,
        primary: frontPrimary || undefined,
      })
      setFrontListen('')
      setFrontPublic('')
      setFrontPrimary(false)
      setError(null)
      await refresh()
    } catch (e) {
      setError(String(e))
    }
  }

  const onDetachFront = (l: ListenerSummary, frontId: string) =>
    armDelete(frontId, () => void (async () => {
      try {
        await detachListenerFront(engagementId, l.id, frontId)
        setError(null)
        await refresh()
      } catch (e) {
        setError(String(e))
      }
    })())

  const onPromoteFront = async (l: ListenerSummary, frontId: string) => {
    try {
      await promoteListenerFront(engagementId, l.id, frontId)
      setError(null)
      await refresh()
    } catch (e) {
      setError(String(e))
    }
  }

  const onArmDelete = (l: ListenerSummary) =>
    armDelete(l.id, () => void onDelete(l))

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
        dial. Every front carries every behavior; the wires differ in encryption, posture, and
        speed — pick by the target's egress (the Build form's summary spells out how each
        behavior rides). HTTPS/mTLS remain the recommended posture, cleartext HTTP the lab one.
      </p>

      {/* The same labeled-grid shape as the Build form: every field carries
          its name above it, placeholders stay as hints only. */}
      {/* Three row groups, one concern each: identity (name, wire, the
          egress fold it opens), the socket (interface, host, port), and the
          dial (what address, whose certificate) -- each row wraps on a
          narrow card instead of the fields interleaving across grid
          columns. */}
      <form className="listener-form" onSubmit={onCreate}>
        <div className="listener-row">
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
              title="The wire this listener speaks. Every front carries every behavior; the wires differ in their own properties -- encryption (TLS, app-layer seal, or the DNS tradeoff), posture (recommended, lab, weak-inspection, internal segment), and mode shape (a held stream or one exchange per contact). How each behavior rides is the build's pick -- the Build form's summary spells it out. The DNS family below the fold answers DNS-only egress."
            >
              {TRANSPORT_GROUPS.map((group) => (
                <optgroup key={group.label} label={group.label}>
                  {group.transports.map((t) => (
                    <option key={t.value} value={t.value}>
                      {t.label}
                    </option>
                  ))}
                </optgroup>
              ))}
              {showEgress && (
                <optgroup label={EGRESS_GROUP.label}>
                  {EGRESS_GROUP.transports.map((t) => (
                    <option key={t.value} value={t.value}>
                      {t.label}
                    </option>
                  ))}
                </optgroup>
              )}
              <optgroup label={CATCHERS_GROUP.label}>
                {CATCHERS_GROUP.transports.map((t) => (
                  <option key={t.value} value={t.value}>
                    {t.label}
                  </option>
                ))}
              </optgroup>
            </select>
          </label>
          <label
            className="checkbox-label"
            title="The DNS family — TXT over UDP or the same grammar over HTTPS: the refresh carrier for egress that only lets DNS-shaped traffic leave. Contacts step down to it (presence, short tasking, chunked results); no enroll and no interactive — a datagram poll has no input half, so channel tasks queue until a stream front answers. Show them when that is the shape you have."
          >
            Egress &amp; pivots
            <span className="checkbox-row">
              <input
                type="checkbox"
                checked={showEgress}
                onChange={(e) => {
                  setShowEgress(e.target.checked)
                  // Hiding the family cannot leave one of its transports
                  // selected: fall back to the default posture.
                  if (!e.target.checked && EGRESS_TRANSPORTS.has(transport)) {
                    setTransport('https')
                    setBindPort('443')
                  }
                }}
              />
            </span>
          </label>
        </div>
        <div className="listener-row">
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
                row never reshuffles when the custom entry comes and goes;
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
              placeholder="443"
              value={bindPort}
              onChange={(e) => setBindPort(e.target.value)}
              title="The port this listener opens"
              required
            />
          </label>
        </div>
        <div className="listener-row listener-dial">
          <label className="endpoint-label">
            Public endpoint
            <input
              className="endpoint-input"
              placeholder={endpointPlaceholder}
              title={endpointTitle}
              value={publicEndpoint}
              onChange={(e) => setPublicEndpoint(e.target.value)}
            />
          </label>
          {isWebTransport && (
            <label>
              Certificate
              <select
                value={trust}
                onChange={(e) => setTrust(e.target.value)}
                title="Whose certificate this front presents -- the fact every build against it inherits as its TLS roots. Pinned (the default): the engagement CA terminates the front, and artifacts pin that CA -- works for any domain you point at the listener, no public CA involved. Public: a real domain whose publicly-trusted certificate an operator-run edge terminates in front of this listener -- the shape that survives TLS inspection; the public endpoint must be the https address of that edge."
              >
                <option value="pinned">engagement CA (default)</option>
                <option value="public">public — real-domain edge</option>
              </select>
            </label>
          )}
        </div>
        <div className="listener-form-actions">
          <button
            className="ghost"
            type="button"
            onClick={onFillEndpoint}
            disabled={transport === 'dns' || transport === 'doh'}
            title={
              transport === 'dns' || transport === 'doh'
                ? 'A DNS zone is a fact about the target network — a host this server runs on cannot derive it from its interfaces. Type it directly.'
                : 'Compose the public endpoint from the picks above: the dialable host (the interface, or the host\'s first dialable NIC for a wildcard bind), the bind port, and this transport\'s own dial shape. A redirector replaces it later.'
            }
          >
            Fill endpoint
          </button>
          <button className="primary" type="submit" disabled={busy}>
            Create
          </button>
        </div>
      </form>

      {/* The enrolled-fleet half (architecture.md Sec 8): attach a front to
          one of the listeners below, fronted by a redirector from the
          roster. The dns family waits on the redirector's datagram relay;
          the form's listener list names the frontable transports only. It
          sits with the create form above the roster: both are actions on
          the table, and the table is the result. */}
      <h4>Attach a front</h4>
      <p className="muted">
        Stand an enrolled redirector in front of a listener: the redirector binds the endpoint
        you name, splices every byte to the listener's own bind, and implants dial the
        redirector's public address — never the teamserver. <strong>Redirector listens on</strong>{' '}
        is the socket on the redirector itself (wildcard or its IP); <strong>Public
        endpoint</strong> is what builds bake as the address implants dial — leave it empty and
        the listen port completes a bare host (a dns/doh listener wants the resolver implants
        query, the zone stays the listener's); <strong>Primary</strong> makes this front the
        endpoint new builds bake — the attach form of rotation, unchecked it serves beside the
        current fronts.
      </p>
      <form className="listener-form" onSubmit={onAttachFront}>
        <div className="listener-row">
          <label>
            Listener
            <select
              value={frontListener}
              onChange={(e) => onPickFrontListener(e.target.value)}
              title="The listener this front serves -- the redirector splices to its bind. Picking one drafts the form: standard listen port, Primary pre-checked for a first front."
              required
            >
              <option value="">Pick a listener…</option>
              {frontable.map((l) => (
                <option key={l.id} value={l.id}>
                  {l.name} ({l.transport})
                </option>
              ))}
            </select>
          </label>
          <label>
            Redirector
            {nodes.length > 0 ? (
              <select
                value={frontNode}
                onChange={(e) => setFrontNode(e.target.value)}
                title="An enrolled redirector (the Redirectors page mints credentials)."
                required
              >
                <option value="">Pick a redirector…</option>
                {nodes.map((n) => (
                  <option key={n.id} value={n.id}>
                    {n.name} {n.online ? '(online)' : '(offline)'}
                  </option>
                ))}
              </select>
            ) : (
              <input
                placeholder="redirector id"
                value={frontNode}
                onChange={(e) => setFrontNode(e.target.value)}
                title="No roster loaded -- type the enrolled redirector's id."
                required
              />
            )}
          </label>
        </div>
        <div className="listener-row">
          <label>
            Redirector listens on
            <input
              placeholder="0.0.0.0:443"
              value={frontListen}
              onChange={(e) => setFrontListen(e.target.value)}
              title="The endpoint the redirector binds -- host:port on the redirector itself (a wildcard or its own IP), not the public address. Pre-filled with the transport's standard port when the listener is picked."
              required
            />
            </label>
          <label>
            Public endpoint
            <input
              placeholder="c2.example.test — empty completes from the listen port"
              value={frontPublic}
              onChange={(e) => setFrontPublic(e.target.value)}
              title="What implants dial through this front. The web family completes a bare host with the scheme and the node's listen port; the socket family wants host:port; the DNS family (dns/doh) wants the RESOLVER implants dial -- a bare host or host:port, the zone stays the listener's own."
            />
          </label>
          <label className="checkbox-label" title="Insert at the head of the front list and repoint the listener to this front -- the attach form of rotation. Without it, the front serves beside the current one.">
            Primary
            <span className="checkbox-row">
              <input
                type="checkbox"
                checked={frontPrimary}
                onChange={(e) => setFrontPrimary(e.target.checked)}
              />
            </span>
          </label>
        </div>
        <div className="listener-form-actions">
          <button className="primary" type="submit" disabled={busy}>
            Attach
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
        <table className="listeners-table">
          <thead>
            <tr>
              <th>Name</th>
              <th>Transport</th>
              <th>Bind</th>
              <th>Public endpoint</th>
              <th>State</th>
              <th className="col-actions"></th>
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
              <Fragment key={l.id}>
                <tr>
                  <td className="listener-name-cell">
                    <Icon name="radio" className="row-icon" />
                    <span className="row-label">{l.name}</span>
                  </td>
                  <td>
                    {l.transport}
                    {l.trustPosture === 'public' && (
                      <span
                        className="muted"
                        title="This front presents a real-domain certificate an operator-run edge terminates; builds against it validate against public roots."
                      >
                        {' '}· public cert
                      </span>
                    )}
                  </td>
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
                      {armed === l.id ? 'Confirm' : 'Delete'}
                    </button>
                  </td>
                </tr>
                {l.fronts.length > 0 &&
                  l.fronts.map((f) => (
                    <tr key={f.frontId} className="front-row">
                      <td className="front-name-cell">
                        {/* The listener row's antenna icon sits ahead of its
                            name; an equally wide blank keeps the front's name
                            on the same starting line. */}
                        <span className="row-icon" aria-hidden="true" />
                        <strong className="row-label" title={f.nodeId}>
                          {f.nodeName || f.nodeId.slice(0, 8)}
                        </strong>
                      </td>
                      <td></td>
                      <td>
                        <code title="The endpoint this redirector binds">{f.listenEndpoint}</code>
                      </td>
                      <td>
                        <code title="What implants dial through this front">{f.publicEndpoint}</code>
                        {f.allowList && (
                          <span className="muted" title="The source CIDRs the redirector enforces on this front.">
                            {' '}
                            · allow {f.allowList}
                          </span>
                        )}
                      </td>
                      <td>
                        {f.isPrimary ? (
                          <span className="status completed" title="The primary front -- the public endpoint builds bake.">
                            primary
                          </span>
                        ) : (
                          <span className="status queued">front</span>
                        )}
                      </td>
                      <td>
                        {!f.isPrimary && (
                          <button
                            className="ghost sm"
                            onClick={() => void onPromoteFront(l, f.frontId)}
                            title="Make this the primary front: the public endpoint builds bake follows it. The rotation op for enrolled fleets."
                          >
                            Promote
                          </button>
                        )}
                        <button
                          className={`ghost sm danger${armed === f.frontId ? ' armed' : ''}`}
                          onClick={() => onDetachFront(l, f.frontId)}
                          title={
                            armed === f.frontId
                              ? 'Click again — the button reverts on its own after a few seconds'
                              : 'Detach this front: the redirector stops forwarding on the next rule push. Detaching the primary promotes the next front.'
                          }
                        >
                          {armed === f.frontId ? 'Confirm' : 'Detach'}
                        </button>
                      </td>
                    </tr>
                  ))}
              </Fragment>
            ))}
          </tbody>
        </table>
      </div>

      {/* The enrolled-fleet half (architecture.md Sec 8): attach a front to
          one of the listeners above, fronted by a node from the roster. The
          dns family waits on the node's datagram relay; the form's listener
          list names the frontable transports only. */}
    </div>
  )
}
