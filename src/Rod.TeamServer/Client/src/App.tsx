import { useCallback, useEffect, useState } from 'react'
import './App.css'
import {
  type Engagement,
  type Implant,
  type StagerToken,
  type Task,
  createEngagement,
  issueTask,
  listEngagements,
  listImplants,
  listTasks,
  mintStagerToken,
} from './api'

// Minimal operator UI (roadmap M1.5): list engagements, drill into one to see
// its enrolled sessions (with a live online dot), mint a stager token, and issue
// a shell.exec task against an implant and watch its result. Navigation is
// hash-based so the host's static-file fallback keeps deep links working
// without a server-side router.

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

function App() {
  const route = useRoute()
  return (
    <div className="app">
      <header className="app-header">
        <h1>Rod teamserver</h1>
        <a className="crumb" href="#/engagements">
          Engagements
        </a>
      </header>
      <main className="app-main">
        {route.kind === 'engagements' ? (
          <EngagementsView />
        ) : (
          <EngagementView engagementId={route.engagementId} />
        )}
      </main>
    </div>
  )
}

function EngagementsView() {
  const [items, setItems] = useState<Engagement[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  // Owner identity is resolved from the request in the walking skeleton (M2.4
  // adds real auth). Let the operator self-assign a handle for the session.
  const [ownerHandle, setOwnerHandle] = useState('operator')
  const [ownerDisplay, setOwnerDisplay] = useState('Operator')
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
        ownerId: crypto.randomUUID(),
        ownerHandle,
        ownerDisplayName: ownerDisplay,
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
          value={ownerHandle}
          onChange={(e) => setOwnerHandle(e.target.value)}
          required
        />
        <input
          placeholder="Owner display name"
          value={ownerDisplay}
          onChange={(e) => setOwnerDisplay(e.target.value)}
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

function EngagementView({ engagementId }: { engagementId: string }) {
  const [implants, setImplants] = useState<Implant[]>([])
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
    // Light polling so a freshly enrolled implant shows up without a manual
    // reload; real-time push arrives with the operator layer (M2.4).
    const timer = window.setInterval(() => void refresh(), 3000)
    return () => window.clearInterval(timer)
  }, [refresh])

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
        <ImplantTasks engagementId={engagementId} implant={implants[0]} />
      )}
    </section>
  )
}

function ImplantTasks({ engagementId, implant }: { engagementId: string; implant: Implant }) {
  const [tasks, setTasks] = useState<Task[]>([])
  const [verb, setVerb] = useState('shell.exec')
  const [args, setArgs] = useState('whoami')
  const [issuedBy] = useState(() => crypto.randomUUID())
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
    const timer = window.setInterval(() => void refresh(), 2000)
    return () => window.clearInterval(timer)
  }, [refresh])

  const onIssue = async (event: React.FormEvent) => {
    event.preventDefault()
    setBusy(true)
    try {
      await issueTask(engagementId, { implantId: implant.implantId, issuedBy, verb, arguments: args })
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
