import { useEffect, useRef } from 'react'
import { releaseClaim } from './api'
import { useLive } from './shell'

// The claim-aware input gate the interact panes share (architecture.md
// Sec 4.5): reads the engagement's live claims to know who holds the
// surface, resolves the holder's handle off the roster, and releases a
// claim this console holds when the pane unmounts -- dismissing a pane is
// letting go of the shell.
export function useClaim(engagementId: string, kind: 'channel' | 'shell', surfaceId: string) {
  const live = useLive()
  const held = live?.claims.find((c) => c.kind === kind && c.surfaceId === surfaceId) ?? null
  const mine = live?.operatorId ?? ''
  const locked = held != null && held.operatorId !== mine

  const holderHandle = held
    ? live?.operators.find((o) => o.id === held.operatorId)?.handle ?? held.operatorId.slice(0, 8)
    : null

  // Whether this console holds the claim right now -- tracked in a ref so the
  // unmount cleanup below reads the latest answer without re-subscribing.
  const heldByMe = held != null && held.operatorId === mine
  const heldByMeRef = useRef(false)
  useEffect(() => {
    heldByMeRef.current = heldByMe
  }, [heldByMe])

  useEffect(() => {
    return () => {
      // Release only a claim this operator holds, and only for the surface
      // this pane is tearing down. A 404 back (already released by the
      // server -- the surface ended, the stream dropped) is fine.
      if (heldByMeRef.current) void releaseClaim(engagementId, kind, surfaceId)
    }
  }, [engagementId, kind, surfaceId])

  return { locked, holderHandle, heldByMe }
}
