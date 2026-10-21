using Rod.CoreState.Tasks;

namespace Rod.CoreState.Collaboration;

/// <summary>
/// The task-reference use cases (architecture.md Sec 15.3, ADR-005):
/// validation at publish -- every well-formed token in a comment, a chat
/// message, or task input must address an entity of the same engagement --
/// and credential substitution at dispatch -- <c>$cred-&lt;id&gt;</c> in
/// task input resolves to the stored secret on the wire, while every
/// stored form (task row, audit trail, transcript) keeps the placeholder.
/// One service so comment publish, chat send, task issuance, and dispatch
/// resolve against exactly the same stores.
/// </summary>
public sealed class TaskReferenceService
{
    private readonly ITaskRepository _tasks;
    private readonly ICredentialStore _credentials;

    public TaskReferenceService(ITaskRepository tasks, ICredentialStore credentials)
    {
        _tasks = tasks;
        _credentials = credentials;
    }

    /// <summary>
    /// The tokens in <paramref name="text"/> that do not resolve inside
    /// <paramref name="engagement"/> -- empty when the text's references are
    /// all sound. The caller decides the refusal's shape; this service only
    /// names the offenders.
    /// </summary>
    public async Task<IReadOnlyList<TaskReferenceToken>> UnresolvedAsync(
        EngagementId engagement,
        string text,
        CancellationToken cancellationToken = default)
    {
        var unresolved = new List<TaskReferenceToken>();
        foreach (var token in TaskReferences.Extract(text))
        {
            switch (token.Kind)
            {
                case TaskReferenceKind.Task:
                    var task = await _tasks.FindAsync(new TaskId(token.Id), cancellationToken);
                    if (task is null || task.EngagementId != engagement)
                        unresolved.Add(token);
                    break;
                case TaskReferenceKind.Credential:
                    var credential = await _credentials.FindAsync(new CredentialId(token.Id), cancellationToken);
                    if (credential is null || credential.EngagementId != engagement)
                        unresolved.Add(token);
                    break;
            }
        }

        return unresolved;
    }

    /// <summary>
    /// Replaces every credential token in <paramref name="text"/> with the
    /// stored secret, for the dispatch wire and nothing else. A token whose
    /// row no longer resolves (an in-memory store lost to a restart, the
    /// degenerate case) stays as written -- the placeholder reaching the
    /// implant is visible in the task's output, where a silent empty
    /// substitution would not be. Task tokens pass through: a task id is
    /// already the value.
    /// </summary>
    public async Task<string> SubstituteCredentialsAsync(
        EngagementId engagement,
        string text,
        CancellationToken cancellationToken = default)
    {
        // Replace by matched span, not by rendered token: the text may carry
        // either guid form and the replacement must hit what was written.
        var replacements = new List<(int Index, int Length, string Secret)>();
        foreach (var (index, length) in TaskReferences.CredentialSpans(text))
        {
            var idText = text[(index + 6)..(index + length)];
            if (!Guid.TryParse(idText, out var id))
                continue;
            var record = await _credentials.FindAsync(new CredentialId(id), cancellationToken);
            if (record is null || record.EngagementId != engagement || record.Secret is not { Length: > 0 } secret)
                continue;
            replacements.Add((index, length, secret));
        }

        if (replacements.Count == 0)
            return text;

        var resolved = new System.Text.StringBuilder(text.Length);
        var written = 0;
        foreach (var (index, length, secret) in replacements)
        {
            resolved.Append(text, written, index - written);
            resolved.Append(secret);
            written = index + length;
        }

        resolved.Append(text, written, text.Length - written);
        return resolved.ToString();
    }
}
