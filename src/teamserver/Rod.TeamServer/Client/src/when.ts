import { useEffect, useState } from 'react'

// Relative-time helpers shared by the fleet table and the session console.
// The clock argument exists because "how long ago" goes stale between
// renders: views pass useNow's ticking value so a row read at 14:59 does not
// keep claiming "2m ago" at 15:20 when nothing else re-rendered the table.

// Compact "how long ago" an operator actually reads: seconds just now, then
// minutes, then hours, then days.
export function ago(iso: string, now: number = Date.now()): string {
  const seconds = Math.max(0, Math.floor((now - new Date(iso).getTime()) / 1000))
  if (seconds < 10) return 'now'
  if (seconds < 60) return `${seconds}s ago`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`
  return `${Math.floor(seconds / 86400)}d ago`
}

// Re-renders the calling view on a fixed interval and returns the current
// clock, for displays that derive text from the passage of time (relative
// last-seen stamps) rather than from data changes alone.
export function useNow(intervalMs: number): number {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), intervalMs)
    return () => window.clearInterval(id)
  }, [intervalMs])
  return now
}

// Seconds rendered the way beacon.sleep speaks them: whole seconds plain,
// sub-second precision to one decimal (the near-interactive end matters).
// Shared by the fleet's detail strip and the session console's facts line.
export function formatSeconds(seconds: number): string {
  return seconds >= 10 || Number.isInteger(seconds)
    ? `${Math.round(seconds)}s`
    : `${seconds.toFixed(1)}s`
}
