import { useCallback, useEffect, useState } from 'react'
import './App.css'
import { type LoginInput, type SessionOperator, getSessionOperator, login, logout } from './api'
import { EngagementView } from './views/EngagementView'
import { EngagementsView } from './views/EngagementsView'
import { LoginView } from './views/LoginView'

// Operator UI shell (roadmap M1.5, expanded M11.1): lists engagements, drills
// into one to reach the full capability surface -- tasking (recon through
// exploit), implants (with retire), the M6 evidence views (audit, artifacts,
// timeline, report), and the M4 OPSEC controls (listeners/redirectors, payload
// build). Each open engagement holds a Server-Sent Events stream open so every
// connected operator sees tasking, results, and presence in real time (M2.4).
//
// The session is the teamserver's cookie: GET /operators/me resolves the signed-
// in operator (never a client-generated id), and an unauthenticated browser is
// routed to the login view. Navigation is hash-based so the host's static-file
// fallback keeps deep links working without a server-side router.

type Route =
  | { kind: 'engagements' }
  | { kind: 'engagement'; engagementId: string; tab: string }

function parseHash(): Route {
  const hash = window.location.hash.replace(/^#/, '')
  // Tabbed engagement route first (more specific); bare engagement route falls
  // back to the default tab.
  const tabbed = /^\/engagements\/([\da-fA-F-]+)\/(\w+)\/?$/.exec(hash)
  if (tabbed) return { kind: 'engagement', engagementId: tabbed[1], tab: tabbed[2] }
  const match = /^\/engagements\/([\da-fA-F-]+)\/?$/.exec(hash)
  if (match) return { kind: 'engagement', engagementId: match[1], tab: 'tasking' }
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
  // The operator identity is whatever the teamserver says it is over the session
  // cookie. null while the session is being resolved or when no session exists;
  // the route guard renders the login view in the latter case.
  const [operator, setOperator] = useState<SessionOperator | null>(null)
  const [checking, setChecking] = useState(true)

  useEffect(() => {
    let cancelled = false
    getSessionOperator()
      .then((op) => {
        if (!cancelled) setOperator(op)
      })
      .catch(() => {
        // No session cookie (401); stay on the login view.
      })
      .finally(() => {
        if (!cancelled) setChecking(false)
      })
    return () => {
      cancelled = true
    }
  }, [])

  const onLogin = useCallback(async (input: LoginInput) => {
    await login(input)
    setOperator(await getSessionOperator())
  }, [])

  const onLogout = useCallback(async () => {
    await logout()
    setOperator(null)
  }, [])

  const onTab = useCallback((tab: string) => {
    if (route.kind === 'engagement') {
      window.location.hash = `/engagements/${route.engagementId}/${tab}`
    }
  }, [route])

  if (checking) {
    return (
      <div className="app">
        <main className="app-main">
          <p className="muted">Loading session…</p>
        </main>
      </div>
    )
  }

  if (!operator) {
    return (
      <div className="app">
        <main className="app-main">
          <LoginView onLogin={onLogin} />
        </main>
      </div>
    )
  }

  return (
    <div className="app">
      <header className="app-header">
        <h1>Rod teamserver</h1>
        <a className="crumb" href="#/engagements">
          Engagements
        </a>
        {route.kind === 'engagement' && (
          <>
            <span className="crumb-sep">/</span>
            <a className="crumb" href={`#/engagements/${route.engagementId}`}>
              {route.engagementId.slice(0, 8)}
            </a>
          </>
        )}
        <span className="who" title="Your authenticated session">
          <code>{operator.handle}</code>
        </span>
        <button className="link" onClick={() => void onLogout()}>
          Sign out
        </button>
      </header>
      <main className="app-main">
        {route.kind === 'engagements' ? (
          <EngagementsView />
        ) : (
          <EngagementView
            engagementId={route.engagementId}
            operator={operator}
            tab={route.tab}
            onTab={onTab}
          />
        )}
      </main>
    </div>
  )
}

export default App
