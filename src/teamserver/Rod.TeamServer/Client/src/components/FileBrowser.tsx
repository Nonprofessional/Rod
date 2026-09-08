import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  fetchArtifactBlob,
  getTask,
  issueTask,
  listArtifacts,
} from '../api'
import { browseInFlight, ensureBrowse, lastBrowsedPath, rememberBrowsedPath, useBrowseEntry } from '../browserCache'
import { Icon } from './Icons'
import { StatusBadge } from './StatusBadge'
import { readSelectedFile, VERB_FORMS, type SelectedFile } from '../verbForms'

// The file browser: fs.list walks the target's tree, file.push uploads into
// the listed directory, file.pull downloads the picked file. The snapshot
// discipline is the process browser's: refresh is the operator's, and the
// tree never pretends to be live.
//
// Directory listings ride the browse-result cache, one entry per (implant,
// path): reopening lands on the last visited directory with its cached
// listing, walking back up is instant, a listing in flight when the pane
// closed is attached to instead of re-issued, and completions land in the
// cache whether or not the pane is open. Refresh forces a fresh listing of
// the current directory (cancelling the queued one it replaces). Uploads
// and downloads are actions, not browses -- they issue directly and poll
// their own task while the pane is open.
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
  const [path, setPath] = useState(
    lastBrowsedPath(engagementId, implantId) ??
      (osHint && /windows/i.test(osHint) ? 'C:\\' : '/'),
  )
  const [pathDraft, setPathDraft] = useState(path)
  const [pending, setPending] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const closedRef = useRef(false)
  const fileInput = useRef<HTMLInputElement>(null)

  useEffect(() => {
    return () => {
      closedRef.current = true
    }
  }, [])

  // The current directory's listing, from the cache. Cached directories
  // render instantly; a cold one issues and the shared poller owns the wait.
  useEffect(() => {
    void ensureBrowse(engagementId, implantId, 'fs.list', path).catch((e) => setError(String(e)))
  }, [engagementId, implantId, path])

  const entry = useBrowseEntry(engagementId, implantId, 'fs.list', path)
  const listing = browseInFlight(entry)

  const entries = useMemo(() => {
    if (!entry || entry.outcome !== 'Succeeded' || !entry.output) return null
    return parseListing(entry.output)
  }, [entry])

  const listError =
    entry && !listing && entry.outcome && entry.outcome !== 'Succeeded'
      ? (entry.output ?? `fs.list ${entry.outcome}`)
      : null

  // A successful listing of a new directory becomes the remembered landing
  // spot, so the next open resumes the walk where it left off.
  useEffect(() => {
    if (entry?.outcome === 'Succeeded' && !browseInFlight(entry)) {
      rememberBrowsedPath(engagementId, implantId, path)
      setPathDraft(path)
    }
  }, [engagementId, implantId, path, entry])

  const parent = useMemo(() => {
    const trimmed = path.replace(/[\\/]+$/, '')
    const cut = Math.max(trimmed.lastIndexOf('/'), trimmed.lastIndexOf('\\'))
    if (cut <= 0) return null
    const up = trimmed.slice(0, cut)
    return up.length > 0 ? up : null
  }, [path])

  const joinPath = (name: string) =>
    path.endsWith('/') || path.endsWith('\\') ? `${path}${name}` : `${path}/${name}`

  // Actions (upload/download) issue directly and poll their own task while
  // the pane is open; returns the final detail, or null when the pane closed
  // mid-wait.
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
        if (detail.status !== 'Queued' && detail.status !== 'Dispatched') {
          return { id: task.taskId, detail }
        }
        await wait(700)
      }
    },
    [engagementId, implantId],
  )

  const onDownload = async (target: Entry) => {
    if (pending) return
    const remote = joinPath(target.name)
    setPending(`downloading ${target.name}…`)
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
        saveBlob(blob, target.name)
      } else if (done.detail.output !== null) {
        saveBlob(new Blob([done.detail.output], { type: 'text/plain' }), target.name)
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
    if (!file || pending) return
    let selected: SelectedFile
    try {
      selected = await readSelectedFile(file)
    } catch (e) {
      setError(String(e))
      return
    }
    setPending(`uploading ${file.name}…`)
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
      // The directory just changed: refresh it (forcing past the cached
      // listing of the old contents).
      await ensureBrowse(engagementId, implantId, 'fs.list', path, { force: true })
    } catch (e) {
      setError(String(e))
    } finally {
      setPending(null)
      if (fileInput.current) fileInput.current.value = ''
    }
  }

  const busy = listing || !!pending

  return (
    <div className="modal-backdrop" onClick={(e) => e.target === e.currentTarget && onClose()}>
      <div className="card modal wide">
        <div className="inline-form">
          <h3 style={{ marginRight: 'auto' }}>Files</h3>
          <button
            className="ghost"
            onClick={() => parent && setPath(parent)}
            disabled={!parent || busy}
            title={parent ? `List ${parent}` : 'Already at the root'}
          >
            Up
          </button>
          <input
            className="wide"
            value={pathDraft}
            placeholder={path}
            onChange={(e) => setPathDraft(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') setPath(pathDraft.trim() || path)
            }}
            title="The directory to list; Enter applies."
          />
          <button
            className="ghost"
            onClick={() => setPath(pathDraft.trim() || path)}
            disabled={busy}
            title="List the typed path"
          >
            Go
          </button>
          <button
            className="ghost"
            onClick={() => void ensureBrowse(engagementId, implantId, 'fs.list', path, { force: true }).catch((e) => setError(String(e)))}
            disabled={busy}
            title="Cancel the queued listing (if any) and issue a fresh fs.list of this directory"
          >
            Refresh
          </button>
          <button className="ghost" onClick={() => fileInput.current?.click()} disabled={busy}>
            Upload here
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
          {entry && (
            <>
              · <StatusBadge status={entry.status} />
            </>
          )}
          {pending && <> · {pending}</>}
        </p>
        {entries === null && !listError && !error && (
          <div className="empty">
            <span className="spinner" />
            {listing ? 'Listing…' : 'Waiting for the implant to answer…'}
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
                {entries.map((fileEntry) => (
                  <tr key={fileEntry.name}>
                    <td>
                      <button
                        className="link file-entry"
                        onClick={() => fileEntry.dir && setPath(joinPath(fileEntry.name))}
                        disabled={!fileEntry.dir || busy}
                        title={fileEntry.dir ? 'Open directory' : undefined}
                      >
                        <Icon name={fileEntry.dir ? 'folder' : 'file'} className="wire-icon" />
                        <code>{fileEntry.name}</code>
                      </button>
                    </td>
                    <td>{fileEntry.dir ? '—' : `${fileEntry.size} B`}</td>
                    <td>{new Date(fileEntry.mtime).toLocaleString()}</td>
                    <td>
                      {!fileEntry.dir && (
                        <button
                          className="sm"
                          onClick={() => void onDownload(fileEntry)}
                          disabled={busy}
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
        {(listError || error) && <p className="error">{listError ?? error}</p>}
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
