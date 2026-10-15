import { Fragment, useCallback, useEffect, useState } from 'react'
import { readPollIntervalSeconds, writePollIntervalSeconds } from '../clientPrefs'
import {
  type MintedOperatorToken,
  type OperatorAccount,
  type OperatorTokenRow,
  type SessionOperator,
  createOperator,
  disableOperator,
  enableOperator,
  getBuildSettings,
  getSessionSettings,
  listOperatorTokens,
  listOperators,
  mintOperatorToken,
  putBuildSettings,
  putSessionSettings,
  revokeOperatorToken,
  setOperatorPassword,
} from '../api'
import { HelpHint } from '../components/HelpHint'
import { Icon } from '../components/Icons'
import { useArmedDelete } from '../useArmedDelete'

// The settings page, split by who may write what and laid out as tabs --
// the same toggle shape the build page's implant/webshell pick uses -- so a
// long member roster can never push the other sections off-screen. The
// Account and Console tabs are everyone's: password and API tokens are
// self-service over the signed-in operator's own account, and the console
// knobs are this browser's. The two management tabs -- the member roster
// (provisioning, resets, the disable switch) and the server-wide runtime
// settings -- render for the deployment admin alone: a member's settings
// page is purely personal, and the server draws the same line the page
// draws (a member's roster read refuses with 403).

// Up to two leading characters of a handle, for the avatar tile -- the same
// rule the topbar's identity chip uses.
function initials(handle: string): string {
  return handle.replace(/[^a-zA-Z0-9]/g, '').slice(0, 2).toUpperCase() || '?'
}

type SettingsTab = 'account' | 'members' | 'runtime' | 'console'

// The tab strip's entries, in strip order. adminOnly drives both the strip
// (the tab does not render for a member) and the section guard below.
const SETTINGS_TABS: { id: SettingsTab; label: string; help: string; adminOnly: boolean }[] = [
  {
    id: 'account',
    label: 'Account',
    help: 'The signed-in operator\'s own surface: identity, the password change, and this account\'s API tokens.',
    adminOnly: false,
  },
  {
    id: 'members',
    label: 'Members',
    help: "The deployment's account roster: provisioning, password resets, the API-token shelves, and the disable switch.",
    adminOnly: true,
  },
  {
    id: 'runtime',
    label: 'Runtime',
    help: 'Server-wide runtime settings: session presence and the shared build cache. Live on save, remembered across restarts.',
    adminOnly: true,
  },
  {
    id: 'console',
    label: 'Console',
    help: "This browser's own preferences, kept in localStorage and never sent to the teamserver.",
    adminOnly: false,
  },
]

export function SettingsView({ operator }: { operator: SessionOperator }) {
  const isAdmin = operator.role === 'admin'
  const [tab, setTab] = useState<SettingsTab>('account')
  return (
    <section className="view">
      <h2>
        Settings
        <HelpHint>
          Your account and this browser's console behavior -- and, for the deployment admin,
          the member roster and the server-wide runtime settings as their own tabs.
        </HelpHint>
      </h2>
      <div className="inline-form">
        {SETTINGS_TABS.filter((t) => !t.adminOnly || isAdmin).map((t) => (
          <button
            key={t.id}
            className={tab === t.id ? 'primary sm' : 'ghost sm'}
            onClick={() => setTab(t.id)}
            title={t.help}
          >
            {t.label}
          </button>
        ))}
      </div>
      {tab === 'members' && isAdmin && <MembersCard operator={operator} />}
      {tab === 'runtime' && isAdmin && <RuntimeCard />}
      {tab === 'account' && <AccountCard operator={operator} />}
      {tab === 'console' && <ConsoleCard />}
    </section>
  )
}

// The member roster and its management (the deployment admin's alone): the
// provisioning form, the account table, and the per-row actions -- password
// resets, the API-token shelf, and the disable switch. What an account may
// reach inside engagements arrives separately, as memberships each
// engagement's owner grants from that engagement's Members panel.
function MembersCard({ operator }: { operator: SessionOperator }) {
  const [accounts, setAccounts] = useState<OperatorAccount[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState<string | null>(null)

  const [handle, setHandle] = useState('')
  const [password, setPassword] = useState('')

  // The row whose management sheet is open. Closing it drops any one-time
  // token secret with it.
  const [openId, setOpenId] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    setBusy(true)
    try {
      setAccounts(await listOperators())
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
    if (password.length < 8) {
      setError('The initial password must be at least 8 characters.')
      return
    }
    try {
      const created = await createOperator({ handle: handle.trim(), password })
      setNote(`Provisioned '${created.handle}' -- it can log in now with the password you set.`)
      setError(null)
      setHandle('')
      setPassword('')
      await refresh()
    } catch (e) {
      setNote(null)
      setError(String(e))
    }
  }

  // An admin reset on another account states no proof; on the admin's own
  // row the change is self-service, and self-service proves the current
  // password (the server enforces both).
  const onResetPassword = async (account: OperatorAccount) => {
    const own = account.id === operator.operatorId
    let current: string | null = null
    if (own) {
      current = window.prompt(
        `Current password for '${account.handle}' (a change on your own account proves the current password).`,
      )
      if (current === null) return
    }
    const next = window.prompt(
      `New password for '${account.handle}' (at least 8 characters). ` +
        'A reset is a new credential generation: every live session of this account ends at its next request.',
    )
    if (next === null) return
    if (next.length < 8) {
      setError('The new password must be at least 8 characters.')
      return
    }
    try {
      await setOperatorPassword(account.id, next, current ?? undefined)
      setNote(
        `Password replaced for '${account.handle}'. Its live sessions ended` +
          (own ? ' -- including this one; sign in again with the new password.' : '.'),
      )
      setError(null)
      await refresh()
    } catch (e) {
      setNote(null)
      setError(String(e))
    }
  }

  const [armed, armDisable] = useArmedDelete()

  const onDisable = async (account: OperatorAccount) => {
    try {
      await disableOperator(account.id)
      setNote(
        `'${account.handle}' disabled -- its sessions and tokens refuse at their next use; enabling restores exactly what it had.`,
      )
      setError(null)
      setOpenId(null)
      await refresh()
    } catch (e) {
      setNote(null)
      setError(String(e))
    }
  }

  const onEnable = async (account: OperatorAccount) => {
    try {
      await enableOperator(account.id)
      setNote(`'${account.handle}' enabled -- it can log in again with the memberships it kept.`)
      setError(null)
      await refresh()
    } catch (e) {
      setNote(null)
      setError(String(e))
    }
  }

  return (
    <div className="card">
      <h3>
        <Icon name="users" />
        Members
        <HelpHint>
          The accounts that can log in. The bootstrap seed is the deployment admin (the '· admin'
          mark); every account after it is provisioned here and comes up a member. Reach is granted
          per engagement, from each engagement's Members panel.
        </HelpHint>
      </h3>

      <form className="build-form settings-form" onSubmit={onCreate}>
        <fieldset>
          <legend>New operator</legend>
          <label>
            Handle
            <input
              value={handle}
              onChange={(e) => setHandle(e.target.value)}
              placeholder="alice"
              title="The login handle. One account per handle -- a taken handle refuses with a conflict."
              required
            />
          </label>
          <label>
            Initial password
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              title="The password the account first logs in with (at least 8 characters). Hand it over out of band; it can be reset from the roster at any time."
              required
            />
          </label>
          <p className="muted" style={{ gridColumn: '1 / -1', margin: 0 }}>
            Accounts come up as deployment members -- after one logs in, grant its reach per
            engagement from that engagement's Members panel.
          </p>
        </fieldset>
        <button className="primary" type="submit" disabled={busy}>
          Provision
        </button>
      </form>
      {note && <p className="muted">{note}</p>}
      {error && <p className="error">{error}</p>}

      <div className="inline-form">
        <button className="ghost" onClick={() => void refresh()} disabled={busy}>
          <Icon name="refresh" />
          Refresh
        </button>
      </div>
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Handle</th>
              <th>Created</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {accounts.length === 0 && (
              <tr>
                <td colSpan={3}>
                  <div className="empty">
                    <Icon name="users" />
                    The roster did not load -- refresh to try again.
                  </div>
                </td>
              </tr>
            )}
            {accounts.map((account) => {
              const own = account.id === operator.operatorId
              return (
                <Fragment key={account.id}>
                  <tr className={account.disabled ? 'roster-row-disabled' : undefined}>
                    <td>
                      <span className="endpoint-cell">
                        <code>{account.handle}</code>
                        {own && <span className="muted"> (you)</span>}
                        {account.role === 'admin' && (
                          <span
                            className="muted"
                            title="The deployment admin: provisions accounts, holds the disable switch, writes the fleet and runtime settings. Seeded at bootstrap; the console only grows members."
                          >
                            {' '}
                            · admin
                          </span>
                        )}
                        {account.disabled && (
                          <span
                            className="muted"
                            title="Switched off: no authentication path accepts this account (login, cookie session, API token) and its sessions ended at their next use. Its memberships wait as they were, so enabling restores exactly what it had."
                          >
                            {' '}
                            · disabled
                          </span>
                        )}
                        {!account.hasCredential && (
                          <span
                            className="muted"
                            title="No password stands behind this account: the credential was revoked, and login refuses until a new one is provisioned."
                          >
                            {' '}
                            · no password
                          </span>
                        )}
                      </span>
                    </td>
                    <td title={new Date(account.createdAt).toLocaleString()}>
                      {new Date(account.createdAt).toLocaleDateString()}
                    </td>
                    <td>
                      <span className="row-actions">
                        <button
                          className="ghost sm"
                          onClick={() => setOpenId(openId === account.id ? null : account.id)}
                          title="Open this account's management sheet: the API-token shelf."
                        >
                          <Icon name="edit" />
                          {openId === account.id ? 'Close' : 'Manage'}
                        </button>
                        <button
                          className="ghost sm"
                          onClick={() => void onResetPassword(account)}
                          title={
                            own
                              ? 'Change your own password: states the current password, and the change signs this session out too.'
                              : "Set a new password for this account. A reset is a new credential generation: the account's live sessions end at their next request."
                          }
                        >
                          Reset password
                        </button>
                        {account.disabled ? (
                          <button
                            className="sm"
                            onClick={() => void onEnable(account)}
                            title="Re-open every authentication path for this account, with the memberships it kept through the disable."
                          >
                            Enable
                          </button>
                        ) : (
                          <button
                            className={`sm danger${armed === account.id ? ' armed' : ''}`}
                            onClick={() => armDisable(account.id, () => void onDisable(account))}
                            title={
                              own
                                ? 'Switch this account off (two clicks). Disabling yourself ends this session at its next request -- you will need another admin to enable you, so this is usually a mistake.'
                                : armed === account.id
                                  ? 'Click again to disable — the button reverts on its own after a few seconds'
                                  : "Switch this account off (two clicks: the first arms, the second disables). Every authentication path refuses at its next use; enabling restores exactly what it had. The last enabled account or admin can't be disabled."
                            }
                          >
                            {armed === account.id ? 'Confirm' : 'Disable'}
                          </button>
                        )}
                      </span>
                    </td>
                  </tr>
                  {openId === account.id && (
                    <tr className="roster-detail-row">
                      <td colSpan={3}>
                        <div className="roster-detail">
                          <TokenShelf operatorId={account.id} />
                        </div>
                      </td>
                    </tr>
                  )}
                </Fragment>
              )
            })}
          </tbody>
        </table>
      </div>
    </div>
  )
}

// The teamserver's runtime settings -- server-wide knobs the deployment
// admin adjusts while working. The session-presence pair explains the
// fleet's offline behavior: how long a silent session holds its Online dot
// before the sweep closes it, and how often the sweep checks. Changes apply
// live (the sweeper reads the current values on every pass; no restart) and
// the server persists them, so they survive the next restart.
//
// The bounds mirror the server's validation: threshold 1 minute..24 hours,
// sweep interval 10 seconds..1 hour. The threshold must stay above any
// implant's contact interval -- a threshold shorter than the sleep flaps
// every quiet period to offline -- which is why the minimum is a minute and
// the help text says so.
function RuntimeCard() {
  const [threshold, setThreshold] = useState('')
  const [interval, setIntervalValue] = useState('')
  const [saved, setSaved] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  // The build cache: the shared cargo target dir behind warm builds. Its
  // own draft and save, because it applies to the next build rather than
  // the next sweep pass.
  const [cacheDir, setCacheDir] = useState('')
  const [cacheSaved, setCacheSaved] = useState<string | null>(null)
  const [cacheBusy, setCacheBusy] = useState(false)

  useEffect(() => {
    void (async () => {
      try {
        const current = await getSessionSettings()
        setThreshold(String(current.thresholdMinutes))
        setIntervalValue(String(current.sweepIntervalMinutes))
        setError(null)
      } catch (e) {
        setError(String(e))
      }
    })()
  }, [])

  useEffect(() => {
    void (async () => {
      try {
        const current = await getBuildSettings()
        setCacheDir(current.rustTargetDir ?? '')
      } catch (e) {
        setError(String(e))
      }
    })()
  }, [])

  const onSaveCache = async (event: React.FormEvent) => {
    event.preventDefault()
    setCacheBusy(true)
    try {
      const applied = await putBuildSettings({ rustTargetDir: cacheDir.trim() || null })
      setCacheDir(applied.rustTargetDir ?? '')
      setCacheSaved(
        applied.rustTargetDir
          ? `Applied -- the next build compiles against '${applied.rustTargetDir}'.`
          : 'Applied -- builds are hermetic again (a fresh target dir per build).',
      )
      setError(null)
    } catch (e) {
      setCacheSaved(null)
      setError(String(e))
    } finally {
      setCacheBusy(false)
    }
  }

  const onSave = async (event: React.FormEvent) => {
    event.preventDefault()
    const thresholdMinutes = Number(threshold)
    const sweepIntervalMinutes = Number(interval)
    if (!Number.isFinite(thresholdMinutes) || !Number.isFinite(sweepIntervalMinutes)) {
      setError('Both values must be numbers (minutes).')
      return
    }
    setBusy(true)
    try {
      const applied = await putSessionSettings({ thresholdMinutes, sweepIntervalMinutes })
      setThreshold(String(applied.thresholdMinutes))
      setIntervalValue(String(applied.sweepIntervalMinutes))
      setSaved(
        `Applied -- the next sweep pass (within ${applied.sweepIntervalMinutes} min) uses the new threshold.`,
      )
      setError(null)
    } catch (e) {
      setSaved(null)
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="card">
      <h3>
        Runtime settings
        <HelpHint>
          Teamserver runtime settings -- server-wide, live on save, remembered across
          restarts.
        </HelpHint>
      </h3>

      <form className="build-form settings-form" onSubmit={onSave}>
        <fieldset>
          <legend>Session presence</legend>
          <label>
            Offline after (min)
            <input
              value={threshold}
              onChange={(e) => setThreshold(e.target.value)}
              title="How long a session may go silent before the sweep closes it and the implant shows offline. A stream that closes cleanly drops immediately; this is the wait for one that dies silently. Keep it above your implants' contact interval (a shorter threshold flaps every quiet period). 1..1440; default 15."
            />
          </label>
          <label>
            Sweep every (min)
            <input
              value={interval}
              onChange={(e) => setIntervalValue(e.target.value)}
              title="How often the staleness check runs. A shorter interval drops vanished implants sooner at the cost of more frequent passes. 0.17..60; default 1."
            />
          </label>
          <p className="muted" style={{ gridColumn: '1 / -1', margin: 0 }}>
            A beacon stream that dies without a clean close keeps its Online dot until the sweep
            closes it -- the dot lags reality by at most the threshold; Last seen always tells the
            truth meanwhile.
          </p>
        </fieldset>
        <button className="primary" type="submit" disabled={busy}>
          Save
        </button>
      </form>
      {saved && <p className="muted">{saved}</p>}

      <form className="build-form form-section settings-form" onSubmit={onSaveCache}>
        <fieldset>
          <legend>Build cache</legend>
          <label>
            Shared cargo target dir
            <input
              value={cacheDir}
              onChange={(e) => setCacheDir(e.target.value)}
              placeholder="/var/lib/rod/cargo-target"
              title="The persistent directory payload builds share as their cargo target dir -- the warm compile cache. Dependency artifacts are reused across builds (only the implant crate recompiles), turning a cold cross-compile into a one-time cost; concurrent builds queue on cargo's own lock. Empty = hermetic: a fresh disposable target dir per build, every cold compile pays in full. Must be an absolute path. The boot default is the ROD_RUST_TARGET_DIR environment variable; this setting outranks it and survives restarts."
            />
            <span className="field-help">empty = hermetic per-build dirs</span>
          </label>
          <p className="muted" style={{ gridColumn: '1 / -1', margin: 0 }}>
            Where payload builds compile. The System page's build findings read this value; the
            first build after a change pays the cold compile into the new directory.
          </p>
        </fieldset>
        <button className="primary" type="submit" disabled={cacheBusy}>
          Save
        </button>
      </form>
      {cacheSaved && <p className="muted">{cacheSaved}</p>}
      {error && <p className="error">{error}</p>}
    </div>
  )
}

// The signed-in operator's own surface: identity at a glance, the password
// change (self-service -- the current password is stated and verified, and
// a change ends this session at its next request), and this account's API
// tokens. The same card renders for admin and member alike; what differs
// by role lives in the management cards above.
function AccountCard({ operator }: { operator: SessionOperator }) {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [confirm, setConfirm] = useState('')
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const onChangePassword = async (event: React.FormEvent) => {
    event.preventDefault()
    if (next.length < 8) {
      setError('The new password must be at least 8 characters.')
      setSaved(null)
      return
    }
    if (next !== confirm) {
      setError('The new password and its confirmation differ.')
      setSaved(null)
      return
    }
    setBusy(true)
    try {
      await setOperatorPassword(operator.operatorId, next, current)
      setSaved(
        'Password replaced -- this session ends at its next request; sign in again with the new one.',
      )
      setError(null)
      setCurrent('')
      setNext('')
      setConfirm('')
    } catch (e) {
      setSaved(null)
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="card">
      <h3>
        Account
        <HelpHint>
          Your own account: the password change and the API tokens are self-service -- the
          deployment admin never needs to stand in.
        </HelpHint>
      </h3>

      <div className="settings-grid">
        <div className="settings-col">
          <div className="account-head">
            <span className="avatar account-avatar">{initials(operator.handle)}</span>
            <div>
              <div className="account-handle">
                <code>{operator.handle}</code>
                <span className="muted"> · {operator.role}</span>
              </div>
              <div
                className="muted"
                title={new Date(operator.createdAt).toLocaleString()}
                style={{ fontSize: 11 }}
              >
                created {new Date(operator.createdAt).toLocaleDateString()}
              </div>
            </div>
          </div>

          <form className="build-form settings-form" onSubmit={onChangePassword}>
            <fieldset>
              <legend>Change password</legend>
              <label>
                Current password
                <input
                  type="password"
                  value={current}
                  onChange={(e) => setCurrent(e.target.value)}
                  title="Self-service proves possession: the current password is verified against the stored hash."
                  required
                />
              </label>
              <label>
                New password
                <input
                  type="password"
                  value={next}
                  onChange={(e) => setNext(e.target.value)}
                  title="At least 8 characters. A change is a new credential generation: every live session of this account (including this one) ends at its next request."
                  required
                />
              </label>
              <label>
                Confirm new password
                <input
                  type="password"
                  value={confirm}
                  onChange={(e) => setConfirm(e.target.value)}
                  required
                />
              </label>
            </fieldset>
            <button className="primary" type="submit" disabled={busy}>
              Change password
            </button>
          </form>
          {saved && <p className="muted">{saved}</p>}
          {error && <p className="error">{error}</p>}
        </div>

        <div className="token-shelf-box">
          <TokenShelf operatorId={operator.operatorId} />
        </div>
      </div>
    </div>
  )
}

// One account's API tokens: bearer credentials for the API and the MCP
// surface, usable wherever the cookie session is not. The one-time secret
// is shown exactly once, at mint; only the digest is stored afterwards.
// Revocation bites at the token's next use, and a disabled account's
// tokens refuse until it is enabled again.
function TokenShelf({ operatorId }: { operatorId: string }) {
  const [tokens, setTokens] = useState<OperatorTokenRow[]>([])
  const [minted, setMinted] = useState<MintedOperatorToken | null>(null)
  const [secretCopied, setSecretCopied] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const reload = useCallback(async () => {
    try {
      setTokens(await listOperatorTokens(operatorId))
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }, [operatorId])

  useEffect(() => {
    void reload()
  }, [reload])

  const onMint = async () => {
    try {
      setMinted(await mintOperatorToken(operatorId))
      setSecretCopied(false)
      setError(null)
      await reload()
    } catch (e) {
      setError(String(e))
    }
  }

  const onRevoke = async (tokenId: string) => {
    try {
      await revokeOperatorToken(operatorId, tokenId)
      if (minted?.tokenId === tokenId) setMinted(null)
      setError(null)
      await reload()
    } catch (e) {
      setError(String(e))
    }
  }

  const onCopySecret = async () => {
    if (!minted) return
    try {
      await navigator.clipboard.writeText(minted.token)
      setSecretCopied(true)
    } catch {
      // Clipboard access can be denied; the text stays selectable to copy.
    }
  }

  return (
    <section className="token-shelf">
      <div className="roster-detail-title">API tokens</div>
      {minted && (
        <div className="token-reveal">
          <code>{minted.token}</code>
          <button className="ghost sm" onClick={() => void onCopySecret()}>
            {secretCopied ? 'Copied' : 'Copy'}
          </button>
          <span className="muted">shown once -- only the digest is stored from here on</span>
        </div>
      )}
      <div>
        <button className="sm" onClick={() => void onMint()}>
          Mint token
        </button>
      </div>
      {tokens.length > 0 ? (
        <ul className="token-list">
          {tokens.map((t) => (
            <li key={t.tokenId}>
              <code>{t.tokenId.slice(0, 8)}</code>
              <span className="muted"> minted {new Date(t.createdAt).toLocaleDateString()}</span>
              <button
                className="ghost sm"
                onClick={() => void onRevoke(t.tokenId)}
                title="Revoke this token: it fails at its next use. Idempotent."
              >
                Revoke
              </button>
            </li>
          ))}
        </ul>
      ) : (
        !error && (
          <p className="muted" style={{ margin: 0 }}>
            No tokens minted for this account.
          </p>
        )
      )}
      {error && <p className="error">{error}</p>}
    </section>
  )
}

// This browser's own console preferences: presentation state the UI keeps
// in localStorage, applies on the spot, and never sends to the server --
// unlike the runtime card above, these follow the operator's browser, not
// the teamserver.
function ConsoleCard() {
  const [pollDraft, setPollDraft] = useState(() => String(readPollIntervalSeconds()))
  const [saved, setSaved] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const onSavePoll = (event: React.FormEvent) => {
    event.preventDefault()
    const seconds = Number(pollDraft.trim())
    if (!Number.isFinite(seconds)) {
      setError('Seconds must be a number.')
      setSaved(null)
      return
    }
    const clamped = writePollIntervalSeconds(seconds)
    setPollDraft(String(clamped))
    setSaved(`Applied -- presence surfaces now re-read every ${clamped}s.`)
    setError(null)
  }

  return (
    <div className="card">
      <h3>
        Console behavior
        <HelpHint>
          This browser's own preferences -- saved locally, applied on the spot, never sent to the
          teamserver.
        </HelpHint>
      </h3>
      <form className="build-form settings-form" onSubmit={onSavePoll}>
        <fieldset>
          <legend>Refresh cadence</legend>
          <label>
            Poll every (seconds)
            <input
              value={pollDraft}
              onChange={(e) => setPollDraft(e.target.value)}
              title="How often presence-style surfaces re-read while their tab is visible: the redirector fleet's roster, the engagement view's fallback refresh, the shell roster. Build progress keeps its own faster cadence -- it is task progress, not presence. 2..60; default 5."
            />
            <span className="field-help">2..60 · default 5</span>
          </label>
        </fieldset>
        <button className="primary" type="submit">
          Save
        </button>
      </form>
      {saved && <p className="muted">{saved}</p>}
      {error && <p className="error">{error}</p>}
    </div>
  )
}
