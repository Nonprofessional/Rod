import { useCallback, useEffect, useRef, useState } from 'react'

// The one copy affordance every command block and credential shares:
// clipboard write, a "Copied" confirmation that fades after a moment, and a
// quiet no-op on denied permission (the text stays selectable to copy by
// hand). Extracted so the four hand-rolled copies of the same state machine
// cannot drift apart again.
export function CopyButton({ text, label = 'Copy' }: { text: string; label?: string }) {
  const [copied, setCopied] = useState(false)
  const fade = useRef<number | null>(null)

  useEffect(
    () => () => {
      if (fade.current != null) window.clearTimeout(fade.current)
    },
    [],
  )

  const copy = useCallback(async () => {
    try {
      await navigator.clipboard.writeText(text)
      setCopied(true)
      if (fade.current != null) window.clearTimeout(fade.current)
      fade.current = window.setTimeout(() => setCopied(false), 1500)
    } catch {
      // Clipboard permission denied: the text stays selectable to copy
      // by hand.
    }
  }, [text])

  return (
    <button className="ghost sm" onClick={() => void copy()}>
      {copied ? 'Copied' : label}
    </button>
  )
}
