// A minimal tab bar. The active tab is held by the parent (controlled) and
// surfaced in the URL hash by the parent, so deep links keep working. Tabs are
// plain anchors so keyboard navigation and middle-click work without extra
// handling.

export interface TabDef {
  id: string
  label: string
}

export function Tabs({
  tabs,
  active,
  onSelect,
}: {
  tabs: readonly TabDef[]
  active: string
  onSelect: (id: string) => void
}) {
  return (
    <nav className="tabs">
      {tabs.map((tab) => (
        <button
          key={tab.id}
          type="button"
          className={`tab ${tab.id === active ? 'active' : ''}`}
          onClick={() => onSelect(tab.id)}
        >
          {tab.label}
        </button>
      ))}
    </nav>
  )
}
