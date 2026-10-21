using System.Text.Json;

namespace Rod.CoreState.Collaboration;

/// <summary>
/// The credential-row grammar (architecture.md Sec 15.2, the contract
/// trilogy's output contract): one JSON object per output line,
/// <c>kind</c> and <c>principal</c> required, <c>secret</c>/<c>source</c>/
/// <c>host</c>/<c>note</c> optional, unknown fields ignored. A line that
/// does not parse stays in the transcript and lands nowhere else. The
/// grammar lives here so the harvest path and any consumer share one
/// parser.
/// </summary>
public static class CredentialRow
{
    /// <summary>
    /// Parses one credential row from an output line. Returns false on a
    /// line that is not a row this grammar can read; throws only the
    /// <see cref="JsonException"/> the caller already tolerates for
    /// malformed JSON.
    /// </summary>
    public static bool TryParse(string line, out (string Kind, string Principal, string? Secret, string? Source, string? Host, string? Note) row)
    {
        row = default;
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        if (!root.TryGetProperty("kind", out var kindElement)
            || kindElement.ValueKind != JsonValueKind.String
            || kindElement.GetString() is not { Length: > 0 } kind)
        {
            return false;
        }

        if (!root.TryGetProperty("principal", out var principalElement)
            || principalElement.ValueKind != JsonValueKind.String
            || principalElement.GetString() is not { Length: > 0 } principal)
        {
            return false;
        }

        string? StringProperty(string name)
            => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;

        row = (kind, principal, StringProperty("secret"), StringProperty("source"), StringProperty("host"), StringProperty("note"));
        return true;
    }
}
