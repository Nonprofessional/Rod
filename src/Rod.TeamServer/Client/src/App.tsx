import { useCallback, useEffect, useState } from 'react'
import './App.css'
import {
  type Engagement,
  type Implant,
  type LiveOperator,
  type StagerToken,
  type Task,
  createEngagement,
  issueTask,
  listEngagements,
  listImplants,
  listTasks,
  mintStagerToken,
  subscribeToEngagement,
} from './api'

// Operator UI (roadmap M1.5, live multiplayer M2.4): list engagements, drill
// into one to see its enrolled sessions (with a live online dot), mint a stager
// token, and issue a shell.exec task against an implant and watch its result.
// Each open engagement holds a Server-Sent Events stream open so every
// connected operator sees tasking, results, and presence in real time. Identity
// is self-assigned for the session (real operator auth arrives later);
// navigation is hash-based so the host's static-file fallback keeps deep links
// working without a server-side router.

type Route =
  | { kind: 'engagements' }
  | { kind: 'engagement'; engagementId: string }

function parseHash(): Route {
  const hash = window.location.hash.replace(/^#/, '')
  const match = /^\/engagements\/([\da-fA-F-]+)\/?$/.exec(hash)
  if (match) return { kind: 'engagement', engagementId: match[1] }
  return { kind: 'engagements' }
}

function useRoute(): Route {
  const [route, setRoute] = useState<Route>(() => parseHash())
  useEffect(() => {
    const onHash = () => setRoute(parseHash())
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])
  return route
}

// One operator identity for the browser session. The walking skeleton resolves
// it client-side (a self-typed handle plus a stable id); real operator
// authentication arrives later and replaces only how this identity is
// established, not the components that consume it. Persists across route
// changes so engagement ownership and live presence stay consistent.
interface SessionOperator {
  operatorId: string
  handle: string
  displayName: string
}

function useSessionOperator(): [SessionOperator, (next: Partial<SessionOperator>) => void] {
  const [operator, setOperator] = useState<SessionOperator>(() => {
    const stored = window.localStorage.getItem('rod.operator')
    if (stored) {
      try {
        return JSON.parse(stored) as SessionOperator
      } catch {
        // Corrupt store; fall through to a fresh identity.
      }
    }
    return { operatorId: crypto.randomUUID(), handle: 'operator', displayName: 'Operator' }
  })

  const update = useCallback((next: Partial<SessionOperator>) => {
    setOperator((current) => {
      const merged = { ...current, ...next }
      window.localStorage.setItem('rod.operator', JSON.stringify(merged))
      return merged
    })
  }, [])

  return [operator, update]
}

function App() {
  const route = useRoute()
  const [operator, setOperator] = useSessionOperator()
  return (
    <div className="app">
      <header className="app-header">
        <h1>Rod teamserver</h1>
        <a className="crumb" href="#/engagements">
          Engagements
        </a>
        <span className="who" title="Your session identity (real operator auth arrives later)">
          <code>{operator.handle}</code>
        </span>
      </header>
      <main className="app-main">
        {route.kind === 'engagements' ? (
          <EngagementsView operator={operator} setOperator={setOperator} />
        ) : (
          <EngagementView engagementId={route.engagementId} operator={operator} />
        )}
      </main>
    </div>
  )
}

function EngagementsView({
  operator,
  setOperator,
}: {
  operator: SessionOperator
  setOperator: (next: Partial<SessionOperator>) => void
}) {
  const [items, setItems] = useState<Engagement[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [name, setName] = useState('')

  const refresh = useCallback(async () => {
    setBusy(true)
    try {
      setItems(await listEngagements())
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const onCreate = async (event: React.FormEvent) => {
    event.preventDefault()
    try {
      await createEngagement({
        ownerId: operator.operatorId,
        ownerHandle: operator.handle,
        ownerDisplayName: operator.displayName,
        name,
      })
      setName('')
      await refresh()
    } catch (e) {
      setError(String(e))
    }
  }

  return (
    <section>
      <h2>Engagements</h2>
      {error && <p className="error">{error}</p>}
      <form className="inline-form" onSubmit={onCreate}>
        <input
          placeholder="Engagement name"
          value={name}
          onChange={(e) => setName(e.target.value)}
          required
        />
        <input
          placeholder="Owner handle"
          value={operator.handle}
          onChange={(e) => setOperator({ handle: e.target.value })}
          required
        />
        <input
          placeholder="Owner display name"
          value={operator.displayName}
          onChange={(e) => setOperator({ displayName: e.target.value })}
          required
        />
        <button type="submit" disabled={busy}>
          Create
        </button>
      </form>

      {items.length === 0 ? (
        <p className="muted">No engagements yet.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Name</th>
              <th>Owner</th>
              <th>Created</th>
            </tr>
          </thead>
          <tbody>
            {items.map((e) => (
              <tr key={e.engagementId}>
                <td>
                  <a href={`#/engagements/${e.engagementId}`}>{e.name}</a>
                </td>
                <td>{e.ownerHandle || e.ownerId.slice(0, 8)}</td>
                <td>{new Date(e.createdAt).toLocaleString()}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  )
}

function EngagementView({
  engagementId,
  operator,
}: {
  engagementId: string
  operator: SessionOperator
}) {
  const [implants, setImplants] = useState<Implant[]>([])
  const [online, setOnline] = useState<LiveOperator[]>([])
  const [error, setError] = useState<string | null>(null)
  const [minted, setMinted] = useState<StagerToken | null>(null)
  const [busy, setBusy] = useState(false)

  const refresh = useCallback(async () => {
    setBusy(true)
    try {
      setImplants(await listImplants(engagementId))
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

  // The live event stream (roadmap M2.4): one SSE connection per open
  // engagement. TaskIssued / TaskCompleted events refresh the affected list
  // instead of a blind poll; presence events keep the "operators online" view
  // current. The stream carries the session identity in its query parameters.
  useEffect(() => {
    if (!operator.handle.trim()) return
    const close = subscribeToEngagement(engagementId, operator, {
      onHello: (operators) => setOnline(operators),
      onOperatorJoined: (id, handle) =>
        setOnline((current) =>
          current.some((o) => o.id === id) ? current : [...current, { id, handle, displayName: handle }],
        ),
      onOperatorLeft: (id) => setOnline((current) => current.filter((o) => o.id !== id)),
      // Any tasking activity on the engagement is reason to re-read the current
      // implant/task state, since implants other tabs task won't match the view's
      // own subscriptions. Keeps the lists fresh without per-view polling.
      onTaskIssued: () => void refresh(),
      onTaskCompleted: () => void refresh(),
    })
    return close
  }, [engagementId, operator, refresh])

  const onMint = async () => {
    try {
      setMinted(await mintStagerToken(engagementId))
    } catch (e) {
      setError(String(e))
    }
  }

  return (
    <section>
      <h2>Engagement</h2>
      <p className="muted">
        <code>{engagementId}</code>
      </p>
      {error && <p className="error">{error}</p>}

      <div className="card">
        <h3>Operators online</h3>
        {online.length === 0 ? (
          <p className="muted">No other operators connected.</p>
        ) : (
          <ul className="sessions">
            {online.map((o) => (
              <li key={o.id}>
                <span className="dot online" title="online" />
                <code>{o.handle || o.id.slice(0, 8)}</code>
                {o.id === operator.operatorId && <span className="muted"> (you)</span>}
              </li>
            ))}
          </ul>
        )}
      </div>

      <div className="card">
        <h3>Stager token</h3>
        <p className="muted">
          Mint a single-use token, then redeem it at <code>POST /implants/enroll</code> to enroll an
          implant. The secret is shown once.
        </p>
        <button onClick={onMint} disabled={busy}>
          Mint stager token
        </button>
        {minted && (
          <dl className="kv">
            <dt>Secret</dt>
            <dd>
              <code>{minted.secret}</code>
            </dd>
            <dt>Expires</dt>
            <dd>{new Date(minted.expiresAt).toLocaleString()}</dd>
            <dt>Max uses</dt>
            <dd>{minted.maxUses}</dd>
          </dl>
        )}
      </div>

      <div className="card">
        <h3>Sessions</h3>
        {implants.length === 0 ? (
          <p className="muted">No implants enrolled yet.</p>
        ) : (
          <ul className="sessions">
            {implants.map((i) => (
              <li key={i.implantId}>
                <span className={`dot ${i.isOnline ? 'online' : 'offline'}`} title={i.isOnline ? 'online' : 'offline'} />
                <code>{i.implantId.slice(0, 8)}</code>
                <span className="muted"> {i.class}</span>
              </li>
            ))}
          </ul>
        )}
      </div>

      {implants.length > 0 && (
        <ImplantTasks
          engagementId={engagementId}
          implant={implants[0]}
          operator={operator}
        />
      )}
    </section>
  )
}

function ImplantTasks({
  engagementId,
  implant,
  operator,
}: {
  engagementId: string
  implant: Implant
  operator: SessionOperator
}) {
  const [tasks, setTasks] = useState<Task[]>([])
  const [verb, setVerb] = useState('shell.exec')
  const [args, setArgs] = useState('whoami')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const refresh = useCallback(async () => {
    try {
      setTasks(await listTasks(engagementId, implant.implantId))
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }, [engagementId, implant.implantId])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const onIssue = async (event: React.FormEvent) => {
    event.preventDefault()
    setBusy(true)
    try {
      await issueTask(engagementId, {
        implantId: implant.implantId,
        issuedBy: operator.operatorId,
        verb,
        arguments: args,
      })
      await refresh()
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="card">
      <h3>
        Tasks &mdash; <code>{implant.implantId.slice(0, 8)}</code>
      </h3>
      <p className="muted">
        Issue a one-shot verb. The implant drains it on its next beacon and the result appears below.
      </p>
      <form className="task-form" onSubmit={onIssue}>
        <input value={verb} onChange={(e) => setVerb(e.target.value)} required />
        <input
          className="wide"
          placeholder="arguments"
          value={args}
          onChange={(e) => setArgs(e.target.value)}
        />
        <button type="submit" disabled={busy}>
          Issue
        </button>
      </form>
      {error && <p className="error">{error}</p>}
      {tasks.length === 0 ? (
        <p className="muted">No tasks yet.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Verb</th>
              <th>By</th>
              <th>Status</th>
              <th>Output</th>
              <th>At</th>
            </tr>
          </thead>
          <tbody>
            {[...tasks].reverse().map((t) => (
              <tr key={t.taskId}>
                <td>
                  <code>{t.verb}</code> {t.arguments}
                </td>
                <td>
                  <code>{t.issuedBy === operator.operatorId ? 'you' : t.issuedBy.slice(0, 8)}</code>
                </td>
                <td>
                  <span className={`status ${t.status.toLowerCase()}`}>{t.status}</span>
                </td>
                <td>
                  <pre className="output">{t.output ?? '\u2014'}</pre>
                </td>
                <td>{t.completedAt ? new Date(t.completedAt).toLocaleTimeString() : new Date(t.createdAt).toLocaleTimeString()}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}

export default App
