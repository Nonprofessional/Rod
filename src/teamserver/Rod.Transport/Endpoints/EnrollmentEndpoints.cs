using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Implants;
using Rod.CoreState.Staging;
using Rod.Transport.Payloads;
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
///
/// The stage-2 fetch route below is the stage-1 half of staging
/// (architecture.md Sec 6): a stager presents the same deployment credential
/// enroll takes, verified without being spent, and receives the stage-2 bytes
/// it then runs.
/// </summary>
public static class EnrollmentEndpoints
{
    public static IEndpointRouteBuilder MapEnrollmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/implants");

        group.MapPost("/enroll", EnrollAsync)
            .WithName(nameof(EnrollAsync));
        group.MapGet("/stage2/{payloadId}", FetchStage2Async)
            .WithName(nameof(FetchStage2Async));

        return endpoints;
    }

    // Serves a built stage-2 payload to a presenting stage-1 stager. The
    // stager token in the X-Stager-Token header resolves the engagement; the
    // payload must exist in that engagement, so a token for one engagement
    // never reaches another engagement's payloads (architecture.md Sec 3).
    // The token is verified, not redeemed: the fetch is pre-identity
    // transport, and the enrollment that follows is the audited record.
    private static async Task<IResult> FetchStage2Async(
        string payloadId,
        HttpRequest http,
        IStagerTokenService tokens,
        Rod.Transport.Listeners.IListenerRegistry listeners,
        TimeProvider clock,
        IPayloadStore payloads,
        CancellationToken cancellationToken)
    {
        var secret = http.Headers["X-Stager-Token"].ToString();
        if (string.IsNullOrWhiteSpace(secret))
            return Results.Json(
                new Problem("X-Stager-Token header is required."),
                statusCode: StatusCodes.Status401Unauthorized);
        if (!Guid.TryParse(payloadId, out var payloadValue))
            return Results.BadRequest(new Problem("Payload id is not a valid identifier."));

        try
        {
            var token = await tokens.VerifyAsync(secret, clock.GetUtcNow(), cancellationToken);

            // The scope check an engagement's own listener enforces: the
            // token must belong to the engagement this socket answers for, so
            // a leaked token from another engagement is refused here, before
            // any bytes leave -- and a shared-tier socket (the operator
            // front) refuses implant ingress outright.
            if (!await TokenMatchesListenerScopeAsync(http, listeners, token, cancellationToken))
                return Results.Json(
                    new Problem("Stager token was not accepted."),
                    statusCode: StatusCodes.Status401Unauthorized);

            // Scoped by the token's engagement: a payload id from another
            // engagement is indistinguishable from a nonexistent one.
            var payload = await payloads.FindAsync(payloadValue, token.EngagementId.Value, cancellationToken);
            if (payload is null)
                return Results.NotFound(new Problem("Payload does not exist in this engagement."));

            return Results.File(payload.Content, payload.ContentType, $"rod-stage2-{payloadValue:N}.bin");
        }
        catch (StagerTokenRedeemException)
        {
            // The same refusal shape enroll gives: no distinction between
            // unknown, expired, and spent on the wire.
            return Results.Json(
                new Problem("Stager token was not accepted."),
                statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    private static async Task<IResult> EnrollAsync(
        HttpRequest http,
        EnrollmentService service,
        Rod.Transport.Listeners.IListenerRegistry listeners,
        IStagerTokenService tokens,
        TimeProvider clock,
        IAuditStore audit,
        IPayloadStore payloads,
        EnvelopeCheckInKeys checkInKeys,
        CancellationToken cancellationToken)
    {
        var body = await ReadEnrollRequestAsync(http, payloads, cancellationToken);
        if (body is null)
        {
            return Results.BadRequest(new Problem(
                "Request body is not an enroll request (raw JSON, a base64-wrapped JSON string, or an AES-GCM-wrapped one)."));
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

        // The scope check before the token is spent: when the socket this
        // request arrived on belongs to one engagement, a token minted for
        // any other engagement is refused whole -- it keeps its uses for the
        // listener it was minted for -- and a shared-tier socket (the
        // operator front) refuses implant ingress outright (architecture.md
        // Sec 8). The verified token is kept: when the redeem below succeeds,
        // its id is what binds the enrollment to the build that minted it.
        RedeemedStagerToken? presentedToken = null;
        try
        {
            presentedToken = await tokens.VerifyAsync(body.StagerTokenSecret, clock.GetUtcNow(), cancellationToken);
            if (!await TokenMatchesListenerScopeAsync(http, listeners, presentedToken, cancellationToken))
                return Results.Json(
                    new EnrollmentResponse(EnrollStatus.BadToken, null, null, null, null, null),
                    statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (StagerTokenRedeemException)
        {
            // The pre-check refuses quietly; the redeem inside EnrollAsync
            // produces the precise refused-once-more status below.
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

            // Bind the enrollment to its build's check-in key (architecture.md
            // Sec 8/9): a token minted with a payload -- the baked credential
            // both build paths mint -- names the artifact, and the artifact
            // names the key. From here the implant's envelope check-ins seal
            // under that key; a plaintext body from it is refused.
            await BindCheckInKeyAsync(enrolled, presentedToken, payloads, checkInKeys, cancellationToken);

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
        catch (EngagementClosedException ex)
        {
            // The token redeemed but its engagement is frozen for close-out or
            // retired (architecture.md Sec 2 step 10): no new deployments. A
            // 409, not the 401/BadToken shape -- the token was valid, and the
            // operator driving the deployment needs the real cause.
            return Results.Conflict(new Problem(ex.Message));
        }
        catch (InvalidOperationException)
        {
            // The token redeemed but its engagement was since torn down.
            return Results.Json(
                new EnrollmentResponse(EnrollStatus.BadToken, null, null, null, null, null),
                statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    // Binds a fresh enrollment to its build's check-in key (architecture.md
    // Sec 8/9): when the redeemed token was minted with a payload -- the
    // baked credential both build paths mint -- the enrollment binds the new
    // implant to that artifact's envelope key, so its later check-ins cannot
    // downgrade to plaintext frames. A manually minted token names no
    // payload and leaves the implant unbound: its sealed check-ins still
    // authenticate by the key id every sealed body prefixes, but a plaintext
    // check-in is not refused. A null token means the pre-check could not
    // verify it; the redeem inside EnrollAsync is what refused the enroll.
    private static async Task BindCheckInKeyAsync(
        EnrollmentResult enrolled,
        RedeemedStagerToken? token,
        IPayloadStore payloads,
        EnvelopeCheckInKeys checkInKeys,
        CancellationToken cancellationToken)
    {
        if (token is null)
            return;
        var carrier = await payloads.FindByTokenAsync(token.Id.Value, cancellationToken);
        if (carrier?.EnvelopeKeyId is { } keyId && carrier.EnvelopeKey is { } key)
            checkInKeys.Bind(enrolled.ImplantId, keyId, key);
    }

    // The engagement-scope check shared by enroll and the stage-2 fetch: the
    // socket this request arrived on must be the token's own engagement's
    // listener. A shared-tier (startup-configuration) socket refuses implant
    // ingress outright -- the operator front carries no enrollment, and each
    // engagement brings its own listener. A socket the registry does not know
    // (the in-memory test harness, which binds no real ports) stays
    // token-scoped only, the shape the harness has always used.
    private static async Task<bool> TokenMatchesListenerScopeAsync(
        HttpRequest http,
        Rod.Transport.Listeners.IListenerRegistry listeners,
        Rod.CoreState.Staging.RedeemedStagerToken token,
        CancellationToken cancellationToken)
    {
        var listener = await listeners.FindByLocalPortAsync(http.HttpContext.Connection.LocalPort, cancellationToken);
        return listener is null || listener.EngagementId == token.EngagementId;
    }

    // Reads the enroll body in any of the shapes the malleable transport
    // profile allows (architecture.md Sec 7): the raw JSON document, a single
    // base64 string wrapping it (the base64 envelope -- the body stops looking
    // like a structured C2 message), or a single base64 string wrapping
    // AES-256-GCM ciphertext under the artifact's per-build envelope key (the
    // AesGcm envelope -- the body stays opaque even where TLS terminates
    // early). The teamserver understands all of them, so an envelope-profiled
    // implant enrolls against a stock deployment with no unwrapping edge in
    // front of it. Anything that matches no shape returns null for a 400.
    private static async Task<EnrollRequest?> ReadEnrollRequestAsync(
        HttpRequest http, IPayloadStore payloads, CancellationToken cancellationToken)
    {
        string raw;
        using (var reader = new StreamReader(http.Body))
            raw = await reader.ReadToEndAsync(cancellationToken);
        raw = raw.Trim();
        if (raw.Length == 0)
            return null;

        // A leading quote means the body is a JSON string -- an envelope.
        // Which one depends on the decoded bytes: the R1 magic names the
        // AES-Gcm shape (resolve the key by the id it prefixes, decrypt);
        // anything else is the base64 envelope's inner JSON.
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

            if (AesGcmEnvelope.TryReadKeyId(wrapped) is { } keyId)
            {
                // The key lives beside the stored payload it was minted with;
                // a deleted payload takes its key along, and that artifact's
                // envelopes stop being decodable.
                var carrier = await payloads.FindByEnvelopeKeyAsync(keyId, cancellationToken);
                if (carrier?.EnvelopeKey is not { } key)
                    return null;
                var plaintext = AesGcmEnvelope.TryUnwrap(
                    wrapped, carrier.EnvelopeKeyId!.Value, key, AesGcmEnvelope.Aad);
                if (plaintext is null)
                    return null;
                raw = System.Text.Encoding.UTF8.GetString(plaintext);
            }
            else
            {
                try
                {
                    raw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(wrapped));
                }
                catch (FormatException)
                {
                    return null;
                }
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
