package exec

// recon.go holds the recon capability verbs the reference implant advertises
// (architecture.md Sec 10.1, roadmap M5.1): recon.portscan, recon.hostenum,
// recon.service. Each is a benign, portable reference handler -- it dials or
// introspects with the Go standard library and reports what it finds. As with
// shell.exec, it performs no evasion, no obfuscation, and no destructive
// behavior (RESPONSIBLE-USE.md, architecture.md Sec 7); the operator is
// responsible for targeting only systems they are authorized to test
// (RESPONSIBLE-USE.md).

import (
	"bufio"
	"context"
	"fmt"
	"net"
	"os"
	"runtime"
	"strconv"
	"strings"
	"time"

	"github.com/cw/rod/implant/rodpb"
)

// Per-port dial timeout for the network-touching recon verbs. Short so a wide
// port range finishes promptly; long enough that a reachable port on a quiet
// host completes its handshake.
const reconDialTimeout = 300 * time.Millisecond

// How long a service-probe waits for a banner after connecting.
const reconBannerTimeout = 500 * time.Millisecond

// portScan dials each port in "start-end" on host and reports the open ones,
// one per line as "<host>:<port> open". Arguments are "<host> <start-end>"
// (e.g. "127.0.0.1 1-1024"). Malformed arguments yield a Failed outcome with a
// clear cause; a closed range yields Succeeded with no lines.
func (r *Runner) portScan(ctx context.Context, arguments string) (rodpb.TaskOutcome, string) {
	host, startPort, endPort, ok := parseScanArgs(arguments)
	if !ok {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED, "recon.portscan expects '<host> <start-end>'"
	}

	var lines []string
	for port := startPort; port <= endPort; port++ {
		if ctx.Err() != nil {
			return rodpb.TaskOutcome_TASK_OUTCOME_FAILED, ctx.Err().Error()
		}
		if isOpen(ctx, host, port) {
			lines = append(lines, fmt.Sprintf("%s:%d open", host, port))
		}
	}
	if len(lines) == 0 {
		return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED, ""
	}
	return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED, strings.Join(lines, "\n")
}

// hostEnum reports local host facts: hostname, OS/arch, and the non-loopback
// unicast addresses on each interface. It introspects the running host and
// never probes a remote one, so the optional argument is informational only.
func (r *Runner) hostEnum(_ context.Context, _ string) (rodpb.TaskOutcome, string) {
	hostname, err := os.Hostname()
	if err != nil {
		hostname = "(unknown)"
	}

	var b strings.Builder
	fmt.Fprintf(&b, "hostname=%s\n", hostname)
	fmt.Fprintf(&b, "goos=%s goarch=%s\n", runtime.GOOS, runtime.GOARCH)

	ifaces, err := net.Interfaces()
	if err != nil {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED, "host enum failed to list interfaces: " + err.Error()
	}
	for _, iface := range ifaces {
		addrs, err := iface.Addrs()
		if err != nil {
			continue
		}
		for _, addr := range addrs {
			ip := extractIP(addr)
			if ip == nil || ip.IsLoopback() {
				continue
			}
			fmt.Fprintf(&b, "iface=%s ip=%s\n", iface.Name, ip.String())
		}
	}
	return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED, strings.TrimRight(b.String(), "\n")
}

// serviceProbe dials each listed port on host, reads a short banner from an
// open port, and reports one line per port as "<host>:<port> <banner-or-open>".
// Arguments are "<host> <port[,port2,...]>" (e.g. "127.0.0.1 22,80"). The
// outcome is Succeeded if at least one port was open, Failed otherwise.
func (r *Runner) serviceProbe(ctx context.Context, arguments string) (rodpb.TaskOutcome, string) {
	host, ports, ok := parseServiceArgs(arguments)
	if !ok {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED, "recon.service expects '<host> <port[,port2,...]>'"
	}

	var lines []string
	anyOpen := false
	for _, port := range ports {
		if ctx.Err() != nil {
			return rodpb.TaskOutcome_TASK_OUTCOME_FAILED, ctx.Err().Error()
		}
		banner, open := probeService(ctx, host, port)
		if !open {
			continue
		}
		anyOpen = true
		if banner == "" {
			banner = "open"
		}
		lines = append(lines, fmt.Sprintf("%s:%d %s", host, port, banner))
	}
	if !anyOpen {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED, fmt.Sprintf("no open ports on %s", host)
	}
	return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED, strings.Join(lines, "\n")
}

// parseScanArgs splits "<host> <start-end>" into its parts and validates the
// range. Ports stay in [1, 65535] and start <= end; the second token uses a
// hyphen separator to match the documented argument format.
func parseScanArgs(arguments string) (host string, startPort, endPort int, ok bool) {
	fields := strings.Fields(arguments)
	if len(fields) != 2 {
		return "", 0, 0, false
	}
	parts := strings.SplitN(fields[1], "-", 2)
	if len(parts) != 2 {
		return "", 0, 0, false
	}
	start, err := strconv.Atoi(parts[0])
	if err != nil {
		return "", 0, 0, false
	}
	end, err := strconv.Atoi(parts[1])
	if err != nil {
		return "", 0, 0, false
	}
	if !validPort(start) || !validPort(end) || start > end {
		return "", 0, 0, false
	}
	return fields[0], start, end, true
}

// parseServiceArgs splits "<host> <port[,port2,...]>" into the host and a
// validated list of ports.
func parseServiceArgs(arguments string) (host string, ports []int, ok bool) {
	fields := strings.Fields(arguments)
	if len(fields) != 2 {
		return "", nil, false
	}
	tokens := strings.Split(fields[1], ",")
	ports = make([]int, 0, len(tokens))
	for _, tok := range tokens {
		port, err := strconv.Atoi(strings.TrimSpace(tok))
		if err != nil || !validPort(port) {
			return "", nil, false
		}
		ports = append(ports, port)
	}
	if len(ports) == 0 {
		return "", nil, false
	}
	return fields[0], ports, true
}

// isOpen reports whether a TCP port accepts a connection within the recon dial
// timeout. A refused or timed-out dial is simply closed; the caller decides how
// to report it. The dial itself is bounded by reconDialTimeout; the caller's
// context is consulted before each dial so a cancelled scan stops promptly.
func isOpen(ctx context.Context, host string, port int) bool {
	if ctx.Err() != nil {
		return false
	}
	address := net.JoinHostPort(host, strconv.Itoa(port))
	conn, err := net.DialTimeout("tcp", address, reconDialTimeout)
	if err != nil {
		return false
	}
	_ = conn.Close()
	return true
}

// probeService dials a port and, on success, reads a short banner. Returns the
// banner (trimmed) and whether the port was open.
func probeService(ctx context.Context, host string, port int) (banner string, open bool) {
	if ctx.Err() != nil {
		return "", false
	}
	address := net.JoinHostPort(host, strconv.Itoa(port))
	conn, err := net.DialTimeout("tcp", address, reconDialTimeout)
	if err != nil {
		return "", false
	}
	defer conn.Close()

	// Many services greet on connect (SSH, SMTP, FTP); silent services
	// (HTTPS, etc.) send nothing. Read with a short deadline so the probe
	// does not stall, and treat a timeout as "open, no banner".
	_ = conn.SetReadDeadline(time.Now().Add(reconBannerTimeout))
	reader := bufio.NewReader(conn)
	line, err := reader.ReadString('\n')
	if err != nil && line == "" {
		return "", true
	}
	return strings.TrimRight(line, "\r\n"), true
}

// extractIP pulls a single IP from a net.Addr (interface or interface+mask
// forms), returning nil for non-IP addresses.
func extractIP(addr net.Addr) net.IP {
	if ipnet, ok := addr.(*net.IPNet); ok {
		return ipnet.IP
	}
	if ipaddr, ok := addr.(*net.IPAddr); ok {
		return ipaddr.IP
	}
	return nil
}

// validPort is the inclusive TCP port range.
func validPort(port int) bool {
	return port >= 1 && port <= 65535
}
