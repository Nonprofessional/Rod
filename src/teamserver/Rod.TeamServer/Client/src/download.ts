// Save a fetched blob as a browser download -- the shared tail of every
// "download artifact / payload / evidence package" button.
export function saveBlob(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = name
  anchor.click()
  window.setTimeout(() => URL.revokeObjectURL(url), 30_000)
}
