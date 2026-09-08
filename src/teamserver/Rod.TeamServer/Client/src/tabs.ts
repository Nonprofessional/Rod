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
  | 'audit'
  | 'artifacts'
  | 'timeline'
  | 'report'
  | 'listeners'
  | 'build'
  | 'payloads'

export interface NavItemDef {
  id: TabId
  label: string
  icon: IconName
}

// Grouped the way operators think: the implants tab is the operating surface
// (issuing happens in the implant's context menu and session console, not in
// a tab of its own); infrastructure next; evidence last in reading order --
// the task log joins it because it is read during and after the work, while
// its Cancel action stays available wherever its rows are.
export const NAV_GROUPS: readonly { label: string | null; items: readonly NavItemDef[] }[] = [
  {
    label: null,
    items: [{ id: 'implants', label: 'Implants', icon: 'cpu' }],
  },
  {
    label: 'Infrastructure',
    items: [
      { id: 'listeners', label: 'Listeners', icon: 'radio' },
      { id: 'build', label: 'Build', icon: 'package' },
      { id: 'payloads', label: 'Payloads', icon: 'archive' },
    ],
  },
  {
    label: 'Evidence',
    items: [
      { id: 'audit', label: 'Audit', icon: 'list' },
      { id: 'tasking', label: 'Task log', icon: 'inbox' },
      { id: 'timeline', label: 'Timeline', icon: 'clock' },
      { id: 'artifacts', label: 'Artifacts', icon: 'archive' },
      { id: 'report', label: 'Report', icon: 'file' },
    ],
  },
]

// Every tab id, for route validation in the views.
export const ENGAGEMENT_TABS: readonly TabId[] = NAV_GROUPS.flatMap((g) =>
  g.items.map((i) => i.id),
)
