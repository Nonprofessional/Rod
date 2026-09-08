import { useState } from 'react'

// The state side of the context menu, in its own module so the component
// file holds only components and stays fast-refresh clean (the same discipline
// as tabs.ts).

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
