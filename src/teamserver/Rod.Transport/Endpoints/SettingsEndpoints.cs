using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.CoreState.Operators;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The operator-facing runtime settings endpoints: the live session-presence
/// knobs (the staleness sweep's threshold and interval), readable and writable
/// over the API so the settings page adjusts them without a restart. The PUTs
/// apply immediately -- the sweeper reads the live values on every pass --
/// and persist behind the scenes so a restart keeps the operator's values.
/// Both reads are open to every operator and both writes are the deployment
/// admin's alone (the deployment-role gate of Sec 4.5): runtime settings are
/// deployment state, not engagement work.
/// </summary>
public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/settings")
            .RequireAuthorization();

        group.MapGet("/sessions", GetSessionsAsync).WithName(nameof(GetSessionsAsync));
        group.MapPut("/sessions", PutSessionsAsync)
            .WithName(nameof(PutSessionsAsync))
            .RequireAuthorization(DeploymentAdmin());
        group.MapGet("/build", GetBuildAsync).WithName(nameof(GetBuildAsync));
        group.MapPut("/build", PutBuildAsync)
            .WithName(nameof(PutBuildAsync))
            .RequireAuthorization(DeploymentAdmin());

        return endpoints;
    }

    // The authorization requirement the settings writes carry: the
    // deployment-role claim the login stamps (Sec 4.5). It composes with the
    // group's plain requirement -- a member is authenticated and still
    // forbidden (403), an anonymous request is challenged (401).
    private static Action<AuthorizationPolicyBuilder> DeploymentAdmin()
        => policy => policy.RequireClaim(
            OperatorClaims.DeploymentRoleClaimType, DeploymentRoles.AdminName);

    private static IResult GetSessionsAsync(SessionRuntimeSettings settings)
    {
        var values = settings.Current;
        return Results.Ok(SessionSettingsResponse.Of(values));
    }

    private static IResult PutSessionsAsync(
        SessionSettingsRequest body,
        SessionRuntimeSettings settings)
    {
        if (body.ThresholdMinutes is null || body.SweepIntervalMinutes is null)
            return Results.BadRequest(new Problem("Both thresholdMinutes and sweepIntervalMinutes are required."));

        var threshold = TimeSpan.FromMinutes(body.ThresholdMinutes.Value);
        var interval = TimeSpan.FromMinutes(body.SweepIntervalMinutes.Value);
        SessionStalenessValues values;
        try
        {
            values = settings.Change(threshold, interval);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Results.BadRequest(new Problem(ex.Message));
        }

        return Results.Ok(SessionSettingsResponse.Of(values));
    }

    private static IResult GetBuildAsync(BuildRuntimeSettings settings)
        => Results.Ok(BuildSettingsResponse.Of(settings.RustTargetDir));

    private static IResult PutBuildAsync(
        BuildSettingsRequest body,
        BuildRuntimeSettings settings)
    {
        if (body.RustTargetDir is null)
            return Results.BadRequest(new Problem("RustTargetDir is required (empty string returns to hermetic builds)."));
        try
        {
            var applied = settings.Change(body.RustTargetDir);
            return Results.Ok(BuildSettingsResponse.Of(applied));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new Problem(ex.Message));
        }
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    // Minutes are the unit operators think in for both knobs; the bounds ride
    // the setters (threshold 1 minute..24 hours, interval 10 seconds..1 hour),
    // and the client mirrors them in the form's help text.
    public sealed record SessionSettingsRequest(double? ThresholdMinutes, double? SweepIntervalMinutes);

    /// <summary>
    /// The build-section write: the shared cargo target dir, or the empty
    /// string to return to hermetic per-build target dirs. Null (absent) is
    /// refused rather than read as "unchanged" -- a settings write states
    /// the whole value.
    /// </summary>
    public sealed record BuildSettingsRequest(string? RustTargetDir);

    public sealed record BuildSettingsResponse(string? RustTargetDir)
    {
        public static BuildSettingsResponse Of(string? value) => new(value);
    }

    public sealed record SessionSettingsResponse(double ThresholdMinutes, double SweepIntervalMinutes)
    {
        public static SessionSettingsResponse Of(SessionStalenessValues values) => new(
            values.Threshold.TotalMinutes,
            values.SweepInterval.TotalMinutes);
    }

}
