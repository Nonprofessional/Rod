// Inline stroke icons (24x24 grid, currentColor) so the UI needs no icon
// package and no network fetch: an operator console may run fully offline.
// Icons are sized by their container (.nav-icon by default).

const ICONS = {
  terminal: (
    <>
      <polyline points="4 17 10 11 4 5" />
      <line x1="12" y1="19" x2="20" y2="19" />
    </>
  ),
  cpu: (
    <>
      <rect x="4.5" y="4.5" width="15" height="15" rx="2" />
      <rect x="9.5" y="9.5" width="5" height="5" rx="1" />
      <line x1="9" y1="2" x2="9" y2="4.5" />
      <line x1="15" y1="2" x2="15" y2="4.5" />
      <line x1="9" y1="19.5" x2="9" y2="22" />
      <line x1="15" y1="19.5" x2="15" y2="22" />
      <line x1="2" y1="9" x2="4.5" y2="9" />
      <line x1="2" y1="15" x2="4.5" y2="15" />
      <line x1="19.5" y1="9" x2="22" y2="9" />
      <line x1="19.5" y1="15" x2="22" y2="15" />
    </>
  ),
  radio: (
    <>
      <circle cx="12" cy="12" r="1.8" />
      <path d="M8.5 8.5a5 5 0 0 0 0 7" />
      <path d="M15.5 8.5a5 5 0 0 1 0 7" />
      <path d="M5.6 5.6a9 9 0 0 0 0 12.8" />
      <path d="M18.4 5.6a9 9 0 0 1 0 12.8" />
    </>
  ),
  package: (
    <>
      <path d="M21 8 12 3 3 8v8l9 5 9-5V8Z" />
      <path d="M3.5 8.3 12 13l8.5-4.7" />
      <line x1="12" y1="13" x2="12" y2="21.5" />
    </>
  ),
  list: (
    <>
      <line x1="9" y1="6" x2="21" y2="6" />
      <line x1="9" y1="12" x2="21" y2="12" />
      <line x1="9" y1="18" x2="21" y2="18" />
      <line x1="4" y1="6" x2="4.01" y2="6" />
      <line x1="4" y1="12" x2="4.01" y2="12" />
      <line x1="4" y1="18" x2="4.01" y2="18" />
    </>
  ),
  archive: (
    <>
      <rect x="2.5" y="3" width="19" height="5" rx="1" />
      <path d="M5 8v10a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8" />
      <line x1="10" y1="12" x2="14" y2="12" />
    </>
  ),
  clock: (
    <>
      <circle cx="12" cy="12" r="9" />
      <polyline points="12 7 12 12 15.5 13.5" />
    </>
  ),
  file: (
    <>
      <path d="M14.5 2.5H6.5a2 2 0 0 0-2 2v15a2 2 0 0 0 2 2h11a2 2 0 0 0 2-2V7.5l-5-5Z" />
      <polyline points="14 2.5 14 8 19.5 8" />
      <line x1="8.5" y1="13" x2="15.5" y2="13" />
      <line x1="8.5" y1="17" x2="15.5" y2="17" />
    </>
  ),
  globe: (
    <>
      <circle cx="12" cy="12" r="9" />
      <line x1="3" y1="12" x2="21" y2="12" />
      <path d="M12 3a14 14 0 0 1 3.5 9A14 14 0 0 1 12 21a14 14 0 0 1-3.5-9A14 14 0 0 1 12 3Z" />
    </>
  ),
  users: (
    <>
      <path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2" />
      <circle cx="9" cy="7" r="4" />
      <path d="M22 21v-2a4 4 0 0 0-3-3.87" />
      <path d="M16 3.13a4 4 0 0 1 0 7.75" />
    </>
  ),
  logout: (
    <>
      <path d="M9 21H6a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h3" />
      <polyline points="16 17 21 12 16 7" />
      <line x1="21" y1="12" x2="9" y2="12" />
    </>
  ),
  chevronRight: <polyline points="9 18 15 12 9 6" />,
  chevronDown: <polyline points="6 9 12 15 18 9" />,
  edit: (
    <>
      <path d="M12 20h9" />
      <path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z" />
    </>
  ),
  check: <polyline points="20 6 9 17 4 12" />,
  // OS brand marks, simplified to the icon language (24x24, stroke). They
  // decorate device group headers and the console title bar; the reported OS
  // string rides beside them for the exact version.
  osWindows: (
    <>
      <path d="M3 5.6 10.6 4.5v7H3Z" />
      <path d="M11.6 4.3 21 3v8.5h-9.4Z" />
      <path d="M3 12.5h7.6v7L3 18.4Z" />
      <path d="M11.6 12.5H21V21l-9.4-1.3Z" />
    </>
  ),
  osApple: (
    <>
      <path d="M12 7.6c1-1.8 3.4-2.1 4.7-.4 1.3 1.7.9 4.7-.4 6.7-.7 1.1-1.5 2.2-2.6 2.2-.6 0-1-.4-1.7-.4s-1.1.4-1.7.4c-1.1 0-1.9-1.1-2.6-2.2-1.3-2-1.7-5-.4-6.7 1.3-1.7 3.7-1.4 4.7.4Z" />
      <path d="M12 7.4c-.2-1.7 1-3.2 2.6-3.4.2 1.7-1 3.2-2.6 3.4Z" />
    </>
  ),
  osLinux: (
    <>
      <path d="M12 3c1.9 0 3.1 1.6 3.1 3.4 0 1.4-.5 2.4-.5 3.5 0 2.1 3 3.4 3 6.6 0 2.5-2.1 4.5-5.6 4.5S6.4 19 6.4 16.5c0-3.2 3-4.5 3-6.6 0-1.1-.5-2.1-.5-3.5C8.9 4.6 10.1 3 12 3Z" />
      <circle cx="10.6" cy="6.8" r="0.2" />
      <circle cx="13.4" cy="6.8" r="0.2" />
      <path d="m11.2 8.4.8.8.8-.8" />
    </>
  ),
  x: (
    <>
      <line x1="18" y1="6" x2="6" y2="18" />
      <line x1="6" y1="6" x2="18" y2="18" />
    </>
  ),
  more: (
    <>
      <circle cx="12" cy="5" r="1.4" />
      <circle cx="12" cy="12" r="1.4" />
      <circle cx="12" cy="19" r="1.4" />
    </>
  ),
  folder: (
    <>
      <path d="M3 7a2 2 0 0 1 2-2h4l2 2.5h8a2 2 0 0 1 2 2V18a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7Z" />
    </>
  ),
  activity: (
    <>
      <polyline points="22 12 18 12 15 21 9 3 6 12 2 12" />
    </>
  ),
  copy: (
    <>
      <rect x="9" y="9" width="12" height="12" rx="2" />
      <path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1" />
    </>
  ),
  refresh: (
    <>
      <path d="M21 12a9 9 0 1 1-2.64-6.36" />
      <polyline points="21 3 21 8.5 15.5 8.5" />
    </>
  ),
  inbox: (
    <>
      <polyline points="22 12 16.5 12 14.5 15 9.5 15 7.5 12 2 12" />
      <path d="M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11Z" />
    </>
  ),
  settings: (
    <>
      <line x1="4" y1="7" x2="20" y2="7" />
      <circle cx="9.5" cy="7" r="2.2" />
      <line x1="4" y1="12" x2="20" y2="12" />
      <circle cx="14.5" cy="12" r="2.2" />
      <line x1="4" y1="17" x2="20" y2="17" />
      <circle cx="7.5" cy="17" r="2.2" />
    </>
  ),
} as const

export type IconName = keyof typeof ICONS

export function Icon({ name, className }: { name: IconName; className?: string }) {
  return (
    <svg
      className={className ?? 'nav-icon'}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      {ICONS[name]}
    </svg>
  )
}
