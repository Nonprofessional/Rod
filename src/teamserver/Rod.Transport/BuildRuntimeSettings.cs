using System.Text.Json;
using Rod.BuildPipeline.PayloadBuild;

namespace Rod.Transport;

/// <summary>
/// The runtime-mutable build settings: the shared cargo target directory
/// (<see cref="RustTargetDir"/>) -- the warm compile cache behind
/// <c>ROD_RUST_TARGET_DIR</c>. Boot default is that environment variable
/// (the composition shape a service unit sets); the settings page then
/// changes the value live, every build reading the current one, and the
/// change persists to a small JSON file so a restart keeps it. The
/// persistence is best-effort, the same posture as the session settings:
/// the in-memory value is authoritative while the process lives.
/// </summary>
public sealed class BuildRuntimeSettings : IBuildRuntimeSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string? _persistencePath;
    private volatile string? _rustTargetDir;

    public BuildRuntimeSettings(string? persistencePath, string? bootDefault = null)
    {
        _persistencePath = persistencePath;
        _rustTargetDir = Normalize(bootDefault);
        if (persistencePath is null || !File.Exists(persistencePath))
            return;

        // A persisted operator change outranks the boot default; an
        // unparseable file falls back to it, and the next settings write
        // repairs the file.
        try
        {
            var persisted = JsonSerializer.Deserialize<PersistedValues>(
                File.ReadAllText(persistencePath), JsonOptions);
            if (persisted?.RustTargetDir is not null)
                _rustTargetDir = Normalize(persisted.RustTargetDir);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            // Fall back to the boot default.
        }
    }

    public string? RustTargetDir => _rustTargetDir;

    /// <summary>
    /// Replaces the live value and persists it. An empty or null value
    /// returns to hermetic per-build target dirs; a non-empty one must be
    /// an absolute path (a relative one would resolve inside each build's
    /// disposable staging dir, which is the hermetic shape wearing a cache's
    /// name). Throws <see cref="ArgumentException"/> outside that contract;
    /// the endpoint maps it to a 400.
    /// </summary>
    public string? Change(string? rustTargetDir)
    {
        var value = Normalize(rustTargetDir);
        _rustTargetDir = value;
        Persist(value);
        return value;
    }

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        if (!Path.IsPathRooted(trimmed))
            throw new ArgumentException(
                $"The build cache directory must be an absolute path (got '{trimmed}'): a relative one would "
                + "resolve inside each build's disposable staging dir.");
        return trimmed;
    }

    // Write-then-move so the next boot never observes a torn file; failures
    // are swallowed because the live value is already applied.
    private void Persist(string? value)
    {
        if (_persistencePath is null)
            return;
        try
        {
            var json = JsonSerializer.Serialize(
                new PersistedValues(value ?? ""), JsonOptions);
            var temporary = _persistencePath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _persistencePath, overwrite: true);
        }
        catch (Exception)
        {
            // Best-effort persistence; the live value stands.
        }
    }

    private sealed record PersistedValues(string? RustTargetDir);
}
