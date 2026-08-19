using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Rod.Operators.Auth;

// The session stamp (architecture.md Sec 9, certificate revocation): what
// ends a live cookie session when its credential is revoked. A cookie is
// self-contained, so its lifetime has to be bounded against the credential
// it was issued from -- at login the principal carries a stamp derived from
// the stored password verifier, and every authenticated request re-derives
// the stamp from the verifier the store holds now. A revoked credential (the
// verifier is gone) or a re-provisioned one (a new password is a new
// verifier) fails the comparison and the principal is rejected at the very
// request that presented the cookie. The stamp is a digest of the verifier,
// not the verifier: the cookie carries nothing an attacker could use even
// with the cookie in hand.

/// <summary>
/// Derives and reads the session-stamp claim that binds a cookie session to
/// the generation of the credential that issued it.
/// </summary>
public static class SessionStamp
{
    /// <summary>
    /// The claim carrying the stamp. Only this layer writes and reads it, so
    /// unlike the identity claims it lives here rather than in core state's
    /// shared contract.
    /// </summary>
    public const string ClaimType = "rod:operator-session-stamp";

    /// <summary>
    /// Computes the stamp for a stored password verifier: a 128-bit digest --
    /// enough to distinguish credential generations, small enough to ride in
    /// the cookie unnoticed.
    /// </summary>
    public static string Compute(string passwordHash)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash));
        return Convert.ToBase64String(digest, 0, 16);
    }

    /// <summary>Builds the stamp claim for a login whose verifier verified.</summary>
    public static Claim Claim(string passwordHash) => new(ClaimType, Compute(passwordHash));
}
