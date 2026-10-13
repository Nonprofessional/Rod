import type { ReactNode } from 'react'
import { Icon } from './Icons'

// A panel's standfirst, folded behind a question mark beside its headline.
// The sentence or two of orientation rides out on hover or keyboard focus
// instead of stacking above the content on every visit, so a panel opens at
// its headline row and nothing else. The body is real content (focusable
// host, never display:none) so assistive tech reads what sighted operators
// hover for.
export function HelpHint({ children }: { children: ReactNode }) {
  return (
    <span className="help-hint" tabIndex={0}>
      <Icon name="help" className="help-hint-icon" />
      <span className="help-hint-body">{children}</span>
    </span>
  )
}
