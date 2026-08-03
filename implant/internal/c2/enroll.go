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
type enrollRequest struct {
	StagerTokenSecret string  `json:"stagerTokenSecret"`
	Class             *string `json:"class,omitempty"`
	PublicKey         string  `json:"publicKey,omitempty"`
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
}

// Enrollment is the result of a successful enroll: the implant's identity, its
// engagement, the leaf certificate paired with its private key (so it can be
// presented in mTLS), and the CA chain to trust as the server identity.
type Enrollment struct {
	ImplantID    string
	EngagementID string
	// Leaf is the issued certificate paired with the implant's private key, ready
	// to present as a TLS client certificate.
	Leaf tls.Certificate
	// CAs are the teamserver CA(s), trusted as the mTLS server identity and used
	// to validate the leaf's chain at enroll.
	CAs []*x509.Certificate
}

// Enroll redeems the stager token at the teamserver, sending the implant's own
// public key, and returns the bound leaf paired with the private key. The
// implant owns its private key throughout; only the public half crosses the wire
// (architecture.md Sec 9). serverCAs pins which server identity to accept over
// the enroll TLS connection (empty trusts the system roots).
func Enroll(enrollURL, stagerToken string, privateKey *rsa.PrivateKey, serverCAs *x509.CertPool) (*Enrollment, error) {
	pubDER, err := x509.MarshalPKIXPublicKey(&privateKey.PublicKey)
	if err != nil {
		return nil, fmt.Errorf("marshal public key: %w", err)
	}
	body := enrollRequest{
		StagerTokenSecret: stagerToken,
		PublicKey:         base64.StdEncoding.EncodeToString(pubDER),
	}
	raw, err := json.Marshal(body)
	if err != nil {
		return nil, err
	}

	client := &http.Client{
		Timeout: 30 * time.Second,
		Transport: &http.Transport{
			TLSClientConfig: &tls.Config{RootCAs: serverCAs, MinVersion: tls.VersionTLS12},
		},
	}
	resp, err := client.Post(enrollURL, "application/json", bytes.NewReader(raw))
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
		CAs: cas,
	}, nil
}
