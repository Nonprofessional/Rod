using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The engagement close-out endpoints (architecture.md Sec 2 step 10, Sec 11):
/// the path a finished engagement takes out of service. <c>POST
/// /engagements/{id}:freeze</c> stops new tasking and deployments so the trail
/// is final; <c>POST /engagements/{id}:evidence-package</c> exports the
/// hash-chained trail, the artifacts, and the report as one ZIP that re-verifies
/// offline (<see cref="EvidencePackage"/>); <c>POST /engagements/{id}:retire</c>
/// completes the close-out, terminal. A mistaken freeze is reversible before
/// retirement: <c>POST /engagements/{id}:unfreeze</c> reopens the engagement,
/// and both events stay in the trail.
///
/// Every step is an audited, attributed operator action, so the trail carries
/// its own close-out story. The export is refused on an open engagement -- the
/// package exists to be the final account, and an open engagement's trail is
/// still growing. A retired engagement can still be exported: the evidence
/// outlives the operation.
/// </summary>
public static class CloseoutEndpoints
{
    // The report JSON in the package serializes exactly as the report endpoint
    // returns it (ASP.NET's Web defaults), so the two renderings of the same
    // state are the same bytes.
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapCloseoutEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // The colon-action routes follow the listeners' repoint shape: the
        // action rides the id's segment (/{id}:freeze), so the close-out reads
        // as an action on the engagement itself.
        var group = endpoints.MapGroup("/engagements").RequireAuthorization();
        group.MapPost("/{engagementId}:freeze", FreezeAsync).WithName("FreezeEngagement");
        group.MapPost("/{engagementId}:unfreeze", UnfreezeAsync).WithName("UnfreezeEngagement");
        group.MapPost("/{engagementId}:evidence-package", ExportEvidencePackageAsync).WithName("ExportEvidencePackage");
        group.MapPost("/{engagementId}:retire", RetireAsync).WithName("RetireEngagement");
        return endpoints;
    }

    private static async Task<IResult> FreezeAsync(
        string engagementId,
        ClaimsPrincipal user,
        IEngagementRepository engagements,
        EngagementService service,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var (error, engagement) = await ResolveEngagementAsync(engagementId, engagements, cancellationToken);
        if (engagement is null)
            return error!;
        var operatorId = user.TryGetOperatorId();
        if (operatorId is null)
            return Results.Unauthorized();

        EngagementFrozen frozen;
        try
        {
            frozen = await service.FreezeAsync(new FreezeEngagementCommand(engagement.Id), cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // The aggregate rejected the transition: already frozen or retired.
            return Results.Conflict(new Problem(ex.Message));
        }

        // The freeze is recorded (architecture.md Sec 11): attributed to the
        // freezing operator, the payload the freeze timestamp, the outcome the
        // engagement id. The first event of the close-out arc.
        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: frozen.EngagementId.Value,
                operatorId: operatorId.Value.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "freeze-engagement",
                kind: AuditEventKind.EngagementFrozen,
                payload: $"frozenAt={frozen.FrozenAt:O}",
                output: null,
                outcome: frozen.EngagementId.ToString(),
                at: clock.GetUtcNow()),
            cancellationToken);

        return Results.Ok(new EngagementClosedResponse(frozen.EngagementId.ToString(), frozen.FrozenAt));
    }

    private static async Task<IResult> UnfreezeAsync(
        string engagementId,
        ClaimsPrincipal user,
        IEngagementRepository engagements,
        EngagementService service,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var (error, engagement) = await ResolveEngagementAsync(engagementId, engagements, cancellationToken);
        if (engagement is null)
            return error!;
        var operatorId = user.TryGetOperatorId();
        if (operatorId is null)
            return Results.Unauthorized();

        EngagementUnfrozen unfrozen;
        try
        {
            unfrozen = await service.UnfreezeAsync(new UnfreezeEngagementCommand(engagement.Id), cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // The aggregate rejected the transition: not frozen, or retired
            // (the close-out completed and the record is sealed).
            return Results.Conflict(new Problem(ex.Message));
        }

        // The unfreeze is recorded like the freeze it reverses: attributed,
        // timestamped, on the live trail. The trail keeps both events, so the
        // mistaken freeze stays part of the story instead of being erased.
        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: unfrozen.EngagementId.Value,
                operatorId: operatorId.Value.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "unfreeze-engagement",
                kind: AuditEventKind.EngagementUnfrozen,
                payload: $"unfrozenAt={unfrozen.UnfrozenAt:O}",
                output: null,
                outcome: unfrozen.EngagementId.ToString(),
                at: clock.GetUtcNow()),
            cancellationToken);

        return Results.Ok(new EngagementReopenedResponse(unfrozen.EngagementId.ToString(), unfrozen.UnfrozenAt));
    }

    private static async Task<IResult> ExportEvidencePackageAsync(
        string engagementId,
        ClaimsPrincipal user,
        IEngagementRepository engagements,
        IAuditStore audit,
        IArtifactStore artifacts,
        IOperatorRepository operators,
        IImplantRepository implants,
        ITaskRepository tasks,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var (error, engagement) = await ResolveEngagementAsync(engagementId, engagements, cancellationToken);
        if (engagement is null)
            return error!;
        var operatorId = user.TryGetOperatorId();
        if (operatorId is null)
            return Results.Unauthorized();

        if (!engagement.IsClosed)
        {
            return Results.Conflict(new Problem(
                $"Engagement {engagement.Id} is open; freeze it before exporting its evidence package."));
        }

        // The package is the report projection plus the raw evidence it renders
        // from: the full trail and the artifacts. The report builder resolves
        // and verifies all of it (a broken chain surfaces here, before anything
        // leaves the server).
        var builder = await ReportBuilder.BuildAsync(
            engagement, audit, artifacts, operators, implants, tasks, cancellationToken);
        var report = builder.Report(engagement);

        var documents = new List<KeyValuePair<string, byte[]>>
        {
            new("report.json", JsonSerializer.SerializeToUtf8Bytes(report, WebJson)),
            new("report.md", Encoding.UTF8.GetBytes(ReportMarkdown.Render(report))),
        };

        var exportedAt = clock.GetUtcNow();
        using var buffer = new MemoryStream();
        var manifest = await EvidencePackage.WriteAsync(
            buffer,
            new EvidencePackageHeader(engagement.Id.Value, engagement.Name, operatorId.Value.Value, exportedAt),
            builder.Trail,
            builder.Artifacts,
            documents,
            cancellationToken);

        // The export is recorded (architecture.md Sec 11): the outcome is the
        // exported trail's chain-head hash -- the digest that pins everything
        // the package carries -- and the payload the counts. Written after the
        // package is built, so the exported trail is exactly the one verified;
        // this event rides the live trail a later re-export would carry.
        var chainHead = builder.Trail.Count == 0 ? AuditChain.GenesisHash : builder.Trail[^1].Hash;
        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: engagement.Id.Value,
                operatorId: operatorId.Value.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "export-evidence",
                kind: AuditEventKind.EvidenceExported,
                payload: $"events={manifest.EventCount} artifacts={manifest.ArtifactCount} files={manifest.Files.Count + 1}",
                output: null,
                outcome: chainHead,
                at: exportedAt),
            cancellationToken);

        return Results.File(
            buffer.ToArray(),
            "application/zip",
            $"rod-evidence-{engagement.Id.Value:N}.zip");
    }

    private static async Task<IResult> RetireAsync(
        string engagementId,
        ClaimsPrincipal user,
        IEngagementRepository engagements,
        EngagementService service,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var (error, engagement) = await ResolveEngagementAsync(engagementId, engagements, cancellationToken);
        if (engagement is null)
            return error!;
        var operatorId = user.TryGetOperatorId();
        if (operatorId is null)
            return Results.Unauthorized();

        EngagementRetired retired;
        try
        {
            retired = await service.RetireAsync(new RetireEngagementCommand(engagement.Id), cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // The aggregate rejected the transition: open (close-out skipped)
            // or already retired.
            return Results.Conflict(new Problem(ex.Message));
        }

        // The retirement is recorded (architecture.md Sec 11): terminal, and
        // attributed like every engagement fact. The trail -- this event
        // included -- remains the durable account after the infrastructure is
        // gone.
        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: retired.EngagementId.Value,
                operatorId: operatorId.Value.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "retire-engagement",
                kind: AuditEventKind.EngagementRetired,
                payload: $"retiredAt={retired.RetiredAt:O}",
                output: null,
                outcome: retired.EngagementId.ToString(),
                at: clock.GetUtcNow()),
            cancellationToken);

        return Results.Ok(new EngagementClosedResponse(retired.EngagementId.ToString(), retired.RetiredAt));
    }

    // Shared resolution: a malformed id is a 400, an unknown engagement a 404.
    // The state guards throw after this, so an InvalidOperationException from
    // the service is unambiguously a transition conflict -> 409.
    private static async Task<(IResult? Error, Engagement? Engagement)> ResolveEngagementAsync(
        string engagementId,
        IEngagementRepository engagements,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var idValue))
            return (Results.BadRequest(new Problem("Engagement id is not a valid identifier.")), null);

        var engagement = await engagements.FindAsync(new EngagementId(idValue), cancellationToken);
        if (engagement is null)
            return (Results.NotFound(new Problem($"Engagement {engagementId} does not exist.")), null);

        return (null, engagement);
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    public sealed record EngagementClosedResponse(string EngagementId, DateTimeOffset At);

    public sealed record EngagementReopenedResponse(string EngagementId, DateTimeOffset UnfrozenAt);

}
