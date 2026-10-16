// Classification of an implant's reported os string: the platform family
// the UI groups by, and the glyph that family renders. The string is
// whatever the implant reported at enroll (Environment.OSDescription-shaped),
// so the match is a loose keyword scan, never a parse.

import type { IconName } from './components/Icons'

export type OsFamily = 'windows' | 'apple' | 'linux' | 'other'

export function osFamilyOf(os: string | null | undefined): OsFamily {
  const value = (os ?? '').toLowerCase()
  if (value.includes('windows')) return 'windows'
  if (value.includes('darwin') || value.includes('mac') || value.includes('osx')) {
    return 'apple'
  }
  if (value.includes('linux') || value.includes('fedora') || value.includes('ubuntu') || value.includes('debian')) {
    return 'linux'
  }
  return 'other'
}

const FAMILY_ICONS: Record<OsFamily, IconName> = {
  windows: 'osWindows',
  apple: 'osApple',
  linux: 'osLinux',
  other: 'cpu',
}

export function osIconFor(os: string | null | undefined): IconName {
  return FAMILY_ICONS[osFamilyOf(os)]
}
