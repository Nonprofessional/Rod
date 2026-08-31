// Sidebar navigation for an engagement. Tab ids are the hash route's tab
// segment, so items are plain anchors: deep links, keyboard navigation, and
// middle-click all work through the shell's hashchange listener with no
// extra handler here. Grouping lives in tabs.ts.

import { Icon } from './Icons'
import { NAV_GROUPS, type TabId } from '../tabs'

export function EngagementNav({
  engagementId,
  active,
}: {
  engagementId: string
  active: TabId
}) {
  return (
    <>
      {NAV_GROUPS.map((group) => (
        <div className="nav-group" key={group.label ?? group.items[0].id}>
          {group.label && <div className="nav-group-label">{group.label}</div>}
          {group.items.map((item) => (
            <a
              key={item.id}
              className={`nav-item${item.id === active ? ' active' : ''}`}
              href={`#/engagements/${engagementId}/${item.id}`}
            >
              <Icon name={item.icon} />
              <span className="label">{item.label}</span>
            </a>
          ))}
        </div>
      ))}
    </>
  )
}
