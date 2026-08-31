// Engagement-scoped live state shared with the app shell. EngagementView
// owns the SSE stream and the fleet/presence queries; the shell's topbar
// renders the live indicator and presence chips from this context without
// the stream itself being lifted out of the view.

import { createContext, useContext } from 'react'
import type { LiveOperator } from './api'

export interface EngagementLive {
  // True after the SSE hello, false while the stream is reconnecting.
  connected: boolean
  operators: LiveOperator[]
  implantCount: number
  onlineCount: number
}

export const LiveContext = createContext<EngagementLive | null>(null)

export function useLive(): EngagementLive | null {
  return useContext(LiveContext)
}
