import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  type AutomationRule,
  type Implant,
  createAutomationRule,
  deleteAutomationRule,
  disableAutomationRule,
  enableAutomationRule,
  listAutomationRules,
  listImplants,
} from '../api'
import { Icon } from '../components/Icons'
import { OpsecBadges } from '../components/OpsecBadges'
import { StatusBadge } from '../components/StatusBadge'
import { loadCapabilityGroups, verbAttributes, type CapabilityGroup } from '../capabilities'
import { CHANNEL_VERBS } from '../verbForms'
import { useArmedDelete } from '../useArmedDelete'
import { ago, until, useNow } from '../when'

// The automation panel (architecture.md Sec 10.4): the engagement's declarative
// rules -- what fires while no operator watches. Creation is the three-part
// form (trigger, condition, action); the table reads each rule's own guard
// state back (fire count, next fire, refusal streak) because that state is the
// engine's live bookkeeping, persisted with the rule and updated per firing.

// The event kinds the server's trigger whitelist admits. Duplicated here
// because the server exposes no "triggerable kinds" read; the server stays the
// authority -- anything else it refuses at creation with a readable 422.
const EVENT_KINDS: readonly { value: string; label: string }[] = [
  { value: 'SessionOpened', label: 'Session opened' },
  { value: 'SessionClosed', label: 'Session closed' },
  { value: 'TaskIssued', label: 'Task issued' },
  { value: 'TaskCompleted', label: 'Task completed' },
  { value: 'TaskCancelled', label: 'Task cancelled' },
  { value: 'ImplantRetired', label: 'Implant retired' },
]

// A cadence an operator reads: 1800 -> "30m", not "1800s".
function compactInterval(seconds: number): string {
  if (seconds % 86400 === 0) return `${seconds / 86400}d`
  if (seconds % 3600 === 0) return `${seconds / 3600}h`
  if (seconds >= 3600) return `${Math.floor(seconds / 3600)}h${Math.round((seconds % 3600) / 60)}m`
  if (seconds >= 60) return `${Math.round(seconds / 60)}m`
  return `${seconds}s`
}

function implantLabel(implants: readonly Implant[], id: string): string {
  const implant = implants.find((i) => i.implantId === id)
  if (!implant) return id.slice(0, 8)
  return implant.hostname ? `${implant.hostname} (${id.slice(0, 8)})` : id.slice(0, 8)
}

function eventLabel(kind: string): string {
  return EVENT_KINDS.find((k) => k.value === kind)?.label ?? kind
}

// The trigger column: "every 30m", "on Session opened · web01 (abc12345)",
// "on Task completed of recon.portscan".
function describeTrigger(rule: AutomationRule, implants: readonly Implant[]): string {
  if (rule.triggerKind === 'interval' && rule.intervalSeconds !== null)
    return `every ${compactInterval(rule.intervalSeconds)}`
  if (rule.triggerKind !== 'event' || rule.eventKind === null) return rule.triggerKind
  let text = `on ${eventLabel(rule.eventKind)}`
  if (rule.completedVerb) text += ` of ${rule.completedVerb}`
  if (rule.onlyImplantId) text += ` · ${implantLabel(implants, rule.onlyImplantId)}`
  return text
}

export function AutomationView({
  engagementId,
  onlineTick,
}: {
  engagementId: string
  onlineTick?: number
}) {
  const [rules, setRules] = useState<AutomationRule[]>([])
  const [implants, setImplants] = useState<Implant[]>([])
  const [capabilityGroups, setCapabilityGroups] = useState<CapabilityGroup[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  // Create-form draft. The engagement switch resets it whole: a target picked
  // in one engagement is a foreign implant in the next and can only 422.
  const [name, setName] = useState('')
  const [triggerKind, setTriggerKind] = useState<'interval' | 'event'>('interval')
  const [intervalSeconds, setIntervalSeconds] = useState('1800')
  const [eventKind, setEventKind] = useState('SessionOpened')
  const [onlyImplantId, setOnlyImplantId] = useState('')
  const [completedVerb, setCompletedVerb] = useState('')
  const [targetImplantId, setTargetImplantId] = useState('')
  const [verb, setVerb] = useState('shell.exec')
  const [arguments_, setArguments_] = useState('')
  const [cooldownSeconds, setCooldownSeconds] = useState('')
  const [maxFirings, setMaxFirings] = useState('')

  const [armed, arm] = useArmedDelete()

  const now = useNow(10_000)

  const refresh = useCallback(async () => {
    setBusy(true)
    try {
      setRules(await listAutomationRules(engagementId))
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }, [engagementId])

  // The rule rows ride the live tick: every firing is a TaskIssued event on
  // the engagement's stream, so fire counts and next-fire stamps move without
  // a manual refresh.
  useEffect(() => {
    void refresh()
  }, [refresh, onlineTick])

  // Pickers load once per engagement: the implant list for both the target and
  // the event filter, the capability catalog for the verb select.
  useEffect(() => {
    let cancelled = false
    void (async () => {
      try {
        const [allImplants, groups] = await Promise.all([
          listImplants(engagementId),
          loadCapabilityGroups(),
        ])
        if (cancelled) return
        setImplants(allImplants)
        setCapabilityGroups(groups)
        const firstLive = allImplants.find((i) => !i.retiredAt)
        if (firstLive) setTargetImplantId(firstLive.implantId)
      } catch (e) {
        if (!cancelled) setError(String(e))
      }
    })()
    return () => {
      cancelled = true
    }
  }, [engagementId])

  // Engagement switch: drop every cross-engagement choice.
  useEffect(() => {
    setName('')
    setOnlyImplantId('')
    setCompletedVerb('')
    setTargetImplantId('')
    setArguments_('')
    setCooldownSeconds('')
    setMaxFirings('')
  }, [engagementId])

  const attributesByVerb = useMemo(() => verbAttributes(capabilityGroups), [capabilityGroups])

  const onCreate = async (e: React.FormEvent) => {
    e.preventDefault()
    const seconds = Number.parseInt(intervalSeconds, 10)
    if (triggerKind === 'interval' && (!Number.isFinite(seconds) || seconds <= 0)) {
      setError('The interval needs seconds greater than zero.')
      return
    }
    if (!targetImplantId) {
      setError('Pick the implant every firing tasks.')
      return
    }
    try {
      await createAutomationRule(engagementId, {
        name,
        trigger:
          triggerKind === 'interval'
            ? { kind: 'interval', intervalSeconds: seconds }
            : { kind: 'event', eventKind },
        onlyImplantId: onlyImplantId || null,
        completedVerb: completedVerb.trim() || null,
        targetImplantId,
        verb,
        arguments: arguments_,
        cooldownSeconds: cooldownSeconds.trim() ? Number.parseInt(cooldownSeconds, 10) : null,
        maxFirings: maxFirings.trim() ? Number.parseInt(maxFirings, 10) : null,
      })
      setName('')
      setArguments_('')
      setError(null)
      await refresh()
    } catch (err) {
      setError(String(err))
    }
  }

  const onToggle = async (rule: AutomationRule) => {
    try {
      if (rule.enabled) await disableAutomationRule(engagementId, rule.id)
      else await enableAutomationRule(engagementId, rule.id)
      setError(null)
      await refresh()
    } catch (err) {
      setError(String(err))
    }
  }

  const onDelete = async (rule: AutomationRule) => {
    try {
      await deleteAutomationRule(engagementId, rule.id)
      setError(null)
      await refresh()
    } catch (err) {
      setError(String(err))
    }
  }

  return (
    <section className="view">
      <h2>Automation</h2>
      <p className="muted">
        Rules that fire while no operator watches -- every firing issues through the ordinary
        tasking gates, attributes to the automation operator, and lands in the audit trail.
        Disabling is the cancel; a disabled rule fires nothing until re-armed from now.
      </p>

      {error && <p className="error">{error}</p>}

      <div className="card">
        <h3>New rule</h3>
        <form className="listener-form" onSubmit={onCreate}>
          <div className="listener-row">
            <label>
              Name
              <input
                placeholder="overnight-watch"
                value={name}
                onChange={(e) => setName(e.target.value)}
                required
                title="The operator-facing name this rule reads under in the list and the audit trail."
              />
            </label>
            <label>
              Trigger
              <select
                value={triggerKind}
                onChange={(e) => setTriggerKind(e.target.value as 'interval' | 'event')}
                title="A fixed cadence, or one of the triggerable live events on this engagement."
              >
                <option value="interval">Every interval</option>
                <option value="event">On event</option>
              </select>
            </label>
            {triggerKind === 'interval' ? (
              <label>
                Interval (seconds)
                <input
                  type="number"
                  min={5}
                  step={1}
                  placeholder="1800"
                  value={intervalSeconds}
                  onChange={(e) => setIntervalSeconds(e.target.value)}
                  title="The cadence the schedule runs on. Shortest 5s (lab and tests); practical schedules are minutes. The schedule is durable -- it survives a teamserver restart, and fires missed while the server was down are skipped."
                />
              </label>
            ) : (
              <>
                <label>
                  Event
                  <select
                    value={eventKind}
                    onChange={(e) => setEventKind(e.target.value)}
                    title="The engagement's operational beats. Operator presence and channel output are not triggerable."
                  >
                    {EVENT_KINDS.map((k) => (
                      <option key={k.value} value={k.value}>
                        {k.label}
                      </option>
                    ))}
                  </select>
                </label>
                <label>
                  Only this implant
                  <select
                    value={onlyImplantId}
                    onChange={(e) => setOnlyImplantId(e.target.value)}
                    title="Narrow the trigger to one implant's events. Empty matches any implant."
                  >
                    <option value="">Any implant</option>
                    {implants.map((i) => (
                      <option key={i.implantId} value={i.implantId}>
                        {implantLabel(implants, i.implantId)}
                      </option>
                    ))}
                  </select>
                </label>
                {eventKind === 'TaskCompleted' && (
                  <label>
                    Completed verb
                    <input
                      placeholder="recon.portscan (any if empty)"
                      value={completedVerb}
                      onChange={(e) => setCompletedVerb(e.target.value)}
                      title="Fire only when this verb's task completed. Applies to task-completed triggers only."
                    />
                  </label>
                )}
              </>
            )}
          </div>
          <div className="listener-row">
            <label>
              Target implant
              <select
                value={targetImplantId}
                onChange={(e) => setTargetImplantId(e.target.value)}
                title="The implant every firing tasks."
              >
                {implants
                  .filter((i) => !i.retiredAt)
                  .map((i) => (
                    <option key={i.implantId} value={i.implantId}>
                      {implantLabel(implants, i.implantId)}
                    </option>
                  ))}
              </select>
            </label>
            <label>
              Verb
              <select
                value={verb}
                onChange={(e) => setVerb(e.target.value)}
                title="The action's verb. Channel verbs and sensitive verbs never fire unattended; the server refuses them at creation."
              >
                {capabilityGroups.map((group) => (
                  <optgroup key={group.category} label={group.label}>
                    {group.descriptors.map((d) => (
                      <option
                        key={d.verb}
                        value={d.verb}
                        disabled={CHANNEL_VERBS.includes(d.verb)}
                      >
                        {d.verb}
                      </option>
                    ))}
                  </optgroup>
                ))}
              </select>
            </label>
            <label>
              Arguments
              <input
                placeholder="uptime"
                value={arguments_}
                onChange={(e) => setArguments_(e.target.value)}
                title="Issued verbatim on every firing."
              />
            </label>
          </div>
          <div className="listener-row">
            <label>
              Cooldown (seconds, optional)
              <input
                type="number"
                min={5}
                step={1}
                placeholder="default: the interval / 60s"
                value={cooldownSeconds}
                onChange={(e) => setCooldownSeconds(e.target.value)}
                title="The minimum spacing between firings of this rule -- a burst of matching events yields one firing."
              />
            </label>
            <label>
              Firing cap (optional)
              <input
                type="number"
                min={1}
                max={1000}
                step={1}
                placeholder="default: 100"
                value={maxFirings}
                onChange={(e) => setMaxFirings(e.target.value)}
                title="The rule disables itself after this many firings and says so in the audit trail."
              />
            </label>
            <div className="listener-form-actions">
              {attributesByVerb.has(verb) && <OpsecBadges attributes={attributesByVerb.get(verb)!} />}
              <button className="primary" type="submit" disabled={busy}>
                Create rule
              </button>
            </div>
          </div>
        </form>
      </div>

      <div className="card">
        <div className="table-toolbar">
          <h3>Rules</h3>
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
                <th>Rule</th>
                <th>Trigger</th>
                <th>Action</th>
                <th>Fires</th>
                <th>Next / last</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {rules.length === 0 && (
                <tr>
                  <td colSpan={7}>
                    <div className="empty">
                      <Icon name="clock" />
                      No automation rules for this engagement yet
                    </div>
                  </td>
                </tr>
              )}
              {rules.map((rule) => (
                <tr key={rule.id} className={rule.enabled ? undefined : 'row-dim'}>
                  <td>
                    <StatusBadge status={rule.enabled ? 'enabled' : 'disabled'} />
                  </td>
                  <td title={`${rule.name} · created ${new Date(rule.createdAt).toLocaleString()}`}>
                    {rule.name}
                  </td>
                  <td>{describeTrigger(rule, implants)}</td>
                  <td title={`on ${implantLabel(implants, rule.targetImplantId)} · "${rule.arguments}"`}>
                    <code>{rule.verb}</code>
                  </td>
                  <td title={`Firing cap ${rule.maxFirings}; the rule disables itself when spent.`}>
                    {rule.fireCount}/{rule.maxFirings}
                    {rule.consecutiveRefusals > 0 && (
                      <span className="status failed" title="Consecutive issuance refusals; three in a row disable the rule.">
                        {' '}
                        {rule.consecutiveRefusals} refused
                      </span>
                    )}
                  </td>
                  <td>
                    {rule.enabled && rule.nextFireAt ? (
                      <span title={`Next firing ${new Date(rule.nextFireAt).toLocaleString()}`}>
                        {until(rule.nextFireAt, now)}
                      </span>
                    ) : rule.lastFiredAt ? (
                      <span title={`Last fired ${new Date(rule.lastFiredAt).toLocaleString()}`}>
                        {ago(rule.lastFiredAt, now)}
                      </span>
                    ) : (
                      <span className="muted">—</span>
                    )}
                  </td>
                  <td onClick={(e) => e.stopPropagation()}>
                    <div className="row-actions">
                      <button className="ghost sm" onClick={() => void onToggle(rule)} disabled={busy}>
                        {rule.enabled ? 'Disable' : 'Enable'}
                      </button>
                      <button
                        className={`sm danger${armed === rule.id ? ' armed' : ''}`}
                        onClick={() => arm(rule.id, () => void onDelete(rule))}
                      >
                        {armed === rule.id ? 'Confirm delete' : 'Delete'}
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
