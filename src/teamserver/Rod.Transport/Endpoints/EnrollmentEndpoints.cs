using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Live;
using Rod.CoreState.Deployment;
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
/// (architecture.md Sec 6): a stager presents the deployment credential its
/// own build baked, each served fetch spending one use of it, and receives
/// the stage-2 bytes it then runs -- bytes whose own baked credential is
/// what the enrollment that follows spends.
/// </summary>
public static class EnrollmentEndpoints
{
    public static IEndpointRouteBuilder MapEnrollmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/implants");

        group.MapPost("/enroll", EnrollAsync)
            .WithName(nameof(EnrollAsync));
        group.MapGet("/payloads/{payloadId}", FetchPayloadAsync)
            .WithName(nameof(FetchPayloadAsync));

        return endpoints;
    }

    // Serves a built payload to a presenting downloader one-liner. The deploy
    // token in the X-Deploy-Token header resolves the engagement; the payload
    // must exist in that engagement, so a token for one engagement never
    // reaches another engagement's payloads (architecture.md Sec 3). The
    // fetch REDEEMS one use of the token: the download gate is the token's
    // whole job -- the enrollment that follows rides the credential baked
    // into the fetched artifact, not this one. A single-use token authorizes
    // exactly one download; an unlimited budget (maxUses 0) serves until
    // expiry.
    // Every fetch a resolvable credential gate-keeps lands on the audit
    // trail, served or refused -- the fetcher speaks no Rod protocol yet,
    // so the fact is system-attributed and carries what the wire showed
    // (source address, user agent, the socket it landed on). A credential
    // whose secret resolves to no engagement (unknown, or hard-deleted with
    // its launcher row) leaves no record: it belongs to nobody.
    private static async Task<IResult> FetchPayloadAsync(
        string payloadId,
        HttpRequest http,
        IDeployTokenService tokens,
        Rod.Transport.Listeners.IListenerRegistry listeners,
        TimeProvider clock,
        IPayloadStore payloads,
        IAuditStore audit,
        ILiveEventBus live,
        CancellationToken cancellationToken)
    {
        var secret = http.Headers["X-Deploy-Token"].ToString();
        if (string.IsNullOrWhiteSpace(secret))
            return Results.Json(
                new Problem("X-Deploy-Token header is required."),
                statusCode: StatusCodes.Status401Unauthorized);
        if (!Guid.TryParse(payloadId, out var payloadValue))
            return Results.BadRequest(new Problem("Payload id is not a valid identifier."));

        // What the wire showed, captured before any branching: every fact --
        // served or refused -- describes the same fetcher.
        var remote = http.HttpContext.Connection.RemoteIpAddress is { } remoteIp
            ? $"{remoteIp}:{http.HttpContext.Connection.RemotePort}"
            : "unknown";
        var userAgent = http.Headers.UserAgent.ToString();
        var fetcher = $"remote={remote} listenerPort={http.HttpContext.Connection.LocalPort} "
            + $"ua={(string.IsNullOrWhiteSpace(userAgent) ? "none" : userAgent)}";

        try
        {
            // Resolve without spending first: every refusal below (a foreign
            // socket, an unknown payload) must leave the budget whole, and
            // only a fetch that actually serves bytes redeems its use.
            var token = await tokens.VerifyAsync(secret, clock.GetUtcNow(), cancellationToken);

            // The scope check an engagement's own listener enforces: the
            // token must belong to the engagement this socket answers for, so
            // a leaked token from another engagement is refused here, before
            // any bytes leave -- and a shared-tier socket (the operator
            // front) refuses implant ingress outright.
            if (!await TokenMatchesListenerScopeAsync(http, listeners, token, cancellationToken))
            {
                await RecordFetchAsync(audit, clock, token.EngagementId, token.Id, payloadValue,
                    fetcher, "refused:scope", cancellationToken: cancellationToken);
                return Results.Json(
                    new Problem("Deploy token was not accepted."),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            // Scoped by the token's engagement: a payload id from another
            // engagement is indistinguishable from a nonexistent one.
            var payload = await payloads.FindAsync(payloadValue, token.EngagementId.Value, cancellationToken);
            if (payload is null)
            {
                await RecordFetchAsync(audit, clock, token.EngagementId, token.Id, payloadValue,
                    fetcher, "refused:payload", cancellationToken: cancellationToken);
                return Results.NotFound(new Problem("Payload does not exist in this engagement."));
            }

            // The download's redemption, after every refusal: one served
            // fetch spends one use, and a token at zero serves no more.
            var redeemed = await tokens.RedeemAsync(secret, clock.GetUtcNow(), cancellationToken);

            // The serve is the deployment's first observable footprint. The
            // same frame reaches connected operators live, so a launcher
            // row's remaining budget moves the moment a target pulls it. The
            // enrollment that may follow is the identity-bearing half of the
            // exchange; a burned one-liner pulled by a scanner enrolls never,
            // and this is the only record it leaves.
            await RecordFetchAsync(audit, clock, redeemed.EngagementId, redeemed.Id, payloadValue,
                fetcher, "served", payload.Fingerprint, cancellationToken);
            await live.PublishAsync(
                LiveEvent.PayloadFetched(
                    redeemed.EngagementId,
                    $"{fetcher} payload={payload.Fingerprint} served",
                    clock.GetUtcNow()),
                cancellationToken);

            return Results.File(payload.Content, payload.ContentType, $"rod-payload-{payloadValue:N}.bin");
        }
        catch (DeployTokenRedeemException ex)
        {
            // A credential that still resolves keeps its attempts on the
            // trail: expired, spent, or revoked, the fact says who came back
            // and why it was refused -- the budget-burned retry is exactly
            // the fact an operator wants while the launcher row lives. The
            // wire answer stays uniform: no distinction between unknown,
            // expired, spent, and revoked for the fetcher to read.
            if (ex.EngagementId is { } engagement && ex.TokenId is { } tokenId)
                await RecordFetchAsync(audit, clock, engagement, tokenId, payloadValue,
                    fetcher, $"refused:{ex.Reason.ToString().ToLowerInvariant()}",
                    cancellationToken: cancellationToken);
            return Results.Json(
                new Problem("Deploy token was not accepted."),
                statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    // The one fetch fact: what the wire showed, which credential gated it,
    // and how it ended. The payload names itself in the library's
    // vocabulary -- the fingerprint the Payloads tab matches on -- with the
    // bare artifact id beside it for correlating a pasted command's URL;
    // a fetch that never resolved a payload can name only the id it asked
    // for. Audit-only -- the live push rides the served frame alone,
    // because a refusal moves no state an operator's row displays.
    private static async Task RecordFetchAsync(
        IAuditStore audit,
        TimeProvider clock,
        EngagementId engagement,
        DeployTokenId tokenId,
        Guid payloadId,
        string fetcher,
        string outcome,
        string? fingerprint = null,
        CancellationToken cancellationToken = default)
    {
        var payloadText = fingerprint is null
            ? $"payload={payloadId:N}"
            : $"payload={fingerprint} id={payloadId:N}";
        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: engagement.Value,
                operatorId: OperatorId.Empty.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "payload.fetched",
                kind: AuditEventKind.PayloadFetched,
                payload: $"{fetcher} {payloadText} token={tokenId}",
                output: null,
                outcome,
                at: clock.GetUtcNow()),
            cancellationToken);
    }

    private static async Task<IResult> EnrollAsync(
        HttpRequest http,
        EnrollmentService service,
        Rod.Transport.Listeners.IListenerRegistry listeners,
        IDeployTokenService tokens,
        TimeProvider clock,
        IAuditStore audit,
        IPayloadStore payloads,
        EnvelopeContactKeys contactKeys,
        CancellationToken cancellationToken)
    {
        var body = await ReadEnrollRequestAsync(http, payloads, cancellationToken);
        if (body is null)
        {
            return Results.BadRequest(new Problem(
                "Request body is not an enroll request (raw JSON, a base64-wrapped JSON string, or an AES-GCM-wrapped one)."));
        }

        // The implant's own public key (DER SubjectPublicKeyInfo, base64 over
        // JSON): a Tier 0 obligation the server accepts and validates -- an
        // ECDSA key an implant generates and keeps the private half of. No
        // in-tree consumer mints over it (the transport certificate posture
        // retired with the mTLS family); validation is the contract's own
        // gate, so a recognizable SPKI is enforced here, malformed shapes
        // are a bad request, and the token stays intact either way.
        byte[]? clientPublicKey = null;
        if (!string.IsNullOrWhiteSpace(body.PublicKey))
        {
            try
            {
                clientPublicKey = Convert.FromBase64String(body.PublicKey);
                using var validated = System.Security.Cryptography.ECDsa.Create();
                validated.ImportSubjectPublicKeyInfo(clientPublicKey, out _);
            }
            catch (FormatException)
            {
                return Results.BadRequest(new Problem("Public key is not valid base64."));
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return Results.BadRequest(new Problem("Public key is not a recognizable SubjectPublicKeyInfo."));
            }
        }

        // The ingress this socket carries (architecture.md Sec 8): the
        // listener the local port resolves names the engagement a token must
        // belong to -- refused whole and unspent otherwise -- and the
        // listener id stamped onto the implant record. The shared refusal
        // rules, the enrollment, the audit arc, and the contact key
        // binding live in the shared flow (ScopedEnrollment), the same one
        // the stream carriage's opening exchange drives with its own
        // listener's scope. A
        // socket the registry does not know (the in-memory test harness,
        // which binds no real ports) stays token-scoped only, the shape the
        // harness has always used.
        var ingress = await listeners.FindByLocalPortAsync(
            http.HttpContext.Connection.LocalPort, cancellationToken);

        var outcome = await ScopedEnrollment.EnrollAsync(
            new EnrollWireFields(
                body.DeployTokenSecret,
                body.Class,
                clientPublicKey,
                body.ParentImplantId,
                body.Hostname,
                body.Os,
                body.Arch,
                body.Username,
                body.KillDate,
                body.SleepSeconds,
                body.JitterSeconds),
            ingress,
            service,
            tokens,
            payloads,
            contactKeys,
            audit,
            clock,
            cancellationToken);

        if (!outcome.Accepted)
            return MarshalRefusal(outcome);

        var enrolled = outcome.Enrolled!;
        return Results.Ok(new EnrollmentResponse(
            EnrollStatus.Ok,
            enrolled.ImplantId.ToString(),
            enrolled.EngagementId.ToString(),
            "",
            enrolled.CaChain.Select(Convert.ToBase64String).ToArray(),
            enrolled.ParentImplantId?.ToString()));
    }

    // Shapes a refusal for the HTTP wire: the token states answer the
    // EnrollmentResponse body the implant reads (401, no distinction between
    // unknown, foreign, and spent); the problem causes answer the Problem
    // body an operator reads (400 malformed, 409 a closed engagement) -- the
    // same split the route has always kept, now derived from the shared
    // outcome.
    private static IResult MarshalRefusal(ScopedEnrollmentOutcome outcome)
        => outcome.Problem is { } problem
            ? Results.Json(new Problem(problem), statusCode: outcome.ProblemStatus)
            : Results.Json(
                new EnrollmentResponse(outcome.Status, null, null, null, null, null),
                statusCode: StatusCodes.Status401Unauthorized);

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
        Rod.CoreState.Deployment.RedeemedDeployToken token,
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
        string DeployTokenSecret,
        string? Class = null,
        string? PublicKey = null,
        string? ParentImplantId = null,
        string? Hostname = null,
        string? Os = null,
        string? Arch = null,
        string? Username = null,
        string? KillDate = null,
        // The baked contact cadence the implant reported about itself, in
        // seconds; null on either means "not supplied", the same shape the
        // proto's optional fields carry.
        double? SleepSeconds = null,
        double? JitterSeconds = null);


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
