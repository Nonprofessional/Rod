package exec

// persist_test.go covers the persist.* dispatch surface that does not need a
// privileged install: argument parsing, mechanism routing, the persist.list
// platform branch, and a systemd install/list/remove round-trip against a
// temporary XDG_CONFIG_HOME so the test never touches the developer's own
// units. The Windows-only mechanisms (runkey/schtasks/service) are exercised
// by the platform refusal off-Windows and by the parser tests, mirroring how
// lateral_test.go covers lateral.token.

import (
	"context"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"

	"github.com/cw/rod/implant/rodpb"
)

func TestPersistInstall_MalformedArgs_FailsWithCause(t *testing.T) {
	r := NewRunner(nil)
	for _, args := range []string{"", "   ", "runkey", "runkey onlyname"} {
		outcome, output, _ := r.Dispatch(context.Background(), "persist.install", args)
		if outcome != rodpb.TaskOutcome_TASK_OUTCOME_FAILED {
			t.Fatalf("args=%q: outcome = %v, want FAILED", args, outcome)
		}
		if !strings.Contains(output, "persist.install expects") {
			t.Fatalf("args=%q: output = %q, want a usage message", args, output)
		}
	}
}

func TestPersistRemove_MalformedArgs_FailsWithCause(t *testing.T) {
	r := NewRunner(nil)
	for _, args := range []string{"", "   ", "cron", "cron one two"} {
		outcome, output, _ := r.Dispatch(context.Background(), "persist.remove", args)
		if outcome != rodpb.TaskOutcome_TASK_OUTCOME_FAILED {
			t.Fatalf("args=%q: outcome = %v, want FAILED", args, outcome)
		}
		if !strings.Contains(output, "persist.remove expects") {
			t.Fatalf("args=%q: output = %q, want a usage message", args, output)
		}
	}
}

// TestPersistInstall_UnknownMechanism_FailsWithCause ensures an unsupported
// mechanism name is rejected on either platform rather than silently no-opping.
func TestPersistInstall_UnknownMechanism_FailsWithCause(t *testing.T) {
	r := NewRunner(nil)
	outcome, output, _ := r.Dispatch(context.Background(), "persist.install", "voodoo name payload")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_FAILED {
		t.Fatalf("outcome = %v, want FAILED", outcome)
	}
	if !strings.Contains(output, "unknown mechanism") {
		t.Fatalf("output = %q, want 'unknown mechanism'", output)
	}
}

// TestPersistList_UnknownMechanism_FailsWithCause checks the optional filter
// rejects an unknown mechanism with a clear message rather than returning empty.
func TestPersistList_UnknownMechanism_FailsWithCause(t *testing.T) {
	r := NewRunner(nil)
	outcome, output, _ := r.Dispatch(context.Background(), "persist.list", "voodoo")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_FAILED {
		t.Fatalf("outcome = %v, want FAILED", outcome)
	}
	if !strings.Contains(output, "unknown mechanism") {
		t.Fatalf("output = %q, want 'unknown mechanism'", output)
	}
}

// TestPersistInstallWindowsMechanism_NonWindows_RefusesWithCause documents the
// platform contract: the Windows mechanisms refuse off-Windows with a clear
// cause rather than reaching for tools that do not exist.
func TestPersistInstallWindowsMechanism_NonWindows_RefusesWithCause(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("Windows-mechanism refusal is asserted on non-Windows hosts")
	}
	r := NewRunner(nil)
	for _, mech := range []string{"runkey", "schtasks", "service"} {
		outcome, output, _ := r.Dispatch(
			context.Background(), "persist.install", mech+" name payload")
		if outcome != rodpb.TaskOutcome_TASK_OUTCOME_FAILED {
			t.Fatalf("mech=%s: outcome = %v, want FAILED", mech, outcome)
		}
		if !strings.Contains(output, "Windows-only") {
			t.Fatalf("mech=%s: output = %q, want Windows-only refusal", mech, output)
		}
	}
}

// TestPersistList_SucceedsWithMarkerLines verifies persist.list runs on the
// current platform, returns Succeeded, and produces one line per installed
// entry in the documented "<mechanism> <name>" format. It seeds a temp systemd
// user dir so the listing has at least one entry on Linux.
func TestPersistList_SucceedsWithMarkerLines(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("Linux-only listing fixture; Windows exercised by refusal test")
	}
	dir := t.TempDir()
	t.Setenv("XDG_CONFIG_HOME", dir)
	if err := os.MkdirAll(filepath.Join(dir, "systemd", "user"), 0o755); err != nil {
		t.Fatalf("mkdir: %v", err)
	}
	if err := os.WriteFile(
		filepath.Join(dir, "systemd", "user", "RodMarker.service"),
		[]byte("[Service]\nExecStart=/bin/true\n"), 0o644); err != nil {
		t.Fatalf("write unit: %v", err)
	}

	r := NewRunner(nil)
	outcome, output, _ := r.Dispatch(context.Background(), "persist.list", "")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED {
		t.Fatalf("outcome = %v, want SUCCEEDED, output=%q", outcome, output)
	}
	if !strings.Contains(output, "systemd RodMarker") {
		t.Fatalf("output = %q, want a 'systemd RodMarker' line", output)
	}
}

// TestPersistInstallSystemd_Remove_RoundTrips drives the full install -> list ->
// remove -> list cycle against a per-user systemd user dir rooted in a temp
// XDG_CONFIG_HOME, so the developer's own units are never touched. It skips on
// Windows where systemd does not apply, and tolerates a missing systemctl on
// hosts without systemd by treating the daemon-reload failure as non-fatal for
// the listing assertion (the unit file is still written).
func TestPersistInstallSystemd_Remove_RoundTrips(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("systemd round-trip runs on Linux only")
	}
	dir := t.TempDir()
	t.Setenv("XDG_CONFIG_HOME", dir)

	r := NewRunner(nil)

	// install: writes the unit file. daemon-reload may fail if systemd is not
	// installed on this test host; that does not block the listing path, which
	// reads the directory directly.
	outcome, output, _ := r.Dispatch(
		context.Background(), "persist.install", "systemd RodRT /bin/true")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED {
		// On hosts without systemd the daemon-reload fails; that is
		// environmental, not a handler defect. Surface but do not fail.
		if strings.Contains(output, "daemon-reload") {
			t.Skipf("systemd not available on this host: %s", output)
		}
		t.Fatalf("install: outcome = %v, output = %q", outcome, output)
	}

	// list: the just-installed unit shows up by name.
	outcome, listing, _ := r.Dispatch(context.Background(), "persist.list", "systemd")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED {
		t.Fatalf("list after install: outcome = %v, output = %q", outcome, listing)
	}
	if !strings.Contains(listing, "RodRT") {
		t.Fatalf("list after install = %q, want RodRT present", listing)
	}

	// remove: deletes the unit file and reloads.
	outcome, rmOut, _ := r.Dispatch(
		context.Background(), "persist.remove", "systemd RodRT")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED {
		t.Fatalf("remove: outcome = %v, output = %q", outcome, rmOut)
	}

	// list again: the name is gone. The handler reports "(no entries)" when a
	// mechanism has nothing installed, so assert the name is absent rather than
	// asserting a specific empty marker.
	outcome, listing2, _ := r.Dispatch(context.Background(), "persist.list", "systemd")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED {
		t.Fatalf("list after remove: outcome = %v, output = %q", outcome, listing2)
	}
	if strings.Contains(listing2, "RodRT") {
		t.Fatalf("list after remove = %q, want RodRT gone", listing2)
	}
}

// TestPersistRemove_AlreadyAbsent_IdempotentSucceeded confirms a remove of a
// name that was never installed reports Succeeded with an "already absent"
// note, so retries after a partial install do not strand the operator.
func TestPersistRemove_AlreadyAbsent_IdempotentSucceeded(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("Linux-only listing fixture; Windows exercised by refusal test")
	}
	dir := t.TempDir()
	t.Setenv("XDG_CONFIG_HOME", dir)

	r := NewRunner(nil)
	outcome, output, _ := r.Dispatch(
		context.Background(), "persist.remove", "systemd NeverInstalled")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED {
		t.Fatalf("outcome = %v, want SUCCEEDED for absent entry, output=%q", outcome, output)
	}
	if !strings.Contains(output, "already absent") {
		t.Fatalf("output = %q, want 'already absent'", output)
	}
}

func TestParsePersistInstallArgs(t *testing.T) {
	cases := []struct {
		in              string
		mechanism       string
		name            string
		payload         string
		ok              bool
	}{
		{"", "", "", "", false},
		{"   ", "", "", "", false},
		{"runkey", "", "", "", false},
		{"runkey name", "", "", "", false},
		{"runkey name payload", "runkey", "name", "payload", true},
		{"  runkey   RodRun   /bin/true  ", "runkey", "RodRun", "/bin/true", true},
		{"cron hourlyjob /usr/bin/backup --quiet", "cron", "hourlyjob", "/usr/bin/backup --quiet", true},
	}
	for _, c := range cases {
		mechanism, name, payload, ok := parsePersistInstallArgs(c.in)
		if ok != c.ok || mechanism != c.mechanism || name != c.name || payload != c.payload {
			t.Errorf("parsePersistInstallArgs(%q) = (%q,%q,%q,%v), want (%q,%q,%q,%v)",
				c.in, mechanism, name, payload, ok, c.mechanism, c.name, c.payload, c.ok)
		}
	}
}

func TestParsePersistRemoveArgs(t *testing.T) {
	cases := []struct {
		in        string
		mechanism string
		name      string
		ok        bool
	}{
		{"", "", "", false},
		{"   ", "", "", false},
		{"cron", "", "", false},
		{"cron one two", "", "", false},
		{"cron RodRT", "cron", "RodRT", true},
		{"  runkey   RodRun  ", "runkey", "RodRun", true},
	}
	for _, c := range cases {
		mechanism, name, ok := parsePersistRemoveArgs(c.in)
		if ok != c.ok || mechanism != c.mechanism || name != c.name {
			t.Errorf("parsePersistRemoveArgs(%q) = (%q,%q,%v), want (%q,%q,%v)",
				c.in, mechanism, name, ok, c.mechanism, c.name, c.ok)
		}
	}
}
