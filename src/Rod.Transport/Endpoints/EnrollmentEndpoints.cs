using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Implants;
using Rod.V1;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The implant-side enrollment endpoint (roadmap M1.2): a stager redeems its
/// token and receives a certificate bound to <c>(implant_id, engagement_id)</c>
/// plus the CA chain. The engagement is resolved from the redeemed token -- a
/// real stager carries the secret and the endpoint, not the engagement id.
///
/// Outcomes are mapped to the wire <see cref="EnrollStatus"/>: the language-
/// neutral contract lives in Rod.Protocol (architecture.md Sec 8/9), so this is
/// the layer that translates the core's redeem exceptions into status codes.
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
        EnrollRequest body,
        EnrollmentService service,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body.StagerTokenSecret))
            return Results.Json(
                new EnrollmentResponse(EnrollStatus.BadToken, null, null, null, null),
                statusCode: StatusCodes.Status401Unauthorized);

        if (!Enum.TryParse<ImplantClass>(body.Class, ignoreCase: true, out var @class))
            @class = ImplantClass.Stage2;

        // The implant's own public key (DER SubjectPublicKeyInfo, base64 over JSON).
        // When present the leaf is signed over it so the implant keeps its private
        // key for mTLS (architecture.md Sec 9). Optional: a request without it gets
        // a server-generated ephemeral leaf (the M1.2 shape).
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

        try
        {
            var enrolled = await service.EnrollAsync(
                new EnrollCommand(body.StagerTokenSecret, @class, clientPublicKey),
                cancellationToken);

            var response = new EnrollmentResponse(
                EnrollStatus.Ok,
                enrolled.ImplantId.ToString(),
                enrolled.EngagementId.ToString(),
                Convert.ToBase64String(enrolled.LeafCertificate),
                enrolled.CaChain.Select(Convert.ToBase64String).ToArray());

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
                new EnrollmentResponse(status, null, null, null, null),
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
                new EnrollmentResponse(EnrollStatus.BadToken, null, null, null, null),
                statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    public sealed record EnrollRequest(
        string StagerTokenSecret,
        string? Class = null,
        string? PublicKey = null);

    public sealed record Problem(string Error);

    /// <summary>
    /// Mirrors the wire <see cref="Rod.V1.EnrollResponse"/>. <see cref="Status"/>
    /// is the wire enum so the JSON contract and the proto contract cannot drift.
    /// Certificate material is base64 over JSON; the proto carries raw bytes.
    /// </summary>
    public sealed record EnrollmentResponse(
        EnrollStatus Status,
        string? ImplantId,
        string? EngagementId,
        string? LeafCertificate,
        string[]? CaChain);
}
