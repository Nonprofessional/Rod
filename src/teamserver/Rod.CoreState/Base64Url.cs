namespace Rod.CoreState;

/// <summary>
/// RFC 4648 base64url without padding: URL-safe, so opaque cursors, stager
/// tokens, and implant key ids travel in paths and headers unchanged. One
/// definition for every core-state, persistence, and build-pipeline encoding
/// -- the same shape the reference implant's decoder reads -- so the sides can
/// never drift apart.
/// </summary>
public static class Base64Url
{
    public static string Encode(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    public static byte[] Decode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}
