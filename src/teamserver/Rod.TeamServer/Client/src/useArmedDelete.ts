import { useEffect, useRef, useState } from 'react'

// The two-click delete guard shared by the panels with destructive rows
// (listeners, automation rules): the first click arms the button into a
// "Confirm delete" that auto-reverts after a few seconds, so an accidental
// click never deletes anything; the armed click runs the caller's action.
// The revert timer is cleared on unmount so a slow expiry never sets state
// on a gone panel.
export function useArmedDelete(): [
  armed: string | null,
  arm: (id: string, onConfirm: () => void) => void,
] {
  const [armed, setArmed] = useState<string | null>(null)
  const timer = useRef<number | null>(null)

  useEffect(
    () => () => {
      if (timer.current !== null) window.clearTimeout(timer.current)
    },
    [],
  )

  const arm = (id: string, onConfirm: () => void) => {
    if (armed === id) {
      if (timer.current !== null) window.clearTimeout(timer.current)
      setArmed(null)
      onConfirm()
      return
    }
    setArmed(id)
    timer.current = window.setTimeout(() => setArmed(null), 4000)
  }

  return [armed, arm]
}
