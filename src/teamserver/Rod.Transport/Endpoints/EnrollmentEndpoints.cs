using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Implants;
using Rod.V1;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The implant-side enrollment endpoint: a stager redeems its
/// token and receives a certificate bound to <c>(implant_id, engagement_id)</c>
/// plus the CA chain. The engagement is resolved from the redeemed token -- a
/// real stager carries the secret and the endpoint, not the engagement id.
///
/// Outcomes are mapped to the wire <see cref="EnrollStatus"/>: the language-
/// neutral contract lives in Rod.Protocol (architecture.md Sec 8/9), so this is
/// the layer that translates the core's redeem exceptions into status codes.
///
/// A child implant derives from a parent through the same endpoint
///architecture.md Sec 5.2: the request carries the parent implant id,
/// the service resolves and validates it against the redeemed token's
/// engagement, and the recorded linkage is echoed on the response.
/// </summary>
public static class EnrollmentEndpoints
{
    public static IEndpointRouteBuilder MapEnrollmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/implants");

        group.MapPost("/enroll", EnrollAsync)
            .WithName(nameof(EnrollAsync));

        return endpoints;
    }

    private static async Task<IResult> EnrollAsync(
        HttpRequest http,
        EnrollmentService service,
        IAuditStore audit,
        CancellationToken cancellationToken)
    {
        var body = await ReadEnrollRequestAsync(http, cancellationToken);
        if (body is null)
        {
            return Results.BadRequest(new Problem(
                "Request body is not an enroll request (raw JSON or a base64-wrapped JSON string)."));
        }

        if (string.IsNullOrWhiteSpace(body.StagerTokenSecret))
            return Results.Json(
                new EnrollmentResponse(EnrollStatus.BadToken, null, null, null, null, null),
                statusCode: StatusCodes.Status401Unauthorized);

        if (!Enum.TryParse<ImplantClass>(body.Class, ignoreCase: true, out var @class))
            @class = ImplantClass.Stage2;

        // The implant's own public key (DER SubjectPublicKeyInfo, base64 over JSON).
        // When present the leaf is signed over it so the implant keeps its private
        // key for mTLS (architecture.md Sec 9). Optional: a request without it gets
        // a server-generated ephemeral leaf (the shape).
        byte[]? clientPublicKey = null;
        if (!string.IsNullOrWhiteSpace(body.PublicKey))
        {
            try
            {
                clientPublicKey = Convert.FromBase64String(body.PublicKey);
            }
            catch (FormatException)
            {
                // Malformed base64 is a bad request, not a token failure.
                return Results.BadRequest(new Problem("Public key is not valid base64."));
            }
        }

        // The parent a child is derived from (architecture.md Sec 5.2).
        // Optional: a top-level enroll leaves it null. When present the
        // service resolves the parent and binds the child into the same
        // engagement -- the parent id alone does not grant cross-engagement
        // derivation.
        ImplantId? parentImplantId = null;
        if (!string.IsNullOrWhiteSpace(body.ParentImplantId))
        {
            if (!Guid.TryParse(body.ParentImplantId, out var parentValue))
                return Results.BadRequest(new Problem("Parent implant id is not a valid identifier."));
            parentImplantId = new ImplantId(parentValue);
        }

        try
        {
            var enrolled = await service.EnrollAsync(
                new EnrollCommand(body.StagerTokenSecret, @class, clientPublicKey, parentImplantId),
                cancellationToken);

            // The enrollment is recorded (architecture.md Sec 11).
            // Enrollment is implant-initiated, so it is attributed to the operator
            // who deployed the implant -- the one who minted the redeemed token,
            // carried on the implant as DeployedBy. The payload carries the class
            // (and the parent when it is a child derivation, architecture.md Sec
            // 5.2); the outcome is the new implant id.
            await audit.AppendAsync(
                AuditEvent.Fact(
                    eventId: Guid.NewGuid(),
                    engagementId: enrolled.EngagementId.Value,
                    operatorId: enrolled.DeployedBy.Value,
                    implantId: enrolled.ImplantId.Value,
                    taskId: Guid.Empty,
                    verb: "enroll",
                    kind: AuditEventKind.ImplantEnrolled,
                    payload: enrolled.ParentImplantId is { } parent
                        ? $"{enrolled.Class} parent={parent}"
                        : enrolled.Class.ToString(),
                    output: null,
                    outcome: enrolled.ImplantId.ToString(),
                    at: enrolled.EnrolledAt),
                cancellationToken);

            var response = new EnrollmentResponse(
                EnrollStatus.Ok,
                enrolled.ImplantId.ToString(),
                enrolled.EngagementId.ToString(),
                Convert.ToBase64String(enrolled.LeafCertificate),
                enrolled.CaChain.Select(Convert.ToBase64String).ToArray(),
                enrolled.ParentImplantId?.ToString());

            return Results.Ok(response);
        }
        catch (StagerTokenRedeemException ex)
        {
            // The redeem reason is the actionable cause; map it to a wire status.
            var status = ex.Reason switch
            {
                StagerTokenRedeemReason.Expired => EnrollStatus.Expired,
                StagerTokenRedeemReason.Spent => EnrollStatus.Spent,
                _ => EnrollStatus.BadToken,
            };
            return Results.Json(
                new EnrollmentResponse(status, null, null, null, null, null),
                statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (InvalidParentImplantException)
        {
            // The parent was unknown, foreign to the redeemed engagement, or
            // retired (architecture.md Sec 5.2). The refusal is not separately
            // enumerated on the wire: it collapses to the same 401/BadToken
            // shape an invalid token or a torn-down engagement produces, so an
            // implant gets no signal beyond "no". The distinct reason stays
            // server-side for the operator trail.
            return Results.Json(
                new EnrollmentResponse(EnrollStatus.BadToken, null, null, null, null, null),
                statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (CryptographicException)
        {
            // The supplied public key did not decode as a recognizable SPKI. Treat
            // it as a malformed enroll: the token is intact, but the request is bad.
            return Results.BadRequest(new Problem("Public key is not a recognizable SubjectPublicKeyInfo."));
        }
        catch (InvalidOperationException)
        {
            // The token redeemed but its engagement was since torn down.
            return Results.Json(
                new EnrollmentResponse(EnrollStatus.BadToken, null, null, null, null, null),
                statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    // Reads the enroll body in either of the two shapes the malleable
    // transport profile allows (architecture.md Sec 7): the raw JSON document,
    // or a single base64 string wrapping it (the profile's base64 envelope --
    // the body stops looking like a structured C2 message). The teamserver
    // understands both, so an envelope-profiled implant enrolls against a stock
    // deployment with no unwrapping edge in front; a TLS-terminating edge may
    // still rewrite the shape, but nothing requires one. Anything that is
    // neither shape returns null for a 400.
    private static async Task<EnrollRequest?> ReadEnrollRequestAsync(
        HttpRequest http, CancellationToken cancellationToken)
    {
        string raw;
        using (var reader = new StreamReader(http.Body))
            raw = await reader.ReadToEndAsync(cancellationToken);
        raw = raw.Trim();
        if (raw.Length == 0)
            return null;

        // A leading quote means the body is a JSON string -- the base64
        // envelope. Decode it and parse the inner JSON as the request.
        if (raw[0] == '"')
        {
            string? wrapped;
            try
            {
                wrapped = System.Text.Json.JsonSerializer.Deserialize<string>(raw);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
            if (string.IsNullOrEmpty(wrapped))
                return null;
            try
            {
                raw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(wrapped));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<EnrollRequest>(raw,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    public sealed record EnrollRequest(
        string StagerTokenSecret,
        string? Class = null,
        string? PublicKey = null,
        string? ParentImplantId = null);

    public sealed record Problem(string Error);

    /// <summary>
    /// Mirrors the wire <see cref="Rod.V1.EnrollResponse"/>. <see cref="Status"/>
    /// is the wire enum so the JSON contract and the proto contract cannot drift.
    /// Certificate material is base64 over JSON; the proto carries raw bytes.
    /// <see cref="ParentImplantId"/> records the child's lineage
    /// server-side, implant-side: the binary wire surface carries it as
    /// <c>parent_implant_id</c>, and this JSON contract is the live producer on the
    /// HTTP enroll path.
    /// </summary>
    public sealed record EnrollmentResponse(
        EnrollStatus Status,
        string? ImplantId,
        string? EngagementId,
        string? LeafCertificate,
        string[]? CaChain,
        string? ParentImplantId = null);
}
