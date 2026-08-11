package exec

// recon_test.go covers the recon verbs added in roadmap M5.1
// (recon.portscan, recon.hostenum, recon.service). Each test drives Runner.Dispatch
// against a real loopback listener so an open port is observable without a
// network dependency, mirroring how runner_test.go exercises shell.exec against
// the real platform shell. The reference behavior is benign: the operator is
// responsible for targeting only systems they are authorized to test
// (RESPONSIBLE-USE.md).

import (
	"context"
	"net"
	"strconv"
	"strings"
	"testing"

	"github.com/cw/rod/implant/rodpb"
)

// startLoopbackListener opens a TCP listener on 127.0.0.1 with a kernel-chosen
// port and returns it; the caller closes it. Used so a recon verb has a
// deterministically-open port to find.
func startLoopbackListener(t *testing.T) net.Listener {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen: %v", err)
	}
	return ln
}

func portOf(t *testing.T, addr string) int {
	t.Helper()
	_, portStr, err := net.SplitHostPort(addr)
	if err != nil {
		t.Fatalf("split host port: %v", err)
	}
	port, err := net.LookupPort("tcp", portStr)
	if err != nil {
		t.Fatalf("parse port: %v", err)
	}
	return port
}

func TestDispatch_PortScan_ReportsOpenLoopbackPort(t *testing.T) {
	ln := startLoopbackListener(t)
	defer ln.Close()
	port := portOf(t, ln.Addr().String())

	r := NewRunner(nil)
	outcome, output, _ := r.Dispatch(context.Background(), "recon.portscan",
		formatScanArgs(port))

	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED {
		t.Fatalf("outcome = %v, want SUCCEEDED", outcome)
	}
	want := openPortLine(port)
	if !strings.Contains(output, want) {
		t.Fatalf("output = %q, want it to contain %q", output, want)
	}
}

func TestDispatch_PortScan_MalformedArgs_FailsWithCause(t *testing.T) {
	r := NewRunner(nil)
	outcome, output, _ := r.Dispatch(context.Background(), "recon.portscan", "not-a-range")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_FAILED {
		t.Fatalf("outcome = %v, want FAILED for malformed args", outcome)
	}
	if !strings.Contains(output, "recon.portscan") {
		t.Fatalf("output = %q, want it to name the verb", output)
	}
}

func TestDispatch_PortScan_EmptyRange_SucceedsWithNoLines(t *testing.T) {
	// A range with no open ports is still a successful scan; the operator sees
	// empty output rather than a failure, so a quiet host is not confused with
	// an error.
	r := NewRunner(nil)
	outcome, output, _ := r.Dispatch(context.Background(), "recon.portscan", "127.0.0.1 1-1")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED {
		t.Fatalf("outcome = %v, want SUCCEEDED for a closed range", outcome)
	}
	if output != "" {
		t.Fatalf("output = %q, want empty for a closed range", output)
	}
}

func TestDispatch_HostEnum_ReportsLocalFacts(t *testing.T) {
	r := NewRunner(nil)
	outcome, output, _ := r.Dispatch(context.Background(), "recon.hostenum", "")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED {
		t.Fatalf("outcome = %v, want SUCCEEDED", outcome)
	}
	// hostenum is local introspection; it surfaces the hostname and the
	// goos/goarch pair the runner documents.
	if !strings.Contains(output, "hostname=") {
		t.Fatalf("output = %q, want it to report the hostname", output)
	}
	if !strings.Contains(output, "goos=") || !strings.Contains(output, "goarch=") {
		t.Fatalf("output = %q, want it to report goos and goarch", output)
	}
}

func TestDispatch_ServiceProbe_ReportsOpenLoopbackPort(t *testing.T) {
	ln := startLoopbackListener(t)
	defer ln.Close()
	port := portOf(t, ln.Addr().String())

	r := NewRunner(nil)
	outcome, output, _ := r.Dispatch(context.Background(), "recon.service",
		formatServiceArgs(port))

	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED {
		t.Fatalf("outcome = %v, want SUCCEEDED", outcome)
	}
	want := serviceOpenLine(port)
	if !strings.Contains(output, want) {
		t.Fatalf("output = %q, want it to contain %q", output, want)
	}
}

func TestDispatch_ServiceProbe_NoOpenPort_Fails(t *testing.T) {
	// The documented contract: if none of the listed ports is open, the probe
	// reports FAILED. Port 9 is the discard service and almost never bound in a
	// test environment; the assertion is on the contract, not on port 9 being
	// definitively closed.
	r := NewRunner(nil)
	outcome, output, _ := r.Dispatch(context.Background(), "recon.service", "127.0.0.1 9")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_FAILED {
		t.Fatalf("outcome = %v, want FAILED when no port is open", outcome)
	}
	if !strings.Contains(output, "127.0.0.1") {
		t.Fatalf("output = %q, want it to name the host", output)
	}
}

func TestDispatch_ServiceProbe_MalformedArgs_FailsWithCause(t *testing.T) {
	r := NewRunner(nil)
	outcome, output, _ := r.Dispatch(context.Background(), "recon.service", "127.0.0.1")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_FAILED {
		t.Fatalf("outcome = %v, want FAILED for malformed args", outcome)
	}
	if !strings.Contains(output, "recon.service") {
		t.Fatalf("output = %q, want it to name the verb", output)
	}
}

// formatScanArgs builds "<host> <start-end>" over a tight window around port so
// the scan finishes promptly while still covering the open listener.
func formatScanArgs(port int) string {
	start := port - 1
	if start < 1 {
		start = 1
	}
	return "127.0.0.1 " + strconv.Itoa(start) + "-" + strconv.Itoa(port+1)
}

// formatServiceArgs builds "<host> <port>".
func formatServiceArgs(port int) string {
	return "127.0.0.1 " + strconv.Itoa(port)
}

func openPortLine(port int) string    { return "127.0.0.1:" + strconv.Itoa(port) + " open" }
func serviceOpenLine(port int) string { return "127.0.0.1:" + strconv.Itoa(port) + " open" }
