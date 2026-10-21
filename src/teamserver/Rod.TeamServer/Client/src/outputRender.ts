// Output rendering by declared kind (architecture.md Sec 15.2, the contract
// trilogy's second contract). A verb's descriptor declares what its results
// carry; this module parses completed task output against the kind's
// documented JSON-lines grammar and hands the transcript a typed panel model.
// A verb whose output does not parse -- typed poorly, or plain text from a
// verb nobody described -- degrades to text: the UI keeps working, the module
// just earns no panels.

import type { VerbOutputKind } from './api'

export interface ProcessRow {
  pid: number
  ppid: number
  user: string
  image: string
}

export interface FileRow {
  name: string
  dir: boolean
  size: number
  mtime?: string
}

export interface CredentialRow {
  kind: string
  principal: string
  secret: string | null
  source: string | null
  host: string | null
  note: string | null
}

export interface EnumerationRow {
  host: string
  port: number | null
  state: string | null
  service: string | null
  address: string | null
  os: string | null
  arch: string | null
}

export type OutputPanel =
  | { kind: 'text' }
  | { kind: 'process-list'; rows: ProcessRow[] }
  | { kind: 'file-list'; rows: FileRow[] }
  | { kind: 'credential'; rows: CredentialRow[] }
  | { kind: 'enumeration'; rows: EnumerationRow[] }
  | { kind: 'file' }
  | { kind: 'screenshot' }

// One JSON object per line; a line that does not parse is not a row. Returns
// null when nothing parsed -- the caller's signal to degrade to text.
function parseLines<T>(output: string, map: (value: Record<string, unknown>) => T | null): T[] | null {
  const rows: T[] = []
  for (const line of output.split('\n')) {
    if (line.trim() === '') continue
    let parsed: unknown
    try {
      parsed = JSON.parse(line)
    } catch {
      continue
    }
    if (typeof parsed !== 'object' || parsed === null) continue
    const row = map(parsed as Record<string, unknown>)
    if (row !== null) rows.push(row)
  }
  return rows.length > 0 ? rows : null
}

const str = (value: unknown): string | null => (typeof value === 'string' ? value : null)
const num = (value: unknown): number | null => (typeof value === 'number' ? value : null)

function parseProcesses(output: string): ProcessRow[] | null {
  return parseLines(output, (v) => {
    const pid = num(v.pid)
    const image = str(v.image)
    if (pid === null || image === null) return null
    return {
      pid,
      ppid: num(v.ppid) ?? -1,
      user: str(v.user) ?? '',
      image,
    }
  })
}

function parseFileRows(output: string): FileRow[] | null {
  return parseLines(output, (v) => {
    const name = str(v.name)
    if (name === null) return null
    return {
      name,
      dir: v.dir === true,
      size: num(v.size) ?? 0,
      mtime: str(v.mtime) ?? undefined,
    }
  })
}

function parseCredentials(output: string): CredentialRow[] | null {
  return parseLines(output, (v) => {
    const kind = str(v.kind)
    const principal = str(v.principal)
    if (kind === null || principal === null) return null
    return {
      kind,
      principal,
      secret: str(v.secret),
      source: str(v.source),
      host: str(v.host),
      note: str(v.note),
    }
  })
}

function parseEnumeration(output: string): EnumerationRow[] | null {
  return parseLines(output, (v) => {
    const host = str(v.host)
    if (host === null || host === '') return null
    const addresses = Array.isArray(v.addresses)
      ? v.addresses.filter((a): a is string => typeof a === 'string').join(',')
      : null
    return {
      host,
      port: num(v.port),
      state: str(v.state),
      service: str(v.service),
      address: addresses,
      os: str(v.os),
      arch: str(v.arch),
    }
  })
}

// Parses completed output against the declared kind. Artifact kinds
// ('file', 'screenshot') carry no inline grammar -- their panel is the
// artifact the task produced, joined server-side -- so they return the
// artifact panel shape without parsing.
export function renderOutput(kind: VerbOutputKind | null, output: string): OutputPanel {
  switch (kind) {
    case 'ProcessList':
      return { kind: 'process-list', rows: parseProcesses(output) ?? [] }
    case 'FileList':
      return { kind: 'file-list', rows: parseFileRows(output) ?? [] }
    case 'Credential':
      return { kind: 'credential', rows: parseCredentials(output) ?? [] }
    case 'Enumeration':
      return { kind: 'enumeration', rows: parseEnumeration(output) ?? [] }
    case 'File':
      return { kind: 'file' }
    case 'Screenshot':
      return { kind: 'screenshot' }
    default:
      return { kind: 'text' }
  }
}

// Whether the parsed panel earned its typed render: a row kind that parsed
// zero rows degrades to plain text (the module typed its output poorly, or
// the transcript genuinely holds prose).
export function panelEarnsRender(panel: OutputPanel): boolean {
  switch (panel.kind) {
    case 'process-list':
    case 'file-list':
    case 'credential':
    case 'enumeration':
      return panel.rows.length > 0
    default:
      return true
  }
}
