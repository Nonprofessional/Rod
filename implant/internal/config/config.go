// Package config carries the per-implant profile the reference Go implant
// runs with. In the full design the profile is embedded into the artifact at
// generation time (architecture.md Sec 5.1) -- sleep, jitter, kill date, and the
// C2 endpoint are baked in so each implant is self-contained. The reference
// implant takes them as flags/env instead, so a single binary runs against any
// teamserver and the build unit can inject them via -ldflags without edits here.
package config

import (
	"flag"
	"fmt"
	"os"
	"strings"
	"time"
)

// Config is everything the reference implant needs to enroll and beacon.
type Config struct {
	// EnrollURL is the http(s) URL of the teamserver enroll endpoint
	// (/implants/enroll). The implant redeems its stager token here and
	// receives the leaf certificate plus CA chain bound to
	// (implant_id, engagement_id) (architecture.md Sec 9).
	EnrollURL string
	// BeaconURL is the http(s) URL of the mTLS beacon endpoint (the gRPC
	// Beacon.CheckIn stream). The implant opens a long-lived reverse connection
	// here after enrolling (architecture.md Sec 5/8).
	BeaconURL string
	// StagerToken is the one-use secret the operator minted for the engagement.
	StagerToken string
	// Sleep is the base interval between check-ins. Jitter is applied on top to
	// avoid periodic-check-in detection (architecture.md Sec 7).
	Sleep time.Duration
	// Jitter is the random delta added to each Sleep. Half the window either
	// side, so a 10s jitter on a 30s sleep yields 25s..35s.
	Jitter time.Duration
	// KillDate is the hard self-termination timestamp (architecture.md Sec 7).
	// Past it the implant exits and refuses to run. Enforcement is recorded-only
	// in this milestone; full enforcement arrives with M4.2.
	KillDate time.Time
	// CACertPEM, optionally, pins the teamserver CA the implant trusts as the mTLS
	// server identity. When empty the implant trusts the CA chain returned at
	// enroll (the dev CA shape). Letting enroll supply it keeps the reference
	// binary CA-agnostic; a real deployment pins it at build time.
	CACertPEM string
}

// Parse builds a Config from command-line flags, falling back to the matching
// ROD_ environment variable for each. Flags win over env. Required fields that
// are still empty after both are rejected with a usage error.
func Parse(args []string) (Config, error) {
	fs := flag.NewFlagSet("rod-implant", flag.ContinueOnError)
	var c Config
	fs.StringVar(&c.EnrollURL, "enroll-url", env("ROD_ENROLL_URL", ""), "teamserver enroll endpoint (https://host:port/implants/enroll)")
	fs.StringVar(&c.BeaconURL, "beacon-url", env("ROD_BEACON_URL", ""), "teamserver mTLS beacon endpoint (https://host:port)")
	fs.StringVar(&c.StagerToken, "token", env("ROD_STAGER_TOKEN", ""), "stager token secret redeeming at enroll")
	fs.DurationVar(&c.Sleep, "sleep", envDuration("ROD_SLEEP", 30*time.Second), "beacon sleep interval")
	fs.DurationVar(&c.Jitter, "jitter", envDuration("ROD_JITTER", 10*time.Second), "beacon jitter interval")
	kill := fs.String("kill-date", env("ROD_KILL_DATE", ""), "RFC3339 kill date past which the implant exits")
	fs.StringVar(&c.CACertPEM, "ca-cert", env("ROD_CA_CERT", ""), "optional PEM-encoded teamserver CA to trust")
	if err := fs.Parse(args); err != nil {
		return c, err
	}
	if *kill != "" {
		t, err := time.Parse(time.RFC3339, *kill)
		if err != nil {
			return c, fmt.Errorf("kill-date: %w", err)
		}
		c.KillDate = t
	}
	return c, c.Validate()
}

// Validate enforces the required fields. BeaconURL may be empty when the
// enroll/beacon hosts coincide; BeaconURLFor derives it from EnrollURL then.
func (c Config) Validate() error {
	var missing []string
	if c.EnrollURL == "" {
		missing = append(missing, "-enroll-url/ROD_ENROLL_URL")
	}
	if c.StagerToken == "" {
		missing = append(missing, "-token/ROD_STAGER_TOKEN")
	}
	if len(missing) > 0 {
		return fmt.Errorf("missing required config: %s", strings.Join(missing, ", "))
	}
	return nil
}

func env(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}

func envDuration(key string, fallback time.Duration) time.Duration {
	if v := os.Getenv(key); v != "" {
		if d, err := time.ParseDuration(v); err == nil {
			return d
		}
	}
	return fallback
}
