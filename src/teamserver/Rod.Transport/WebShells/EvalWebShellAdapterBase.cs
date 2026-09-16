using System.Text;

namespace Rod.Transport.WebShells;

/// <summary>
/// The universal eval family's shared half: a short connection token, the
/// classic marker-framed answer (random per-request markers, each echoed as
/// two concatenated halves so the wrapper itself carries no fixed
/// signature, around a base64-encoded body), and the credential members.
/// A language subclass supplies its rendered one-liner and the wrapper its
/// request carries; the answer's framing is the family's own, whichever
/// language the placed script speaks.
/// </summary>
public abstract class EvalWebShellAdapterBase : IWebShellProtocolAdapter
{
    public abstract string Id { get; }
    public abstract string ScriptLanguage { get; }
    public abstract string RenderScript(string password);
    public abstract WebShellRequest EncodeCommand(
        string url, string password, string encoder, string decoder, string command);

    /// <summary>
    /// How the rendered one-liner spells its password literal: the text
    /// before and after the token (each language indexes its request its
    /// own way).
    /// </summary>
    protected abstract (string Prefix, string Suffix) PasswordLiteral { get; }

    public string DefaultEncoder => "base64";
    public string DefaultDecoder => "base64";

    public string GenerateCredential() => WebShellAdapters.RandomToken(6, 12);

    public bool IsValidCredential(string credential)
        => credential.Length is >= 6 and <= 32 && !credential.Contains(' ');

    public string CredentialHint => "a short token without spaces";

    public string? ReadCredentialFromScript(string script)
    {
        var (prefix, suffix) = PasswordLiteral;
        var start = script.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
            return null;
        var from = start + prefix.Length;
        var end = script.IndexOf(suffix, from, StringComparison.Ordinal);
        return end < 0 ? null : script[from..end];
    }

    public string? DecodeResponse(WebShellRequest request, string decoder, ReadOnlySpan<char> body)
    {
        var start = body.IndexOf(request.TagStart);
        if (start < 0)
            return null;
        var afterStart = start + request.TagStart.Length;
        var end = body.Slice(afterStart).IndexOf(request.TagEnd);
        if (end < 0)
            return null;

        var encoded = body.Slice(afterStart, end).Trim();
        if (decoder != "base64")
            return encoded.ToString();

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded.ToString()));
        }
        catch (FormatException)
        {
            // The markers matched but the body between them is not the
            // base64 this decoder expects -- an endpoint answering a
            // different protocol shape.
            return null;
        }
    }

    // The two halves of a marker: split at the middle so the wrapper's own
    // text never contains the marker whole.
    protected static string Half(string tag) => tag[..(tag.Length / 2)];

    protected static string HalfBack(string tag) => tag[(tag.Length / 2)..];
}
