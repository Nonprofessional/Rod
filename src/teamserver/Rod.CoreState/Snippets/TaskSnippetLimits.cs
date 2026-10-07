namespace Rod.CoreState.Snippets;

/// <summary>
/// The snippet surface's standing boundaries: how much of the keyboard a
/// saved sequence may stand for. These are domain rules, not console knobs
/// -- every snippet passes them at save, so a snippet stored in the
/// engagement is a snippet the console can replay.
/// </summary>
public static class TaskSnippetLimits
{
    /// <summary>
    /// How many steps one snippet may hold. A snippet is a typed-out
    /// sequence under one name; a sequence longer than this is a script
    /// and belongs in the operator's tooling, not the engagement's state.
    /// </summary>
    public const int MaxStepsPerSnippet = 20;

    /// <summary>
    /// How many snippets one engagement may hold, the same ceiling
    /// automation rules carry: a saved sequence is standing convenience,
    /// and an engagement that needs more has a scripting problem.
    /// </summary>
    public const int MaxSnippetsPerEngagement = 50;

    /// <summary>Bounds the snippet's display name.</summary>
    public const int MaxNameBytes = 200;

    /// <summary>Bounds a step's verb, the same ceiling a descriptor carries.</summary>
    public const int MaxVerbBytes = 200;

    /// <summary>
    /// Bounds a step's arguments string, the same ceiling task issuance
    /// and an automation action apply.
    /// </summary>
    public const int MaxArgumentBytes = 512 * 1024;
}
