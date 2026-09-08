import { useCallback, useEffect, useState } from 'react'
import './App.css'
import { type LoginInput, type SessionOperator, getSessionOperator, login, logout } from './api'
import { Icon } from './components/Icons'
import { EngagementNav } from './components/Nav'
import { useLive } from './shell'
import { ENGAGEMENT_TABS, type TabId } from './tabs'
import { EngagementView } from './views/EngagementView'
import { EngagementsView } from './views/EngagementsView'
import { LoginView } from './views/LoginView'

// Operator UI shell: a fixed sidebar plus a scrolling content column, the
// layout operations consoles settled on. The sidebar carries brand,
// engagement navigation, and operator identity; the topbar carries the
// engagement breadcrumb and the live engagement state (connection, fleet
// online counts, operator presence) that EngagementView publishes through
// LiveContext. Each open engagement holds a Server-Sent Events stream open so
// every connected operator sees tasking, results, and presence in real time.
//
// The session is the teamserver's cookie: GET /operators/me resolves the signed-
// in operator (never a client-generated id), and an unauthenticated browser is
// routed to the login view. Navigation is hash-based so the host's static-file
// fallback keeps deep links working without a server-side router.

type Route =
  | { kind: 'engagements' }
  | { kind: 'engagement'; engagementId: string; tab: string; implantId?: string }

function parseHash(): Route {
  const hash = window.location.hash.replace(/^#/, '')
  // Most specific first: the session console under the implants tab (an
  // implant drill-in), then any tabbed engagement route, then the bare
  // engagement route's default tab.
  const consoleRoute = /^\/engagements\/([\da-fA-F-]+)\/implants\/([\da-fA-F-]+)\/?$/.exec(hash)
  if (consoleRoute) {
    return { kind: 'engagement', engagementId: consoleRoute[1], tab: 'implants', implantId: consoleRoute[2] }
  }
  const tabbed = /^\/engagements\/([\da-fA-F-]+)\/(\w+)\/?$/.exec(hash)
  if (tabbed) return { kind: 'engagement', engagementId: tabbed[1], tab: tabbed[2] }
  const match = /^\/engagements\/([\da-fA-F-]+)\/?$/.exec(hash)
  if (match) return { kind: 'engagement', engagementId: match[1], tab: 'implants' }
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

// Up to two leading characters of a handle, for avatar tiles.
function initials(handle: string): string {
  return handle.replace(/[^a-zA-Z0-9]/g, '').slice(0, 2).toUpperCase() || '?'
}

function Topbar({ route, operatorId }: { route: Route; operatorId: string }) {
  const live = useLive()
  return (
    <header className="topbar">
      <div className="topbar-crumb">
        <a href="#/engagements">Engagements</a>
        {route.kind === 'engagement' && (
          <>
            <Icon name="chevronRight" className="sep-icon" />
            <span className="current" title={route.engagementId}>
              {route.engagementId.slice(0, 8)}
            </span>
          </>
        )}
      </div>
      <div className="topbar-actions">
        {live && route.kind === 'engagement' && (
          <>
            <span
              className="chip"
              title={live.connected ? 'Live stream connected' : 'Live stream reconnecting…'}
            >
              <span className={`live-dot${live.connected ? '' : ' offline'}`} />
              {live.connected ? 'Live' : 'Reconnecting'}
            </span>
            <span
              className="chip optional"
              title={`${live.onlineCount} of ${live.implantCount} enrolled implants online`}
            >
              <span className="dot online" />
              <strong>{live.onlineCount}</strong>
              <span>/ {live.implantCount} online</span>
            </span>
            {live.operators.length > 0 && (
              <span
                className="avatar-stack"
                title={live.operators
                  .map((o) => (o.id === operatorId ? `${o.handle} (you)` : o.handle))
                  .join(', ')}
              >
                {live.operators.slice(0, 4).map((o) => (
                  <span key={o.id} className="avatar">
                    {initials(o.handle || o.id)}
                  </span>
                ))}
              </span>
            )}
          </>
        )}
      </div>
    </header>
  )
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
    // A mid-session 401 (cookie expired or revoked) anywhere in the API layer
    // returns the shell to the login view instead of leaving a half-working UI
    // that errors on every call.
    const onUnauthorized = () => setOperator(null)
    window.addEventListener('rod-unauthorized', onUnauthorized)
    return () => {
      cancelled = true
      window.removeEventListener('rod-unauthorized', onUnauthorized)
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

  if (checking) {
    return (
      <div className="boot">
        <div className="boot-inner">
          <span className="spinner" />
          <span className="muted">Loading session…</span>
        </div>
      </div>
    )
  }

  if (!operator) {
    return <LoginView onLogin={onLogin} />
  }

  // Unknown tab segments (stale links) fall back to the fleet -- the primary
  // operating surface.
  const routeTab = route.kind === 'engagement' ? route.tab : ''
  const activeTab: TabId = (ENGAGEMENT_TABS as readonly string[]).includes(routeTab)
    ? (routeTab as TabId)
    : 'implants'

  return (
    <div className="shell">
      <aside className="sidebar">
        <a className="brand" href="#/engagements">
          <img className="brand-logo" src="brand-logo.png" alt="" width="28" height="28" />
          <span className="brand-text">
            <span className="brand-name">Rod</span>
            <span className="brand-sub">teamserver</span>
          </span>
        </a>
        <nav className="sidebar-nav">
          <div className="nav-group">
            <a
              className={`nav-item${route.kind === 'engagements' ? ' active' : ''}`}
              href="#/engagements"
            >
              <Icon name="globe" />
              <span className="label">Engagements</span>
            </a>
          </div>
          {route.kind === 'engagement' && (
            <EngagementNav engagementId={route.engagementId} active={activeTab} />
          )}
        </nav>
        <div className="sidebar-footer">
          <div className="operator-chip" title={`Signed in as ${operator.handle}`}>
            <span className="avatar">{initials(operator.handle)}</span>
            <span className="handle">{operator.handle}</span>
          </div>
          <button className="link" onClick={() => void onLogout()}>
            Sign out
          </button>
        </div>
      </aside>
      <div className="frame">
        <Topbar route={route} operatorId={operator.operatorId} />
        <main className="main">
          {route.kind === 'engagements' ? (
            <EngagementsView />
          ) : (
            <EngagementView
              engagementId={route.engagementId}
              operator={operator}
              tab={activeTab}
              implantId={route.implantId}
            />
          )}
        </main>
      </div>
    </div>
  )
}

export default App
