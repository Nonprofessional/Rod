// Engagement tab definitions: the hash route's tab vocabulary plus the
// sidebar's grouping. Lives in a plain module (not the Nav component file) so
// the component file holds only components and stays fast-refresh clean.
//
// Tab ids are stable route segments (deep links keep working); labels and
// order are presentation and move with the UI.

import type { IconName } from './components/Icons'

export type TabId =
  | 'members'
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
  | 'campaigns'
  | 'build'
  | 'payloads'

export interface NavItemDef {
  id: TabId
  label: string
  icon: IconName
}

// Grouped by what a surface acts on, in the order an engagement lives it.
// The target group opens the sidebar because the engagement starts there
// (architecture.md Sec 11): Intel leads as the read operators check in on
// -- the picture of what is known -- with the recon workbench behind it
// as the tool that fills the picture. The group is named for the target
// rather than the discipline so it does not repeat the Intel tab it leads
// with. Operate holds
// the surfaces that act on the target: the implants table (where tasking
// issues from) beside the task log (the live feed of everything issued),
// with the rule engine that fires the same task verbs unattended.
// Infrastructure sits in delivery order -- the listeners, the one-liner
// and lure surfaces that ride them, then the builds those deliver. The
// engagement's own bookkeeping closes the sidebar: the crew roster, the
// out-of-band channels, and the immutable record all tend the engagement
// rather than the target, so they belong together at the bottom, not
// scattered into the operating group.
//
// The task log sits with Implants rather than under the record because it
// is the working log, read while operating; the immutable record (audit,
// report) stays last in reading order. Two feed-shaped tabs retired the
// same way the timeline did -- as extra projections of surfaces that
// already render them: the handoff digest is the Audit panel's window mode
// (the resume question is a query over the ledger), and task artifacts
// surface in the task log's expanded rows, where the task they document
// already lives.
export const NAV_GROUPS: readonly { label: string | null; items: readonly NavItemDef[] }[] = [
  {
    label: 'Target',
    items: [
      // The target picture (architecture.md Sec 11.2): hosts, loot, and the
      // topology -- what the engagement learned, not what it did. The read
      // comes first: operators open it to check in on the picture, then
      // reach for the workbench to extend it.
      { id: 'intel', label: 'Intel', icon: 'target' },
      // The pre-foothold scoping surface (architecture.md Sec 11.4): the
      // external recon workbench, behind Intel -- the read projection it
      // fills.
      { id: 'recon', label: 'Recon', icon: 'search' },
    ],
  },
  {
    label: 'Operate',
    items: [
      { id: 'implants', label: 'Implants', icon: 'cpu' },
      { id: 'shells', label: 'Shells', icon: 'terminal' },
      { id: 'webshells', label: 'Web shells', icon: 'globe' },
      { id: 'tasking', label: 'Task log', icon: 'inbox' },
      // Declarative tasking: the rules fire task verbs while no operator
      // watches, so they round out the manual operating surfaces.
      { id: 'automation', label: 'Automation', icon: 'clock' },
    ],
  },
  {
    label: 'Infrastructure',
    items: [
      { id: 'listeners', label: 'Listeners', icon: 'radio' },
      // The one-liner delivery surface: paste-ready payload fetches beside
      // the listeners they ride and the builds they deliver.
      { id: 'launchers', label: 'Launchers', icon: 'copy' },
      // The tracked-lure delivery surface (architecture.md Sec 11.5): the
      // email-shaped half of delivery, beside the builds each recipient
      // gets and the listeners that front every lure.
      { id: 'campaigns', label: 'Campaigns', icon: 'send' },
      { id: 'build', label: 'Build', icon: 'package' },
      { id: 'payloads', label: 'Payloads', icon: 'archive' },
    ],
  },
  {
    // The engagement tending itself: who holds access (Sec 3, the
    // membership model), which out-of-band channels carry its events, and
    // the immutable record of what it did.
    label: 'Engagement',
    items: [
      { id: 'members', label: 'Members', icon: 'users' },
      { id: 'notifications', label: 'Notifications', icon: 'bell' },
      { id: 'audit', label: 'Audit', icon: 'list' },
      { id: 'report', label: 'Report', icon: 'file' },
    ],
  },
]

// Every tab id, for route validation in the views.
export const ENGAGEMENT_TABS: readonly TabId[] = NAV_GROUPS.flatMap((g) =>
  g.items.map((i) => i.id),
)
