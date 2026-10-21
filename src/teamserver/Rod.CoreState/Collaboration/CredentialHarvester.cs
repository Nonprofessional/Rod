using Rod.CoreState.Application;
using Rod.CoreState.Implants;

namespace Rod.CoreState.Collaboration;

/// <summary>
/// Lands a completed task's harvest in the credential store (architecture.md
/// Sec 15.3, ADR-005): when the verb declares the credential output kind,
/// the output's rows parse against the credential grammar and persist once,
/// keyed by the capturing task -- the stable ids a task-reference
/// placeholder addresses and the search index reads. Called on the
/// completion path, so every transport's result lands the same rows.
/// </summary>
public sealed class CredentialHarvester
{
    private readonly ICredentialStore _credentials;
    private readonly IVerbOutputResolver _outputKinds;

    public CredentialHarvester(ICredentialStore credentials, IVerbOutputResolver outputKinds)
    {
        _credentials = credentials;
        _outputKinds = outputKinds;
    }

    /// <summary>
    /// Captures one completed task's rows. Idempotent per task: a task whose
    /// rows already landed never lands them again.
    /// </summary>
    public async Task CaptureAsync(TaskCompleted completed, CancellationToken cancellationToken = default)
    {
        if (_outputKinds.OutputKind(completed.Verb) != TaskOutputKind.Credential
            || string.IsNullOrEmpty(completed.Output))
        {
            return;
        }

        var existing = await _credentials.ListBySourceTaskAsync(
            completed.EngagementId, completed.TaskId, cancellationToken);
        if (existing.Count > 0)
            return;

        var parsed = 0;
        foreach (var line in completed.Output.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (parsed >= CollaborationLimits.MaxCredentialRowsPerTask)
                break;

            try
            {
                if (!CredentialRow.TryParse(line, out var row))
                    continue;
                await _credentials.AddAsync(
                    new CredentialRecord(
                        CredentialId.New(),
                        completed.EngagementId,
                        completed.TaskId,
                        completed.ImplantId,
                        row.Kind,
                        row.Principal,
                        row.Secret,
                        row.Source,
                        row.Host,
                        row.Note,
                        completed.CompletedAt),
                    cancellationToken);
            }
            catch (System.Text.Json.JsonException)
            {
                continue; // Not a row this grammar reads; the transcript keeps it.
            }

            parsed++;
        }
    }
}
