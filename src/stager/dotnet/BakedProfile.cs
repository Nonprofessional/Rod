namespace Rod.Stager;

// The checked-in stub: compiles empty so the stager runs from flags/env during
// development; the .NET build unit overwrites it with the per-build profile in
// its staging copy (the same mechanism the reference implant's BakedProfile
// uses). The generated shape is a base64url-encoded JSON object with the keys
// the stager consumes: enrollURL, stage2PayloadId, stage2Sha256, killDate.
internal static class BakedProfile
{
    public const string Json = "";
}
