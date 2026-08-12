using System.Net.Http.Headers;

namespace Rod.Integration.Tests;

/// <summary>
/// A <see cref="DelegatingHandler"/> that stores every <c>Set-Cookie</c> the
/// server issues and replays them on subsequent requests, so cookie-authenticated
/// round-trips work through the in-memory TestServer (whose default client does
/// not persist cookies). Cookies are stored by name; an empty value (the expired
/// cookie a sign-out sets) deletes the entry, so logout is observable. ASP.NET
/// Core chunked auth cookies are handled transparently: every chunk is stored
/// under its own name and replayed, and the server reassembles them.
/// </summary>
internal sealed class CookieHandler : DelegatingHandler
{
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

    public CookieHandler(HttpMessageHandler inner)
        : base(inner)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (_cookies.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var header in setCookies)
            {
                // Set-Cookie: Name=Value; attribute=value; ... -- keep only the
                // first Name=Value segment. An empty value is the expired cookie
                // a sign-out issues; treat it as deletion.
                var pair = header.AsSpan();
                var semi = pair.IndexOf(';');
                if (semi >= 0)
                    pair = pair[..semi];

                var eq = pair.IndexOf('=');
                if (eq <= 0)
                    continue;

                var name = pair[..eq].ToString();
                var value = pair[(eq + 1)..].ToString();
                if (string.IsNullOrEmpty(value))
                    _cookies.Remove(name);
                else
                    _cookies[name] = value;
            }
        }

        return response;
    }
}
