using System.Text.RegularExpressions;

namespace Rod.CoreState.Collaboration;

/// <summary>Which entity a task-reference token addresses.</summary>
public enum TaskReferenceKind
{
    /// <summary>A task of the engagement: <c>@task-&lt;id&gt;</c>. A link -- the
    /// console renders it, and in task input it passes through as written
    /// (a task id is already the value).</summary>
    Task,

    /// <summary>A harvested-credential row: <c>$cred-&lt;id&gt;</c>. In comments
    /// and chat the token stays a placeholder; in task input the server
    /// substitutes the secret at dispatch, so the plaintext never lands in
    /// any store.</summary>
    Credential,
}

/// <summary>
/// One task-reference token lifted out of operator text (architecture.md
/// Sec 15.3, ADR-005). Only well-formed ids extract: a marker followed by
/// something that is not a guid stays literal text, never a dangling
/// reference.
/// </summary>
public sealed record TaskReferenceToken(TaskReferenceKind Kind, Guid Id)
{
    /// <summary>The canonical wire form, the form the console inserts.</summary>
    public string Render()
        => $"{(Kind == TaskReferenceKind.Task ? "@task-" : "$cred-")}{Id}";
}

/// <summary>
/// The token grammar and lifter for task references. One implementation, so
/// comment publish, chat send, and task issuance validate exactly the same
/// tokens against exactly the same stores.
/// </summary>
public static partial class TaskReferences
{
    // @task-<guid> / $cred-<guid>, in both guid forms, bounded so a longer
    // hex run is not half-matched into an id.
    [GeneratedRegex(
        @"(@task-|\$cred-)([0-9a-fA-F]{32}|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})(?![0-9a-fA-F-])")]
    private static partial Regex TokenPattern();

    /// <summary>Every well-formed reference token in <paramref name="text"/>, in order.</summary>
    public static IReadOnlyList<TaskReferenceToken> Extract(string text)
    {
        var tokens = new List<TaskReferenceToken>();
        foreach (var match in TokenPattern().EnumerateMatches(text))
        {
            var marker = text[match.Index..(match.Index + 6)];
            var idText = text[(match.Index + marker.Length)..(match.Index + match.Length)];
            if (!Guid.TryParse(idText, out var id))
                continue;
            tokens.Add(new TaskReferenceToken(
                marker.StartsWith('@') ? TaskReferenceKind.Task : TaskReferenceKind.Credential,
                id));
        }

        return tokens;
    }

    /// <summary>True when the text holds at least one credential token -- the
    /// dispatch path's cheap guard before it goes to the store.</summary>
    public static bool ContainsCredential(string text)
    {
        foreach (var match in TokenPattern().EnumerateMatches(text))
        {
            if (text[match.Index] == '$')
                return true;
        }

        return false;
    }

    /// <summary>
    /// The raw credential-token spans of <paramref name="text"/> (start,
    /// length), for the substitution path's span-precise replacement.
    /// ValueMatch is a ref struct, so the spans arrive as plain indices.
    /// </summary>
    public static IReadOnlyList<(int Index, int Length)> CredentialSpans(string text)
    {
        List<(int, int)> spans = [];
        foreach (var match in TokenPattern().EnumerateMatches(text))
        {
            if (text[match.Index] == '$')
                spans.Add((match.Index, match.Length));
        }

        return spans;
    }
}
