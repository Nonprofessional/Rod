using System.Security.Cryptography;
using System.Text;

namespace Rod.Audit;

/// <summary>
/// The per-engagement hash chain over <see cref="AuditEvent"/>s (architecture.md
/// Sec 11; storage &amp; audit layer, roadmap M2.3). Each event's hash is taken
/// over its own contents plus the previous event's hash, so every event commits
/// to its predecessor: altering any stored event changes its hash, which breaks
/// the link its successor carries -- the chain is the tamper-evident binding
/// that makes the audit trail self-checking.
///
/// The canonical form is a fixed-order concatenation of the event's fields and
/// its <see cref="AuditEvent.PreviousHash"/>, hashed with SHA-256 and hex-encoded.
/// SHA-256 over a hand-built byte join (rather than a JSON serialization) keeps
/// the audit layer free of any serializer's property-ordering/options and the
/// hash stable and runtime-independent. The audit layer stays a zero-package
/// classlib on purpose -- it is the innermost ring.
/// </summary>
public static class AuditChain
{
    /// <summary>
    /// The <see cref="AuditEvent.PreviousHash"/> carried by the first event of an
    /// engagement: a SHA-256-length all-zero string. Each engagement's chain is
    /// independent, so every engagement's first link starts here.
    /// </summary>
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Stamps <paramref name="fact"/> onto a chain link that follows
    /// <paramref name="previousHash"/>: sets <see cref="AuditEvent.PreviousHash"/>
    /// and computes <see cref="AuditEvent.Hash"/> over the resulting event. The
    /// incoming fact's hash fields are ignored (a <see cref="AuditEvent.Fact"/>
    /// carries them empty). The hash covers <see cref="AuditEvent.PreviousHash"/>
    /// so the link binds to its predecessor.
    /// </summary>
    public static AuditEvent Chain(AuditEvent fact, string previousHash)
    {
        var linked = fact with { PreviousHash = previousHash };
        return linked with { Hash = ComputeHash(linked) };
    }

    /// <summary>
    /// SHA-256 over the canonical byte form of <paramref name="event"/>. Two
    /// events with identical fields (including <see cref="AuditEvent.PreviousHash"/>)
    /// hash identically; any field change yields a different hash.
    /// </summary>
    public static string ComputeHash(AuditEvent @event)
    {
        // Fixed field order: the canonical form is its own contract, so changing
        // this order changes every hash. Append a separator that cannot appear in
        // the joined value to keep adjacent fields unambiguous.
        var canonical = new StringBuilder()
            .Append(@event.EventId).Append('\u001f')
            .Append(@event.EngagementId).Append('\u001f')
            .Append(@event.OperatorId).Append('\u001f')
            .Append(@event.ImplantId).Append('\u001f')
            .Append(@event.TaskId).Append('\u001f')
            .Append(@event.Verb).Append('\u001f')
            .Append((int)@event.Kind).Append('\u001f')
            .Append(@event.Payload).Append('\u001f')
            .Append(@event.Output ?? string.Empty).Append('\u001f')
            .Append(@event.Outcome).Append('\u001f')
            .Append(@event.At.ToUnixTimeMilliseconds()).Append('\u001f')
            .Append(@event.PreviousHash)
            .ToString();

        var bytes = Utf8.GetBytes(canonical);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Walks an engagement's trail oldest-first and returns the first link whose
    /// stored hash is wrong, or whose <see cref="AuditEvent.PreviousHash"/> does
    /// not match the previous event's hash. A trail that verifies returns null.
    /// This is the tamper check: any rewritten event, or any reordering, surfaces
    /// here. The first event must follow <see cref="GenesisHash"/>.
    /// </summary>
    public static ChainBreak? VerifyTrail(IReadOnlyList<AuditEvent> trail)
    {
        var previousHash = GenesisHash;
        for (var i = 0; i < trail.Count; i++)
        {
            var @event = trail[i];
            if (@event.PreviousHash != previousHash)
                return new ChainBreak(i, ChainBreakKind.PreviousHashMismatch);

            if (ComputeHash(@event) != @event.Hash)
                return new ChainBreak(i, ChainBreakKind.HashMismatch);

            previousHash = @event.Hash;
        }

        return null;
    }
}

/// <summary>
/// Where a trail first fails to verify (returned by
/// <see cref="AuditChain.VerifyTrail"/>). <see cref="Index"/> is the position in
/// the passed trail; <see cref="Kind"/> says whether that event's own hash is
/// wrong (the event was rewritten) or its link to the previous event is wrong
/// (it or an earlier event was rewritten/reordered).
/// </summary>
public sealed record ChainBreak(int Index, ChainBreakKind Kind);

/// <summary>The way a chain link failed verification.</summary>
public enum ChainBreakKind
{
    /// <summary>The event's <see cref="AuditEvent.PreviousHash"/> is not its predecessor's hash.</summary>
    PreviousHashMismatch,

    /// <summary>The event's stored <see cref="AuditEvent.Hash"/> does not match a fresh computation.</summary>
    HashMismatch,
}
