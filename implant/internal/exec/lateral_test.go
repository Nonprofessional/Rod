package exec

// lateral_test.go covers the lateral.* dispatch surface that does not need a
// live enroll endpoint: the lateral.move argument parser, the disabled-bundle
// refusal, the lateral.token platform branch, the lateral.exec_remote parser,
// and the dispatch routing. The end-to-end enroll round-trip -- a real child
// enrolling back against a real teamserver -- is exercised by the
// implant-driven integration test (ChildImplantRoundTripTests), which runs the
// reference implant as a subprocess; reproducing that leaf+CA dance here would
// duplicate the teamserver's enroll handler for a single handler unit test.

import (
	"context"
	"runtime"
	"strings"
	"testing"

	"github.com/cw/rod/implant/rodpb"
)

func TestLateralMove_DisabledBundle_FailsWithCause(t *testing.T) {
	// A runner built without an enroll bundle cannot derive children; the handler
	// reports the cause rather than enrolling against an empty endpoint.
	r := NewRunner(nil)
	outcome, output, _ := r.Dispatch(context.Background(), "lateral.move", "child-token")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_FAILED {
		t.Fatalf("outcome = %v, want FAILED when derivation is disabled", outcome)
	}
	if !strings.Contains(output, "not available") {
		t.Fatalf("output = %q, want it to state derivation is unavailable", output)
	}
}

func TestLateralMove_MalformedArgs_FailsWithCause(t *testing.T) {
	// A bundle with a URL enables derivation, but the argument still must carry a
	// token. Empty or over-long arguments are refused before any key is generated.
	r := NewRunnerWithEnroll(EnrollBundle{URL: "http://127.0.0.1:9/enroll", ParentID: "parent-1"}, nil)
	for _, args := range []string{"", "   ", "a b c"} {
		outcome, output, _ := r.Dispatch(context.Background(), "lateral.move", args)
		if outcome != rodpb.TaskOutcome_TASK_OUTCOME_FAILED {
			t.Fatalf("args=%q: outcome = %v, want FAILED", args, outcome)
		}
		if !strings.Contains(output, "lateral.move expects") {
			t.Fatalf("args=%q: output = %q, want a usage message", args, output)
		}
	}
}

func TestParseMoveArgs(t *testing.T) {
	cases := []struct {
		in    string
		token string
		class string
		ok    bool
	}{
		{"", "", "", false},
		{"   ", "", "", false},
		{"tok", "tok", "", true},
		{"  tok  ", "tok", "", true},
		{"tok stage2", "tok", "stage2", true},
		{"tok a b", "", "", false},
	}
	for _, c := range cases {
		token, class, ok := parseMoveArgs(c.in)
		if ok != c.ok || token != c.token || class != c.class {
			t.Errorf("parseMoveArgs(%q) = (%q,%q,%v), want (%q,%q,%v)",
				c.in, token, class, ok, c.token, c.class, c.ok)
		}
	}
}

// TestLateralToken_NonWindows_RefusesWithCause documents the platform contract
// on a non-Windows test host. Windows hosts exercise the whoami path directly.
func TestLateralToken_NonWindows_RefusesWithCause(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("lateral.token refusal is asserted on non-Windows hosts")
	}
	r := NewRunner(nil)
	outcome, output, _ := r.Dispatch(context.Background(), "lateral.token", "")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_FAILED {
		t.Fatalf("outcome = %v, want FAILED off-Windows", outcome)
	}
	if !strings.Contains(output, "lateral.token") {
		t.Fatalf("output = %q, want it to name the verb", output)
	}
	if !strings.Contains(output, "Windows") {
		t.Fatalf("output = %q, want it to state Windows-only", output)
	}
}

// TestLateralToken_Windows_RunsWhoami exercises the documented whoami path on
// a Windows test host. Off-Windows it skips, since the refusal is covered above.
func TestLateralToken_Windows_RunsWhoami(t *testing.T) {
	if runtime.GOOS != "windows" {
		t.Skip("lateral.token whoami path runs on Windows only")
	}
	r := NewRunner(nil)
	outcome, output, _ := r.Dispatch(context.Background(), "lateral.token", "")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED {
		t.Fatalf("outcome = %v, want SUCCEEDED on Windows, output=%q", outcome, output)
	}
	if !strings.Contains(output, runtime.GOOS) && !strings.Contains(output, "\\") {
		t.Fatalf("output = %q, want a user\\name token line", output)
	}
}

func TestLateralExecRemote_MalformedArgs_FailsWithCause(t *testing.T) {
	r := NewRunner(nil)
	for _, args := range []string{"", "   ", "single-host"} {
		outcome, output, _ := r.Dispatch(context.Background(), "lateral.exec_remote", args)
		if outcome != rodpb.TaskOutcome_TASK_OUTCOME_FAILED {
			t.Fatalf("args=%q: outcome = %v, want FAILED", args, outcome)
		}
		if !strings.Contains(output, "lateral.exec_remote expects") {
			t.Fatalf("args=%q: output = %q, want a usage message", args, output)
		}
	}
}

func TestParseExecRemoteArgs(t *testing.T) {
	cases := []struct {
		in      string
		host    string
		command string
		ok      bool
	}{
		{"", "", "", false},
		{"   ", "", "", false},
		{"host", "", "", false},
		{"host cmd", "host", "cmd", true},
		{"  host   cmd  ", "host", "cmd", true},
		{"host cmd with args", "host", "cmd with args", true},
	}
	for _, c := range cases {
		host, command, ok := parseExecRemoteArgs(c.in)
		if ok != c.ok || host != c.host || command != c.command {
			t.Errorf("parseExecRemoteArgs(%q) = (%q,%q,%v), want (%q,%q,%v)",
				c.in, host, command, ok, c.host, c.command, c.ok)
		}
	}
}
