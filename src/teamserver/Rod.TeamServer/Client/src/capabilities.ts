// Capability-catalog helpers (roadmap M11.1). The operator UI groups verbs by
// category and surfaces each verb's OPSEC attributes as risk badges. The catalog
// is data-driven from GET /capabilities (the registry), so this module only
// knows the category/attribute *labels* -- the verbs themselves arrive from the
// server, never hardcoded here.

import { type CapabilityDescriptor, listCapabilities } from './api'

// Canonical category order for display: core baseline first, then the offensive
// lifecycle (recon -> lateral -> persist -> collect -> exfil), then the two
// sensitive contract categories last. Categories returned by the server but not
// listed here fall through to the end in their natural order.
export const CATEGORY_ORDER: readonly string[] = [
  'Core',
  'Recon',
  'Lateral',
  'Persist',
  'Collect',
  'Exfil',
  'Evasion',
  'Exploit',
]

export function categoryRank(category: string): number {
  const index = CATEGORY_ORDER.indexOf(category)
  return index === -1 ? CATEGORY_ORDER.length : index
}

// A short human label for each category, shown as the group heading in the
// capability picker. Falls back to the raw enum name for anything unexpected.
const CATEGORY_LABELS: Record<string, string> = {
  Core: 'Core',
  Recon: 'Reconnaissance',
  Lateral: 'Lateral movement',
  Persist: 'Persistence',
  Collect: 'Collection',
  Exfil: 'Exfiltration',
  Evasion: 'Evasion (contract)',
  Exploit: 'Exploit (contract)',
}

export function categoryLabel(category: string): string {
  return CATEGORY_LABELS[category] ?? category
}

export interface CapabilityGroup {
  category: string
  label: string
  descriptors: CapabilityDescriptor[]
}

// Groups the catalog into ordered categories for the picker. Stable within a
// group by verb (alphabetical) so the list does not reshuffle on re-fetch.
export function groupByCategory(descriptors: CapabilityDescriptor[]): CapabilityGroup[] {
  const buckets = new Map<string, CapabilityDescriptor[]>()
  for (const d of descriptors) {
    const list = buckets.get(d.category) ?? []
    list.push(d)
    buckets.set(d.category, list)
  }
  for (const list of buckets.values()) {
    list.sort((a, b) => a.verb.localeCompare(b.verb))
  }
  return [...buckets.entries()]
    .sort((a, b) => categoryRank(a[0]) - categoryRank(b[0]))
    .map(([category, items]) => ({ category, label: categoryLabel(category), descriptors: items }))
}

// OPSEC attribute metadata. The server sets free-form key/value flags on each
// descriptor (architecture.md Sec 7); this maps the known flags to a short label
// and a severity, so the picker can badge risky actions consistently. Unknown
// attributes still render, using the raw key.
export interface OpsecAttributeMeta {
  key: string
  label: string
  risk: 'low' | 'medium' | 'high'
}

const KNOWN_ATTRIBUTES: Record<string, OpsecAttributeMeta> = {
  'touches-network': { key: 'touches-network', label: 'network', risk: 'medium' },
  'derives-child': { key: 'derives-child', label: 'child implant', risk: 'high' },
  'touches-credential': { key: 'touches-credential', label: 'credential', risk: 'high' },
  'writes-to-disk': { key: 'writes-to-disk', label: 'writes disk', risk: 'medium' },
  'persists': { key: 'persists', label: 'persists', risk: 'high' },
  'reads-filesystem': { key: 'reads-filesystem', label: 'reads fs', risk: 'low' },
  'reads-credential': { key: 'reads-credential', label: 'reads cred', risk: 'high' },
  'reads-input': { key: 'reads-input', label: 'reads input', risk: 'high' },
  'modifies-defenses': { key: 'modifies-defenses', label: 'modifies defenses', risk: 'high' },
  'exploits-target': { key: 'exploits-target', label: 'exploits target', risk: 'high' },
}

export function attributeMeta(key: string): OpsecAttributeMeta {
  return KNOWN_ATTRIBUTES[key] ?? { key, label: key, risk: 'low' }
}

// Loads and groups the catalog in one call; the views consume the grouped shape.
export async function loadCapabilityGroups(): Promise<CapabilityGroup[]> {
  return groupByCategory(await listCapabilities())
}
