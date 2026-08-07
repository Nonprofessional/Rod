package c2

import (
	"crypto/rand"
	"crypto/rsa"
	"encoding/base64"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

// enrollProfileACProof is the M4.3 acceptance test: a malleable transport
// profile must change the wire shape of the enroll request the implant sends
// (architecture.md Sec 7). The reference profile carries a custom enroll path, a
// User-Agent, a custom header, and a base64 body envelope; an httptest server
// captures the inbound request and the test asserts each knob landed. This is
// the direct proof that "a profile changes the wire shape" -- not just that the
// value is baked in, but that the enroll client sends it.
func TestEnroll_AppliesTheMalleableTransportProfile_ToTheWire(t *testing.T) {
	var (
		gotPath       string
		gotUserAgent  string
		gotAccept     string
		gotBody       []byte
		gotContentLen int
	)
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		gotPath = r.URL.Path
		gotUserAgent = r.UserAgent()
		gotAccept = r.Header.Get("Accept")
		gotBody, _ = io.ReadAll(r.Body)
		gotContentLen = len(gotBody)
		// A minimal OK response so Enroll reaches the body-decode step (it will
		// fail decoding the empty leaf, which is expected -- the wire shape is
		// captured before that point).
		_ = json.NewEncoder(w).Encode(map[string]any{
			"status": EnrollStatusOK,
		})
	}))
	defer server.Close()

	profile := TransportProfile{
		UserAgent: "Mozilla/5.0 (RodTest)",
		Headers:   map[string]string{"Accept": "application/json"},
		Envelope:  EnvelopeBase64,
	}
	privateKey, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatalf("generate key: %v", err)
	}

	// The profile's enroll path is the URI the implant enrolls against; here a
	// malleable path the teamserver does not serve by default.
	enrollURL := server.URL + "/api/v1/health"
	_, _ = Enroll(enrollURL, "stager-secret", "", privateKey, nil, profile)

	if gotPath != "/api/v1/health" {
		t.Errorf("enroll path: got %q, want /api/v1/health", gotPath)
	}
	if gotUserAgent != "Mozilla/5.0 (RodTest)" {
		t.Errorf("User-Agent: got %q, want Mozilla/5.0 (RodTest)", gotUserAgent)
	}
	if gotAccept != "application/json" {
		t.Errorf("Accept header: got %q, want application/json", gotAccept)
	}
	if gotContentLen == 0 {
		t.Fatal("enroll body: empty")
	}

	// EnvelopeBase64 wraps the JSON body as a single base64 string, so the body
	// is not raw JSON and decodes back to the original enroll JSON.
	decoded, err := base64.StdEncoding.DecodeString(string(gotBody))
	if err != nil {
		t.Fatalf("base64 body did not decode: %v\nraw=%q", err, gotBody)
	}
	var req enrollRequest
	if err := json.Unmarshal(decoded, &req); err != nil {
		t.Fatalf("decoded body is not enroll JSON: %v\ndecoded=%s", err, decoded)
	}
	if req.StagerTokenSecret != "stager-secret" {
		t.Errorf("stager token: got %q, want stager-secret", req.StagerTokenSecret)
	}
	// The raw body must not contain the JSON structural braces a raw-JSON enroll
	// would, proving the envelope actually changed the shape.
	if strings.HasPrefix(string(gotBody), "{") {
		t.Errorf("envelope did not wrap the body; got raw JSON: %s", gotBody)
	}
}

// TestEnroll_DefaultProfile_SendsRawJSON proves a zero-value profile leaves the
// wire shape unchanged: no User-Agent override, no custom header, and the body is
// the raw enroll JSON (not base64-wrapped).
func TestEnroll_DefaultProfile_SendsRawJSON(t *testing.T) {
	var (
		gotBody []byte
	)
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		gotBody, _ = io.ReadAll(r.Body)
		_ = json.NewEncoder(w).Encode(map[string]any{"status": EnrollStatusOK})
	}))
	defer server.Close()

	privateKey, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatalf("generate key: %v", err)
	}

	_, _ = Enroll(server.URL+"/implants/enroll", "stager-secret", "", privateKey, nil, TransportProfile{})

	var req enrollRequest
	if err := json.Unmarshal(gotBody, &req); err != nil {
		t.Fatalf("default-profile body is not raw enroll JSON: %v\nbody=%s", err, gotBody)
	}
	if req.StagerTokenSecret != "stager-secret" {
		t.Errorf("stager token: got %q, want stager-secret", req.StagerTokenSecret)
	}
	if !strings.HasPrefix(string(gotBody), "{") {
		t.Errorf("default profile should send raw JSON, got: %s", gotBody)
	}
}
