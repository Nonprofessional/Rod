using System.Security.Cryptography;

namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// SHA-256 fingerprinting of build output (architecture.md Sec 6: every
/// generated artifact is fingerprinted). Lowercase hex, 64 characters. Centralized
/// so the build unit and the recorder cannot drift on the encoding.
/// </summary>
public static class ArtifactFingerprint
{
    /// <summary>SHA-256 over <paramref name="content"/>, lowercase hex.</summary>
    public static string Of(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
