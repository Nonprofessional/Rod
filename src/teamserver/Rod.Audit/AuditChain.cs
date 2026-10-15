using System.Security.Cryptography;
using System.Text;

namespace Rod.Audit;

/// <summary>
/// The per-engagement hash chain over <see cref="AuditEvent"/>s (architecture.md
/// Sec 11; storage &amp; audit layer). Each event's hash is taken
/// over its own contents plus the previous event's hash, so every event commits
/// to its predecessor: altering any stored event changes its hash, which breaks
/// the link its successor carries -- the chain is the tamper-evident binding
/// that makes the audit trail self-verifying.
///
/// The canonical form is a fixed-order concatenation of the event's fields and
/// its <see cref="AuditEvent.PreviousHash"/>. SHA-256 over a hand-built byte
/// join (rather than a JSON serialization) keeps the audit layer free of any
/// serializer's property-ordering/options and the hash stable and
/// runtime-independent. The audit layer stays a zero-package classlib on
/// purpose -- it is the innermost ring.
///
/// An unkeyed chain detects partial rewrites, but anyone with write access to
/// the store can rewrite a whole engagement consistently and pass
/// verification. A keyed chain (<see cref="Chain(AuditEvent, string, byte[]?)"/>
/// with a supplied key) closes that hole against attackers who hold the
/// storage but not the configuration: keyed links are HMAC-SHA256 over the
/// same canonical form, carried with an <c>H1:</c> prefix so the two shapes
/// are distinguishable on the stored event. The boundary stays honest: the
/// key's own custody is only as strong as wherever it is kept, and links
/// written before the key was configured remain unkeyed -- full protection
/// against a whole-trail rewrite of pre-key history needs an out-of-band
/// anchor this layer deliberately does not fake.
/// </summary>
public static class AuditChain
{
    /// <summary>
    /// The <see cref="AuditEvent.PreviousHash"/> carried by the first event of an
    /// engagement: a SHA-256-length all-zero string. Each engagement's chain is
    /// independent, so every engagement's first link starts here.
    /// </summary>
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    // Marks a keyed link's stored hash: HMAC-SHA256 under the configured
    // chain key, hex-encoded after this prefix. Unkeyed links carry the bare
    // hex SHA-256.
    public const string KeyedHashPrefix = "H1:";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Stamps <paramref name="fact"/> onto a chain link that follows
    /// <paramref name="previousHash"/>: sets <see cref="AuditEvent.PreviousHash"/>
    /// and computes <see cref="AuditEvent.Hash"/> over the resulting event. The
    /// incoming fact's hash fields are ignored (a <see cref="AuditEvent.Fact"/>
    /// carries them empty). The hash covers <see cref="AuditEvent.PreviousHash"/>
    /// so the link binds to its predecessor. With a <paramref name="key"/> the
    /// link is an HMAC-SHA256 (prefixed, see remarks on the class).
    /// </summary>
    public static AuditEvent Chain(AuditEvent fact, string previousHash, byte[]? key = null)
    {
        var linked = fact with { PreviousHash = previousHash };
        return linked with { Hash = ComputeHash(linked, key) };
    }

    /// <summary>
    /// SHA-256 (or HMAC-SHA256 under <paramref name="key"/>) over the canonical
    /// byte form of <paramref name="event"/>. Two events with identical fields
    /// (including <see cref="AuditEvent.PreviousHash"/>) hash identically; any
    /// field change yields a different hash. A keyed hash carries the
    /// <see cref="KeyedHashPrefix"/> so the stored form names its own algorithm.
    /// </summary>
    public static string ComputeHash(AuditEvent @event, byte[]? key = null)
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
        if (key is null)
        {
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
        }
        var mac = HMACSHA256.HashData(key, bytes);
        return KeyedHashPrefix + Convert.ToHexString(mac);
    }

    /// <summary>
    /// Walks an engagement's trail oldest-first and returns the first link whose
    /// stored hash is wrong, or whose <see cref="AuditEvent.PreviousHash"/> does
    /// not match the previous event's hash. A trail that verifies returns null.
    /// This is the tamper check: any rewritten event, or any reordering, surfaces
    /// here. The first event must follow <see cref="GenesisHash"/>.
    ///
    /// Each link is re-hashed under the algorithm its own stored form names
    /// (keyed links need <paramref name="key"/>), so a trail that predates keying
    /// still verifies. Two keyed-mode rules close the downgrade paths: once a
    /// keyed link appears, every later link must be keyed too (the writer never
    /// goes back), and verifying a keyed trail without the key fails rather
    /// than waving the links through.
    /// </summary>
    public static ChainBreak? VerifyTrail(IReadOnlyList<AuditEvent> trail, byte[]? key = null)
    {
        var previousHash = GenesisHash;
        var seenKeyed = false;
        for (var i = 0; i < trail.Count; i++)
        {
            var @event = trail[i];
            if (@event.PreviousHash != previousHash)
                return new ChainBreak(i, ChainBreakKind.PreviousHashMismatch);

            var storedIsKeyed = @event.Hash.StartsWith(KeyedHashPrefix, StringComparison.Ordinal);
            if (storedIsKeyed)
            {
                if (key is null)
                    return new ChainBreak(i, ChainBreakKind.HashMismatch);
                seenKeyed = true;
            }
            else if (seenKeyed)
            {
                // The writer moved to keyed links and never goes back: a bare
                // hash after a keyed one is a downgrade rewrite.
                return new ChainBreak(i, ChainBreakKind.HashMismatch);
            }

            if (ComputeHash(@event, storedIsKeyed ? key : null) != @event.Hash)
                return new ChainBreak(i, ChainBreakKind.HashMismatch);

            previousHash = @event.Hash;
        }

        // A verifier holding the key refuses a trail that never carried a
        // keyed link: in a deployment that keys its chain, a fully unkeyed
        // trail is a consistent rewrite (the forger cannot forge keyed
        // links), not something the writer produces. The one honest
        // exception -- a trail that predates the key and never grew after
        // -- is told apart by verifying once without the key.
        if (key is not null && trail.Count > 0 && !seenKeyed)
            return new ChainBreak(0, ChainBreakKind.UnkeyedTrailWithKey);

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

    /// <summary>
    /// The verifier holds a chain key but the trail carries no keyed link at all:
    /// in a deployment that keys its chain this is a rewrite into unkeyed form
    /// (the forger cannot produce keyed links), unless the trail simply predates
    /// the key -- which the verifier can re-check without the key to tell apart.
    /// </summary>
    UnkeyedTrailWithKey,
}
