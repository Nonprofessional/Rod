import { useEffect, useMemo, useState } from 'react'
import { fetchArtifactBlob, listArtifacts } from '../api'
import { fmtBytes } from '../format'
import {
  panelEarnsRender,
  renderOutput,
  type CredentialRow,
  type EnumerationRow,
  type FileRow,
  type ProcessRow,
} from '../outputRender'
import type { VerbOutputKind } from '../api'

// The typed output view (architecture.md Sec 15.2's second contract): the
// generic panels a completed task's output renders into, keyed on the verb's
// declared output kind. Process, file, credential, and enumeration outputs
// render as tables over their JSON-lines grammar; file and screenshot
// outputs render the artifact the task produced; everything else -- a verb
// nobody described, or one whose output did not parse -- degrades to the
// plain transcript. The transcript stays the record: the panel is a lens on
// it, never a replacement for it.

export function TaskOutputView({
  engagementId,
  taskId,
  output,
  outputKind,
  className,
}: {
  engagementId: string
  taskId: string | null
  output: string
  outputKind: VerbOutputKind | null
  className?: string
}) {
  const panel = useMemo(() => renderOutput(outputKind, output), [outputKind, output])
  if (output.length === 0) return null

  if (!panelEarnsRender(panel) || panel.kind === 'text') {
    return <pre className={className}>{output}</pre>
  }

  switch (panel.kind) {
    case 'process-list':
      return <ProcessTable rows={panel.rows} />
    case 'file-list':
      return <FileTable rows={panel.rows} />
    case 'credential':
      return <CredentialTable rows={panel.rows} />
    case 'enumeration':
      return <EnumerationTable rows={panel.rows} />
    case 'file':
      return <ArtifactRows engagementId={engagementId} taskId={taskId} preview={false} />
    case 'screenshot':
      return <ArtifactRows engagementId={engagementId} taskId={taskId} preview={true} />
  }
}

function table(className: string, head: string[], body: React.ReactNode) {
  return (
    <div className={`table-wrap ${className}`}>
      <table>
        <thead>
          <tr>
            {head.map((h) => (
              <th key={h}>{h}</th>
            ))}
          </tr>
        </thead>
        <tbody>{body}</tbody>
      </table>
    </div>
  )
}

function ProcessTable({ rows }: { rows: ProcessRow[] }) {
  return table(
    'process-table',
    ['PID', 'PPID', 'User', 'Image'],
    rows.map((row) => (
      <tr key={`${row.pid}-${row.image}`}>
        <td>{row.pid}</td>
        <td>{row.ppid}</td>
        <td>{row.user}</td>
        <td>
          <code>{row.image}</code>
        </td>
      </tr>
    )),
  )
}

function FileTable({ rows }: { rows: FileRow[] }) {
  return table(
    'file-table',
    ['Name', 'Size', 'Modified'],
    rows.map((row) => (
      <tr key={row.name}>
        <td>
          <code>{row.name}</code>
          {row.dir ? ' /' : ''}
        </td>
        <td>{row.dir ? '—' : fmtBytes(row.size)}</td>
        <td>{row.mtime ? new Date(row.mtime).toLocaleString() : '—'}</td>
      </tr>
    )),
  )
}

function CredentialTable({ rows }: { rows: CredentialRow[] }) {
  const [revealed, setRevealed] = useState<Set<number>>(new Set())
  return table(
    'credential-table',
    ['Kind', 'Principal', 'Secret', 'Source', 'Host', 'Note'],
    rows.map((row, index) => (
      <tr key={`${row.kind}-${row.principal}-${index}`}>
        <td>{row.kind}</td>
        <td>
          <code>{row.principal}</code>
        </td>
        <td>
          {row.secret === null ? (
            '—'
          ) : revealed.has(index) ? (
            <code>{row.secret}</code>
          ) : (
            <button className="sm" onClick={() => setRevealed((current) => new Set(current).add(index))}>
              reveal
            </button>
          )}
        </td>
        <td>{row.source ?? '—'}</td>
        <td>{row.host ?? '—'}</td>
        <td>{row.note ?? '—'}</td>
      </tr>
    )),
  )
}

function EnumerationTable({ rows }: { rows: EnumerationRow[] }) {
  return table(
    'enumeration-table',
    ['Host', 'Port', 'State', 'Service', 'Address', 'OS', 'Arch'],
    rows.map((row, index) => (
      <tr key={`${row.host}-${row.port ?? 'x'}-${index}`}>
        <td>
          <code>{row.host}</code>
        </td>
        <td>{row.port ?? '—'}</td>
        <td>{row.state ?? '—'}</td>
        <td>{row.service ?? '—'}</td>
        <td>{row.address ?? '—'}</td>
        <td>{row.os ?? '—'}</td>
        <td>{row.arch ?? '—'}</td>
      </tr>
    )),
  )
}

// The artifact panel for file- and screenshot-typed outputs: the task's
// artifacts, downloadable byte-exact over the authenticated session, with an
// inline preview for images.
function ArtifactRows({
  engagementId,
  taskId,
  preview,
}: {
  engagementId: string
  taskId: string | null
  preview: boolean
}) {
  const [artifacts, setArtifacts] = useState<{
    artifactId: string
    name: string
    size: number
    contentType: string
  }[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [urls, setUrls] = useState<Record<string, string>>({})

  useEffect(() => {
    if (!taskId) return
    let cancelled = false
    listArtifacts(engagementId, taskId)
      .then((page) => {
        if (!cancelled) setArtifacts(page.items)
      })
      .catch((e) => {
        if (!cancelled) setError(String(e))
      })
    return () => {
      cancelled = true
    }
  }, [engagementId, taskId])

  useEffect(() => {
    if (!preview || !artifacts) return
    let cancelled = false
    const created: string[] = []
    Promise.all(
      artifacts
        .filter((a) => a.contentType.startsWith('image/'))
        .map(async (a) => {
          const blob = await fetchArtifactBlob(engagementId, a.artifactId)
          const url = URL.createObjectURL(blob)
          created.push(url)
          return [a.artifactId, url] as const
        }),
    )
      .then((pairs) => {
        if (!cancelled) setUrls(Object.fromEntries(pairs))
      })
      .catch((e) => {
        if (!cancelled) setError(String(e))
      })
    return () => {
      cancelled = true
      for (const url of created) URL.revokeObjectURL(url)
    }
  }, [engagementId, artifacts, preview])

  if (error) return <p className="error">{error}</p>
  if (!artifacts) return <div className="empty"><span className="spinner" /></div>
  if (artifacts.length === 0) return <p className="muted">No artifact landed for this task.</p>

  return (
    <div className="artifact-rows">
      {artifacts.map((artifact) => (
        <div key={artifact.artifactId} className="inline-form artifact-row">
          {preview && urls[artifact.artifactId] && (
            <a href={urls[artifact.artifactId]} target="_blank" rel="noreferrer">
              <img
                className="screenshot-preview"
                src={urls[artifact.artifactId]}
                alt={artifact.name}
              />
            </a>
          )}
          <code>{artifact.name}</code>
          <span className="muted">{fmtBytes(artifact.size)}</span>
          <a
            className="sm"
            href={`engagements/${engagementId}/artifacts/${artifact.artifactId}`}
            download={artifact.name}
          >
            Download
          </a>
        </div>
      ))}
    </div>
  )
}
