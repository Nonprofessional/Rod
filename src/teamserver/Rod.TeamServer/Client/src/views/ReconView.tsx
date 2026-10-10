import { useEffect, useState } from 'react'
import {
  type Engagement,
  type LootEntry,
  type ReconWorkbenchResult,
  fetchArtifactBlob,
  listEngagements,
  listLoot,
  runReconLookup,
  runReconPortScan,
  runReconResolve,
} from '../api'
import { saveBlob } from '../download'
import { fmtBytes } from '../format'

// The external recon workbench's acting surface (docs/operations/recon.md,
// architecture.md Sec 11.4): the scoping that precedes the first foothold.
// One form, four runs -- the registration lookup, the certificate-
// transparency census, resolution, and the ROE-gated port scan -- each a
// synchronous request whose findings land as a task-less artifact; the
// answer carries the artifact's metadata and the pane below renders its
// bytes. The egress decisions (which RDAP mirror, which CT mirror, whose
// resolver, where a scan dials from) are teamserver configuration, not UI
// state: an unconfigured half answers 503 naming the setting, and that
// sentence is shown verbatim rather than paraphrased into a dead end.

type Mode = 'rdap' | 'subdomains' | 'resolve' | 'portscan'

const MODES: readonly { id: Mode; label: string }[] = [
  { id: 'rdap', label: 'RDAP' },
  { id: 'subdomains', label: 'Subdomains' },
  { id: 'resolve', label: 'Resolve' },
  { id: 'portscan', label: 'Port scan' },
]

// Mirrors Recon:MaxResolveTargets; the server enforces the real cap, this
// only keeps an obviously-over census from leaving the browser.
const MAX_RESOLVE_TARGETS = 256

// A full CT census can carry thousands of names; no operator reads five
// thousand rows inline. The pane is the look, the download the read.
const MAX_RENDER_ROWS = 500

// What the finding pane needs: metadata from either the run answer or a
// loot row -- the bytes are fetched by artifact id either way.
interface Finding {
  artifactId: string
  name: string
  contentType: string
  size: number
  summary?: string
}

function modeHint(mode: Mode): string {
  switch (mode) {
    case 'rdap':
      return 'example.com -- registration data (whois behind it for zones without RDAP)'
    case 'subdomains':
      return 'example.com -- certificate-transparency census'
    case 'resolve':
      return 'one hostname or IP per line -- the first line alone is a single lookup'
    case 'portscan':
      return '10.0.0.5 or web01.example.com -- dials from the teamserver'
  }
}

export function ReconView({
  engagementId,
  onlineTick,
}: {
  engagementId: string
  onlineTick: number
}) {
  const [mode, setMode] = useState<Mode>('subdomains')
  const [targetDraft, setTargetDraft] = useState('')
  const [portsDraft, setPortsDraft] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [engagement, setEngagement] = useState<Engagement | null>(null)
  const [finding, setFinding] = useState<Finding | null>(null)
  const [history, setHistory] = useState<LootEntry[]>([])
  const [historyCursor, setHistoryCursor] = useState<string | null>(null)
  // Bumped after a run so the findings list shows what just landed without
  // waiting for an SSE event the workbench never emits (its runs are
  // synchronous request bodies, not tasks).
  const [ranAt, setRanAt] = useState(0)

  // The ROE note explains the scan's gate before the server has to refuse
  // one; the engagement list is the only read that carries the profile.
  useEffect(() => {
    void (async () => {
      try {
        const all = await listEngagements()
        setEngagement(all.find((e) => e.engagementId === engagementId) ?? null)
      } catch {
        // The note stays hidden; a scan still reports the gate itself.
      }
    })()
  }, [engagementId, onlineTick])

  useEffect(() => {
    let cancelled = false
    void (async () => {
      try {
        const page = await listLoot(engagementId)
        if (!cancelled) {
          setHistory(page.items.filter((entry) => entry.name.startsWith('recon.')))
          setHistoryCursor(page.nextCursor)
        }
      } catch {
        // The form works without the history listing; the next tick retries.
      }
    })()
    return () => {
      cancelled = true
    }
  }, [engagementId, onlineTick, ranAt])

  const loadOlder = async () => {
    if (!historyCursor) return
    try {
      const page = await listLoot(engagementId, undefined, historyCursor)
      setHistory((current) => [
        ...current,
        ...page.items.filter((entry) => entry.name.startsWith('recon.')),
      ])
      setHistoryCursor(page.nextCursor)
    } catch (e) {
      setError(String(e))
    }
  }

  const submit = async () => {
    if (busy) return
    const lines = targetDraft
      .split('\n')
      .map((line) => line.trim())
      .filter((line) => line.length > 0)
    if (lines.length === 0) return
    if (mode === 'resolve' && lines.length > MAX_RESOLVE_TARGETS) {
      setError(`At most ${MAX_RESOLVE_TARGETS} names per resolution; split the census into walks.`)
      return
    }
    setBusy(true)
    setError(null)
    try {
      let answer: ReconWorkbenchResult
      if (mode === 'rdap' || mode === 'subdomains') {
        // The passive halves are one-target runs; lines beyond the first
        // are paste noise, not a census.
        answer = await runReconLookup(engagementId, mode, lines[0])
      } else if (mode === 'resolve') {
        answer = await runReconResolve(engagementId, lines)
      } else {
        answer = await runReconPortScan(engagementId, lines[0], portsDraft.trim() || undefined)
      }
      setFinding(answer)
      setRanAt(Date.now())
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="card">
      <h3>Recon</h3>
      <p className="muted">
        Pre-foothold scoping on the teamserver: registration lookups, the certificate-transparency
        census, name resolution, and the ROE-gated port scan. Every run lands its findings as
        attributed loot and joins the Intel picture. Each half runs only once its egress service
        is configured -- docs/operations/recon.md -- and a half that is not answers with the
        setting it wants.
      </p>
      <div className="table-toolbar">
        {MODES.map((m) => (
          <button
            key={m.id}
            className={mode === m.id ? 'sm' : 'ghost sm'}
            onClick={() => {
              setMode(m.id)
              setError(null)
            }}
          >
            {m.label}
          </button>
        ))}
      </div>
      <form
        className="task-form"
        onSubmit={(e) => {
          e.preventDefault()
          void submit()
        }}
      >
        <textarea
          className="wide"
          rows={mode === 'resolve' ? 4 : 1}
          placeholder={modeHint(mode)}
          value={targetDraft}
          onChange={(e) => setTargetDraft(e.target.value)}
          spellCheck={false}
        />
        {mode === 'portscan' && (
          <input
            className="wide"
            placeholder="ports: 22,80,443 or 1-1024 -- blank scans the curated default set"
            value={portsDraft}
            onChange={(e) => setPortsDraft(e.target.value)}
            spellCheck={false}
          />
        )}
        <button className="sm" type="submit" disabled={busy || targetDraft.trim().length === 0}>
          {busy ? 'Running…' : 'Run'}
        </button>
      </form>
      {mode === 'portscan' && engagement && (
        <p className="muted">
          {engagement.roe.permittedTargets.length === 0
            ? 'ROE target scope: unrestricted -- any scan target passes the gate.'
            : `ROE target scope: ${engagement.roe.permittedTargets.join(
                ', ',
              )} -- a target outside it is refused before any connection opens.`}
        </p>
      )}
      {busy && (
        <p className="muted">
          The run happens inside the request; a scan at the port cap can take half a minute.
        </p>
      )}
      {error && <p className="error">{error}</p>}
      {finding && (
        <FindingPane
          engagementId={engagementId}
          finding={finding}
          onClose={() => setFinding(null)}
        />
      )}
      <h3>Findings</h3>
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Name</th>
              <th>Captured</th>
              <th>Size</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {history.map((entry) => (
              <tr key={entry.artifactId}>
                <td title={entry.contentType}>{entry.name}</td>
                <td>{new Date(entry.storedAt).toLocaleString()}</td>
                <td>{fmtBytes(entry.size)}</td>
                <td>
                  <button className="ghost sm" onClick={() => setFinding(entry)}>
                    View
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {history.length === 0 && <p className="muted">No workbench findings yet.</p>}
      {historyCursor && (
        <button className="ghost sm" onClick={() => void loadOlder()}>
          Load older
        </button>
      )}
    </div>
  )
}

// --- The finding pane: metadata above, the artifact's bytes below ----------

function FindingPane({
  engagementId,
  finding,
  onClose,
}: {
  engagementId: string
  finding: Finding
  onClose: () => void
}) {
  const [text, setText] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  // Reading the bytes records the trail's ArtifactViewed event, same as
  // opening any other loot -- rendering here is a read, not a free copy.
  useEffect(() => {
    let cancelled = false
    setText(null)
    setError(null)
    void (async () => {
      try {
        const blob = await fetchArtifactBlob(engagementId, finding.artifactId)
        if (!cancelled) setText(await blob.text())
      } catch (e) {
        if (!cancelled) setError(String(e))
      }
    })()
    return () => {
      cancelled = true
    }
  }, [engagementId, finding.artifactId])

  const download = async () => {
    try {
      saveBlob(await fetchArtifactBlob(engagementId, finding.artifactId), finding.name)
    } catch (e) {
      setError(String(e))
    }
  }

  return (
    <div className="recon-finding">
      <div className="table-toolbar">
        <strong>{finding.name}</strong>
        {finding.summary && <span className="muted">{finding.summary}</span>}
        <span className="muted">{fmtBytes(finding.size)}</span>
        <button className="ghost sm" onClick={() => void download()}>
          Download
        </button>
        <button className="ghost sm" onClick={onClose}>
          Close
        </button>
      </div>
      {text === null && !error && <p className="muted">Reading the finding…</p>}
      {error && <p className="error">{error}</p>}
      {text !== null && renderFinding(finding.name, text)}
    </div>
  )
}

// Each artifact shape has its documented grammar (recon.md): ndjson rows
// for the census, resolution, and scan findings; an indented registration
// record; the whois fallback's verbatim text. An unexpected shape falls
// back to the raw bytes rather than refusing to show the finding.
function renderFinding(name: string, text: string) {
  if (name.startsWith('recon.rdap:')) {
    try {
      return <pre className="recon-raw">{JSON.stringify(JSON.parse(text), null, 2)}</pre>
    } catch {
      return <pre className="recon-raw">{text}</pre>
    }
  }
  if (name.startsWith('recon.whois:')) {
    return <pre className="recon-raw">{text}</pre>
  }

  let rows: unknown[]
  try {
    rows = text
      .split('\n')
      .filter((line) => line.length > 0)
      .map((line) => JSON.parse(line))
  } catch {
    return <pre className="recon-raw">{text}</pre>
  }

  const shown = rows.slice(0, MAX_RENDER_ROWS)
  const rest = rows.length - shown.length
  const capped =
    rest > 0 ? (
      <p className="muted">
        First {MAX_RENDER_ROWS} of {rows.length} rows -- download for the full set.
      </p>
    ) : null

  if (name.startsWith('recon.subdomains:')) {
    return (
      <>
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Host</th>
              </tr>
            </thead>
            <tbody>
              {(shown as { host: string }[]).map((row) => (
                <tr key={row.host}>
                  <td>{row.host}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {capped}
      </>
    )
  }

  if (name.startsWith('recon.resolve:')) {
    return (
      <>
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Host</th>
                <th>Addresses</th>
                <th>CNAME</th>
              </tr>
            </thead>
            <tbody>
              {(shown as { host: string; addresses?: string[]; cname?: string }[]).map(
                (row, index) => (
                  <tr key={`${row.host}-${index}`}>
                    <td>{row.host}</td>
                    <td>{row.addresses?.join(', ') || <span className="muted">—</span>}</td>
                    <td>{row.cname ?? <span className="muted">—</span>}</td>
                  </tr>
                ),
              )}
            </tbody>
          </table>
        </div>
        {capped}
      </>
    )
  }

  // The scan's findings (and an unrecognized recon name whose rows still
  // parse): open ports read best ordered by port, not by which dial won
  // the race.
  const scan = (shown as { host: string; port: number; state: string }[])
    .slice()
    .sort((a, b) => a.port - b.port)
  return (
    <>
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Host</th>
              <th>Port</th>
              <th>State</th>
            </tr>
          </thead>
          <tbody>
            {scan.map((row, index) => (
              <tr key={`${row.host}:${row.port}:${index}`}>
                <td>{row.host}</td>
                <td>{row.port}</td>
                <td>{row.state}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {capped}
    </>
  )
}
