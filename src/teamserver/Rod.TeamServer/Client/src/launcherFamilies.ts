// The downloader families a render answers with, in operator language:
// what each one-liner does on the target, so the list explains itself
// instead of naming shell tools and hoping. Shared by the Launchers tab's
// kept rows and the shell console's upgrade panel.
const FAMILY_HINTS: Record<string, string> = {
  'unix-curl':
    'Downloads the payload with curl to /tmp, marks it executable, runs it -- a file lands and stays until the target cleans it.',
  'unix-wget':
    'Downloads the payload with wget to /tmp, marks it executable, runs it -- a file lands and stays until the target cleans it.',
  'windows-powershell':
    "Downloads the payload with PowerShell's iwr into the temp directory and runs it -- a file lands (Windows targets).",
  'unix-python-memfd':
    'Fileless: python3 fetches the bytes into a memory-backed fd (memfd) and execs them through /proc/self/fd -- nothing is written to disk, so no file to find afterwards. Needs python3 on the target (Linux).',
}

export function launcherHint(id: string): string | undefined {
  return FAMILY_HINTS[id]
}
