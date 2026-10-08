// Engagement-scoped live state shared with the app shell. EngagementView
// owns the SSE stream and the fleet/presence queries; the shell's topbar
// renders the live indicator and presence chips from this context without
// the stream itself being lifted out of the view.

import { createContext, useContext } from 'react'
import type { ClaimSummary, DrivingEntry, LiveOperator } from './api'

export interface EngagementLive {
  // True after the SSE hello, false while the stream is reconnecting.
  connected: boolean
  operators: LiveOperator[]
  implantCount: number
  onlineCount: number
  // This console's operator id: claim holders compare against it to know
  // whether a held surface is theirs or a peer's.
  operatorId: string
  // The engagement's held interaction claims (architecture.md Sec 4.5) --
  // seeded by the hello frame, kept current by the claim events.
  claims: ClaimSummary[]
  // Who is driving which implant (activity presence), same seed-and-events
  // lifecycle as the claims.
  driving: DrivingEntry[]
}

export const LiveContext = createContext<EngagementLive | null>(null)

export function useLive(): EngagementLive | null {
  return useContext(LiveContext)
}
