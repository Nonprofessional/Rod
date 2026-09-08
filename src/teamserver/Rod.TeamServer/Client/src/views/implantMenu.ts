import type { Implant } from '../api'
import type { MenuEntry, MenuItem } from '../components/ContextMenu'

// The per-implant context menu shared by the fleet table and the session
// console: the capability verbs an operator reaches for, grouped the way the
// categories already group them, each entry gated on the implant's class.
// Every item carries a hover title that says what it does -- the menu is
// scanned, not studied, so the label names the action and the title carries
// the one sentence behind it. (Labels deliberately end without an ellipsis;
// the desktop "opens a dialog" dot convention read as clipping here.)

// The class verb table mirrored client-side for menu gating. The server's
// ImplantClassCapabilities (the issuance gate) is the authority; this mirror
// only decides what the menu offers, exactly like the channel-verb mirror --
// the server still refuses anything this table gets wrong.
const CLASS_VERBS: Record<string, readonly string[]> = {
  Stage2: [
    'shell.exec', 'shell.interact', 'file.push', 'file.pull', 'fs.list', 'proc.kill',
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
  onFiles: () => void
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
    {
      kind: 'item',
      label: 'Interact',
      icon: 'terminal',
      title: 'Open this implant\'s session console',
      onSelect: actions.onInteract,
    },
    { kind: 'sep' },
    { kind: 'label', label: 'Shell' },
    push(
      {
        kind: 'item',
        label: 'Run command',
        icon: 'terminal',
        title: 'shell.exec -- run one command through the system shell and return its output',
        onSelect: () => actions.onDialog('shell.exec'),
      },
      'shell.exec',
    ),
    push(
      {
        kind: 'item',
        label: 'Interactive shell',
        icon: 'terminal',
        title: 'shell.interact -- a live typing channel held open over the check-in stream',
        onSelect: actions.onInteract,
      },
      'shell.interact',
    ),
    { kind: 'label', label: 'Files' },
    push(
      {
        kind: 'item',
        label: 'Browse files',
        icon: 'folder',
        title: 'fs.list -- walk the target\'s filesystem; cached between visits, refreshed on demand',
        onSelect: actions.onFiles,
      },
      'fs.list',
    ),
    push(
      {
        kind: 'item',
        label: 'Upload file',
        icon: 'package',
        title: 'file.push -- push a local file to a target path',
        onSelect: () => actions.onDialog('file.push'),
      },
      'file.push',
    ),
    push(
      {
        kind: 'item',
        label: 'Download file',
        icon: 'package',
        title: 'file.pull -- pull a target file back (inline when small, else to the artifact store)',
        onSelect: () => actions.onDialog('file.pull'),
      },
      'file.pull',
    ),
    { kind: 'label', label: 'Processes' },
    push(
      {
        kind: 'item',
        label: 'Browse processes',
        icon: 'list',
        title: 'recon.ps -- list the host\'s processes; cached between visits, refreshed on demand',
        onSelect: actions.onProcesses,
      },
      'recon.ps',
    ),
    push(
      {
        kind: 'item',
        label: 'Kill pid',
        icon: 'list',
        danger: true,
        title: 'proc.kill -- terminate one process by pid (observable, irreversible)',
        onSelect: () => actions.onDialog('proc.kill'),
      },
      'proc.kill',
    ),
    { kind: 'label', label: 'Recon' },
    push(
      {
        kind: 'item',
        label: 'Host facts',
        icon: 'cpu',
        title: 'recon.hostenum -- local host facts (os, users, network) in one dump',
        onSelect: () => actions.onIssue('recon.hostenum'),
      },
      'recon.hostenum',
    ),
    push(
      {
        kind: 'item',
        label: 'Port scan',
        icon: 'globe',
        title: 'recon.portscan -- sweep a host\'s port range',
        onSelect: () => actions.onDialog('recon.portscan'),
      },
      'recon.portscan',
    ),
    push(
      {
        kind: 'item',
        label: 'Probe services',
        icon: 'globe',
        title: 'recon.service -- fingerprint the services behind specific ports',
        onSelect: () => actions.onDialog('recon.service'),
      },
      'recon.service',
    ),
    { kind: 'label', label: 'Collect' },
    push(
      {
        kind: 'item',
        label: 'Screenshot',
        icon: 'file',
        title: 'collect.screenshot -- capture the target\'s display',
        onSelect: () => actions.onIssue('collect.screenshot'),
      },
      'collect.screenshot',
    ),
    push(
      {
        kind: 'item',
        label: 'Credentials',
        icon: 'users',
        title: 'collect.cred -- harvest stored credentials',
        onSelect: () => actions.onDialog('collect.cred'),
      },
      'collect.cred',
    ),
    { kind: 'label', label: 'Persistence' },
    push(
      {
        kind: 'item',
        label: 'List',
        icon: 'list',
        title: 'persist.list -- the persistence mechanisms this implant installed',
        onSelect: () => actions.onIssue('persist.list'),
      },
      'persist.list',
    ),
    push(
      {
        kind: 'item',
        label: 'Install',
        icon: 'clock',
        title: 'persist.install -- install a persistence mechanism',
        onSelect: () => actions.onDialog('persist.install'),
      },
      'persist.install',
    ),
    push(
      {
        kind: 'item',
        label: 'Remove',
        icon: 'clock',
        title: 'persist.remove -- remove an installed persistence mechanism',
        onSelect: () => actions.onDialog('persist.remove'),
      },
      'persist.remove',
    ),
  ]
  if (actions.onNotes || (actions.onRetire && !implant.retiredAt)) {
    entries.push({ kind: 'sep' })
    if (actions.onNotes) {
      entries.push({
        kind: 'item',
        label: 'Notes',
        icon: 'list',
        title: 'Free-text notes on this implant, attributed and durable',
        onSelect: actions.onNotes,
      })
    }
    if (actions.onRetire && !implant.retiredAt) {
      entries.push({
        kind: 'item',
        label: 'Retire (burn)',
        icon: 'logout',
        danger: true,
        title: 'Take the implant out of operation: refused at handshake, untaskable afterwards',
        onSelect: actions.onRetire,
      })
    }
  }
  return entries
}
