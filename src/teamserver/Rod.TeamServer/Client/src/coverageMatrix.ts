// Coverage-matrix helpers (ADR-007): the pure filtering and labeling the
// coverage view renders from. The matrix arrives unfiltered from
// GET /capabilities/coverage -- rows plus the tactic and platform facets --
// and the view filters client-side so switching an axis never refetches.

import type { CoverageCarrier, CoverageTechnique } from './api'

// Keeps a row when its technique falls under the tactic (null = all tactics)
// and one of its carriers reaches the platform (null = all platforms).
// Platform matching is on the row's platform set, which the server derives
// from the carriers' class surfaces -- so a technique carried by both an
// implant verb and a browser verb stays visible under either platform.
export function filterCoverage(
  rows: readonly CoverageTechnique[],
  tactic: string | null,
  platform: string | null,
): CoverageTechnique[] {
  return rows.filter(
    (row) =>
      (tactic === null || row.tactics.some((t) => t === tactic)) &&
      (platform === null || row.platforms.some((p) => p === platform)),
  )
}

// The gap rows read as the work queue (ADR-007): a technique whose every
// carrier is a declared-but-unimplemented verb.
export function isGapRow(row: CoverageTechnique): boolean {
  return row.carriers.length > 0 && row.carriers.every((c) => c.status === 'gap')
}

export type CoverageStatus = CoverageCarrier['status']

// Status presentation: the label the matrix shows and the task-status pill
// class it reuses -- green for what the deployment carries, amber for a
// module's contribution, red for the open work item.
export const STATUS_META: Record<CoverageStatus, { label: string; pill: string }> = {
  core: { label: 'in-tree', pill: 'status completed' },
  module: { label: 'module', pill: 'status dispatched' },
  gap: { label: 'gap', pill: 'status failed' },
}

// A carrier's one-line summary: the verb, its module when one backs it, and
// the implant classes whose reduced set gates it.
export function carrierSummary(carrier: CoverageCarrier): string {
  const behind = carrier.module ? ` (${carrier.module})` : ''
  const classes = carrier.implantClasses.join(', ')
  return `${carrier.verb}${behind} — ${classes}`
}
