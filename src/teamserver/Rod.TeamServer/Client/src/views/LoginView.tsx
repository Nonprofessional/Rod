import { useState } from 'react'
import { type LoginInput } from '../api'
import { Icon } from '../components/Icons'

// Operator sign-in (architecture.md Sec 4): a browser session is established by
// a handle and password the teamserver verifies, which sets the auth cookie the
// rest of the UI depends on. Shown by the route guard whenever GET
// /operators/me is unauthorized. Rendered outside the app shell as a centered
// card -- the only screen an unauthenticated browser ever sees.

export function LoginView({
  onLogin,
}: {
  onLogin: (input: LoginInput) => Promise<void>
}) {
  const [handle, setHandle] = useState('')
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const onSubmit = async (event: React.FormEvent) => {
    event.preventDefault()
    setBusy(true)
    try {
      await onLogin({ handle, password })
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="login-page">
      <div className="login-card">
        <div className="login-brand">
          <span className="brand-mark">
            <Icon name="terminal" className="brand-glyph" />
          </span>
          <span className="brand-text">
            <span className="brand-name">Rod</span>
            <span className="brand-sub">teamserver</span>
          </span>
        </div>
        <h2 className="login-title">Sign in</h2>
        <p className="login-sub">Authenticate to reach your engagements.</p>
        <form className="login-form" onSubmit={onSubmit}>
          <div className="login-field">
            <label htmlFor="login-handle">Username</label>
            <input
              id="login-handle"
              placeholder="Username"
              value={handle}
              onChange={(e) => setHandle(e.target.value)}
              required
              autoFocus
            />
          </div>
          <div className="login-field">
            <label htmlFor="login-password">Password</label>
            <input
              id="login-password"
              type="password"
              placeholder="Password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              required
            />
          </div>
          <button className="primary" type="submit" disabled={busy}>
            {busy ? 'Signing in…' : 'Sign in'}
          </button>
        </form>
        {error && <p className="alert">{error}</p>}
      </div>
    </div>
  )
}
