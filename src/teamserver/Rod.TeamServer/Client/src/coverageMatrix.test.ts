// The coverage-matrix acceptance's UI half (ADR-007): the matrix view filters
// by tactic and platform over the rows GET /capabilities/coverage serves, and
// the gap rows -- the work queue -- fall out of the same status vocabulary.
// These checks run against the server's real DTO shape (camelCase over the
// coverage endpoint), with a fixture spanning the three statuses, a technique
// carried by two verbs, and an ungated (per-deployment) row.

import { describe, expect, it } from 'vitest'
import type { CoverageTechnique } from './api'
import { carrierSummary, filterCoverage, isGapRow } from './coverageMatrix'

const row = (over: Partial<CoverageTechnique> & Pick<CoverageTechnique, 'techniqueId'>): CoverageTechnique => ({
  name: over.techniqueId,
  tactics: ['discovery'],
  carriers: [
    { verb: 'recon.hostenum', status: 'gap', module: null, implantClasses: ['implant'] },
  ],
  platforms: ['linux', 'windows'],
  ...over,
})

const ROWS: CoverageTechnique[] = [
  // Two verbs share T1046; one carries a module.
  row({
    techniqueId: 'T1046',
    carriers: [
      { verb: 'recon.portscan', status: 'gap', module: null, implantClasses: ['implant'] },
      { verb: 'sweep.scan', status: 'module', module: 'sweep', implantClasses: ['implant'] },
    ],
  }),
  // A core verb, browser-reached.
  row({
    techniqueId: 'T1185',
    tactics: ['collection'],
    carriers: [
      { verb: 'browser.dom', status: 'core', module: null, implantClasses: ['browser'] },
    ],
    platforms: ['browser'],
  }),
  // Ungated: platform reach decided per deployment, so no platform set.
  row({
    techniqueId: 'T1562',
    tactics: ['defense-evasion'],
    carriers: [{ verb: 'evasion.avoid', status: 'gap', module: null, implantClasses: ['ungated'] }],
    platforms: [],
  }),
]

describe('filterCoverage', () => {
  it('passes everything through when no axis is set', () => {
    expect(filterCoverage(ROWS, null, null)).toEqual(ROWS)
  })

  it('keeps only rows under the tactic', () => {
    const rows = filterCoverage(ROWS, 'discovery', null)
    expect(rows.map((r) => r.techniqueId)).toEqual(['T1046'])
  })

  it('keeps rows a carrier reaches on the platform', () => {
    const rows = filterCoverage(ROWS, null, 'browser')
    expect(rows.map((r) => r.techniqueId)).toEqual(['T1185'])
  })

  it('composes the axes', () => {
    expect(filterCoverage(ROWS, 'collection', 'browser').map((r) => r.techniqueId)).toEqual(['T1185'])
    expect(filterCoverage(ROWS, 'discovery', 'browser')).toEqual([])
  })

  it('keeps an ungated row under its tactic but under no platform', () => {
    expect(filterCoverage(ROWS, 'defense-evasion', null).map((r) => r.techniqueId)).toEqual(['T1562'])
    expect(filterCoverage(ROWS, 'defense-evasion', 'linux')).toEqual([])
  })

  it('matches exactly, never by substring or case', () => {
    expect(filterCoverage(ROWS, 'Discovery', null)).toEqual([])
    expect(filterCoverage(ROWS, null, 'linux-x')).toEqual([])
  })
})

describe('isGapRow', () => {
  it('marks a row gap only when every carrier is a gap', () => {
    expect(isGapRow(ROWS[2])).toBe(true) // evasion.avoid, declared only
    expect(isGapRow(ROWS[1])).toBe(false) // in-tree carrier
    expect(isGapRow(ROWS[0])).toBe(false) // one of two carriers is a module
  })
})

describe('carrierSummary', () => {
  it('names the verb, its module, and the gating classes', () => {
    expect(carrierSummary(ROWS[0].carriers[1])).toBe('sweep.scan (sweep) — implant')
    expect(carrierSummary(ROWS[1].carriers[0])).toBe('browser.dom — browser')
  })
})
