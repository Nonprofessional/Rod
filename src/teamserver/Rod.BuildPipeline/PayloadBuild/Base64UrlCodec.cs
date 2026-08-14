namespace Rod.BuildPipeline.PayloadBuild;

// RFC 4648 base64url without padding. URL-safe, matches the stager-token and
// implant-key encoding in core state and the implant's own DecodeBase64Url
// decoder, so baked profiles round-trip across both sides of the build
// contract.
internal static class Base64UrlCodec
{
    public static string Encode(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
