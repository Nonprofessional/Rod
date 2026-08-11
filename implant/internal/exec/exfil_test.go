package exec

// exfil_test.go covers the exfil.* dispatch surface: argument parsing, the
// name-only staging path, the read/stream path, the missing-file and directory
// refusals, chunk terminal-flag correctness, and the exfil.stage manifest. The
// server-side artifact capture is exercised end-to-end by the integration test
// ExfilRoundTripTests.

import (
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/cw/rod/implant/rodpb"
)

func TestExfilPush_EmptyArgs_FailsWithCause(t *testing.T) {
	r := NewRunner(nil)
	outcome, output, chunks := r.Dispatch(context.Background(), "exfil.push", "")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_FAILED {
		t.Fatalf("outcome = %v, want FAILED", outcome)
	}
	if !strings.Contains(output, "exfil.push expects") {
		t.Fatalf("output = %q, want a usage message", output)
	}
	if chunks != nil {
		t.Fatalf("chunks = %v, want nil for a failed parse", chunks)
	}
}

func TestExfilPush_NameOnly_StagedManifest(t *testing.T) {
	// A name-only invocation stages the artifact by name without streaming
	// bytes; Succeeded with a marker and no chunks.
	r := NewRunner(nil)
	outcome, output, chunks := r.Dispatch(context.Background(), "exfil.push", " loot.tar.gz")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED {
		t.Fatalf("outcome = %v, want SUCCEEDED", outcome)
	}
	if !strings.Contains(output, "staged loot.tar.gz") {
		t.Fatalf("output = %q, want a staged marker", output)
	}
	if chunks != nil {
		t.Fatalf("chunks = %v, want nil for a name-only push", chunks)
	}
}

func TestExfilPush_MissingFile_FailsWithCause(t *testing.T) {
	r := NewRunner(nil)
	outcome, output, _ := r.Dispatch(
		context.Background(), "exfil.push",
		"absent "+filepath.Join(t.TempDir(), "missing"))
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_FAILED {
		t.Fatalf("outcome = %v, want FAILED", outcome)
	}
	if !strings.Contains(output, "stat ") {
		t.Fatalf("output = %q, want a stat error", output)
	}
}

func TestExfilPush_Directory_RefusesWithCause(t *testing.T) {
	r := NewRunner(nil)
	dir := t.TempDir()
	outcome, output, _ := r.Dispatch(context.Background(), "exfil.push", "dir "+dir)
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_FAILED {
		t.Fatalf("outcome = %v, want FAILED", outcome)
	}
	if !strings.Contains(output, "directory") {
		t.Fatalf("output = %q, want a directory refusal", output)
	}
}

func TestExfilPush_StreamsFileContents(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "loot.txt")
	const want = "exfil payload line one\nline two\n"
	if err := os.WriteFile(path, []byte(want), 0o644); err != nil {
		t.Fatalf("write: %v", err)
	}
	r := NewRunner(nil)
	outcome, output, chunks := r.Dispatch(context.Background(), "exfil.push", "loot.txt "+path)
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED {
		t.Fatalf("outcome = %v, want SUCCEEDED, output=%q", outcome, output)
	}
	if !strings.Contains(output, "pushed loot.txt") {
		t.Fatalf("output = %q, want a pushed marker", output)
	}
	if len(chunks) != 1 {
		t.Fatalf("len(chunks) = %d, want 1 for a small file", len(chunks))
	}
	c := chunks[0]
	if c.Name != "loot.txt" {
		t.Fatalf("chunk name = %q, want loot.txt", c.Name)
	}
	if c.ContentType != "text/plain" {
		t.Fatalf("content type = %q, want text/plain", c.ContentType)
	}
	if !c.Terminal {
		t.Fatalf("terminal = false, want true")
	}
	if string(c.Data) != want {
		t.Fatalf("chunk data = %q, want %q", string(c.Data), want)
	}
}

// TestExfilPush_LargeFile_MultiChunkTerminal verifies a file larger than the
// chunk size is split into non-terminal chunks with a terminal last chunk, and
// the reassembled bytes match the file.
func TestExfilPush_LargeFile_MultiChunkTerminal(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "blob.bin")
	size := collectChunkSize*2 + 1024
	payload := make([]byte, size)
	for i := range payload {
		payload[i] = byte(i % 251)
	}
	if err := os.WriteFile(path, payload, 0o644); err != nil {
		t.Fatalf("write: %v", err)
	}
	r := NewRunner(nil)
	outcome, output, chunks := r.Dispatch(context.Background(), "exfil.push", "blob.bin "+path)
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED {
		t.Fatalf("outcome = %v, want SUCCEEDED, output=%q", outcome, output)
	}
	if len(chunks) < 3 {
		t.Fatalf("len(chunks) = %d, want >= 3 for a %d-byte file", len(chunks), size)
	}
	if chunks[0].Terminal {
		t.Fatalf("chunk 0 terminal = true, want false")
	}
	if !chunks[len(chunks)-1].Terminal {
		t.Fatalf("last chunk terminal = false, want true")
	}
	var got []byte
	for i, c := range chunks {
		if int(c.Sequence) != i+1 {
			t.Fatalf("chunk %d sequence = %d, want %d", i, c.Sequence, i+1)
		}
		got = append(got, c.Data...)
	}
	if string(got) != string(payload) {
		t.Fatalf("reassembled %d bytes, want %d", len(got), len(payload))
	}
}

func TestExfilStage_ReportsEmptyManifest(t *testing.T) {
	r := NewRunner(nil)
	outcome, output, chunks := r.Dispatch(context.Background(), "exfil.stage", "")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED {
		t.Fatalf("outcome = %v, want SUCCEEDED", outcome)
	}
	if !strings.Contains(output, "no local staging area") {
		t.Fatalf("output = %q, want the no-staging marker", output)
	}
	if chunks != nil {
		t.Fatalf("chunks = %v, want nil for exfil.stage", chunks)
	}
}

func TestParsePushArgs(t *testing.T) {
	cases := []struct {
		in   string
		name string
		path string
		ok   bool
	}{
		{"", "", "", false},
		{"   ", "", "", false},
		{"name", "name", "", true},
		{"  name  ", "name", "", true},
		{"name /tmp/file", "name", "/tmp/file", true},
		{"name /tmp/with space.txt", "name", "/tmp/with space.txt", true},
	}
	for _, c := range cases {
		name, path, ok := parsePushArgs(c.in)
		if ok != c.ok || name != c.name || path != c.path {
			t.Errorf("parsePushArgs(%q) = (%q,%q,%v), want (%q,%q,%v)",
				c.in, name, path, ok, c.name, c.path, c.ok)
		}
	}
}

func TestSniffContentType(t *testing.T) {
	cases := []struct {
		ext  string
		want string
	}{
		{".txt", "text/plain"},
		{".log", "text/plain"},
		{".json", "application/json"},
		{".xml", "application/xml"},
		{".csv", "text/csv"},
		{".html", "text/html"},
		{".htm", "text/html"},
		{".png", "image/png"},
		{".jpeg", "image/jpeg"},
		{".pdf", "application/pdf"},
		{".bin", "application/octet-stream"},
		{"", "application/octet-stream"},
	}
	for _, c := range cases {
		got := sniffContentType("file"+c.ext, nil)
		if got != c.want {
			t.Errorf("sniffContentType(ext=%q) = %q, want %q", c.ext, got, c.want)
		}
	}
}
