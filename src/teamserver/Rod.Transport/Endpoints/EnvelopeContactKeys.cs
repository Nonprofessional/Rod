using System.Collections.Concurrent;
using Rod.CoreState;

namespace Rod.Transport.Endpoints;

// The per-artifact key posture for the envelope contact (architecture.md
// Sec 8/9): possession of the key the build minted and baked is what
// authenticates an implant's contact bodies -- the mainstream HTTP(S) C2
// shape, replacing the TLS client certificate the https transport no longer
// requests. This is the process-local bookkeeping that posture needs beyond
// the key itself (which lives beside the stored payload, resolved by the key
// id every sealed body prefixes):
//
// - The enroll-time binding: when the redeemed token was the one a build
//   minted, the enrollment binds the new implant to that build's key, so a
//   contact from that implant never downgrades to a plaintext body.
//
// - The contact counter floor: every sealed body covers a strictly
//   increasing counter, and the floor is what turns a replayed body away.
//   In-memory like the sessions it protects: a restart resets the floor, the
//   same freshness boundary the presence roster already documents.

/// <summary>
/// One entry per enrolled implant: the artifact key its sealed contacts
/// authenticated against at enroll time, and the highest contact counter
/// accepted from it. Registered as a singleton; holds no secrets the payload
/// store does not already keep (the key bytes here are the teamserver's
/// recorded half, bound to the implant so the plaintext refusal cannot be
/// talked out of).
/// </summary>
public sealed class EnvelopeContactKeys
{
    private sealed record Binding(Guid KeyId, byte[] Key);

    private readonly ConcurrentDictionary<ImplantId, Binding> _bindings = new();
    private readonly ConcurrentDictionary<ImplantId, long> _floors = new();

    /// <summary>
    /// Binds <paramref name="implant"/> to the artifact key its build minted,
    /// recorded beside the payload the redeemed token was baked into. Called
    /// by the enroll endpoint after a successful enrollment; a later bind for
    /// the same implant (a re-enroll under a different build's token) replaces
    /// the entry -- the newest credential names the posture.
    /// </summary>
    public void Bind(ImplantId implant, Guid keyId, byte[] key)
        => _bindings[implant] = new Binding(keyId, key);

    /// <summary>
    /// The key this implant's contacts must seal under, or null when its
    /// enrollment carried no build-minted credential (the manual-mint shape:
    /// sealed bodies still resolve by their own key id, but a plaintext body
    /// is not refused).
    /// </summary>
    public (Guid KeyId, byte[] Key)? TryGet(ImplantId implant)
        => _bindings.TryGetValue(implant, out var binding)
            ? (binding.KeyId, binding.Key)
            : null;

    /// <summary>
    /// Accepts a strictly increasing contact counter and advances the
    /// implant's floor, or refuses it as a replay. The floor starts at zero
    /// and lives for the process: a fresh counter on every attempt (the
    /// implant increments before each POST, so a retransmitted batch after a
    /// lost response still carries a new counter) is what keeps a lost
    /// response from wedging the cadence while a captured body stays
    /// worthless.
    /// </summary>
    public bool Accept(ImplantId implant, long counter)
    {
        while (true)
        {
            var floor = _floors.GetOrAdd(implant, 0L);
            if (counter <= floor)
                return false;
            if (_floors.TryUpdate(implant, counter, floor))
                return true;
            // Lost the race with a concurrent contact from the same implant;
            // re-read the floor and retry.
        }
    }
}
