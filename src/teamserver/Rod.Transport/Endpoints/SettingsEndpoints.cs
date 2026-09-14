using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The operator-facing runtime settings endpoints: the live session-presence
/// knobs (the staleness sweep's threshold and interval), readable and writable
/// over the API so the settings page adjusts them without a restart. The PUT
/// applies immediately -- the sweeper reads the live values on every pass --
/// and persists behind the scenes so a restart keeps the operator's values.
/// </summary>
public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/settings").RequireAuthorization();

        group.MapGet("/sessions", GetSessionsAsync).WithName(nameof(GetSessionsAsync));
        group.MapPut("/sessions", PutSessionsAsync).WithName(nameof(PutSessionsAsync));

        return endpoints;
    }

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

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    // Minutes are the unit operators think in for both knobs; the bounds ride
    // the setters (threshold 1 minute..24 hours, interval 10 seconds..1 hour),
    // and the client mirrors them in the form's help text.
    public sealed record SessionSettingsRequest(double? ThresholdMinutes, double? SweepIntervalMinutes);

    public sealed record SessionSettingsResponse(double ThresholdMinutes, double SweepIntervalMinutes)
    {
        public static SessionSettingsResponse Of(SessionStalenessValues values) => new(
            values.Threshold.TotalMinutes,
            values.SweepInterval.TotalMinutes);
    }

}
