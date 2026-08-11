package exec

import (
	"context"
	"runtime"
	"strings"
	"testing"

	"github.com/cw/rod/implant/rodpb"
)

func TestDispatch_ShellExec_Succeeds(t *testing.T) {
	r := NewRunner(nil)
	outcome, output, _ := r.Dispatch(context.Background(), "shell.exec", "echo hello-rod")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED {
		t.Fatalf("outcome = %v, want SUCCEEDED", outcome)
	}
	if !strings.Contains(output, "hello-rod") {
		t.Fatalf("output = %q, want it to contain hello-rod", output)
	}
}

func TestDispatch_ShellExec_FailedOutcome_OnBadCommand(t *testing.T) {
	r := NewRunner(nil)
	outcome, _, _ := r.Dispatch(context.Background(), "shell.exec", nonexistentCommand())
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_FAILED {
		t.Fatalf("outcome = %v, want FAILED for a non-zero exit", outcome)
	}
}

func TestDispatch_UnknownVerb_FailedWithCause(t *testing.T) {
	r := NewRunner(nil)
	outcome, output, _ := r.Dispatch(context.Background(), "file.push", "/tmp/x")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_FAILED {
		t.Fatalf("outcome = %v, want FAILED for unknown verb", outcome)
	}
	if !strings.Contains(output, "file.push") {
		t.Fatalf("output = %q, want it to name the unknown verb", output)
	}
}

// nonexistentCommand returns a command string that exits non-zero on the current
// platform: sh cannot exec a missing binary; cmd prints an error and exits 1.
func nonexistentCommand() string {
	if runtime.GOOS == "windows" {
		return "this-command-does-not-exist-xyz"
	}
	return "this-command-does-not-exist-xyz"
}
