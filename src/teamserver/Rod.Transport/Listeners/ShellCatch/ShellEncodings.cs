using System.Text;

namespace Rod.Transport.Listeners.ShellCatch;

/// <summary>
/// The encodings a caught-shell session may speak (architecture.md Sec 8).
/// UTF-8 is the default and the right answer for every Unix shell; GBK
/// covers the cmd.exe boxes still on a legacy Chinese code page. The set is
/// closed on purpose: an operator picks from what real targets run, not an
/// arbitrary code-page hunt, and each entry's decode is available on every
/// host this assembly loads into.
/// </summary>
public static class ShellEncodings
{
    /// <summary>
    /// The legacy code pages (GBK & kin) live outside .NET's core lib: the
    /// provider must be registered before <see cref="Find"/> can resolve
    /// them. The composition root calls this once (TransportHost's service
    /// setup), so every host shape -- the teamserver and the integration
    /// test hosts -- shares the one registration point. Idempotent.
    /// </summary>
    public static void EnsureProviderRegistered()
        => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// Resolves a wire name to its encoding, or null when the session
    /// encoding does not offer it.
    /// </summary>
    public static Encoding? Find(string name)
        => name.Trim().ToLowerInvariant() switch
        {
            "utf-8" or "utf8" => Encoding.UTF8,
            "gbk" => Encoding.GetEncoding(936),
            _ => null,
        };

    /// <summary>
    /// The wire name an encoding reports back -- .NET calls code page 936
    /// "gb2312", its IANA alias, while the offered name the console sends is
    /// "gbk"; the roster must answer with the name the catalog offered, or
    /// the selector shows a value it never set.
    /// </summary>
    public static string NameOf(Encoding encoding)
        => encoding.WebName == "gb2312" ? "gbk" : encoding.WebName;

    /// <summary>
    /// The names the console offers, in preference order; each maps through
    /// <see cref="Find"/>.
    /// </summary>
    public static readonly string[] Offered = ["utf-8", "gbk"];
}
