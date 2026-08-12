// A small status pill. The class is derived from the task/event status string
// (queued, dispatched, completed, failed) so the badge colour matches the
// existing task-table styling. Unknown statuses fall back to the neutral style.

export function StatusBadge({ status }: { status: string }) {
  const normalized = status.toLowerCase()
  return <span className={`status ${normalized}`}>{status}</span>
}
