import type { IconName } from './components/Icons'

// The OS glyph for a reported os string: a brand mark where the platform is
// recognized, the neutral cpu elsewhere. The string is whatever the implant
// reported at enroll (Environment.OSDescription-shaped), so the match is a
// loose keyword scan, never a parse.

export function osIconFor(os: string | null | undefined): IconName {
  const value = (os ?? '').toLowerCase()
  if (value.includes('windows')) return 'osWindows'
  if (value.includes('darwin') || value.includes('mac') || value.includes('osx')) {
    return 'osApple'
  }
  if (value.includes('linux') || value.includes('fedora') || value.includes('ubuntu') || value.includes('debian')) {
    return 'osLinux'
  }
  return 'cpu'
}
