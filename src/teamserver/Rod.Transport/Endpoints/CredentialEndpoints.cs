using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Tasks;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The engagement's credential rows (architecture.md Sec 15.2, the contract
/// trilogy's second contract): assembled at read time, never stored. Every
/// completed task whose verb declares the credential output kind parses
/// against the documented JSON-lines grammar -- one row per line, unknown
/// fields ignored, a line that does not parse stays in the transcript -- so
/// the credential panel reads any module's harvest, framework verb or not,
/// the same way the topology projection reads enumeration findings. The
/// persisted credential entity (reuse through task references, rotation
/// marks) is the entity-model item's work, not this projection's.
///
/// Read-only by construction and scoped by engagement (architecture.md
/// Sec 3): the engagement id in the path binds every task the projection
/// walks. Secret material renders masked by default in the UI; the row
/// carries it because the operator's report needs what was actually
/// harvested, and it never enters the audit chain (Sec 11's mutable-content
/// rule cuts the other way: chain events carry operational facts, not
/// harvest payloads).
/// </summary>
public static class CredentialEndpoints
{
    // A harvest against a large environment can print a lot of lines; past
    // this the parse stops per task, the read stays bounded no matter what a
    // handler printed -- the same ceiling the topology parse holds.
    private const int MaxRowsPerTask = 10_000;

    public static IEndpointRouteBuilder MapCredentialEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/engagements/{engagementId}/credentials", ListHarvestAsync)
            .RequireAuthorization().AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Read))
            .WithName(nameof(ListHarvestAsync));
        return endpoints;
    }

    private static async Task<IResult> ListHarvestAsync(
        string engagementId,
        IEngagementRepository engagements,
        ITaskRepository tasks,
        IVerbOutputResolver outputKinds,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        var engagement = await engagements.FindAsync(new EngagementId(engagementValue), cancellationToken);
        if (engagement is null)
            return Results.NotFound(new Problem("Engagement does not exist."));

        var rows = new List<CredentialRowResponse>();
        var history = await tasks.ListByEngagementAsync(new EngagementId(engagementValue), cancellationToken);
        foreach (var task in history)
        {
            if (task.Status != Rod.CoreState.Tasks.TaskStatus.Completed
                || outputKinds.OutputKind(task.Verb) != TaskOutputKind.Credential
                || string.IsNullOrEmpty(task.Output))
            {
                continue;
            }

            var parsed = 0;
            foreach (var line in task.Output.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                if (parsed >= MaxRowsPerTask)
                    break;

                CredentialRowResponse? row;
                try
                {
                    row = ParseRow(line);
                }
                catch (JsonException)
                {
                    continue; // Not a row this view can read; the transcript keeps it.
                }

                if (row is null)
                    continue;

                parsed++;
                rows.Add(row with
                {
                    TaskId = task.Id.ToString(),
                    ImplantId = task.ImplantId.ToString(),
                    Verb = task.Verb,
                    CapturedAt = task.CompletedAt ?? task.CreatedAt,
                });
            }
        }

        return Results.Ok(new CredentialListResponse([.. rows
            .OrderBy(r => r.CapturedAt)
            .ThenBy(r => r.Principal, StringComparer.Ordinal)]));
    }

    // One credential row: the grammar is one JSON object per line with
    // "kind" and "principal" required, everything else optional and unknown
    // fields ignored -- the same leniency the recon finding grammar holds.
    private static CredentialRowResponse? ParseRow(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (!root.TryGetProperty("kind", out var kindElement)
            || kindElement.ValueKind != JsonValueKind.String
            || kindElement.GetString() is not { Length: > 0 } kind)
        {
            return null;
        }

        if (!root.TryGetProperty("principal", out var principalElement)
            || principalElement.ValueKind != JsonValueKind.String
            || principalElement.GetString() is not { Length: > 0 } principal)
        {
            return null;
        }

        string? StringProperty(string name)
            => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;

        return new CredentialRowResponse(
            kind,
            principal,
            StringProperty("secret"),
            StringProperty("source"),
            StringProperty("host"),
            StringProperty("note"),
            TaskId: null,
            ImplantId: null,
            Verb: null!,
            CapturedAt: default);
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    /// <summary>
    /// One harvested credential, attributed to the task that captured it.
    /// <paramref name="Secret"/> is the material when the source carried it
    /// (the reference <c>collect.cred</c> lists stores without secret
    /// material; a harvesting module may carry it) -- the UI masks it by
    /// default.
    /// </summary>
    public sealed record CredentialRowResponse(
        string Kind,
        string Principal,
        string? Secret,
        string? Source,
        string? Host,
        string? Note,
        string? TaskId,
        string? ImplantId,
        string Verb,
        DateTimeOffset CapturedAt);

    public sealed record CredentialListResponse(IReadOnlyList<CredentialRowResponse> Items);
}
