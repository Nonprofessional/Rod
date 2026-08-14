import { useCallback, useEffect, useState } from 'react'
import {
  type ArtifactSummary,
  type EngagementTask,
  attachArtifact,
  fetchArtifactBlob,
  listArtifacts,
  listEngagementTasks,
} from '../api'

// First-class evidence objects: artifacts are attached to tasks.
// This view lists the engagement's tasks, shows each task's artifacts, lets an
// operator attach a file (encoded as base64 over the JSON API), and downloads a
// stored artifact's bytes through the file endpoint. Scoped by engagement.

export function ArtifactsView({
  engagementId,
  onlineTick,
}: {
  engagementId: string
  onlineTick: number
}) {
  const [tasks, setTasks] = useState<EngagementTask[]>([])
  const [taskId, setTaskId] = useState('')
  const [artifacts, setArtifacts] = useState<ArtifactSummary[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [fileName, setFileName] = useState('')
  const [fileBytes, setFileBytes] = useState('')

  const refreshTasks = useCallback(async () => {
    try {
      setTasks(await listEngagementTasks(engagementId))
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }, [engagementId])

  const refreshArtifacts = useCallback(async () => {
    if (!taskId) {
      setArtifacts([])
      return
    }
    try {
      setArtifacts(await listArtifacts(engagementId, taskId))
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }, [engagementId, taskId])

  useEffect(() => {
    void refreshTasks()
  }, [refreshTasks, onlineTick])

  useEffect(() => {
    void refreshArtifacts()
  }, [refreshArtifacts])

  // Default to the most recent task once tasks arrive.
  useEffect(() => {
    if (taskId || tasks.length === 0) return
    setTaskId(tasks[tasks.length - 1].taskId)
  }, [tasks, taskId])

  const onAttach = async (event: React.FormEvent) => {
    event.preventDefault()
    if (!taskId || !fileName) return
    setBusy(true)
    try {
      await attachArtifact(engagementId, taskId, {
        name: fileName,
        contentType: null,
        content: fileBytes,
      })
      setFileName('')
      setFileBytes('')
      await refreshArtifacts()
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  const onDownload = async (artifact: ArtifactSummary) => {
    try {
      const blob = await fetchArtifactBlob(engagementId, artifact.artifactId)
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = artifact.name
      link.click()
      // Revoke after the click has been handed to the browser: revoking in the
      // same tick aborts the download in some browsers.
      setTimeout(() => URL.revokeObjectURL(url), 30_000)
    } catch (e) {
      setError(String(e))
    }
  }

  const onFile = (event: React.ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0]
    if (!file) return
    setFileName(file.name)
    const reader = new FileReader()
    reader.onload = () => {
      // reader.result is a data URL; strip the prefix to get the raw base64.
      const result = String(reader.result ?? '')
      const comma = result.indexOf(',')
      setFileBytes(comma >= 0 ? result.slice(comma + 1) : result)
    }
    reader.readAsDataURL(file)
  }

  return (
    <div className="card">
      <h3>Artifacts</h3>
      <p className="muted">
        Evidence objects are attached to tasks. Pick a task to list, attach, and download its
        artifacts.
      </p>
      <select value={taskId} onChange={(e) => setTaskId(e.target.value)}>
        {tasks.length === 0 && <option value="">no tasks yet</option>}
        {[...tasks].reverse().map((t) => (
          <option key={t.taskId} value={t.taskId}>
            {t.verb} ({t.taskId.slice(0, 8)})
          </option>
        ))}
      </select>

      {taskId && (
        <form className="inline-form" onSubmit={onAttach}>
          <input type="file" onChange={onFile} required />
          <input placeholder="name" value={fileName} onChange={(e) => setFileName(e.target.value)} required />
          <button type="submit" disabled={busy || !fileBytes}>
            Attach
          </button>
        </form>
      )}

      {error && <p className="error">{error}</p>}

      {artifacts.length === 0 ? (
        <p className="muted">No artifacts on this task.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Name</th>
              <th>Content type</th>
              <th>Size</th>
              <th>Stored</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {artifacts.map((a) => (
              <tr key={a.artifactId}>
                <td>{a.name}</td>
                <td>{a.contentType}</td>
                <td>{a.size}</td>
                <td>{new Date(a.storedAt).toLocaleString()}</td>
                <td>
                  <button onClick={() => void onDownload(a)}>Download</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}
