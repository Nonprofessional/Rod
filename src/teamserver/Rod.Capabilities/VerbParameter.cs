namespace Rod.Capabilities;

/// <summary>
/// The kind of one task-verb parameter (architecture.md Sec 15.2, the
/// contract trilogy's first contract). The kind is presentation and
/// serialization metadata for the operator UI's generic task form -- it never
/// reaches the wire, where task arguments stay the verb's own opaque string
/// (architecture.md Sec 10).
/// </summary>
public enum VerbParameterKind
{
    /// <summary>A single-line token (a path, a host, a name).</summary>
    String,

    /// <summary>Free text that may span spaces or lines (a command, a unit
    /// body); renders wide.</summary>
    Text,

    /// <summary>An integer.</summary>
    Number,

    /// <summary>A true/false flag; an unchecked optional parameter is
    /// omitted from the arguments rather than serialized false.</summary>
    Boolean,

    /// <summary>One of a closed choice set; renders as a picker.</summary>
    Enum,

    /// <summary>An operator-supplied file. Serialization rides the task's
    /// inline or staged content arm, never the space-joined tokens.</summary>
    File,

    /// <summary>A list of tokens the verb's grammar reads as one comma-joined
    /// argument (architecture.md Sec 10 names comma lists among the per-verb
    /// grammars).</summary>
    Array,
}

/// <summary>
/// One declared parameter of a verb: the field the operator UI renders
/// generically, in declaration order, for a verb it has never seen
/// (architecture.md Sec 15.2). The schema describes the form; the verb's own
/// handler stays the parser and the authority.
/// </summary>
/// <param name="Name">The field key, unique within the verb.</param>
/// <param name="Label">The human label the form shows.</param>
/// <param name="Kind">The rendering and serialization kind.</param>
/// <param name="Required">Whether the task cannot issue without a value.</param>
/// <param name="Help">One sentence of context under the field.</param>
/// <param name="Placeholder">The input's placeholder text.</param>
/// <param name="Choices">The closed choice set; required for
/// <see cref="VerbParameterKind.Enum"/> and meaningless otherwise.</param>
/// <param name="Trailing">Marks the final parameter as the raw remainder of
/// the argument string after the preceding space-joined tokens -- the
/// trailing-command shape several grammars use (architecture.md Sec 10).
/// Only legal on the last declared parameter.</param>
/// <param name="Inline">For <see cref="VerbParameterKind.File"/> only: whether
/// the verb accepts the bytes as inline base64 in the arguments (under the
/// 1 MiB ceiling). False means the bytes always ride the staged content arm
/// -- the shape <c>module.load</c> carries, whose handler refuses the inline
/// grammar outright.</param>
public sealed record VerbParameter(
    string Name,
    string Label,
    VerbParameterKind Kind,
    bool Required = false,
    string? Help = null,
    string? Placeholder = null,
    IReadOnlyList<string>? Choices = null,
    bool Trailing = false,
    bool Inline = true)
{
    /// <summary>Shorthand for the common single-line required token.</summary>
    public static VerbParameter Token(string name, string label, string? placeholder = null, string? help = null)
        => new(name, label, VerbParameterKind.String, Required: true, Placeholder: placeholder, Help: help);
}
