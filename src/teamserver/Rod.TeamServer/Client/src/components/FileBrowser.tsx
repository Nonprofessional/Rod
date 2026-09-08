import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  fetchArtifactBlob,
  getTask,
  issueTask,
  listArtifacts,
} from '../api'
import { Icon } from './Icons'
import { StatusBadge } from './StatusBadge'
import { readSelectedFile, VERB_FORMS, type SelectedFile } from '../verbForms'

// The file browser: fs.list walks the target's tree, file.push uploads into
// the listed directory, file.pull downloads the picked file. The same
// snapshot discipline as the process browser -- refresh is the operator's,
// and the pane never pretends the tree is live.
//
// Downloads ride the implant's own arms: a pull that fits the inline ceiling
// comes back in the task output and saves from memory; a larger one streams
// into the artifact store and downloads from there. Small binary files that
// came back inline are decoded as UTF-8 text -- the inline arm is textual --
// so anything binary-critical should be pulled above the ceiling; the row's
// tooltip says so.

interface Entry {
  name: string
  dir: boolean
  size: number
  mtime: string
}

function parseListing(output: string): Entry[] {
  const entries: Entry[] = []
  for (const line of output.split('\n')) {
    if (line.trim() === '') continue
    try {
      entries.push(JSON.parse(line) as Entry)
    } catch {
      // A non-JSON line is not an entry; the browser shows whatever parsed.
    }
  }
  return entries.sort((a, b) =>
    a.dir === b.dir ? a.name.localeCompare(b.name) : a.dir ? -1 : 1,
  )
}

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

export function FileBrowser({
  engagementId,
  implantId,
  osHint,
  onClose,
}: {
  engagementId: string
  implantId: string
  // The implant's reported OS: picks the browser's starting directory.
  osHint: string | null
  onClose: () => void
}) {
  const [path, setPath] = useState(() =>
    osHint && /windows/i.test(osHint) ? 'C:\\' : '/',
  )
  const [pathDraft, setPathDraft] = useState(path)
  const [entries, setEntries] = useState<Entry[] | null>(null)
  const [status, setStatus] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [pending, setPending] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const closedRef = useRef(false)
  const fileInput = useRef<HTMLInputElement>(null)

  useEffect(() => {
    return () => {
      closedRef.current = true
    }
  }, [])

  // Issues a verb and polls its task to a terminal state; the shared loop of
  // both browsers (files and processes). Returns the final task detail, or
  // null when the pane closed mid-wait.
  const runTask = useCallback(
    async (verb: string, args: string, content?: string) => {
      const task = await issueTask(engagementId, {
        implantId,
        verb,
        arguments: args,
        content,
      })
      for (;;) {
        if (closedRef.current) return null
        const detail = await getTask(engagementId, task.taskId)
        setStatus(detail.status)
        if (detail.status !== 'Queued' && detail.status !== 'Dispatched') {
          return { id: task.taskId, detail }
        }
        await wait(700)
      }
    },
    [engagementId, implantId],
  )

  const list = useCallback(
    async (target: string) => {
      if (busy) return
      setBusy(true)
      setError(null)
      setEntries(null)
      setStatus('Queued')
      try {
        const done = await runTask('fs.list', target)
        if (!done) return
        if (done.detail.outcome === 'Succeeded' && done.detail.output) {
          setEntries(parseListing(done.detail.output))
          setPath(target)
          setPathDraft(target)
        } else {
          setError(done.detail.output ?? `fs.list ${done.detail.outcome ?? done.detail.status}`)
        }
      } catch (e) {
        setError(String(e))
      } finally {
        setBusy(false)
      }
    },
    [busy, runTask],
  )

  useEffect(() => {
    void list(path)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const parent = useMemo(() => {
    const trimmed = path.replace(/[\\/]+$/, '')
    const cut = Math.max(trimmed.lastIndexOf('/'), trimmed.lastIndexOf('\\'))
    if (cut <= 0) return null
    const up = trimmed.slice(0, cut)
    return up.length > 0 ? up : null
  }, [path])

  const joinPath = (name: string) =>
    path.endsWith('/') || path.endsWith('\\') ? `${path}${name}` : `${path}/${name}`

  const onDownload = async (entry: Entry) => {
    if (busy) return
    const remote = joinPath(entry.name)
    setPending(`downloading ${entry.name}…`)
    setStatus('Queued')
    try {
      const done = await runTask('file.pull', remote)
      if (!done) return
      if (done.detail.outcome !== 'Succeeded') {
        setError(done.detail.output ?? `file.pull ${done.detail.outcome}`)
        return
      }
      // A larger pull streams into the artifact store; prefer that download
      // (byte-exact) over the inline text.
      const artifacts = await listArtifacts(engagementId, done.id)
      if (artifacts.items.length > 0) {
        const blob = await fetchArtifactBlob(engagementId, artifacts.items[0].artifactId)
        saveBlob(blob, entry.name)
      } else if (done.detail.output !== null) {
        saveBlob(new Blob([done.detail.output], { type: 'text/plain' }), entry.name)
      } else {
        setError('file.pull returned no output and no artifact.')
      }
    } catch (e) {
      setError(String(e))
    } finally {
      setPending(null)
    }
  }

  const onUploadPicked = async (file: globalThis.File | undefined) => {
    if (!file || busy) return
    let selected: SelectedFile
    try {
      selected = await readSelectedFile(file)
    } catch (e) {
      setError(String(e))
      return
    }
    setPending(`uploading ${file.name}…`)
    setStatus('Queued')
    try {
      // The shared file.push grammar: inline under the ceiling, staged above.
      const built = await VERB_FORMS['file.push'].build(
        { path: joinPath(file.name) },
        { file: selected },
      )
      const done = await runTask('file.push', built.arguments, built.content)
      if (!done) return
      if (done.detail.outcome !== 'Succeeded') {
        setError(done.detail.output ?? `file.push ${done.detail.outcome}`)
        return
      }
      await list(path)
    } catch (e) {
      setError(String(e))
    } finally {
      setPending(null)
      if (fileInput.current) fileInput.current.value = ''
    }
  }

  return (
    <div className="modal-backdrop" onClick={(e) => e.target === e.currentTarget && onClose()}>
      <div className="card modal wide">
        <div className="inline-form">
          <h3 style={{ marginRight: 'auto' }}>Files</h3>
          <button className="ghost" onClick={() => parent && void list(parent)} disabled={!parent || busy}>
            Up
          </button>
          <input
            className="wide"
            value={pathDraft}
            placeholder={path}
            onChange={(e) => setPathDraft(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') void list(pathDraft.trim() || path)
            }}
            title="The directory to list; Enter applies."
          />
          <button className="ghost" onClick={() => void list(pathDraft.trim() || path)} disabled={busy}>
            Go
          </button>
          <button className="ghost" onClick={() => void list(path)} disabled={busy}>
            Refresh
          </button>
          <button className="ghost" onClick={() => fileInput.current?.click()} disabled={busy}>
            Upload here…
          </button>
          <input
            ref={fileInput}
            type="file"
            hidden
            onChange={(e) => void onUploadPicked(e.target.files?.[0])}
          />
          <button className="ghost" onClick={onClose}>
            Close
          </button>
        </div>
        <p className="muted">
          <code>{path}</code>{' '}
          {status && (
            <>
              · <StatusBadge status={status} />
            </>
          )}
          {pending && <> · {pending}</>}
        </p>
        {entries === null && !error && (
          <div className="empty">
            <span className="spinner" />
            {busy ? 'Listing…' : 'Waiting for the implant to answer…'}
          </div>
        )}
        {entries !== null && (
          <div className="table-wrap process-table">
            <table>
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Size</th>
                  <th>Modified</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                {entries.length === 0 && (
                  <tr>
                    <td colSpan={4} className="muted">
                      Empty directory.
                    </td>
                  </tr>
                )}
                {entries.map((entry) => (
                  <tr key={entry.name}>
                    <td>
                      <button
                        className="link file-entry"
                        onClick={() => entry.dir && void list(joinPath(entry.name))}
                        disabled={!entry.dir || busy}
                        title={entry.dir ? 'Open directory' : undefined}
                      >
                        <Icon name={entry.dir ? 'folder' : 'file'} className="wire-icon" />
                        <code>{entry.name}</code>
                      </button>
                    </td>
                    <td>{entry.dir ? '—' : `${entry.size} B`}</td>
                    <td>{new Date(entry.mtime).toLocaleString()}</td>
                    <td>
                      {!entry.dir && (
                        <button
                          className="sm"
                          onClick={() => void onDownload(entry)}
                          disabled={busy || !!pending}
                          title="Small files come back inline as text; larger ones stream to the artifact store byte-exact."
                        >
                          Download
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {error && <p className="error">{error}</p>}
      </div>
    </div>
  )
}

function saveBlob(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = name
  anchor.click()
  window.setTimeout(() => URL.revokeObjectURL(url), 30_000)
}
