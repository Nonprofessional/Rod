// Package exec dispatches the core capability verbs the reference implant
// advertises (architecture.md Sec 10). Only shell.exec is wired in this
// milestone; the runner is the dispatch point future verbs (file.push,
// probe.read, ...) extend.
//
// This is a benign reference runner: it shells out to the platform shell for the
// one core verb and reports output. It performs no evasion, no obfuscation, and
// no destructive behavior (RESPONSIBLE-USE.md, architecture.md Sec 7).
package exec

import (
	"context"
	"io"
	"log"
	"os/exec"
	"runtime"

	"github.com/cw/rod/implant/rodpb"
)

// Runner dispatches capability verbs. It is safe for concurrent use: each
// Dispatch call runs an independent command.
type Runner struct {
	log *log.Logger
}

// NewRunner builds a Runner. logger may be nil (a no-op logger is used then);
// the param is named to avoid shadowing the standard log package inside the body.
func NewRunner(logger *log.Logger) *Runner {
	if logger == nil {
		logger = log.New(io.Discard, "", 0)
	}
	return &Runner{log: logger}
}

// Dispatch runs verb against arguments and returns the wire outcome plus the
// captured output (combined stdout/stderr). An unknown verb reports Failed with
// a clear message rather than panicking, so the operator sees the cause.
func (r *Runner) Dispatch(ctx context.Context, verb, arguments string) (rodpb.TaskOutcome, string) {
	switch verb {
	case "shell.exec":
		return r.shellExec(ctx, arguments)
	default:
		r.log.Printf("unknown verb: %s", verb)
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED, "unknown verb: " + verb
	}
}

// shellExec runs the argument string through the platform shell and returns the
// combined output. A non-zero exit is a Failed outcome with the output captured
// so the operator sees the cause; the shell itself failing to start is also
// Failed.
func (r *Runner) shellExec(ctx context.Context, command string) (rodpb.TaskOutcome, string) {
	shell, flag := platformShell()
	cmd := exec.CommandContext(ctx, shell, flag, command)
	out, err := cmd.CombinedOutput()
	output := string(out)
	if err != nil {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED, appendIfMissing(output, err.Error())
	}
	return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED, output
}

// platformShell returns the shell and its command flag for shell.exec on the
// current OS. Linux/macOS use sh -c; Windows uses cmd /c.
func platformShell() (string, string) {
	if runtime.GOOS == "windows" {
		return "cmd", "/c"
	}
	return "sh", "-c"
}

// appendIfMissing joins output and suffix on a newline when output is non-empty,
// so a Failed outcome shows both the command output and the error.
func appendIfMissing(output, suffix string) string {
	if output == "" {
		return suffix
	}
	if suffix == "" {
		return output
	}
	return output + "\n" + suffix
}
