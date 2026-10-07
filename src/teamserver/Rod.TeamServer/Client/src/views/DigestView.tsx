import { useCallback, useEffect, useState } from 'react'
import {
  type DigestEntry,
  type DigestWindow,
  type HandoffDigest,
  getHandoffDigest,
  getHandoffDigestMarkdown,
} from '../api'
import { Icon } from '../components/Icons'

// The handoff digest (architecture.md Sec 11.1): the resuming operator's read.
// One ordered account of the watch's beats -- sessions opened and closed,
// implants enrolled and retired, tasking with its outcomes, ROE refusals,
// implant notes, shell sessions -- over a window the operator picks (the last
// watch is the default). The audit ledger stays the raw, paged reading
// surface; this is the sized, curated answer to "what happened while I was
// away", and the markdown button renders it as the note for the next watch.

const QUICK_WINDOWS: readonly { label: string; hours: number }[] = [
  { label: 'Last 12h', hours: 12 },
  { label: 'Last 24h', hours: 24 },
  { label: 'Last 48h', hours: 48 },
]

function when(iso: string): string {
  return new Date(iso).toLocaleString()
}

function entryActor(e: DigestEntry): string {
  return e.operator?.handle ?? 'system'
}

function entrySubject(e: DigestEntry): string {
  return e.implant?.class ?? '\u2014'
}

// The non-zero counts, in watch reading order, so the row sizes the watch
// before the table tells it.
function countChips(d: HandoffDigest): { n: number; label: string }[] {
  const s = d.summary
  return [
    { n: s.sessionsOpened, label: 'sessions opened' },
    { n: s.sessionsClosed, label: 'sessions closed' },
    { n: s.implantsEnrolled, label: 'implants enrolled' },
    { n: s.implantsRetired, label: 'implants retired' },
    { n: s.tasksIssued, label: 'tasks issued' },
    { n: s.tasksCompleted, label: 'completed' },
    { n: s.tasksCancelled, label: 'cancelled' },
    { n: s.roeRefusals, label: 'ROE refusals' },
    { n: s.notesAdded, label: 'notes added' },
    { n: s.shellSessionsOpened, label: 'shells opened' },
    { n: s.shellSessionsEnded, label: 'shells ended' },
  ].filter((c) => c.n > 0)
}

export function DigestView({
  engagementId,
  onlineTick,
}: {
  engagementId: string
  onlineTick: number
}) {
  // undefined from/to asks the server for its default: to = now, from =
  // twelve hours before it. Picking anchors the window where it was picked;
  // refresh re-reads the same bounds, so late-landing facts inside the window
  // appear while the window itself never slides.
  const [bounds, setBounds] = useState<DigestWindow>({})
  const [digest, setDigest] = useState<HandoffDigest | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [loading, setLoading] = useState(true)
  const [copying, setCopying] = useState(false)
  // Custom-bound drafts; they commit on Apply, not per keystroke.
  const [fromDraft, setFromDraft] = useState('')
  const [toDraft, setToDraft] = useState('')

  const refresh = useCallback(async () => {
    setBusy(true)
    try {
      setDigest(await getHandoffDigest(engagementId, bounds))
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
      setLoading(false)
    }
  }, [engagementId, bounds])

  useEffect(() => {
    void refresh()
  }, [refresh, onlineTick])

  const pickQuick = (hours: number) => {
    const to = new Date()
    const from = new Date(to.getTime() - hours * 3_600_000)
    setBounds({ from: from.toISOString(), to: to.toISOString() })
    setFromDraft('')
    setToDraft('')
  }

  const applyCustom = () => {
    const from = fromDraft ? new Date(fromDraft).toISOString() : undefined
    const to = toDraft ? new Date(toDraft).toISOString() : undefined
    setBounds({ from, to })
  }

  const copyHandoffNote = async () => {
    setCopying(true)
    try {
      const markdown = await getHandoffDigestMarkdown(engagementId, bounds)
      await navigator.clipboard.writeText(markdown)
      setNotice('Handoff note copied -- paste it to the next watch.')
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setCopying(false)
    }
  }

  return (
    <section className="view">
      <h2>Handoff digest</h2>
      <p className="muted">
        The watch in one ordered account: sessions, tasking and its outcomes, refusals,
        notes, shells -- the window you pick, the trail as the source. The audit ledger
        remains the raw surface; this is the resume read.
      </p>

      {error && <p className="error">{error}</p>}
      {notice && <p className="notice">{notice}</p>}

      <div className="card">
        <h3>The window</h3>
        <div className="inline-form">
          {QUICK_WINDOWS.map((q) => (
            <button
              key={q.hours}
              className="ghost"
              onClick={() => pickQuick(q.hours)}
              title={`Anchor the window to the ${q.label.toLowerCase()}`}
            >
              {q.label}
            </button>
          ))}
          <input
            className="filter-text"
            type="datetime-local"
            value={fromDraft}
            onChange={(e) => setFromDraft(e.target.value)}
            title="Custom window start (inclusive)"
            aria-label="Window start"
          />
          <input
            className="filter-text"
            type="datetime-local"
            value={toDraft}
            onChange={(e) => setToDraft(e.target.value)}
            title="Custom window end (inclusive; empty = now)"
            aria-label="Window end"
          />
          <button className="ghost" onClick={applyCustom} title="Apply the custom bounds">
            Apply
          </button>
          <button className="ghost" onClick={() => void refresh()} disabled={busy}>
            <Icon name="refresh" />
            Refresh
          </button>
          <button
            className="ghost"
            onClick={() => void copyHandoffNote()}
            disabled={copying}
            title="Copy the same account as a markdown handoff note"
          >
            {copying ? 'Copying…' : 'Copy handoff note'}
          </button>
        </div>
        {digest && (
          <p className="muted" title={`Integrity ${digest.contentHash}`}>
            Window {when(digest.from)} — {when(digest.to)}, generated{' '}
            {when(digest.generatedAt)}.{' '}
            {digest.chainVerified
              ? 'Audit chain verified.'
              : `Audit chain verification FAILED: ${digest.chainBreak}`}
          </p>
        )}
      </div>

      {digest && countChips(digest).length > 0 && (
        <div className="count-row">
          {countChips(digest).map((c) => (
            <span key={c.label} className="chip">
              <strong>{c.n}</strong> {c.label}
            </span>
          ))}
        </div>
      )}

      <div className="card">
        <h3>The watch in order</h3>
        {loading ? (
          <div className="empty">
            <span className="spinner" />
            Reading the trail…
          </div>
        ) : (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>At</th>
                  <th>Kind</th>
                  <th>Verb</th>
                  <th>Operator</th>
                  <th>Implant</th>
                  <th>Payload</th>
                  <th>Outcome</th>
                </tr>
              </thead>
              <tbody>
                {(!digest || digest.entries.length === 0) && (
                  <tr>
                    <td colSpan={7}>
                      <div className="empty">
                        <Icon name="activity" />
                        No watch events in the window.
                      </div>
                    </td>
                  </tr>
                )}
                {digest?.entries.map((e) => (
                  <tr key={e.eventId}>
                    <td title={when(e.at)}>{when(e.at)}</td>
                    <td>
                      <span className="status">{e.kind}</span>
                    </td>
                    <td>
                      <code>{e.task?.verb ?? e.verb}</code>
                    </td>
                    <td>
                      <code title={e.operator?.operatorId ?? undefined}>{entryActor(e)}</code>
                    </td>
                    <td>
                      <code title={e.implant?.implantId}>{entrySubject(e)}</code>
                    </td>
                    <td>
                      <pre className="output">{e.payload || '\u2014'}</pre>
                    </td>
                    <td>{e.task?.outcome ?? (e.outcome || '\u2014')}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </section>
  )
}
