namespace Rod.Transport.Listeners.Providers;

/// <summary>
/// The public endpoint's dial shapes, shared by the operator surface and the
/// providers (architecture.md Sec 8): the address implants are told to dial,
/// which each transport shapes differently. The web family dials an endpoint
/// -- an absolute http(s) URL is already what a payload build bakes, a bare
/// host:port is the redirector front, and a hostname without a port is
/// completed against the listener's own bind port. The socket-owning
/// transports dial their own shapes: a DNS zone, a pipe path, a host:port.
/// </summary>
public static class PublicEndpointShapes
{
    /// <summary>An absolute http(s) URL, the shape a payload build bakes verbatim.</summary>
    public static bool IsAbsoluteHttpUrl(string text)
        => Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// A DNS name or literal IP with a port; no scheme-less bare host,
    /// because nothing downstream can guess a port.
    /// </summary>
    public static bool IsHostPort(string text)
    {
        var value = text.Trim();
        var colon = value.LastIndexOf(':');
        if (colon <= 0 || colon == value.Length - 1)
            return false;
        if (!int.TryParse(value[(colon + 1)..], out var port) || port is < 1 or > 65535)
            return false;
        var host = value[..colon];
        return host.Length > 0
            && host.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');
    }

    /// <summary>
    /// The web family's two complete-ish shapes: an absolute URL or a
    /// host:port pair.
    /// </summary>
    public static bool IsWebDial(string text)
        => IsAbsoluteHttpUrl(text) || IsHostPort(text);

    /// <summary>
    /// A hostname without a port: host characters and at least one letter --
    /// the letter requirement keeps an all-digits typo (a port typed alone)
    /// from reading as a dialable name.
    /// </summary>
    public static bool IsBareHost(string value)
        => value.Length > 0
            && value.Any(char.IsLetter)
            && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');

    /// <summary>A DNS zone: letters, digits, dots, hyphens.</summary>
    public static bool IsDnsZone(string value)
        => value.Length > 0
            && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-');

    /// <summary>A pipe path: the Windows pipe prefix's backslashes included.</summary>
    public static bool IsPipePath(string value)
        => value.Length > 0
            && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '\\' or '_');

    /// <summary>A socket host:port or host shape for the raw-TCP dial.</summary>
    public static bool IsSocketDial(string value)
        => value.Length > 0
            && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or ':' or '_');
}
