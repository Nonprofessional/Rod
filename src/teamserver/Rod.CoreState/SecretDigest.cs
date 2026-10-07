using System.Security.Cryptography;

namespace Rod.CoreState;

/// <summary>
/// Mint-and-digest helpers for bearer secrets (operator API tokens, deploy
/// tokens): a secret is 32 random bytes shown to an operator exactly once as
/// base64url, and only its SHA-256 digest is ever stored or compared. One
/// definition for every store -- in-memory and durable alike -- so entropy,
/// encoding, and digest can never drift between them.
/// </summary>
public static class SecretDigest
{
    /// <summary>Mints a fresh secret with the digest to store beside its record.</summary>
    public static (string Secret, byte[] Digest) Mint()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return (Base64Url.Encode(bytes), SHA256.HashData(bytes));
    }

    /// <summary>
    /// Digests a presented secret for a lookup. A malformed secret answers
    /// <see langword="null"/> -- it never reaches the digest comparison.
    /// </summary>
    public static byte[]? DigestOf(string secret)
    {
        try
        {
            return SHA256.HashData(Base64Url.Decode(secret));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
