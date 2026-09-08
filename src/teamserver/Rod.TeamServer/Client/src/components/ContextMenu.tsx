import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { Icon, type IconName } from './Icons'

// The right-click menu (and its per-row three-dot trigger). One component
// serves both entry points: web right-click is an acquired habit, so every
// menu also opens from a visible button; both land on the same entries.
//
// Positioning is viewport-clamped: the menu prefers the cursor corner and
// flips inside when it would spill an edge, so a row at the screen's bottom
// still opens a usable menu.

export interface MenuItem {
  kind: 'item'
  label: string
  icon?: IconName
  danger?: boolean
  disabled?: boolean
  title?: string
  onSelect: () => void
}

export interface MenuLabel {
  kind: 'label'
  label: string
}

export interface MenuSeparator {
  kind: 'sep'
}

export type MenuEntry = MenuItem | MenuLabel | MenuSeparator

export function ContextMenu({
  x,
  y,
  entries,
  onClose,
}: {
  x: number
  y: number
  entries: MenuEntry[]
  onClose: () => void
}) {
  const ref = useRef<HTMLDivElement>(null)
  const [pos, setPos] = useState({ left: x, top: y })

  // Clamp after the first layout so the flip uses the real rendered size.
  useLayoutEffect(() => {
    const el = ref.current
    if (!el) return
    const rect = el.getBoundingClientRect()
    const left = Math.min(x, window.innerWidth - rect.width - 8)
    const top = Math.min(y, window.innerHeight - rect.height - 8)
    setPos({ left: Math.max(8, left), top: Math.max(8, top) })
  }, [x, y])

  useEffect(() => {
    const onPointerDown = (event: PointerEvent) => {
      if (ref.current && !ref.current.contains(event.target as Node)) onClose()
    }
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose()
    }
    window.addEventListener('pointerdown', onPointerDown, true)
    window.addEventListener('keydown', onKey)
    window.addEventListener('resize', onClose)
    return () => {
      window.removeEventListener('pointerdown', onPointerDown, true)
      window.removeEventListener('keydown', onKey)
      window.removeEventListener('resize', onClose)
    }
  }, [onClose])

  return (
    <div className="context-menu" style={pos} ref={ref}>
      {entries.map((entry, index) => {
        if (entry.kind === 'label') {
          return (
            <div className="context-menu-label" key={`label-${index}`}>
              {entry.label}
            </div>
          )
        }
        if (entry.kind === 'sep') {
          return <div className="context-menu-sep" key={`sep-${index}`} />
        }
        return (
          <button
            key={entry.label}
            className={`context-menu-item${entry.danger ? ' danger' : ''}`}
            title={entry.title}
            disabled={entry.disabled}
            onClick={() => {
              onClose()
              entry.onSelect()
            }}
          >
            {entry.icon && <Icon name={entry.icon} />}
            {entry.label}
          </button>
        )
      })}
    </div>
  )
}

// Owns one open menu's coordinates. `openAt` takes a mouse event (the
// contextmenu handler) or explicit viewport coordinates (the three-dot
// button's bounding box); renders nothing while closed.
export function useContextMenu(): {
  menu: { x: number; y: number } | null
  openAt: (at: { clientX: number; clientY: number } | { x: number; y: number }) => void
  close: () => void
} {
  const [at, setAt] = useState<{ x: number; y: number } | null>(null)
  return {
    menu: at,
    openAt: (point) =>
      setAt('clientX' in point ? { x: point.clientX, y: point.clientY } : point),
    close: () => setAt(null),
  }
}
