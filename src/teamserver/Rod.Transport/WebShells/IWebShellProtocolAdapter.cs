using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Rod.Transport.WebShells;

/// <summary>
/// One encoded request to a web-shell endpoint, ready to POST as a form.
/// The per-request markers (<see cref="TagStart"/>/<see cref="TagEnd"/>)
/// ride on the request because the request itself bakes them into the
/// payload it sends -- the classic managers' shape, where the shell only
/// evaluates what arrives and the client frames its own answers -- so
/// the decoder on the response side reads the same pair back. The
/// credential rides along for the families that seal: a Rod-native
/// request decrypts its answer under the same key it encrypted with.
/// </summary>
public sealed record WebShellRequest(
    string Url,
    IReadOnlyDictionary<string, string> Form,
    string TagStart,
    string TagEnd,
    string Credential);

/// <summary>
/// A web-shell protocol adapter (architecture.md Sec 5.2's Web-shell
/// class): the pure encode/decode half of talking to one script family.
/// The adapter knows no HTTP, no tasks, and no engagement -- it turns a
/// command into a form and a response body into text, which keeps every
/// protocol family independently testable and keeps the out-of-tree
/// boundary clean: an adapter for anything else plugs in as a module
/// without the core learning anything about it (architecture.md Sec 13).
///
/// The in-tree families are the Rod-native one (a one-line script whose
/// 256-bit baked key seals the channel as AES-256-GCM) and the
/// AntSword-compatible eval family (MIT, kept as interop for scripts
/// placed for other managers); adapters for closed-source tools'
/// protocols arrive out-of-tree through the tradecraft layer's
/// registration path.
/// </summary>
public interface IWebShellProtocolAdapter
{
    /// <summary>The adapter's registry id (e.g. <c>rod-php</c>).</summary>
    string Id { get; }

    /// <summary>The script language the generated one-liner is written in.</summary>
    string ScriptLanguage { get; }

    /// <summary>The request encoder this adapter implements.</summary>
    string DefaultEncoder { get; }

    /// <summary>The response decoder this adapter implements.</summary>
    string DefaultDecoder { get; }

    /// <summary>
    /// A fresh credential for this family -- the connection password the
    /// classic managers use, or the 256-bit key the sealed families bake
    /// into the script. Called when a register or generate request names
    /// no credential of its own.
    /// </summary>
    string GenerateCredential();

    /// <summary>
    /// Whether a caller-supplied credential is usable by this family --
    /// any token for the password families, a base64 256-bit key for the
    /// sealed ones. Registration and generation check this so a malformed
    /// credential is refused before a broken script is ever placed.
    /// </summary>
    bool IsValidCredential(string credential);

    /// <summary>
    /// A noun phrase naming the credential shape this family accepts, for
    /// refusal messages ("the base64 of a 256-bit key", "a short token").
    /// </summary>
    string CredentialHint { get; }

    /// <summary>
    /// Renders the script an operator places in the target's web root --
    /// the whole server-side footprint of the endpoint.
    /// </summary>
    string RenderScript(string password);

    /// <summary>
    /// Encodes one command as a POST form against the endpoint. Throws
    /// <see cref="NotSupportedException"/> for an encoder or decoder the
    /// adapter does not implement.
    /// </summary>
    WebShellRequest EncodeCommand(
        string url,
        string password,
        string encoder,
        string decoder,
        string command);

    /// <summary>
    /// Decodes a response body using the request's own markers and the
    /// profile's decoder. Returns null when the markers are absent -- an
    /// endpoint answering something that is not this protocol.
    /// </summary>
    string? DecodeResponse(WebShellRequest request, string decoder, ReadOnlySpan<char> body);
}

/// <summary>
/// The adapter registry, keyed by id -- the web-shell surface's answer to
/// the transport provider registry: an adapter added later registers
/// itself rather than an enumeration growing an arm. In-tree adapters
/// register here at startup; out-of-tree adapters register through the
/// tradecraft layer's module path.
/// </summary>
public static class WebShellAdapters
{
    private static readonly ConcurrentDictionary<string, IWebShellProtocolAdapter> Adapters = new();

    static WebShellAdapters()
    {
        Register(new RodPhpAdapter());
        Register(new AntSwordPhpAdapter());
    }

    /// <summary>Registers an adapter under its id; a duplicate id is refused.</summary>
    public static void Register(IWebShellProtocolAdapter adapter)
    {
        if (!Adapters.TryAdd(adapter.Id, adapter))
            throw new InvalidOperationException($"A web-shell adapter is already registered as '{adapter.Id}'.");
    }

    /// <summary>The adapter for an id, or null when none is registered.</summary>
    public static IWebShellProtocolAdapter? Find(string? id)
        => id is null ? null : Adapters.TryGetValue(id, out var adapter) ? adapter : null;

    /// <summary>Every registered adapter id, for the operator surface to offer.</summary>
    public static IReadOnlyList<string> Names() => Adapters.Keys.OrderBy(k => k).ToArray();

    /// <summary>
    /// A random lowercase-alphanumeric name -- the POST variable the
    /// payload rides in and the marker halves both draw from this shape,
    /// so one helper serves both.
    /// </summary>
    internal static string RandomToken(int minLength, int maxLength)
    {
        var length = RandomNumberGenerator.GetInt32(minLength, maxLength + 1);
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }
}
