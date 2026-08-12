import { attributeMeta } from '../capabilities'

// Renders a capability descriptor's OPSEC attributes as labelled risk badges
// (architecture.md Sec 7). High-risk flags are red, medium amber, low neutral;
// an empty attribute set renders nothing, so low-profile verbs stay clean.

export function OpsecBadges({ attributes }: { attributes: Record<string, string> }) {
  const keys = Object.keys(attributes)
  if (keys.length === 0) return null
  return (
    <span className="opsec">
      {keys.map((key) => {
        const meta = attributeMeta(key)
        return (
          <span key={key} className={`opsec-badge risk-${meta.risk}`} title={`${key}=${attributes[key]}`}>
            {meta.label}
          </span>
        )
      })}
    </span>
  )
}
