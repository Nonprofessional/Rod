package exec

// lateral_test.go covers the lateral.move dispatch surface that does not need a
// live enroll endpoint: the argument parser, the disabled-bundle refusal, and
// the dispatch routing. The end-to-end enroll round-trip -- a real child
// enrolling back against a real teamserver -- is exercised by the
// implant-driven integration test (ChildImplantRoundTripTests), which runs the
// reference implant as a subprocess; reproducing that leaf+CA dance here would
// duplicate the teamserver's enroll handler for a single handler unit test.

import (
	"context"
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
