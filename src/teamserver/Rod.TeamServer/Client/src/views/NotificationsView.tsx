import { useCallback, useEffect, useState } from 'react'
import {
  NOTIFIABLE_EVENT_KINDS,
  type WebhookSubscription,
  deleteWebhookSubscription,
  disableWebhookSubscription,
  enableWebhookSubscription,
  listWebhookSubscriptions,
  registerWebhookSubscription,
  testWebhookSubscription,
} from '../api'
import { Icon } from '../components/Icons'
import { StatusBadge } from '../components/StatusBadge'
import { useArmedDelete } from '../useArmedDelete'
import { ago, useNow } from '../when'

// The notifications panel (architecture.md Sec 4.4): the engagement's
// out-of-band channels -- the webhook URLs that receive the live events no
// operator sat up for. Creation is name, URL, and the event kinds to push;
// the table reads each channel's own bookkeeping back (deliveries, last
// push, the failure run that ends in the channel parking itself) because
// that state is the forwarder's live record, persisted with the
// subscription.

// The channel target renders as its host: the URL itself is the channel's
// bearer secret, and the console treats it like one -- full value only in
// the edit field it was typed into.
function hostOf(url: string): string {
  try {
    return new URL(url).host
  } catch {
    return url
  }
}

function kindLabel(kind: string): string {
  return NOTIFIABLE_EVENT_KINDS.find((k) => k.value === kind)?.label ?? kind
}

export function NotificationsView({
  engagementId,
  onlineTick,
}: {
  engagementId: string
  onlineTick?: number
}) {
  const [subscriptions, setSubscriptions] = useState<WebhookSubscription[]>([])
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  // Create-form draft. The engagement switch resets it whole: a channel
  // registered in one engagement is invisible in the next.
  const [name, setName] = useState('')
  const [url, setUrl] = useState('')
  const [kinds, setKinds] = useState<ReadonlySet<string>>(
    new Set(['SessionOpened', 'ShellSessionOpened']),
  )

  const [armed, arm] = useArmedDelete()

  const now = useNow(10_000)

  const refresh = useCallback(async () => {
    setBusy(true)
    try {
      setSubscriptions(await listWebhookSubscriptions(engagementId))
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }, [engagementId])

  // The channel rows ride the live tick: pushes do not ride the engagement
  // stream themselves, but the events behind them do, so delivery counts
  // move on the same cadence the rest of the console refreshes on.
  useEffect(() => {
    void refresh()
  }, [refresh, onlineTick])

  // Engagement switch: drop every cross-engagement choice.
  useEffect(() => {
    setName('')
    setUrl('')
    setKinds(new Set(['SessionOpened', 'ShellSessionOpened']))
  }, [engagementId])

  const toggleKind = (kind: string) => {
    const next = new Set(kinds)
    if (next.has(kind)) next.delete(kind)
    else next.add(kind)
    setKinds(next)
  }

  const onRegister = async (e: React.FormEvent) => {
    e.preventDefault()
    if (kinds.size === 0) {
      setError('Pick at least one event kind to push.')
      return
    }
    try {
      await registerWebhookSubscription(engagementId, {
        name,
        url,
        eventKinds: [...kinds],
      })
      setName('')
      setUrl('')
      setError(null)
      setNotice(null)
      await refresh()
    } catch (err) {
      setError(String(err))
    }
  }

  const onToggle = async (subscription: WebhookSubscription) => {
    try {
      if (subscription.enabled)
        await disableWebhookSubscription(engagementId, subscription.id)
      else await enableWebhookSubscription(engagementId, subscription.id)
      setError(null)
      await refresh()
    } catch (err) {
      setError(String(err))
    }
  }

  // The pre-bed verification: one test frame down the same delivery path.
  // The outcome is the answer either way.
  const onTest = async (subscription: WebhookSubscription) => {
    try {
      const result = await testWebhookSubscription(engagementId, subscription.id)
      setError(null)
      setNotice(`Test push on "${subscription.name}": ${result.outcome}`)
    } catch (err) {
      setError(String(err))
    }
  }

  const onDelete = async (subscription: WebhookSubscription) => {
    try {
      await deleteWebhookSubscription(engagementId, subscription.id)
      setError(null)
      await refresh()
    } catch (err) {
      setError(String(err))
    }
  }

  return (
    <section className="view">
      <h2>Notifications</h2>
      <p className="muted">
        Channels that reach the operator off-console -- each pushes the live events it
        names to one webhook URL, the frame a connected console sees. Delivery is
        single-attempt and best-effort; every attempt lands in the audit trail, the
        URL is the channel's bearer secret and never does, and three failures in a
        row park the channel with the cause in the trail.
      </p>

      {error && <p className="error">{error}</p>}
      {notice && <p className="notice">{notice}</p>}

      <div className="card">
        <h3>New channel</h3>
        <form className="listener-form" onSubmit={onRegister}>
          <div className="listener-row">
            <label>
              Name
              <input
                placeholder="overnight-watch"
                value={name}
                onChange={(e) => setName(e.target.value)}
                required
                title="The operator-facing name this channel reads under in the list and the audit trail."
              />
            </label>
            <label>
              Webhook URL
              <input
                type="url"
                placeholder="https://hooks.example.test/push?token=…"
                value={url}
                onChange={(e) => setUrl(e.target.value)}
                required
                title="Absolute https (plain http on loopback only). The URL is the channel's bearer secret -- the query is where incoming-webhook credentials ride. Treat it like a password."
              />
            </label>
          </div>
          <fieldset className="listener-row">
            <legend>Push on</legend>
            {NOTIFIABLE_EVENT_KINDS.map((k) => (
              <label key={k.value} className="checkbox-label">
                {k.label}
                <span className="checkbox-row">
                  <input
                    type="checkbox"
                    checked={kinds.has(k.value)}
                    onChange={() => toggleKind(k.value)}
                  />
                </span>
              </label>
            ))}
          </fieldset>
          <div className="listener-row">
            <div className="listener-form-actions">
              <button className="primary" type="submit" disabled={busy}>
                Register channel
              </button>
            </div>
          </div>
        </form>
      </div>

      <div className="card">
        <div className="table-toolbar">
          <h3>Channels</h3>
          <button className="ghost" onClick={() => void refresh()} disabled={busy}>
            <Icon name="refresh" />
            Refresh
          </button>
        </div>
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Status</th>
                <th>Channel</th>
                <th>Target</th>
                <th>Pushes on</th>
                <th>Deliveries</th>
                <th>Health</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {subscriptions.length === 0 && (
                <tr>
                  <td colSpan={7}>
                    <div className="empty">
                      <Icon name="bell" />
                      No notification channels for this engagement yet
                    </div>
                  </td>
                </tr>
              )}
              {subscriptions.map((subscription) => (
                <tr
                  key={subscription.id}
                  className={subscription.enabled ? undefined : 'row-dim'}
                >
                  <td>
                    <StatusBadge
                      status={subscription.enabled ? 'enabled' : 'disabled'}
                    />
                  </td>
                  <td
                    title={`${subscription.name} · registered ${new Date(subscription.createdAt).toLocaleString()}`}
                  >
                    {subscription.name}
                  </td>
                  <td title="The URL is the channel's bearer secret; the console shows only its host.">
                    {hostOf(subscription.url)}
                  </td>
                  <td>{subscription.eventKinds.map(kindLabel).join(' · ')}</td>
                  <td
                    title={
                      subscription.lastDeliveredAt
                        ? `Last delivered ${new Date(subscription.lastDeliveredAt).toLocaleString()}`
                        : 'Nothing pushed yet.'
                    }
                  >
                    {subscription.deliveryCount}
                    {subscription.lastDeliveredAt && (
                      <span className="muted"> · {ago(subscription.lastDeliveredAt, now)}</span>
                    )}
                  </td>
                  <td>
                    {subscription.consecutiveFailures > 0 ? (
                      <span
                        className="status failed"
                        title={subscription.lastFailureReason ?? undefined}
                      >
                        {subscription.consecutiveFailures} failed in a row
                      </span>
                    ) : (
                      <span className="muted">—</span>
                    )}
                  </td>
                  <td onClick={(e) => e.stopPropagation()}>
                    <div className="row-actions">
                      <button
                        className="ghost sm"
                        onClick={() => void onTest(subscription)}
                        disabled={busy}
                      >
                        Test
                      </button>
                      <button
                        className="ghost sm"
                        onClick={() => void onToggle(subscription)}
                        disabled={busy}
                      >
                        {subscription.enabled ? 'Disable' : 'Enable'}
                      </button>
                      <button
                        className={`sm danger${armed === subscription.id ? ' armed' : ''}`}
                        onClick={() =>
                          arm(subscription.id, () => void onDelete(subscription))
                        }
                      >
                        {armed === subscription.id ? 'Confirm delete' : 'Delete'}
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </section>
  )
}
