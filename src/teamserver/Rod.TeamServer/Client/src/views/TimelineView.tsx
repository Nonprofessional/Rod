import { useCallback, useEffect, useMemo, useState } from 'react'
import { type TimelineEntry, type TimelineReport, getTimeline, getTimelineMarkdown } from '../api'
import { Icon } from '../components/Icons'

// The engagement story: a reproducible, content-hashed projection of the
// audit trail rendered as a narrative timeline -- day by day, who acted, on
// what, with what outcome. Where the Audit tab is the raw paged ledger for
// forensic reading, this view is for a human catching up on what happened:
// entries group by day along a spine, each entry names its actor and subject
// in prose form, and filters (kind, actor, free text) narrow the story without
// paging. The Markdown export stays for cut-paste into a report.

const ALL = '(all)'

// One icon per entry family, so the spine reads at a glance: tasking arcs,
// lifecycle facts, evidence movement, scope changes.
function iconForKind(kind: string) {
  if (kind.startsWith('Task')) return 'terminal'
  if (kind.startsWith('Engagement')) return 'globe'
  if (kind.startsWith('Implant') || kind === 'SessionOpened' || kind === 'SessionClosed')
    return 'cpu'
  if (kind.startsWith('Artifact') || kind.startsWith('Exfil') || kind.startsWith('Evidence'))
    return 'archive'
  if (kind.startsWith('Roe') || kind.startsWith('Channel') || kind.startsWith('Relay'))
    return 'list'
  if (kind.startsWith('Payload')) return 'package'
  if (kind.startsWith('DeployToken')) return 'inbox'
  return 'clock'
}

function dayOf(iso: string): string {
  return new Date(iso).toLocaleDateString(undefined, {
    weekday: 'short',
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  })
}

function timeOf(iso: string): string {
  return new Date(iso).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' })
}

export function TimelineView({ engagementId }: { engagementId: string }) {
  const [report, setReport] = useState<TimelineReport | null>(null)
  const [markdown, setMarkdown] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [view, setView] = useState<'timeline' | 'markdown'>('timeline')
  const [kind, setKind] = useState(ALL)
  const [actor, setActor] = useState(ALL)
  // The text filter commits on Enter or the Search button; the dropdowns stay
  // live.
  const [queryDraft, setQueryDraft] = useState('')
  const [query, setQuery] = useState('')

  const refresh = useCallback(async () => {
    setBusy(true)
    try {
      setReport(await getTimeline(engagementId))
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }, [engagementId])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const showMarkdown = async () => {
    try {
      setMarkdown(await getTimelineMarkdown(engagementId))
      setView('markdown')
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }

  const kinds = useMemo(
    () => [ALL, ...[...new Set((report?.entries ?? []).map((e) => e.kind))].sort()],
    [report],
  )
  const actors = useMemo(
    () => [
      ALL,
      ...[...new Set((report?.entries ?? []).map((e) => e.operator?.handle ?? 'system'))].sort(),
    ],
    [report],
  )

  const entries = useMemo(() => {
    const needle = query.trim().toLowerCase()
    return (report?.entries ?? [])
      .filter((e) => kind === ALL || e.kind === kind)
      .filter((e) => actor === ALL || (e.operator?.handle ?? 'system') === actor)
      .filter(
        (e) =>
          needle === '' ||
          e.verb.toLowerCase().includes(needle) ||
          e.payload.toLowerCase().includes(needle) ||
          (e.output ?? '').toLowerCase().includes(needle) ||
          e.outcome.toLowerCase().includes(needle),
      )
      .slice()
      .reverse() // newest day first; within a day, oldest first
  }, [report, kind, actor, query])

  // Group the filtered entries by calendar day, preserving order.
  const days = useMemo(() => {
    const groups: { day: string; entries: TimelineEntry[] }[] = []
    for (const e of entries) {
      const day = dayOf(e.at)
      const last = groups[groups.length - 1]
      if (last && last.day === day) last.entries.push(e)
      else groups.push({ day, entries: [e] })
    }
    return groups
  }, [entries])

  return (
    <div className="card">
      <h3>Timeline</h3>
      <p className="muted" title="The content-hashed projection of the audit trail, read as a narrative, day by day. The Audit tab is the same facts as a raw ledger.">
        The engagement's story, day by day.
      </p>
      <div className="inline-form">
        <div className="segmented" role="group" aria-label="Timeline format">
          <button className={view === 'timeline' ? 'active' : ''} onClick={() => setView('timeline')}>
            Story
          </button>
          <button className={view === 'markdown' ? 'active' : ''} onClick={() => void showMarkdown()}>
            Markdown
          </button>
        </div>
        {view === 'timeline' && (
          <>
            <select
              value={kind}
              onChange={(e) => setKind(e.target.value)}
              title="Event kind filter"
              aria-label="Event kind filter"
            >
              {kinds.map((k) => (
                <option key={k} value={k}>
                  {k}
                </option>
              ))}
            </select>
            <select
              value={actor}
              onChange={(e) => setActor(e.target.value)}
              title="Actor filter"
              aria-label="Actor filter"
            >
              {actors.map((a) => (
                <option key={a} value={a}>
                  {a}
                </option>
              ))}
            </select>
            <input
              className="filter-text"
              placeholder="Filter by verb, payload, outcome…"
              value={queryDraft}
              onChange={(e) => setQueryDraft(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') setQuery(queryDraft)
              }}
              title="Free text across verb, payload, and outcome. Enter or the Search button applies; the dropdown filters are live."
            />
            <button
              className="ghost"
              onClick={() => setQuery(queryDraft)}
              title="Apply the text filter (Enter works too)"
            >
              Search
            </button>
          </>
        )}
        <button className="ghost" onClick={() => void refresh()} disabled={busy}>
          <Icon name="refresh" />
          Refresh
        </button>
        {report && (
          <span className="muted">
            hash <code>{report.contentHash.slice(0, 12)}</code>
          </span>
        )}
      </div>
      {error && <p className="error">{error}</p>}

      {view === 'markdown' ? (
        <pre className="output long">{markdown ?? 'loading\u2026'}</pre>
      ) : entries.length === 0 ? (
        <div className="empty">
          <Icon name="clock" />
          No timeline entries.
        </div>
      ) : (
        <div className="timeline">
          {days.map((group) => (
            <section key={group.day} className="timeline-day">
              <h4 className="timeline-day-head">{group.day}</h4>
              <ol className="timeline-spine">
                {group.entries.map((e) => (
                  <li key={e.eventId} className="timeline-entry">
                    <span className="timeline-icon">
                      <Icon name={iconForKind(e.kind)} />
                    </span>
                    <div className="timeline-body">
                      <div className="timeline-line">
                        <span className="timeline-time">{timeOf(e.at)}</span>
                        <span className="status">{e.kind}</span>
                        <code>{e.verb}</code>
                        {e.operator && <span className="chip">{e.operator.handle}</span>}
                        {e.implant && (
                          <span className="chip" title={e.implant.implantId}>
                            {e.implant.class} {e.implant.implantId.slice(0, 8)}
                          </span>
                        )}
                      </div>
                      {e.payload && <p className="timeline-payload">{e.payload}</p>}
                      {e.task && e.task.outcome && (
                        <p className="muted">outcome: {e.task.outcome}</p>
                      )}
                    </div>
                  </li>
                ))}
              </ol>
            </section>
          ))}
        </div>
      )}
    </div>
  )
}
