import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  type Engagement,
  type EngagementTask,
  type Implant,
  listEngagements,
  listEngagementTasks,
  listImplants,
} from '../api'
import { HelpHint } from '../components/HelpHint'
import { type IconName, Icon } from '../components/Icons'
import { StatusBadge } from '../components/StatusBadge'
import { osFamilyOf, type OsFamily } from '../osKind'
import { ago, useNow } from '../when'

// The home dashboard (`#/`; the bare hash and the brand mark both land here):
// one orienting page over every engagement the operator can reach, composed
// client-side from the roster plus the same per-engagement reads the
// engagement views make. No dedicated cross-engagement aggregate endpoint
// exists -- and the composition keeps the dashboard inside the
// engagement-scoped surface the server already enforces. Retired records are
// sealed: they count toward the roster card only, and their fleet/tasking
// reads are skipped.

// The activity window the charts and cards answer over. Task history is paged
// (200 is the listing's hard page cap); the window reads the newest page per
// engagement, so an engagement with more history than the cap undercounts its
// oldest days -- the dashboard is an operating overview, not a ledger.
const TASK_WINDOW_DAYS = 14
const TASKS_PER_ENGAGEMENT = 200

// Task statuses collapse into the classes the activity chart stacks. The
// in-flight pair (queued, dispatched) reads as one "open" series; anything
// unrecognized joins it rather than inventing a legend entry.
type TaskClass = 'completed' | 'open' | 'cancelled' | 'failed'

function taskClass(status: string): TaskClass {
  switch (status.toLowerCase()) {
    case 'completed':
      return 'completed'
    case 'failed':
      return 'failed'
    case 'cancelled':
    case 'canceled':
      return 'cancelled'
    default:
      return 'open'
  }
}

const TASK_CLASSES: readonly TaskClass[] = ['completed', 'open', 'cancelled', 'failed']
const TASK_CLASS_LABEL: Record<TaskClass, string> = {
  completed: 'completed',
  open: 'open (queued or dispatched)',
  cancelled: 'cancelled',
  failed: 'failed',
}

function engagementStatus(e: Engagement): { label: string; tone: string } {
  if (e.retiredAt) return { label: 'retired', tone: 'status retired' }
  if (e.frozenAt) return { label: 'frozen', tone: 'status' }
  return { label: 'open', tone: 'status completed' }
}

// Local-day key: the operator reads the chart in their own timezone. Keys are
// only compared for equality (bucket lookup), never ordering.
function dayKey(date: Date): string {
  return `${date.getFullYear()}-${date.getMonth()}-${date.getDate()}`
}

interface DayBucket {
  key: string
  label: string
  counts: Record<TaskClass, number>
  total: number
}

// One engagements-list row's posture, precomputed so the render stays a map.
interface FleetRow {
  engagement: Engagement
  status: { label: string; tone: string }
  active: number
  online: number
}

interface HomeData {
  engagements: Engagement[]
  // Per-engagement reads for non-retired engagements only; absent when that
  // read failed, so the row falls back to the roster's summary counts.
  implants: Partial<Record<string, Implant[]>>
  tasks: Partial<Record<string, EngagementTask[]>>
}

function StatCard({
  icon,
  label,
  value,
  sub,
  title,
}: {
  icon: IconName
  label: string
  value: string
  sub?: string
  title?: string
}) {
  return (
    <div className="stat-card" title={title}>
      <div className="stat-head">
        <Icon name={icon} />
        <span className="stat-label">{label}</span>
      </div>
      <div className="stat-value">{value}</div>
      {sub && <div className="stat-sub">{sub}</div>}
    </div>
  )
}

// Stacked daily bars over the task window, in pure CSS: one flex column per
// local day, segments proportioned against the busiest day. The native title
// on each column carries the exact breakdown.
function ActivityChart({ buckets }: { buckets: DayBucket[] }) {
  const max = Math.max(1, ...buckets.map((b) => b.total))
  return (
    <div className="activity-chart" role="img" aria-label="Tasks per day over the window">
      {buckets.map((bucket, i) => (
        <div
          key={bucket.key}
          className="activity-col"
          title={`${bucket.label} -- ${bucket.total} task${bucket.total === 1 ? '' : 's'}${TASK_CLASSES.filter(
            (cls) => bucket.counts[cls] > 0,
          )
            .map((cls) => `, ${bucket.counts[cls]} ${TASK_CLASS_LABEL[cls]}`)
            .join('')}`}
        >
          <div className="activity-stack">
            {TASK_CLASSES.map((cls) =>
              bucket.counts[cls] > 0 ? (
                <div
                  key={cls}
                  className={`activity-seg ${cls}`}
                  style={{ height: `${(bucket.counts[cls] / max) * 100}%` }}
                />
              ) : null,
            )}
          </div>
          {(i % 2 === 0 || i === buckets.length - 1) && (
            <span className="activity-label">{bucket.label}</span>
          )}
        </div>
      ))}
    </div>
  )
}

// A donut on 42x42 viewBox geometry where the track circle's circumference is
// ~100, so segment lengths are percentages directly.
function Donut({
  segments,
  center,
}: {
  segments: { label: string; value: number; color: string }[]
  center: string
}) {
  const total = segments.reduce((n, s) => n + s.value, 0)
  let cumulative = 0
  return (
    <div className="donut-wrap">
      <svg viewBox="0 0 42 42" className="donut" role="img" aria-label="Implants by OS family">
        <circle className="donut-track" cx="21" cy="21" r="15.9155" />
        {total > 0 &&
          segments
            .filter((s) => s.value > 0)
            .map((s) => {
              const pct = (s.value / total) * 100
              const offset = 25 - cumulative
              cumulative += pct
              return (
                <circle
                  key={s.label}
                  className="donut-seg"
                  cx="21"
                  cy="21"
                  r="15.9155"
                  style={{ stroke: s.color }}
                  strokeDasharray={`${pct} ${100 - pct}`}
                  strokeDashoffset={offset}
                />
              )
            })}
        <text className="donut-center" x="21" y="21" dominantBaseline="central" textAnchor="middle">
          {center}
        </text>
      </svg>
      <div className="donut-legend">
        {segments.map((s) => (
          <div key={s.label} className="donut-legend-row">
            <span className="donut-swatch" style={{ background: s.color }} />
            <span className="donut-legend-label">{s.label}</span>
            <span className="donut-legend-value">{s.value}</span>
          </div>
        ))}
      </div>
    </div>
  )
}

// A labeled share bar (the fleet and verb cards): a track plus filled
// segments, widths in percent of the row's own scale.
function ShareBar({ parts, scale }: { parts: { value: number; className: string }[]; scale: number }) {
  const total = parts.reduce((n, p) => n + p.value, 0)
  if (total === 0) return <div className="share-bar empty" title="No active implants" />
  return (
    <div className="share-bar">
      {parts.map((part, i) =>
        part.value > 0 ? (
          <div
            key={i}
            className={`share-fill ${part.className}`}
            style={{ width: `${(part.value / Math.max(scale, 1)) * 100}%` }}
          />
        ) : null,
      )}
    </div>
  )
}

export function HomeView() {
  const [data, setData] = useState<HomeData | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [loading, setLoading] = useState(true)
  const now = useNow(30_000)

  const load = useCallback(async () => {
    setBusy(true)
    try {
      const engagements = await listEngagements()
      const live = engagements.filter((e) => !e.retiredAt)
      // A failed per-engagement read leaves that engagement's entry absent,
      // so its panels degrade to the roster's own summary counts rather than
      // failing the page (or reading as an empty fleet).
      const [implantLists, taskLists] = await Promise.all([
        Promise.all(live.map((e) => listImplants(e.engagementId).then((v) => v, () => null))),
        Promise.all(
          live.map((e) =>
            listEngagementTasks(e.engagementId, undefined, TASKS_PER_ENGAGEMENT)
              .then((page) => page.items)
              .catch(() => null),
          ),
        ),
      ])
      const implants: HomeData['implants'] = {}
      const tasks: HomeData['tasks'] = {}
      live.forEach((e, i) => {
        if (implantLists[i]) implants[e.engagementId] = implantLists[i]
        if (taskLists[i]) tasks[e.engagementId] = taskLists[i]
      })
      setData({ engagements, implants, tasks })
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void load()
    // Roster changes (create, close-out, membership) re-shape the dashboard's
    // whole basis, so they re-read it on the spot.
    window.addEventListener('rod-engagements-changed', load)
    return () => window.removeEventListener('rod-engagements-changed', load)
  }, [load])

  const derived = useMemo(() => {
    if (!data) return null
    const { engagements, implants, tasks } = data
    const live = engagements.filter((e) => !e.retiredAt)

    // Fleet posture over non-retired engagements: fetched rows where the read
    // landed, the roster summary where it did not.
    let fleetActive = 0
    let fleetOnline = 0
    let lastSeen: string | null = null
    const osCounts = new Map<OsFamily, number>([
      ['windows', 0],
      ['linux', 0],
      ['apple', 0],
      ['other', 0],
    ])
    const fleetRows: FleetRow[] = engagements.map((e) => {
      const rows = implants[e.engagementId]
      const active = rows
        ? rows.filter((i) => !i.retiredAt).length
        : (e.summary?.implantCount ?? 0)
      const online = rows
        ? rows.filter((i) => i.isOnline && !i.retiredAt).length
        : (e.summary?.onlineCount ?? 0)
      if (!e.retiredAt) {
        fleetActive += active
        fleetOnline += online
        for (const i of rows ?? []) {
          if (i.retiredAt) continue
          osCounts.set(osFamilyOf(i.os), (osCounts.get(osFamilyOf(i.os)) ?? 0) + 1)
          if (i.lastSeenAt && (!lastSeen || i.lastSeenAt > lastSeen)) lastSeen = i.lastSeenAt
        }
      }
      return { engagement: e, status: engagementStatus(e), active, online }
    })
    const fleetScale = Math.max(1, ...fleetRows.map((r) => r.active))

    // Daily buckets across the window, oldest first; the flat aggregates
    // (verb mix, recent feed) draw from the same task pages.
    const buckets: DayBucket[] = []
    const bucketByKey = new Map<string, DayBucket>()
    const today = new Date()
    for (let back = TASK_WINDOW_DAYS - 1; back >= 0; back--) {
      const d = new Date(today.getFullYear(), today.getMonth(), today.getDate() - back)
      const bucket: DayBucket = {
        key: dayKey(d),
        label: d.toLocaleDateString(undefined, { month: 'numeric', day: 'numeric' }),
        counts: { completed: 0, open: 0, cancelled: 0, failed: 0 },
        total: 0,
      }
      buckets.push(bucket)
      bucketByKey.set(bucket.key, bucket)
    }
    const windowStart = new Date(today.getFullYear(), today.getMonth(), today.getDate() - (TASK_WINDOW_DAYS - 1)).getTime()

    const verbCounts = new Map<string, number>()
    const recent: { task: EngagementTask; engagement: Engagement }[] = []
    let windowTotal = 0
    let windowFailed = 0
    for (const e of live) {
      for (const t of tasks[e.engagementId] ?? []) {
        recent.push({ task: t, engagement: e })
        const created = new Date(t.createdAt).getTime()
        if (created >= windowStart) {
          windowTotal += 1
          const cls = taskClass(t.status)
          if (cls === 'failed') windowFailed += 1
          const bucket = bucketByKey.get(dayKey(new Date(t.createdAt)))
          if (bucket) {
            bucket.counts[cls]++
            bucket.total++
          }
          verbCounts.set(t.verb, (verbCounts.get(t.verb) ?? 0) + 1)
        }
      }
    }
    recent.sort((a, b) => b.task.createdAt.localeCompare(a.task.createdAt))
    const verbs = [...verbCounts.entries()].sort((a, b) => b[1] - a[1]).slice(0, 6)

    return {
      open: live.filter((e) => !e.frozenAt).length,
      frozen: live.filter((e) => e.frozenAt).length,
      retired: engagements.length - live.length,
      fleetActive,
      fleetOnline,
      fleetScale,
      lastSeen,
      osCounts,
      fleetRows,
      buckets,
      windowTotal,
      windowFailed,
      verbs,
      recent: recent.slice(0, 12),
    }
  }, [data])

  if (loading) {
    return (
      <section className="view">
        <h2>Home</h2>
        <div className="empty">
          <span className="spinner" />
          Loading the dashboard…
        </div>
      </section>
    )
  }

  const engagements = data?.engagements ?? []

  return (
    <section className="view">
      <h2>
        Home
        <HelpHint>
          Posture and activity across every engagement you can reach -- fleet health, tasking
          volume, and the newest operator work, over open and frozen engagements. Retired records
          count toward the roster card only; the engagements list stays the full account.
        </HelpHint>
        <button className="sm ghost" onClick={() => void load()} disabled={busy} title="Re-read the dashboard">
          <Icon name="refresh" />
        </button>
      </h2>
      {error && <p className="error">{error}</p>}

      {engagements.length === 0 ? (
        <div className="card empty">
          <Icon name="globe" />
          No engagements yet -- the dashboard lights up once you create one.
          <a href="#/engagements">Open the engagements list</a>
        </div>
      ) : (
        derived && (
          <>
            <div className="stat-grid">
              <StatCard
                icon="globe"
                label="Engagements"
                value={String(derived.open + derived.frozen)}
                sub={`${derived.open} open · ${derived.frozen} frozen · ${derived.retired} retired`}
                title="Non-retired engagements you can reach, split by close-out state"
              />
              <StatCard
                icon="cpu"
                label="Fleet"
                value={String(derived.fleetOnline)}
                sub={`of ${derived.fleetActive} active implants online`}
                title="Live sessions over active (non-retired) implants across non-retired engagements"
              />
              <StatCard
                icon="inbox"
                label={`Tasking (${TASK_WINDOW_DAYS}d)`}
                value={String(derived.windowTotal)}
                sub={
                  derived.windowFailed > 0
                    ? `${derived.windowFailed} failed`
                    : 'no failures in the window'
                }
                title="Tasks issued inside the window, read from each engagement's newest 200"
              />
              <StatCard
                icon="clock"
                label="Last heard"
                value={derived.lastSeen ? ago(derived.lastSeen, now) : '--'}
                sub="newest implant check-in"
                title={derived.lastSeen ? new Date(derived.lastSeen).toLocaleString() : undefined}
              />
            </div>

            <div className="dash-grid">
              <div className="card dash-wide">
                <h3>Task activity -- last {TASK_WINDOW_DAYS} days</h3>
                {derived.windowTotal > 0 ? (
                  <ActivityChart buckets={derived.buckets} />
                ) : (
                  <p className="muted">No tasking inside the window.</p>
                )}
                <div className="legend-row">
                  {TASK_CLASSES.map((cls) => (
                    <span key={cls} className="legend-item">
                      <span className={`swatch ${cls}`} />
                      {cls === 'open' ? 'open (queued/dispatched)' : cls}
                    </span>
                  ))}
                </div>
              </div>

              <div className="card">
                <h3>Fleet by OS</h3>
                {derived.fleetActive > 0 ? (
                  <Donut
                    center={String(derived.fleetActive)}
                    segments={[
                      { label: 'Windows', value: derived.osCounts.get('windows') ?? 0, color: 'var(--info)' },
                      { label: 'Linux', value: derived.osCounts.get('linux') ?? 0, color: 'var(--success)' },
                      { label: 'macOS', value: derived.osCounts.get('apple') ?? 0, color: 'var(--warn)' },
                      { label: 'Other', value: derived.osCounts.get('other') ?? 0, color: 'var(--muted)' },
                    ]}
                  />
                ) : (
                  <p className="muted">No active implants.</p>
                )}
              </div>

              <div className="card">
                <h3>Engagements</h3>
                <div className="fleet-list">
                  {derived.fleetRows.map((row) => (
                    <a
                      key={row.engagement.engagementId}
                      className="fleet-row"
                      href={`#/engagements/${row.engagement.engagementId}`}
                    >
                      <span className="fleet-name" title={`${row.engagement.name} (${row.engagement.engagementId})`}>
                        {row.engagement.name}
                      </span>
                      <span className={row.status.tone}>{row.status.label}</span>
                      <ShareBar
                        scale={derived.fleetScale}
                        parts={
                          row.engagement.retiredAt
                            ? [{ value: row.active, className: 'retired' }]
                            : [
                                { value: row.online, className: 'online' },
                                { value: row.active - row.online, className: 'offline' },
                              ]
                        }
                      />
                      <code>
                        {row.online}/{row.active}
                      </code>
                    </a>
                  ))}
                </div>
              </div>

              <div className="card">
                <h3>Top task verbs</h3>
                {derived.verbs.length > 0 ? (
                  <div className="verb-list">
                    {derived.verbs.map(([verb, count]) => (
                      <div key={verb} className="verb-row">
                        <code>{verb}</code>
                        <ShareBar scale={derived.verbs[0][1]} parts={[{ value: count, className: 'verb' }]} />
                        <span className="muted">{count}</span>
                      </div>
                    ))}
                  </div>
                ) : (
                  <p className="muted">No tasking inside the window.</p>
                )}
              </div>
            </div>

            <div className="card">
              <h3>Recent tasking</h3>
              {derived.recent.length > 0 ? (
                <div className="recent-list">
                  {derived.recent.map(({ task, engagement }) => (
                    <a
                      key={task.taskId}
                      className="recent-row"
                      href={`#/engagements/${engagement.engagementId}/tasking`}
                      title={`${engagement.name} -- ${task.verb} ${task.arguments}`}
                    >
                      <span className="recent-time" title={new Date(task.createdAt).toLocaleString()}>
                        {ago(task.createdAt, now)}
                      </span>
                      <StatusBadge status={task.status} />
                      <code>{task.verb}</code>
                      <span className="recent-args">{task.arguments || '\u00a0'}</span>
                      <span className="recent-engagement" title={engagement.engagementId}>
                        {engagement.name}
                      </span>
                      <span className="muted">{task.issuedBy}</span>
                    </a>
                  ))}
                </div>
              ) : (
                <p className="muted">Nothing issued yet.</p>
              )}
            </div>
          </>
        )
      )}
    </section>
  )
}
