// Small display formatters shared across views: one-line previews and
// byte sizes, so columns and previews read consistently everywhere.

// Collapses whitespace and caps the line at max characters with an
// ellipsis -- the argument/output preview shape the task rows share.
export function ellipsize(value: string, max = 96): string {
  const single = value.replace(/\s+/g, ' ')
  return single.length > max ? `${single.slice(0, max)}…` : single
}

// Human byte sizes for artifact and payload columns.
export function fmtBytes(size: number): string {
  if (size >= 1024 * 1024) return `${(size / (1024 * 1024)).toFixed(1)} MB`
  if (size >= 1024) return `${(size / 1024).toFixed(0)} KB`
  return `${size} B`
}
