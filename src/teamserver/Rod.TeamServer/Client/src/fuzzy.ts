// The palette's fuzzy matcher: a small scored subsequence search, written
// here rather than pulled from a dependency because the whole job is one
// walk over the target. The scoring ladder mirrors how an operator scans:
// a prefix beats a word start beats a contiguous run beats scattered
// letters, and shorter targets win ties so the shortest reach lands first.

const SEPARATORS = new Set([' ', '-', '_', '.', '/', '\\', ':'])

export interface FuzzyResult {
  score: number
}

/**
 * Scores `query` against `target`, case-insensitively. Every query letter
 * must appear in target in order (the subsequence rule); null means no
 * match. An empty query matches everything at score zero -- the palette's
 * unfiltered listing.
 */
export function fuzzyScore(query: string, target: string): number | null {
  const q = query.trim().toLowerCase()
  const t = target.toLowerCase()
  if (q === '') return 0

  let score = 0
  let from = 0
  let contiguous = 0
  for (const letter of q) {
    const at = t.indexOf(letter, from)
    if (at === -1) return null
    if (at === 0) {
      score += 100
    } else if (SEPARATORS.has(t[at - 1])) {
      score += 20
    } else if (at === from && contiguous > 0) {
      score += 8
    } else {
      score += 1
    }
    contiguous = at === from ? contiguous + 1 : 1
    from = at + 1
  }

  // The whole query as one substring outranks the same letters scattered
  // through a long identifier.
  if (t.includes(q)) score += 15
  // Prefer the shorter target among equals: the tighter name is the one
  // being reached for.
  score -= Math.min(t.length * 0.1, 20)
  return score
}

/**
 * The best score across a target's searchable fields (an implant matches
 * by hostname, user, or id). Null when no field matches.
 */
export function fuzzyBest(query: string, targets: readonly string[]): number | null {
  let best: number | null = null
  for (const target of targets) {
    const score = fuzzyScore(query, target)
    if (score !== null && (best === null || score > best)) best = score
  }
  return best
}
