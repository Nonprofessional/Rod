namespace Rod.Capabilities;

/// <summary>
/// The declaration of one capability a module provides (architecture.md Sec 10):
/// a namespaced verb (<c>namespace.action</c>, e.g. <c>shell.exec</c>), its
/// <see cref="Category"/>, a <see cref="Version"/>, a free-form set of
/// <see cref="Attributes"/>, and the contract-trilogy metadata
/// (architecture.md Sec 15.2, ADR-003): the <see cref="Parameters"/> schema the
/// operator UI renders its task form from, and the <see cref="Output"/> taxonomy
/// kind that routes its results to the generic panels. The teamserver gates
/// dispatch on the verb an implant advertises; the descriptor is what a module
/// registers so the dispatcher knows it exists and where to route it.
/// </summary>
/// <remarks>
/// <see cref="Attributes"/> is the per-command OPSEC metadata surface
/// (architecture.md Sec 7): flags such as "writes to disk" that let operators and
/// tradecraft filters avoid risky actions. This layer holds only the shape; the
/// concrete attributes a real module sets arrive with that module.
///
/// <see cref="Parameters"/> and <see cref="Output"/> are descriptor metadata,
/// never wire shape: task arguments stay the opaque string of Sec 10, parsed by
/// the verb's own handler, and results stay captured output -- the schema and
/// taxonomy tell the UI how to render both for a verb it has never met.
/// </remarks>
public sealed record CapabilityDescriptor(
    string Verb,
    CapabilityCategory Category,
    string Version,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<VerbParameter>? Parameters = null,
    VerbOutput? Output = null,
    IReadOnlyList<string>? Techniques = null)
{
    /// <summary>
    /// Builds a descriptor. Optional <paramref name="parameters"/> and
    /// <paramref name="output"/> carry the contract-trilogy metadata; a
    /// descriptor without them renders the generic raw-arguments form and
    /// plain-text output, which is exactly the honest fallback for a verb
    /// nobody has described yet. Optional <paramref name="techniques"/> is the
    /// module's ATT&CK declaration (ADR-007): when set it replaces the kernel
    /// mapping's whole entry for the verb -- the same authority a registration
    /// carries everywhere else -- and when null the kernel entry stands.
    /// </summary>
    public static CapabilityDescriptor Of(
        string verb,
        CapabilityCategory category,
        string version,
        IReadOnlyDictionary<string, string>? attributes = null,
        IReadOnlyList<VerbParameter>? parameters = null,
        VerbOutput? output = null,
        IReadOnlyList<string>? techniques = null)
        => new(
            verb,
            category,
            version,
            attributes ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            parameters,
            output,
            techniques);

    /// <summary>
    /// Validates the parameter schema's own invariants, reported as
    /// human-readable errors the registering module's author can act on:
    /// unique non-empty field names, a choice set on enum parameters (and
    /// only there), no trailing parameter anywhere but last, and no schema
    /// after a trailing parameter (which would be unreachable). Registration
    /// refuses a descriptor that fails this -- a form the UI cannot render is
    /// a module the operator cannot task.
    /// </summary>
    public IReadOnlyList<string> ValidateParameters()
    {
        var errors = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < (Parameters?.Count ?? 0); i++)
        {
            var parameter = Parameters![i];
            if (string.IsNullOrWhiteSpace(parameter.Name))
                errors.Add($"parameters[{i}].name is required.");
            else if (!names.Add(parameter.Name))
                errors.Add($"parameter name '{parameter.Name}' is declared twice.");
            if (string.IsNullOrWhiteSpace(parameter.Label))
                errors.Add($"parameter '{parameter.Name}' needs a label.");

            if (parameter.Kind == VerbParameterKind.Enum
                && (parameter.Choices is not { Count: > 0 }))
            {
                errors.Add($"parameter '{parameter.Name}' is an enum and needs at least one choice.");
            }

            if (parameter.Kind == VerbParameterKind.Enum && parameter.Choices is { Count: > 1 }
                && parameter.Choices.Distinct(StringComparer.Ordinal).Count() != parameter.Choices.Count)
            {
                errors.Add($"parameter '{parameter.Name}' carries duplicate choices.");
            }

            if (parameter.Trailing && i != Parameters!.Count - 1)
                errors.Add($"parameter '{parameter.Name}' is trailing but not last; only the final parameter may take the raw remainder.");
        }

        return errors;
    }

    /// <summary>
    /// Validates the ATT&CK declaration's own shape: every id a well-formed
    /// <c>T####</c> or <c>T####.###</c>, no duplicates, and not an empty list
    /// (an undeclared mapping is <see langword="null"/>, not empty).
    /// Registration refuses a descriptor that fails this -- the coverage
    /// matrix and the Navigator layer both render these ids verbatim.
    /// </summary>
    public IReadOnlyList<string> ValidateTechniques()
    {
        var errors = new List<string>();
        if (Techniques is null)
            return errors;

        if (Techniques.Count == 0)
        {
            errors.Add("techniques is empty; omit it to let the kernel mapping stand.");
            return errors;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Techniques.Count; i++)
        {
            var id = Techniques[i];
            if (!IsTechniqueId(id))
                errors.Add($"techniques[{i}] '{id}' is not an ATT&CK technique id (T#### or T####.###).");
            if (!seen.Add(id))
                errors.Add($"techniques carries '{id}' twice.");
        }

        return errors;
    }

    internal static bool IsTechniqueId(string id)
        => id is { Length: 5 or 9 }
            && id[0] == 'T'
            && Digits(id.Substring(1, 4))
            && (id.Length == 5 || (id[5] == '.' && Digits(id.Substring(6, 3))));

    private static bool Digits(string s)
    {
        foreach (var c in s)
            if (!char.IsDigit(c))
                return false;
        return true;
    }
}
