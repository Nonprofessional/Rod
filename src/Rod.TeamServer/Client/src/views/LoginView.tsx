import { useState } from 'react'
import { type LoginInput } from '../api'

// Operator sign-in (architecture.md Sec 4): a browser session is established by
// a handle and password the teamserver verifies, which sets the auth cookie the
// rest of the UI depends on. Replaces the walking skeleton's self-assigned
// identity. Shown by the route guard whenever GET /operators/me is unauthorized.

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
    <section className="card">
      <h2>Sign in</h2>
      <p className="muted">Authenticate to the Rod teamserver.</p>
      <form className="login-form" onSubmit={onSubmit}>
        <input
          placeholder="Handle"
          value={handle}
          onChange={(e) => setHandle(e.target.value)}
          required
          autoFocus
        />
        <input
          type="password"
          placeholder="Password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          required
        />
        <button type="submit" disabled={busy}>
          Sign in
        </button>
      </form>
      {error && <p className="error">{error}</p>}
    </section>
  )
}
