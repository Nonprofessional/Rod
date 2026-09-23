// Engagement tab definitions: the hash route's tab vocabulary plus the
// sidebar's grouping. Lives in a plain module (not the Nav component file) so
// the component file holds only components and stays fast-refresh clean.
//
// Tab ids are stable route segments (deep links keep working); labels and
// order are presentation and move with the UI.

import type { IconName } from './components/Icons'

export type TabId =
  | 'tasking'
  | 'implants'
  | 'shells'
  | 'webshells'
  | 'audit'
  | 'artifacts'
  | 'report'
  | 'listeners'
  | 'launchers'
  | 'build'
  | 'payloads'

export interface NavItemDef {
  id: TabId
  label: string
  icon: IconName
}

// Grouped the way operators think: the operating pair on top -- the
// implants table (where tasking issues from) beside the task log (the live
// feed of everything issued) -- infrastructure next, evidence last in
// reading order. The task log sits with Implants rather than under Evidence
// because it is the working log, read while operating; the immutable record
// (audit, report) stays under Evidence. The timeline's own tab retired with
// the narrative/report split it duplicated: the audit ledger is the raw
// reading surface, and the report's timeline section is the narrative
// deliverable, so a third projection of the same trail had no job left.
export const NAV_GROUPS: readonly { label: string | null; items: readonly NavItemDef[] }[] = [
  {
    label: 'Operate',
    items: [
      { id: 'implants', label: 'Implants', icon: 'cpu' },
      { id: 'shells', label: 'Shells', icon: 'terminal' },
      { id: 'webshells', label: 'Web shells', icon: 'globe' },
      { id: 'tasking', label: 'Task log', icon: 'inbox' },
    ],
  },
  {
    label: 'Infrastructure',
    items: [
      { id: 'listeners', label: 'Listeners', icon: 'radio' },
      // The one-liner delivery surface: paste-ready payload fetches beside
      // the listeners they ride and the builds they deliver.
      { id: 'launchers', label: 'Launchers', icon: 'copy' },
      { id: 'build', label: 'Build', icon: 'package' },
      { id: 'payloads', label: 'Payloads', icon: 'archive' },
    ],
  },
  {
    label: 'Evidence',
    items: [
      { id: 'audit', label: 'Audit', icon: 'list' },
      { id: 'artifacts', label: 'Artifacts', icon: 'archive' },
      { id: 'report', label: 'Report', icon: 'file' },
    ],
  },
]

// Every tab id, for route validation in the views.
export const ENGAGEMENT_TABS: readonly TabId[] = NAV_GROUPS.flatMap((g) =>
  g.items.map((i) => i.id),
)
