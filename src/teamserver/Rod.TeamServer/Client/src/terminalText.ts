// Terminal byte-stream rendering for the caught-shell transcript. A shell
// under a pseudo-terminal emits its prompt and programs' output as terminal
// control data: SGR colors, operating-system commands (a title set, the
// shell-integration markers some targets configure --OSC 133-style-- that
// carry command metadata), and cursor moves. Dumped raw into a <pre>, the
// transcript reads as noise; this module reduces the stream to styled lines
// a plain markup tree can render.
//
// The scope is deliberately a subset: everything visible is kept (text,
// colors, bold/dim/underline), and the invisible is consumed -- OSC strings
// whole, non-SGR CSI sequences whole, lone escapes. Cursor motion inside a
// line is honored where it is cheap and common (carriage return, backspace,
// tab stops); anything fancier (absolute positioning, erase-below) has no
// honest rendering in a scrolling transcript and is dropped. The parser runs
// over the whole transcript each time (re-mounted chunks re-parse; sessions
// are console-sized), so no state survives across calls and a sequence split
// across read chunks still resolves once both halves have arrived.

// One styled run of text inside a line: the style is the SGR state at write
// time, expressed as CSS-ready values.
export interface TerminalSpan {
  text: string
  color?: string
  bold?: boolean
  dim?: boolean
  underline?: boolean
}

export interface TerminalLine {
  spans: TerminalSpan[]
}

// The SGR state at write time; identical to a span minus the text.
interface SgrStyle {
  color?: string
  bold?: boolean
  dim?: boolean
  underline?: boolean
}

interface Cell {
  ch: string
  style: SgrStyle | null
}

// The xterm palette for SGR 30-37/90-97, indexed by (color - 30) with the
// bright row offset by 8. Hex keeps the render off palette assumptions in
// CSS; a themed console can map these further.
const XTERM_COLORS: readonly string[] = [
  '#000000', '#800000', '#008000', '#808000', '#000080', '#800080', '#008080', '#c0c0c0',
  '#808080', '#ff0000', '#00ff00', '#ffff00', '#0000ff', '#ff00ff', '#00ffff', '#ffffff',
]

// The 256-color palette's 6x6x6 cube and grayscale tail, for SGR 38;5;n.
function color256(n: number): string {
  if (n < 16) return XTERM_COLORS[n]
  if (n < 232) {
    const level = (v: number) => (v === 0 ? 0 : 55 + v * 40)
    const r = Math.floor((n - 16) / 36)
    const g = Math.floor(((n - 16) % 36) / 6)
    const b = (n - 16) % 6
    const hex = (v: number) => level(v).toString(16).padStart(2, '0')
    return `#${hex(r)}${hex(g)}${hex(b)}`
  }
  const v = (8 + (n - 232) * 10).toString(16).padStart(2, '0')
  return `#${v}${v}${v}`
}

// Parses a transcript's raw bytes into styled lines. Empty input parses to a
// single empty line (the caller renders its own placeholder).
export function parseTerminalOutput(raw: string): TerminalLine[] {
  const lines: Cell[][] = [[]]
  let head = 0
  let style: SgrStyle | null = null

  const line = () => lines[lines.length - 1]
  const write = (ch: string) => {
    const cells = line()
    if (head < cells.length) cells[head] = { ch, style }
    else cells.push({ ch, style })
    head++
  }

  let i = 0
  while (i < raw.length) {
    const ch = raw[i]

    if (ch === '\n') {
      lines.push([])
      head = 0
      i++
      continue
    }
    if (ch === '\r') {
      head = 0
      i++
      continue
    }
    if (ch === '\b') {
      head = Math.max(0, head - 1)
      i++
      continue
    }
    if (ch === '\t') {
      do {
        write(' ')
      } while (head % 8 !== 0)
      i++
      continue
    }
    // Escape sequences ahead of the C0 catch-all: ESC itself is a C0 byte
    // (0x1b < 0x20), so the drop below would otherwise eat the introducer
    // and leak the sequence's payload as text.
    if (ch === '\x1b') {
      // OSC strings run to BEL or ST and are consumed whole -- a title set
      // and the shell-integration markers are metadata, not output. CSI
      // sequences parse their parameter list; the 'm' final applies SGR
      // state, every other final (cursor moves, erase, mode sets) is
      // consumed. Anything else after ESC is a two-byte sequence: consume.
      const next = raw[i + 1]
      if (next === ']') {
        const end = findOscEnd(raw, i + 2)
        i = end
        continue
      }
      if (next === '[') {
        let j = i + 2
        while (j < raw.length && !isCsiFinal(raw[j])) j++
        if (j >= raw.length) break // split sequence: wait for the rest
        const params = raw.slice(i + 2, j)
        if (raw[j] === 'm') style = applySgr(params, style)
        i = j + 1
        continue
      }
      i += 2
      continue
    }

    if (ch < ' ' || ch === '\x7f') {
      // Other C0 controls and DEL: BEL, VT, SO/SI font shifts and friends
      // have no rendering here; consume and move on.
      i++
      continue
    }

    write(ch)
    i++
  }

  return lines.map(toLine)
}

// An OSC string ends at BEL, or at ST (ESC \). Returns the index past the
// terminator; an unterminated string (a read chunk cut it) ends the parse
// at the input's end so the next call resolves it.
function findOscEnd(raw: string, from: number): number {
  for (let i = from; i < raw.length; i++) {
    if (raw[i] === '\x07') return i + 1
    if (raw[i] === '\x1b' && raw[i + 1] === '\\') return i + 2
  }
  return raw.length
}

// CSI final bytes: the parameter/intermediate zone is 0x20-0x3f, finals sit
// in 0x40-0x7e.
function isCsiFinal(ch: string): boolean {
  return ch >= '@' && ch <= '~'
}

// Applies one SGR parameter list to the style state. An empty list is a
// reset (SGR 0 implied); unknown codes leave the state alone -- an
// unstyled-but-visible answer beats a wrongly-styled one.
function applySgr(params: string, current: SgrStyle | null): SgrStyle | null {
  const codes = params === ''
    ? [0]
    : params.split(';').map((p) => Number.parseInt(p, 10) || 0)
  // An all-default style renders like no style, so it folds back to null
  // and unstyled cells keep merging.
  const isEmpty = (s: SgrStyle | null): boolean =>
    s !== null && !s.color && !s.bold && !s.dim && !s.underline
  let style: SgrStyle | null = current ? { ...current } : null

  let k = 0
  while (k < codes.length) {
    const code = codes[k]
    if (code === 0) {
      style = null
    } else if (code === 1) {
      style = { ...style, bold: true }
    } else if (code === 2) {
      style = { ...style, dim: true }
    } else if (code === 4) {
      style = { ...style, underline: true }
    } else if (code === 22) {
      style = style ? { ...style, bold: undefined, dim: undefined } : style
    } else if (code === 24) {
      style = style ? { ...style, underline: undefined } : style
    } else if (code === 39) {
      style = style ? { ...style, color: undefined } : style
    } else if ((code >= 30 && code <= 37) || (code >= 90 && code <= 97)) {
      style = { ...style, color: XTERM_COLORS[code - 30] }
    } else if (code === 38 || code === 48) {
      // Extended color on the foreground is honored; background color has
      // no rendering in a flat transcript, but its parameters must still be
      // consumed so the stream stays aligned.
      const type = codes[k + 1]
      if (type === 5 && k + 2 < codes.length) {
        if (code === 38) style = { ...style, color: color256(codes[k + 2]) }
        k += 2
      } else if (type === 2 && k + 4 < codes.length) {
        if (code === 38) {
          const hex = (v: number) =>
            Math.max(0, Math.min(255, v)).toString(16).padStart(2, '0')
          style = {
            ...style,
            color: `#${hex(codes[k + 2])}${hex(codes[k + 3])}${hex(codes[k + 4])}`,
          }
        }
        k += 4
      }
    }
    if (isEmpty(style)) style = null
    k++
  }
  return style
}

// One line's cells fold into spans: consecutive cells written under the
// same style share a span.
function toLine(cells: Cell[]): TerminalLine {
  const spans: TerminalSpan[] = []
  for (const cell of cells) {
    const last = spans[spans.length - 1]
    if (last !== undefined && sameStyle(last, cell.style)) {
      last.text += cell.ch
    } else {
      spans.push({ text: cell.ch, ...(cell.style ?? {}) })
    }
  }
  return { spans }
}

// Style equality on the normalized key; an absent style and an all-default
// span are the same rendering.
function sameStyle(a: TerminalSpan | undefined, b: SgrStyle | null): boolean {
  const key = (s: { color?: string; bold?: boolean; dim?: boolean; underline?: boolean } | null | undefined): string =>
    s
      ? `${s.color ?? ''}\u0000${s.bold ?? false}\u0000${s.dim ?? false}\u0000${s.underline ?? false}`
      : '\u0000false\u0000false\u0000false'
  return key(a) === key(b)
}
