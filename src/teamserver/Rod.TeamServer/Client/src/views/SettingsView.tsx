import { Fragment, useCallback, useEffect, useState } from 'react'
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
import { Icon } from '../components/Icons'
import { useArmedDelete } from '../useArmedDelete'

// The teamserver's runtime settings -- operator-level, not engagement-level:
// server-wide knobs an operator adjusts while working. The session-presence
// pair explains the fleet's offline behavior: how long a silent session holds
// its Online dot before the sweep closes it, and how often the sweep checks.
// Changes apply live (the sweeper reads the current values on every pass; no
// restart) and the server persists them, so they survive the next restart.
//
// The bounds mirror the server's validation: threshold 1 minute..24 hours,
// sweep interval 10 seconds..1 hour. The threshold must stay above any
// implant's contact interval -- a threshold shorter than the sleep flaps
// every quiet period to offline -- which is why the minimum is a minute and
// the help text says so.

export function SettingsView({ operator }: { operator: SessionOperator }) {
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
    <>
      <div className="card">
        <h3>Settings</h3>
        <p className="muted">
          Teamserver runtime settings -- server-wide, live on save, remembered across restarts.
        </p>

        <form className="build-form" onSubmit={onSave}>
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
              Why a vanished implant holds its dot: a beacon stream that dies without a clean close
              keeps its session Active until the sweep closes it -- the Online dot lags reality by at
              most the threshold, and the fleet's Last seen column always tells the truth in the
              meantime.
            </p>
          </fieldset>
          <button className="primary" type="submit" disabled={busy}>
            Save
          </button>
        </form>
        {saved && <p className="muted">{saved}</p>}
        {error && <p className="error">{error}</p>}

        <form className="build-form form-section" onSubmit={onSaveCache}>
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
      </div>

      <OperatorsCard operator={operator} />
    </>
  )
}

// The account roster, the provisioning form, and the per-account management
// row. The bootstrap account (Operators:Initial) is the first operator;
// every account after it arrives here -- POST /operators registers the
// handle and its initial password in one step, so the account works the
// moment the row appears. An account carries no permission: what it may
// reach arrives per engagement, as the memberships its owners grant (the
// engagement's Members panel). Each row opens into its management sheet --
// the API-token shelf (mint with its one-time secret, list, revoke) and the
// disable/enable switch, the account off switch that ends every
// authentication path while the memberships wait as they were.
function OperatorsCard({ operator }: { operator: SessionOperator }) {
  const [accounts, setAccounts] = useState<OperatorAccount[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState<string | null>(null)

  const [handle, setHandle] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [password, setPassword] = useState('')

  // The expanded management row: the account being managed, its token list,
  // and the one-time secret of the token just minted (the secret lives only
  // here -- closing the row or minting again drops it).
  const [openId, setOpenId] = useState<string | null>(null)
  const [tokens, setTokens] = useState<OperatorTokenRow[]>([])
  const [minted, setMinted] = useState<MintedOperatorToken | null>(null)
  const [secretCopied, setSecretCopied] = useState(false)

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
      const created = await createOperator({
        handle: handle.trim(),
        displayName: displayName.trim() || undefined,
        password,
      })
      setNote(`Provisioned '${created.handle}' -- it can log in now with the password you set.`)
      setError(null)
      setHandle('')
      setDisplayName('')
      setPassword('')
      await refresh()
    } catch (e) {
      setNote(null)
      setError(String(e))
    }
  }

  const onResetPassword = async (account: OperatorAccount) => {
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
      await setOperatorPassword(account.id, next)
      setNote(
        `Password replaced for '${account.handle}'. Its live sessions ended` +
          (account.id === operator.operatorId ? ' -- including this one; sign in again with the new password.' : '.'),
      )
      setError(null)
      await refresh()
    } catch (e) {
      setNote(null)
      setError(String(e))
    }
  }

  // --- The expanded management row ---------------------------------------

  const reloadTokens = async (operatorId: string) => {
    try {
      setTokens(await listOperatorTokens(operatorId))
    } catch (e) {
      setError(String(e))
    }
  }

  const onManage = async (account: OperatorAccount) => {
    if (openId === account.id) {
      setOpenId(null)
      setMinted(null)
      return
    }
    setOpenId(account.id)
    setMinted(null)
    setSecretCopied(false)
    await reloadTokens(account.id)
  }

  const onMint = async (account: OperatorAccount) => {
    try {
      const secret = await mintOperatorToken(account.id)
      setMinted(secret)
      setSecretCopied(false)
      setError(null)
      await reloadTokens(account.id)
    } catch (e) {
      setError(String(e))
    }
  }

  const onRevokeToken = async (account: OperatorAccount, tokenId: string) => {
    try {
      await revokeOperatorToken(account.id, tokenId)
      if (minted?.tokenId === tokenId) setMinted(null)
      setError(null)
      await reloadTokens(account.id)
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

  const [armed, armDisable] = useArmedDelete()

  const onDisable = async (account: OperatorAccount) => {
    try {
      await disableOperator(account.id)
      setNote(
        `'${account.handle}' disabled -- its sessions and tokens refuse at their next use; enabling restores exactly what it had.`,
      )
      setError(null)
      setMinted(null)
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
        Operators
      </h3>
      <p className="muted">
        The accounts that can log in. The configured initial account is the bootstrap; every account
        after it is provisioned here -- handle and initial password in one step. Reach is granted
        per engagement, from each engagement's Members panel.
      </p>

      <form className="build-form" onSubmit={onCreate}>
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
              Display name
              <input
                value={displayName}
                onChange={(e) => setDisplayName(e.target.value)}
                placeholder="optional -- defaults to the handle"
                title="The name the roster and audit views render; the handle stands in when left empty."
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
              An account holds no permission on its own -- after it logs in, grant its reach per
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
              <th>Display name</th>
              <th>Created</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {accounts.length === 0 && (
              <tr>
                <td colSpan={4}>
                  <div className="empty">
                    <Icon name="users" />
                    The roster did not load -- refresh to try again.
                  </div>
                </td>
              </tr>
            )}
            {accounts.map((account) => (
              <Fragment key={account.id}>
                <tr className={account.disabled ? 'roster-row-disabled' : undefined}>
                  <td>
                    <span className="endpoint-cell">
                      <code>{account.handle}</code>
                      {account.id === operator.operatorId && <span className="muted"> (you)</span>}
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
                  <td>{account.displayName}</td>
                  <td title={new Date(account.createdAt).toLocaleString()}>
                    {new Date(account.createdAt).toLocaleDateString()}
                  </td>
                  <td>
                    <span className="row-actions">
                        <button
                          className="ghost sm"
                          onClick={() => void onManage(account)}
                          title="Open this account's management sheet: the API-token shelf and the disable switch."
                        >
                          <Icon name="edit" />
                          {openId === account.id ? 'Close' : 'Manage'}
                        </button>
                        <button
                          className="ghost sm"
                          onClick={() => void onResetPassword(account)}
                          title="Set a new password for this account. A reset is a new credential generation: the account's live sessions end at their next request -- resetting your own signs this session out too."
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
                              account.id === operator.operatorId
                                ? 'Switch this account off (two clicks). Disabling yourself ends this session at its next request -- you will need a colleague to enable you, so this is usually a mistake.'
                                : armed === account.id
                                  ? 'Click again to disable — the button reverts on its own after a few seconds'
                                  : "Switch this account off (two clicks: the first arms, the second disables). Every authentication path refuses at its next use; enabling restores exactly what it had. The last enabled account can't be disabled."
                            }
                          >
                            {armed === account.id ? 'Confirm disable' : 'Disable'}
                          </button>
                        )}
                      </span>
                  </td>
                </tr>
                {openId === account.id && (
                  <tr className="roster-detail-row">
                    <td colSpan={4}>
                      <div className="roster-detail">
                        <section>
                          <div className="roster-detail-title">API tokens</div>
                          {minted && (
                            <div className="token-reveal">
                              <code>{minted.token}</code>
                              <button className="ghost sm" onClick={() => void onCopySecret()}>
                                {secretCopied ? 'Copied' : 'Copy'}
                              </button>
                              <span className="muted">
                                shown once -- only the digest is stored from here on
                              </span>
                            </div>
                          )}
                          <div>
                            <button className="sm" onClick={() => void onMint(account)}>
                              Mint token
                            </button>
                          </div>
                          {tokens.length > 0 ? (
                            <ul className="token-list">
                              {tokens.map((t) => (
                                <li key={t.tokenId}>
                                  <code>{t.tokenId.slice(0, 8)}</code>
                                  <span className="muted">
                                    {' '}
                                    minted {new Date(t.createdAt).toLocaleDateString()}
                                  </span>
                                  <button
                                    className="ghost sm"
                                    onClick={() => void onRevokeToken(account, t.tokenId)}
                                    title="Revoke this token: it fails at its next use. Idempotent."
                                  >
                                    Revoke
                                  </button>
                                </li>
                              ))}
                            </ul>
                          ) : (
                            <p className="muted" style={{ margin: 0 }}>
                              No tokens minted for this account.
                            </p>
                          )}
                          <p className="muted" style={{ margin: 0 }}>
                            Bearer credentials for the API and the MCP surface, usable wherever the
                            cookie session is not. Revocation bites at the token's next use; a
                            disabled account's tokens refuse until it is enabled again.
                          </p>
                        </section>
                      </div>
                    </td>
                  </tr>
                )}
              </Fragment>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}
