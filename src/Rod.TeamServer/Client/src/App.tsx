import { useCallback, useEffect, useState } from 'react'
import './App.css'
import { EngagementView } from './views/EngagementView'
import { EngagementsView, type SessionOperator } from './views/EngagementsView'

// Operator UI shell (roadmap M1.5, expanded M11.1): lists engagements, drills
// into one to reach the full capability surface -- tasking (recon through
// exploit), implants (with retire), the M6 evidence views (audit, artifacts,
// timeline, report), and the M4 OPSEC controls (listeners/redirectors, payload
// build). Each open engagement holds a Server-Sent Events stream open so every
// connected operator sees tasking, results, and presence in real time (M2.4).
// Identity is self-assigned for the session (real operator auth arrives later);
// navigation is hash-based so the host's static-file fallback keeps deep links
// working without a server-side router.

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

// One operator identity for the browser session. The walking skeleton resolves
// it client-side (a self-typed handle plus a stable id); real operator
// authentication arrives later and replaces only how this identity is
// established, not the components that consume it. Persists across route
// changes so engagement ownership and live presence stay consistent.
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

  const onTab = useCallback((tab: string) => {
    if (route.kind === 'engagement') {
      window.location.hash = `/engagements/${route.engagementId}/${tab}`
    }
  }, [route])

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
        <span className="who" title="Your session identity (real operator auth arrives later)">
          <code>{operator.handle}</code>
        </span>
      </header>
      <main className="app-main">
        {route.kind === 'engagements' ? (
          <EngagementsView operator={operator} setOperator={setOperator} />
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
