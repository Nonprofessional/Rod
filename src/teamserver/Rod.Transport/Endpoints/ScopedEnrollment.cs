using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Staging;
using Rod.Transport.Payloads;
using Rod.V1;

namespace Rod.Transport.Endpoints;

// The engagement-scoped enrollment flow every implant ingress drives
// (architecture.md Sec 8): the refusal rules (the token must belong to the
// engagement the socket answers for, refused whole and unspent otherwise),
// the enrollment itself, the audit arc, and the contact key binding. The
// web enroll route resolves its scope from the local port; the stream
// carriage's listener knows its own engagement directly. Everything except the wire marshaling
// lives here, so an enrollment over either carriage is refused, recorded,
// and audited identically.

/// <summary>
/// The enroll fields exactly as a wire body carries them, pre-parse: the
/// JSON DTO and the rod.v1 frame marshal into this one shape, so the shared
/// flow never knows a carriage. Optional strings arrive null when the wire
/// left them empty; the optional cadence pair arrives null when the implant
/// did not advertise one.
/// </summary>
internal sealed record EnrollWireFields(
    string StagerTokenSecret,
    string? Class,
    byte[]? PublicKey,
    string? ParentImplantId,
    string? Hostname,
    string? Os,
    string? Arch,
    string? Username,
    string? KillDate,
    double? SleepSeconds = null,
    double? JitterSeconds = null);

/// <summary>
/// One enrollment attempt's outcome: accepted with the result and the build
/// the redeemed token minted, or refused with either a wire
/// <see cref="EnrollStatus"/> (the token states -- an implant gets the
/// actionable reason) or a problem detail (a malformed request, a closed
/// engagement -- causes the HTTP arm answers with a problem body and the
/// stream arm collapses to the generic refusal status -- the same "no signal
/// beyond no" discipline the web route keeps).
/// </summary>
internal sealed record ScopedEnrollmentOutcome(
    bool Accepted,
    EnrollStatus Status,
    string? Problem,
    int ProblemStatus,
    EnrollmentResult? Enrolled,
    PayloadRecord? Build)
{
    public static ScopedEnrollmentOutcome Refused(EnrollStatus status)
        => new(false, status, null, 0, null, null);

    public static ScopedEnrollmentOutcome Malformed(string problem)
        => new(false, EnrollStatus.Unspecified, problem, StatusCodes.Status400BadRequest, null, null);

    public static ScopedEnrollmentOutcome Accept(EnrollmentResult enrolled, PayloadRecord? build)
        => new(true, EnrollStatus.Ok, null, 0, enrolled, build);
}

internal static class ScopedEnrollmentResponse
{
    /// <summary>
    /// Assembles the rod.v1 EnrollResponse off one outcome -- the answer
    /// every enrollment carriage sends, whatever its wire (the HTTP route's
    /// JSON twin aside): the status, the ids, the leaf and chain, the
    /// parent, and the build's per-artifact contact key when the redeemed
    /// token bound one. A refusal carries just the status, no signal
    /// beyond no.
    /// </summary>
    public static Rod.V1.EnrollResponse Build(ScopedEnrollmentOutcome outcome)
    {
        if (!outcome.Accepted)
            return new Rod.V1.EnrollResponse { Status = outcome.Status };

        var enrolled = outcome.Enrolled!;
        var response = new Rod.V1.EnrollResponse
        {
            Status = EnrollStatus.Ok,
            ImplantId = enrolled.ImplantId.ToString(),
            EngagementId = enrolled.EngagementId.ToString(),
            // The leaf stays empty: no in-tree family mints transport
            // certificates (architecture.md Sec 8/9), and the frozen field
            // reads as not-supplied.
        };
        if (enrolled.ParentImplantId is { } parent)
            response.ParentImplantId = parent.ToString();
        foreach (var chainCert in enrolled.CaChain)
            response.CaChain.Add(Google.Protobuf.ByteString.CopyFrom(chainCert));
        if (outcome.Build?.EnvelopeKeyId is { } keyId && outcome.Build.EnvelopeKey is { } key)
        {
            response.EnvelopeKeyId = Google.Protobuf.ByteString.CopyFrom(keyId.ToByteArray());
            response.EnvelopeKey = Google.Protobuf.ByteString.CopyFrom(key);
        }
        return response;
    }
}

internal static class ScopedEnrollment
{
    /// <summary>
    /// Verifies, scopes, redeems, records, and audits one enrollment. The
    /// scope check runs before the redeem so a foreign engagement's token is
    /// refused unspent (architecture.md Sec 8): <paramref name="ingress"/> is
    /// the listener the ingress socket belongs to -- the one the HTTP route
    /// resolved from the local port, the stream listener's own -- and any
    /// ingress at all must be the token's own engagement's (a shared-tier
    /// listener, whose engagement is null, refuses implant ingress outright).
    /// A null ingress is an unattributable socket (the in-memory test
    /// harness), which stays token-scoped only.
    /// </summary>
    public static async Task<ScopedEnrollmentOutcome> EnrollAsync(
        EnrollWireFields fields,
        Rod.Transport.Listeners.Listener? ingress,
        EnrollmentService service,
        IStagerTokenService tokens,
        IPayloadStore payloads,
        EnvelopeContactKeys contactKeys,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fields.StagerTokenSecret))
            return ScopedEnrollmentOutcome.Refused(EnrollStatus.BadToken);

        if (!Enum.TryParse<ImplantClass>(fields.Class, ignoreCase: true, out var @class))
            @class = ImplantClass.Stage2;

        // The parent a child is derived from (architecture.md Sec 5.2).
        // Optional: a top-level enroll leaves it null. When present the
        // service resolves the parent and binds the child into the same
        // engagement -- the parent id alone does not grant cross-engagement
        // derivation.
        ImplantId? parentImplantId = null;
        if (!string.IsNullOrWhiteSpace(fields.ParentImplantId))
        {
            if (!Guid.TryParse(fields.ParentImplantId, out var parentValue))
                return ScopedEnrollmentOutcome.Malformed("Parent implant id is not a valid identifier.");
            parentImplantId = new ImplantId(parentValue);
        }

        // The kill date the artifact baked, as the implant reports it: the
        // recorded fuse mirrors the artifact's own (an open-ended build reports
        // nothing and records null). A malformed or already-passed date is a
        // client mistake the record must not silently paper over.
        DateTimeOffset? killDate = null;
        if (!string.IsNullOrWhiteSpace(fields.KillDate))
        {
            if (!DateTimeOffset.TryParse(
                    fields.KillDate, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return ScopedEnrollmentOutcome.Malformed("KillDate is not a valid timestamp.");
            }
            if (parsed <= clock.GetUtcNow())
                return ScopedEnrollmentOutcome.Malformed("KillDate has already passed.");
            killDate = parsed;
        }

        // The cadence the implant reported about itself, in the same
        // not-supplied-when-null shape as the host facts. A negative or
        // non-finite value is a client mistake the record must not paper
        // over; a zero sleep is legitimate (the back-to-back posture).
        if (!IsReportableSeconds(fields.SleepSeconds) || !IsReportableSeconds(fields.JitterSeconds))
            return ScopedEnrollmentOutcome.Malformed("Sleep/jitter must be non-negative finite seconds.");

        // The scope check before the token is spent: when the socket this
        // request arrived on belongs to a listener, a token minted for any
        // other engagement is refused whole -- it keeps its uses for the
        // listener it was minted for -- and a shared-tier socket (the
        // operator front, whose engagement is null) refuses implant ingress
        // outright (architecture.md Sec 8). The verified token is kept: when
        // the redeem below succeeds, its id is what binds the enrollment to
        // the build that minted it.
        RedeemedStagerToken? presentedToken = null;
        try
        {
            presentedToken = await tokens.VerifyAsync(fields.StagerTokenSecret, clock.GetUtcNow(), cancellationToken);
            if (ingress is not null && ingress.EngagementId != presentedToken.EngagementId)
                return ScopedEnrollmentOutcome.Refused(EnrollStatus.BadToken);
        }
        catch (StagerTokenRedeemException)
        {
            // The pre-check refuses quietly; the redeem inside EnrollAsync
            // produces the precise refused-once-more status below.
        }

        // The build the token was minted for, resolved once for everything
        // the enrollment reads off it: the contact key binding below, and
        // the baked carrier set stamped onto the implant -- the derivation
        // task issuance gates channel verbs on. A token the pre-check could
        // not verify has no build here; the redeem inside EnrollAsync is
        // what refuses that enroll.
        var build = presentedToken is null
            ? null
            : await payloads.FindByTokenAsync(presentedToken.Id.Value, cancellationToken);

        try
        {
            var enrolled = await service.EnrollAsync(
                new EnrollCommand(
                    fields.StagerTokenSecret, @class, fields.PublicKey, parentImplantId,
                    CleanHostFact(fields.Hostname), CleanHostFact(fields.Os),
                    CleanHostFact(fields.Arch), CleanHostFact(fields.Username),
                    fields.SleepSeconds, fields.JitterSeconds,
                    killDate, ingress?.Id.Value, BakedCarriers.From(build)),
                cancellationToken);

            // The enrollment is recorded (architecture.md Sec 11).
            // Enrollment is implant-initiated, so it is attributed to the operator
            // who deployed the implant -- the one who minted the redeemed token,
            // carried on the implant as DeployedBy. The payload carries the class
            // (and the parent when it is a child derivation, architecture.md Sec
            // 5.2) and the host when the implant reported one, so the trail names
            // the machine; the outcome is the new implant id.
            await audit.AppendAsync(
                AuditEvent.Fact(
                    eventId: Guid.NewGuid(),
                    engagementId: enrolled.EngagementId.Value,
                    operatorId: enrolled.DeployedBy.Value,
                    implantId: enrolled.ImplantId.Value,
                    taskId: Guid.Empty,
                    verb: "enroll",
                    kind: AuditEventKind.ImplantEnrolled,
                    payload: BuildEnrollPayload(enrolled),
                    output: null,
                    outcome: enrolled.ImplantId.ToString(),
                    at: enrolled.EnrolledAt),
                cancellationToken);

            // Bind the enrollment to its build's contact key
            // (architecture.md Sec 8/9): a token minted with a payload names
            // the artifact, and the artifact names the key. From here the
            // implant's envelope contacts seal under that key; a plaintext
            // body from it is refused.
            if (build?.EnvelopeKeyId is { } keyId && build.EnvelopeKey is { } key)
                contactKeys.Bind(enrolled.ImplantId, keyId, key);

            return ScopedEnrollmentOutcome.Accept(enrolled, build);
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
            return ScopedEnrollmentOutcome.Refused(status);
        }
        catch (InvalidParentImplantException)
        {
            // The parent was unknown, foreign to the redeemed engagement, or
            // retired (architecture.md Sec 5.2). The refusal is not separately
            // enumerated on the wire: it collapses to the same BadToken shape an
            // invalid token or a torn-down engagement produces, so an implant
            // gets no signal beyond "no". The distinct reason stays server-side
            // for the operator trail.
            return ScopedEnrollmentOutcome.Refused(EnrollStatus.BadToken);
        }
        catch (CryptographicException)
        {
            // The supplied public key did not decode as a recognizable ECDSA
            // SPKI. Treat it as a malformed enroll: the token is intact, but
            // the request is bad.
            return ScopedEnrollmentOutcome.Malformed("Public key is not a recognizable ECDSA SubjectPublicKeyInfo.");
        }
        catch (EngagementClosedException ex)
        {
            // The token redeemed but its engagement is frozen for close-out or
            // retired (architecture.md Sec 2 step 10): no new deployments. A
            // problem, not the BadToken shape -- the token was valid, and the
            // operator driving the deployment needs the real cause.
            return new ScopedEnrollmentOutcome(
                false, EnrollStatus.Unspecified, ex.Message, StatusCodes.Status409Conflict, null, null);
        }
        catch (InvalidOperationException)
        {
            // The token redeemed but its engagement was since torn down.
            return ScopedEnrollmentOutcome.Refused(EnrollStatus.BadToken);
        }
    }

    // A host fact is implant-reported free text: trim it, cap it, and drop it to
    // null when empty, so the stored device identity stays a bounded, honest
    // echo of what the implant said rather than an arbitrary-length blob.
    private const int MaxHostFactLength = 256;

    private static string? CleanHostFact(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? null
            : trimmed.Length <= MaxHostFactLength ? trimmed : trimmed[..MaxHostFactLength];
    }

    // A cadence half the record may hold: null (not supplied) or a finite,
    // non-negative second count. Anything else is a malformed report.
    private static bool IsReportableSeconds(double? value)
        => value is null || (double.IsFinite(value.Value) && value.Value >= 0);

    // The enroll audit payload: class, lineage, and the reported host -- the
    // words an operator reads back in the audit trail for "what enrolled where".
    private static string BuildEnrollPayload(EnrollmentResult enrolled)
    {
        var parts = new List<string> { enrolled.Class.ToString() };
        if (enrolled.ParentImplantId is { } parent)
            parts.Add($"parent={parent}");
        if (enrolled.Hostname is { } hostname)
            parts.Add($"host={hostname}");
        return string.Join(' ', parts);
    }
}
