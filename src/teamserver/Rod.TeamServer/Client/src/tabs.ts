// Engagement tab definitions: the hash route's tab vocabulary plus the
// sidebar's grouping. Lives in a plain module (not the Nav component file) so
// the component file holds only components and stays fast-refresh clean.

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

// Grouped the way operators think: operating the fleet first, infrastructure
// second, evidence last.
export const NAV_GROUPS: readonly { label: string | null; items: readonly NavItemDef[] }[] = [
  {
    label: null,
    items: [
      { id: 'tasking', label: 'Tasking', icon: 'terminal' },
      { id: 'implants', label: 'Implants', icon: 'cpu' },
    ],
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
      { id: 'artifacts', label: 'Artifacts', icon: 'archive' },
      { id: 'timeline', label: 'Timeline', icon: 'clock' },
      { id: 'report', label: 'Report', icon: 'file' },
    ],
  },
]

// Every tab id, for route validation in the views.
export const ENGAGEMENT_TABS: readonly TabId[] = NAV_GROUPS.flatMap((g) =>
  g.items.map((i) => i.id),
)
