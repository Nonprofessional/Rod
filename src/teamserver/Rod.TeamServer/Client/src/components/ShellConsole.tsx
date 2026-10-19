import { useEffect, useRef, useState } from 'react'
import { Terminal } from '@xterm/xterm'
import '@xterm/xterm/css/xterm.css'
import {
  ApiError,
  type ListenerSummary,
  type PayloadSummary,
  type ShellSession,
  type ShellUpgrade,
  closeShell,
  readShellOutput,
  sendShellInput,
  upgradeShell,
} from '../api'
import { launcherHint } from '../launcherFamilies'
import { deliverable, payloadLabel } from '../payloads'
import { useClaim } from '../useClaim'
import { CopyButton } from './CopyButton'
import { StatusBadge } from './StatusBadge'

// The caught-shell console: a real terminal emulator (xterm, the VS Code
// terminal's engine) wired straight to the held connection -- keystrokes ride
// the input route as typed, output long-polls by cursor, and the rendering is
// the terminal's own (colors, cursor addressing, full-screen programs on a
// pty-backed shell). A transcript renderer could only show text; a shell that
// runs vim needs the real thing.

// The emulator's geometry: the socat catch shape sizes its pty at 120x30 and
// the catch socket carries no resize channel, so the console mirrors that
// fixed geometry instead of fitting the card -- a mismatched width garbles
// every full-screen redraw the target makes.
const TERM_COLS = 120
const TERM_ROWS = 30
//
// The typing half is claim-aware (architecture.md Sec 4.5): a shell another
// operator holds blocks input and names the holder, and this console's typing
// takes the claim on first key. The upgrade button renders the paste-ready
// launchers that grow the shell into a real implant.
export function ShellConsole({
  engagementId,
  shell,
  webListeners,
  payloads,
  onEnded,
}: {
  engagementId: string
  shell: ShellSession
  webListeners: ListenerSummary[]
  payloads: PayloadSummary[]
  onEnded: () => void
}) {
  const [error, setError] = useState<string | null>(null)
  const [upgrade, setUpgrade] = useState<ShellUpgrade | null>(null)
  const [upgradeListenerId, setUpgradeListenerId] = useState('')
  const [upgradePayloadId, setUpgradePayloadId] = useState('')
  const [busy, setBusy] = useState(false)
  // Whether the emulator's textarea holds the keyboard focus -- the visible
  // answer to "where does my typing go", as an inset frame on the surface.
  const [focused, setFocused] = useState(false)
  const terminalRef = useRef<HTMLDivElement>(null)
  // The emulator and its write handle: one per shell session, owned by the
  // effect below; the read pump reaches it through the ref.
  const termRef = useRef<Terminal | null>(null)
  const { locked, holderHandle } = useClaim(engagementId, 'shell', shell.sessionId)

  const done = shell.status !== 'live'

  // The roster state's current values, read by the emulator's input gate:
  // the emulator itself must not rebuild when they move (the transcript
  // would drop), so the gate reads through these instead of closing over
  // the render's values.
  const doneRef = useRef(done)
  const lockedRef = useRef(locked)
  // The write queue's buffer and in-flight flag: keystrokes must land on the
  // shell in the order they were struck, but each write is its own request
  // and parallel requests carry no arrival guarantee. One write in flight at
  // a time, with whatever strikes meanwhile coalescing into the next batch,
  // keeps the sequence exact and spares the wire a request per character on
  // bursts and pastes.
  const pendingRef = useRef('')
  const sendingRef = useRef(false)

  // The emulator's lifecycle: created per shell session, themed off the
  // console's own CSS tokens, focused on open (a console's surface is where
  // the operator lands), resized with the window. Keystrokes ride the input
  // route verbatim -- a shell's line discipline, not a form, decides what a
  // key means (Enter, ^C, paste, everything).
  useEffect(() => {
    if (!terminalRef.current) return
    const styles = getComputedStyle(document.documentElement)
    const token = (name: string, fallback: string): string =>
      styles.getPropertyValue(name).trim() || fallback
    const term = new Terminal({
      // The plain catch shapes run the shell without a pty, so their output
      // carries bare \n line endings; a strict terminal answers LF with a
      // line feed alone and the transcript staircases off the column. EOL
      // conversion renders those streams in place, and costs the pty shapes
      // nothing (their \r\n just gains a redundant carriage return).
      convertEol: true,
      // The bar cursor reads as "type here" the way every editor's caret
      // does; a block over a character is easier to miss.
      cursorStyle: 'bar',
      cursorWidth: 2,
      cursorBlink: true,
      scrollback: 5000,
      cols: TERM_COLS,
      rows: TERM_ROWS,
      fontSize: 12,
      fontFamily: token('--font-mono', 'monospace'),
      theme: {
        background: token('--terminal-bg', '#07090d'),
        foreground: token('--terminal-text', '#c9d7e4'),
        cursor: token('--accent', '#2fc6e8'),
        // The glyph color where the bar cursor stands, so the character
        // under it stays readable.
        cursorAccent: token('--terminal-bg', '#07090d'),
        // Without an explicit selection background the highlight renders
        // near-invisible against the dark palette -- a selection the
        // operator cannot see is a selection that cannot be trusted.
        selectionBackground: 'rgba(47, 198, 232, 0.30)',
        selectionInactiveBackground: 'rgba(47, 198, 232, 0.14)',
      },
    })
    term.open(terminalRef.current)
    termRef.current = term
    term.focus()

    const flush = async (): Promise<void> => {
      if (sendingRef.current) return
      sendingRef.current = true
      try {
        while (pendingRef.current !== '') {
          const batch = pendingRef.current
          pendingRef.current = ''
          if (doneRef.current || lockedRef.current) continue
          try {
            await sendShellInput(engagementId, shell.sessionId, batch)
          } catch (e) {
            setError(String(e))
            break
          }
        }
      } finally {
        sendingRef.current = false
      }
    }

    term.onData((data) => {
      if (doneRef.current || lockedRef.current || data === '') return
      pendingRef.current += data
      void flush()
    })

    // The terminal keyboard's copy/paste conventions, spelled out: ^C and
    // ^V are control bytes to the shell, so the clipboard rides the shifted
    // pairs (and the Insert pair for the Windows habit). Native Ctrl+V paste
    // keeps working alongside -- the browser fires it on the terminal's
    // textarea and xterm forwards the text.
    term.attachCustomKeyEventHandler((event) => {
      if (event.type !== 'keydown') return true
      const key = event.key.toLowerCase()
      const copy =
        (event.ctrlKey && event.shiftKey && key === 'c')
        || (event.ctrlKey && key === 'insert')
      const paste =
        (event.ctrlKey && event.shiftKey && key === 'v')
        || (event.shiftKey && !event.ctrlKey && key === 'insert')
      if (copy) {
        const selection = term.getSelection()
        if (selection !== '') {
          void navigator.clipboard
            .writeText(selection)
            .then(() => term.clearSelection())
            .catch((e: unknown) => setError(String(e)))
        }
        return false
      }
      if (paste) {
        void navigator.clipboard
          .readText()
          .then((text) => {
            // Paste rides the same ordered queue as keystrokes: it is just
            // a strike that happens to carry many characters.
            pendingRef.current += text
            void flush()
          })
          .catch((e: unknown) => setError(String(e)))
        return false
      }
      return true
    })

    // The focus frame: the emulator's hidden textarea is where typing
    // lands, so its focus/blur drives the surface's inset frame.
    const onFocus = () => setFocused(true)
    const onBlur = () => setFocused(false)
    term.textarea?.addEventListener('focus', onFocus)
    term.textarea?.addEventListener('blur', onBlur)

    return () => {
      term.textarea?.removeEventListener('focus', onFocus)
      term.textarea?.removeEventListener('blur', onBlur)
      // A queued-but-unsent strike belongs to this session; it must not ride
      // into whatever session the console opens next.
      pendingRef.current = ''
      term.dispose()
      termRef.current = null
    }
    // The roster state (done, locked) deliberately stays out of the deps:
    // re-running this effect would rebuild the emulator and drop the
    // transcript on every roster refresh. The refs below carry the current
    // values into the input gate instead.
  }, [engagementId, shell.sessionId])

  // The claim and roster state move around the emulator's life; the input
  // gate and the stdin lock follow them without rebuilding anything.
  useEffect(() => {
    doneRef.current = done
    lockedRef.current = locked
    if (termRef.current) termRef.current.options.disableStdin = done || locked
  }, [done, locked])

  // The output loop: one long poll in flight at a time; each answer's chunks
  // land on the emulator and advance the cursor. A 410 answer means the shell
  // ended server-side -- mark it in the surface and stop reading.
  useEffect(() => {
    let stopped = false
    const pump = async () => {
      // The cursor never re-reads what the emulator already holds.
      let cursor = 0
      while (!stopped) {
        try {
          const output = await readShellOutput(engagementId, shell.sessionId, cursor)
          if (stopped) return
          if (output.chunks.length > 0) {
            cursor = output.latestSequence
            termRef.current?.write(output.chunks.map((c) => c.text).join(''))
          }
        } catch (e) {
          if (stopped) return
          if (e instanceof ApiError && e.status === 410) {
            termRef.current?.write('\r\n\x1b[90m— shell ended —\x1b[0m\r\n')
            onEnded()
            return
          }
          setError(String(e))
          await new Promise((resolve) => setTimeout(resolve, 1000))
        }
      }
    }
    void pump()
    return () => {
      stopped = true
    }
  }, [engagementId, shell.sessionId, onEnded])

  const onDisconnectShell = async () => {
    setBusy(true)
    try {
      await closeShell(engagementId, shell.sessionId)
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
      termRef.current?.focus()
    }
  }

  // The upgrade grows the shell into an enrolled implant, so it needs a
  // payload whose baked enrollment credential still lives -- the same gate
  // the Launchers picker applies, said up front instead of discovered by
  // the paste.
  const upgradeBlockedReason = payloads.length === 0
    ? 'No payload in this engagement yet -- build one under Build first'
    : payloads.every((p) => !deliverable(p))
      ? "Every built payload's enrollment credential is dead (revoked, spent, or expired) -- build a fresh one"
      : null

  const onUpgrade = async () => {
    setBusy(true)
    try {
      setUpgrade(
        await upgradeShell(
          engagementId,
          shell.sessionId,
          upgradePayloadId || undefined,
          upgradeListenerId || undefined,
        ),
      )
      setError(null)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="terminal">
      <div className="terminal-header">
        <code>shell</code>
        <span>·</span>
        <code>{shell.sessionId.slice(0, 8)}</code>
        <span>·</span>
        <code>{shell.remoteAddress}</code>
        <StatusBadge status={shell.status} />
        {locked && (
          <span className="muted" title="Another operator holds this shell's input; it releases when they dismiss their console or disconnect">
            held by {holderHandle}
          </span>
        )}
        <span className="spacer">
          <button
            className="ghost sm"
            onClick={() => void onUpgrade()}
            disabled={busy || upgradeBlockedReason !== null}
            title={upgradeBlockedReason ?? 'Render the paste-ready launchers that grow this shell into an enrolled implant'}
          >
            Upgrade
          </button>
          <button
            className="ghost sm"
            onClick={() => void onDisconnectShell()}
            disabled={busy || done}
            title="Disconnect this shell: the socket closes and the target-side one-liner exits with it"
          >
            Disconnect
          </button>
        </span>
      </div>
      {/* The terminal surface: typing goes straight to the shell (^C is the
          key, not a button); full-screen programs need the target shell under
          a pty, which the socat catch shape provides. */}
      <div
        className={`terminal-surface${focused ? ' focused' : ''}`}
        ref={terminalRef}
        onMouseDown={() => termRef.current?.focus()}
        title={
          locked
            ? `held by ${holderHandle}`
            : 'Click to type — Ctrl+Shift+C / Ctrl+Shift+V (or the Insert pair) copy and paste; Ctrl+C interrupts; Ctrl+V pastes natively'
        }
      />
      {upgrade && (
        <div className="upgrade-panel">
          <p>
            Paste one of these into the shell to grow it into an enrolled implant. The credential is
            single-use and expires <time>{new Date(upgrade.tokenExpiresAt).toLocaleTimeString()}</time>.
          </p>
          {/* The same picks the Launchers render form offers: which build the
              fetch delivers, and which front serves it. */}
          <div className="upgrade-launcher">
            <code>deliver</code>
            <select
              value={upgradePayloadId}
              onChange={(e) => setUpgradePayloadId(e.target.value)}
              title="The payload the fetch delivers; the newest build stands in when unnamed. A dead credential cannot enroll the beacon it downloads, so those entries refuse."
            >
              <option value="">Newest build</option>
              {payloads.map((p) => {
                const dead = !deliverable(p)
                return (
                  <option key={p.artifactId} value={p.artifactId} disabled={dead}>
                    {payloadLabel(p)}
                    {dead ? ' — credential dead' : ''}
                  </option>
                )
              })}
            </select>
          </div>
          {webListeners.length > 1 && (
            <div className="upgrade-launcher">
              <code>fetch via</code>
              <select
                value={upgradeListenerId}
                onChange={(e) => setUpgradeListenerId(e.target.value)}
                title="Which web listener's front the payload fetch rides -- every HTTP(S)/mTLS listener serves it on its public endpoint; there is no separate file host. Auto prefers https over mTLS over cleartext; name one when the fetch must cross a specific redirector."
              >
                <option value="">auto — hardened front preferred</option>
                {webListeners.map((l) => (
                  <option key={l.id} value={l.id}>
                    {l.name} ({l.transport} → {l.publicEndpoint})
                  </option>
                ))}
              </select>
            </div>
          )}
          {upgrade.launchers.map((launcher) => (
            <div key={launcher.id} className="upgrade-launcher" title={launcherHint(launcher.id)}>
              <code>{launcher.id}</code>
              <code className="upgrade-command">{launcher.command}</code>
              <CopyButton text={launcher.command} />
            </div>
          ))}
        </div>
      )}
      {error && <p className="error terminal-error">{error}</p>}
    </div>
  )
}
