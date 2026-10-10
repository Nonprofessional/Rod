// Engagement tab definitions: the hash route's tab vocabulary plus the
// sidebar's grouping. Lives in a plain module (not the Nav component file) so
// the component file holds only components and stays fast-refresh clean.
//
// Tab ids are stable route segments (deep links keep working); labels and
// order are presentation and move with the UI.

import type { IconName } from './components/Icons'

export type TabId =
  | 'tasking'
  | 'automation'
  | 'notifications'
  | 'implants'
  | 'shells'
  | 'webshells'
  | 'audit'
  | 'report'
  | 'recon'
  | 'intel'
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
// (audit, report) stays under Evidence. Two feed-shaped tabs retired the
// same way the timeline did -- as extra projections of surfaces that
// already render them: the handoff digest is the Audit panel's window mode
// (the resume question is a query over the ledger), and task artifacts
// surface in the task log's expanded rows, where the task they document
// already lives.
export const NAV_GROUPS: readonly { label: string | null; items: readonly NavItemDef[] }[] = [
  {
    label: 'Operate',
    items: [
      { id: 'implants', label: 'Implants', icon: 'cpu' },
      { id: 'shells', label: 'Shells', icon: 'terminal' },
      { id: 'webshells', label: 'Web shells', icon: 'globe' },
      { id: 'tasking', label: 'Task log', icon: 'inbox' },
      { id: 'automation', label: 'Automation', icon: 'clock' },
      { id: 'notifications', label: 'Notifications', icon: 'bell' },
      // The pre-foothold scoping surface (architecture.md Sec 11.4): the
      // external recon workbench, which runs before the target picture it
      // feeds -- so it sits ahead of Intel, the read projection it fills.
      { id: 'recon', label: 'Recon', icon: 'search' },
      // The target picture (architecture.md Sec 11.2): hosts, loot, and the
      // topology -- what the engagement learned, not what it did.
      { id: 'intel', label: 'Intel', icon: 'target' },
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
      { id: 'report', label: 'Report', icon: 'file' },
    ],
  },
]

// Every tab id, for route validation in the views.
export const ENGAGEMENT_TABS: readonly TabId[] = NAV_GROUPS.flatMap((g) =>
  g.items.map((i) => i.id),
)
