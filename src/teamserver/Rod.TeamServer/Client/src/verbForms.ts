// Schema-driven task-form construction (architecture.md Sec 15.2, the
// contract trilogy's first contract). The capability registry (GET
// /capabilities) stays the authority for which verbs exist, their OPSEC
// attributes, and now each verb's parameter schema; this module turns a
// schema into the fields a dialog renders and the values into the verb's
// argument string. It knows the *serialization rule* -- space-joined tokens
// in declaration order, a trailing parameter taking the raw remainder, file
// parameters riding the inline-or-staged content arm -- and nothing about
// any specific verb: a module's descriptor renders its form the moment it
// registers, with zero UI changes.

import type { CapabilityDescriptor, VerbParameter } from './api'

// A file picked in a dialog: its name plus the bytes as base64 (and the byte
// count, so the upload path can pick inline vs staged).
export interface SelectedFile {
  name: string
  base64: string
  bytes: number
}

export interface BuiltTask {
  arguments: string
  // Base64 staged bytes (the issue route's Content arm); present only when
  // the arguments alone cannot carry the task.
  content?: string
}

export interface VerbField {
  key: string
  label: string
  kind: VerbParameter['kind']
  placeholder?: string
  required?: boolean
  help?: string
  choices?: string[]
  // The final parameter that takes the raw remainder of the argument string.
  trailing?: boolean
}

export interface VerbForm {
  title: string
  fields: VerbField[]
  // Async because the staged upload arm hashes the bytes before it can name
  // them in the arguments string.
  build: (
    values: Record<string, string | boolean>,
    files: Record<string, SelectedFile>,
  ) => BuiltTask | Promise<BuiltTask>
}

// The inline ceiling the reference implant accepts inside its arguments
// string; anything larger must ride the staged arm.
const INLINE_FILE_LIMIT = 1024 * 1024

const text = (value: string | boolean | undefined): string =>
  typeof value === 'string' ? value.trim() : ''

// Builds the form a descriptor's schema describes. A descriptor with no
// parameters yields the zero-field form (issue outright); one with no schema
// at all falls back to the raw-argument form -- the honest degradation for a
// verb nobody has described yet.
export function formForDescriptor(descriptor: CapabilityDescriptor | undefined): VerbForm {
  if (!descriptor || !descriptor.parameters) return DEFAULT_TASK_FORM
  const fields: VerbField[] = descriptor.parameters.map((p) => ({
    key: p.name,
    label: p.label,
    kind: p.kind,
    placeholder: p.placeholder ?? undefined,
    required: p.required,
    help: p.help ?? undefined,
    choices: p.choices ?? undefined,
    trailing: p.trailing || undefined,
  }))
  const parameters = descriptor.parameters
  return {
    title: titleFor(descriptor),
    fields,
    build: (values, files) => buildArguments(parameters, values, files),
  }
}

// The dialog title: the verb's own name -- the one label every audience
// shares -- with the category as context. No per-verb sugar to maintain.
function titleFor(descriptor: CapabilityDescriptor): string {
  return `Task ${descriptor.verb}`
}

// The serialization rule (architecture.md Sec 10's per-verb grammars, folded
// into one deterministic build): non-file parameters join with single spaces
// in declaration order, empty optionals drop out, a trailing parameter takes
// everything after as the raw remainder (a value that itself carries spaces
// belongs in the trailing slot -- the join is unquoted, exactly what the
// reference grammars parse), and each file parameter rides the content arm --
// inline base64 under the ceiling when the verb accepts it, staged bytes
// otherwise (the server binds the sha256 into the signed arguments, so the
// client never hashes).
async function buildArguments(
  parameters: VerbParameter[],
  values: Record<string, string | boolean>,
  files: Record<string, SelectedFile>,
): Promise<BuiltTask> {
  const tokens: string[] = []
  let content: string | undefined

  for (const parameter of parameters) {
    if (parameter.kind === 'File') continue
    const raw = values[parameter.name]
    if (parameter.kind === 'Boolean') {
      if (raw === true) tokens.push('true')
      continue
    }

    const value = text(raw)
    if (value === '') continue
    tokens.push(value)
  }

  // The file arm serializes after the tokens -- the reference grammars read
  // the payload as the tail (file.push's "<path> <base64>", the staged
  // sha256 the server appends) -- so display order and serialization order
  // stay independent.
  for (const parameter of parameters) {
    if (parameter.kind !== 'File') continue
    const file = files[parameter.name]
    if (!file) continue
    if (parameter.inline && file.bytes <= INLINE_FILE_LIMIT) {
      tokens.push(file.base64)
    } else {
      content = file.base64
    }
  }

  return { arguments: tokens.join(' '), content }
}

// The verbs whose tasks run as live channels (the server's ChannelVerbs is the
// authority; the operator UI keeps this mirror so a channel task can offer its
// input pane). The input route refuses anything else server-side.
export const CHANNEL_VERBS: readonly string[] = [
  'shell.interact',
  'tunnel.forward',
  'tunnel.socks',
]

export const isChannelVerb = (verb: string): boolean => CHANNEL_VERBS.includes(verb)

// The generic task form a verb with no schema falls back to: the raw
// argument string, nothing more.
export const DEFAULT_TASK_FORM: VerbForm = {
  title: 'Issue task',
  fields: [{ key: 'args', label: 'Arguments', kind: 'Text', placeholder: 'the argument string' }],
  build: (values) => ({ arguments: text(values.args as string) }),
}

// Reads a picked file into the SelectedFile shape: the dialog calls it on
// change and keeps the result in state until submit.
export async function readSelectedFile(file: globalThis.File): Promise<SelectedFile> {
  const bytes = new Uint8Array(await file.arrayBuffer())
  let binary = ''
  const chunk = 0x8000
  for (let i = 0; i < bytes.length; i += chunk) {
    binary += String.fromCharCode(...bytes.subarray(i, i + chunk))
  }
  return { name: file.name, base64: btoa(binary), bytes: bytes.length }
}

// The one-string-plus-inline-file serialization the file browser's upload
// uses: the shared rule over a <path> token and an inline-capable file
// parameter -- the same build a declared schema produces, offered directly
// because the browser uploads without opening a dialog.
export function buildPathUpload(path: string, file: SelectedFile): BuiltTask {
  if (file.bytes <= INLINE_FILE_LIMIT) {
    return { arguments: `${path.trim()} ${file.base64}` }
  }
  return { arguments: path.trim(), content: file.base64 }
}
