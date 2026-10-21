using System.Text.RegularExpressions;

namespace Rod.Cli.Manifest;

/// <summary>
/// The entry kind a module ships as (architecture.md Sec 15.2): a
/// server-side capability module, an implant-side handler module, or a
/// build unit. The kind decides how an installed package is consumed, not
/// how it travels -- one manifest shape, one registry, one CLI.
/// </summary>
public enum ModuleEntryKind
{
    Server,
    Implant,
    BuildUnit,
}

public static class ModuleEntryKindNames
{
    public static bool TryParse(string text, out ModuleEntryKind kind)
    {
        switch (text)
        {
            case "server": kind = ModuleEntryKind.Server; return true;
            case "implant": kind = ModuleEntryKind.Implant; return true;
            case "build-unit": kind = ModuleEntryKind.BuildUnit; return true;
            default: kind = default; return false;
        }
    }

    public static string ToString(ModuleEntryKind kind) => kind switch
    {
        ModuleEntryKind.Server => "server",
        ModuleEntryKind.Implant => "implant",
        ModuleEntryKind.BuildUnit => "build-unit",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>A row of the manifest's compatibility matrix: which implant
/// class, on which platform and arch, the module's execution half serves.</summary>
public sealed record TargetMatrix(string ImplantClass, string Platform, string Arch);

/// <summary>The manifest's OPSEC metadata: what running the module leaves on
/// the target (architecture.md Sec 15.2 -- "lands on disk" / "memory only"
/// carried into the catalog, not left to prose).</summary>
public sealed record OpsecMetadata(bool LandsOnDisk, string? Notes);

/// <summary>Which files <c>rod mod publish</c> packs; empty include means
/// everything under the module directory minus the excludes.</summary>
public sealed record PackageRules(IReadOnlyList<string> Include, IReadOnlyList<string> Exclude);

/// <summary>
/// The parsed <c>rod-module.toml</c> (ADR-009): the self-describing module
/// contract shared by all three entry kinds. This model is the authority for
/// the canonical form that gets signed -- every field it carries participates
/// in the signature, so a package cannot lie about its metadata.
/// </summary>
public sealed record ModuleManifest
{
    public required string Name { get; init; }
    public required SemVer Version { get; init; }
    public required ModuleEntryKind Entry { get; init; }
    public required string License { get; init; }
    public string? Description { get; init; }
    public string? Homepage { get; init; }
    public required SemVer TeamserverMin { get; init; }
    public IReadOnlyList<TargetMatrix> Targets { get; init; } = [];
    public required OpsecMetadata Opsec { get; init; }
    public IReadOnlyList<string> Techniques { get; init; } = [];
    public PackageRules? Package { get; init; }

    private static readonly Regex NamePattern = new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex TechniquePattern = new("^T\\d{4}(\\.\\d{3})?$", RegexOptions.Compiled);

    /// <summary>The name rule shared by the manifest validator and the
    /// scaffold: kebab-case, single segment, at most 64 characters.</summary>
    public static bool ValidName(string name) => NamePattern.IsMatch(name);

    /// <summary>
    /// Field-level validation, reported as human-readable errors. The rules
    /// that matter for trust: single-line fields only (they feed the
    /// canonical form line-per-field), a kebab-case name, and ATT&CK
    /// technique ids in their canonical shape.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (!NamePattern.IsMatch(Name))
            errors.Add($"module.name '{Name}' must be kebab-case: lowercase letters, digits, hyphens, at most 64 characters.");
        if (!SemVer.TryParse(Version.ToString(), out _))
            errors.Add($"module.version '{Version}' is not a SemVer version.");
        if (License.Length == 0)
            errors.Add("module.license is required; use an SPDX identifier.");
        foreach (var (field, value) in new[] { ("module.license", License), ("module.description", Description ?? ""), ("module.homepage", Homepage ?? ""), ("opsec.notes", Opsec.Notes ?? "") })
        {
            if (value.Contains('\n') || value.Contains('\r'))
                errors.Add($"{field} must be a single line (it participates in the signed canonical form).");
        }

        for (var i = 0; i < Targets.Count; i++)
        {
            var target = Targets[i];
            if (target.ImplantClass.Length == 0 || target.Platform.Length == 0 || target.Arch.Length == 0)
                errors.Add($"compatibility.targets[{i}] must carry non-empty implant-class, platform, and arch.");
            foreach (var part in new[] { target.ImplantClass, target.Platform, target.Arch })
            {
                if (part.Contains('\n') || part.Contains('\r') || part.Contains('/'))
                    errors.Add($"compatibility.targets[{i}] fields must be single-line and contain no '/'.");
            }
        }

        for (var i = 0; i < Techniques.Count; i++)
        {
            if (!TechniquePattern.IsMatch(Techniques[i]))
                errors.Add($"attack.techniques[{i}] '{Techniques[i]}' is not an ATT&CK technique id (T#### or T####.###).");
        }

        return errors;
    }
}
