import { useEffect, useState } from 'react'
import { getSessionSettings, putSessionSettings } from '../api'

// The teamserver's runtime settings -- operator-level, not engagement-level:
// server-wide knobs an operator adjusts while working. The session-presence
// pair explains the fleet's offline behavior: how long a silent session holds
// its Online dot before the sweep closes it, and how often the sweep checks.
// Changes apply live (the sweeper reads the current values on every pass; no
// restart) and the server persists them, so they survive the next restart.
//
// The bounds mirror the server's validation: threshold 1 minute..24 hours,
// sweep interval 10 seconds..1 hour. The threshold must stay above any
// implant's check-in interval -- a threshold shorter than the sleep flaps
// every quiet period to offline -- which is why the minimum is a minute and
// the help text says so.

export function SettingsView() {
  const [threshold, setThreshold] = useState('')
  const [interval, setIntervalValue] = useState('')
  const [saved, setSaved] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    void (async () => {
      try {
        const current = await getSessionSettings()
        setThreshold(String(current.thresholdMinutes))
        setIntervalValue(String(current.sweepIntervalMinutes))
        setError(null)
      } catch (e) {
        setError(String(e))
      }
    })()
  }, [])

  const onSave = async (event: React.FormEvent) => {
    event.preventDefault()
    const thresholdMinutes = Number(threshold)
    const sweepIntervalMinutes = Number(interval)
    if (!Number.isFinite(thresholdMinutes) || !Number.isFinite(sweepIntervalMinutes)) {
      setError('Both values must be numbers (minutes).')
      return
    }
    setBusy(true)
    try {
      const applied = await putSessionSettings({ thresholdMinutes, sweepIntervalMinutes })
      setThreshold(String(applied.thresholdMinutes))
      setIntervalValue(String(applied.sweepIntervalMinutes))
      setSaved(
        `Applied -- the next sweep pass (within ${applied.sweepIntervalMinutes} min) uses the new threshold.`,
      )
      setError(null)
    } catch (e) {
      setSaved(null)
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="card">
      <h3>Settings</h3>
      <p className="muted">
        Teamserver runtime settings -- server-wide, live on save, remembered across restarts.
      </p>

      <form className="build-form" onSubmit={onSave}>
        <fieldset>
          <legend>Session presence</legend>
          <label>
            Offline after (min)
            <input
              value={threshold}
              onChange={(e) => setThreshold(e.target.value)}
              title="How long a session may go silent before the sweep closes it and the implant shows offline. A stream that closes cleanly drops immediately; this is the wait for one that dies silently. Keep it above your implants' check-in interval (a shorter threshold flaps every quiet period). 1..1440; default 15."
            />
          </label>
          <label>
            Sweep every (min)
            <input
              value={interval}
              onChange={(e) => setIntervalValue(e.target.value)}
              title="How often the staleness check runs. A shorter interval drops vanished implants sooner at the cost of more frequent passes. 0.17..60; default 1."
            />
          </label>
          <p className="muted" style={{ gridColumn: '1 / -1', margin: 0 }}>
            Why a vanished implant holds its dot: a beacon stream that dies without a clean close
            keeps its session Active until the sweep closes it -- the Online dot lags reality by at
            most the threshold, and the fleet's Last seen column always tells the truth in the
            meantime.
          </p>
        </fieldset>
        <button className="primary" type="submit" disabled={busy}>
          Save
        </button>
      </form>
      {saved && <p className="muted">{saved}</p>}
      {error && <p className="error">{error}</p>}
    </div>
  )
}
