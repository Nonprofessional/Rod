using Microsoft.Extensions.Configuration;

namespace Rod.Transport.Listeners;

/// <summary>
/// The push-only configuration source behind runtime listener management. The
/// Kestrel endpoint reloader binds and unbinds the endpoints under
/// <c>Kestrel:Endpoints</c> when the configuration section's reload token
/// fires, so a listener created after startup rides this provider: the
/// manager writes the endpoint's URL under the listener's id and pushes; a
/// delete removes the keys and pushes. The provider holds nothing but those
/// endpoint entries -- the startup-configured listeners stay on the ordinary
/// code-bound path and are untouched by the reloader.
///
/// Each entry may also carry a per-endpoint <c>ClientCertificateMode</c>, the
/// one HTTPS knob the transports differ on that <c>ConfigureHttpsDefaults</c>
/// cannot decide (it runs for every endpoint alike): the mTLS-shaped listeners
/// request the client certificate, and every other TLS endpoint -- the https
/// transport's fingerprint rule (architecture.md Sec 8/9) -- leaves it unset,
/// which is <c>NoCertificate</c>: no request at all.
/// </summary>
public sealed class DynamicEndpointsConfiguration
{
    private readonly PushableProvider _provider = new();

    /// <summary>The Kestrel section the manager writes endpoint entries into.</summary>
    public IConfiguration KestrelSection { get; }

    /// <summary>Initializes the push-only source; the host hands its section to Kestrel's reloader.</summary>
    public DynamicEndpointsConfiguration()
    {
        var root = new ConfigurationRoot(new IConfigurationProvider[] { _provider });
        KestrelSection = root.GetSection("Kestrel");
    }

    /// <summary>
    /// Publishes an endpoint under the listener's key and signals the reload;
    /// Kestrel binds the socket asynchronously (the reloader's own task),
    /// which is why the manager follows up with a port probe before
    /// reporting the listener as running. The optional
    /// <paramref name="clientCertificateMode"/> rides as the per-endpoint
    /// Kestrel key of the same name (its enum name, e.g.
    /// <c>AllowCertificate</c>); null publishes the URL alone.
    /// </summary>
    public void PublishEndpoint(string key, string url, string? clientCertificateMode = null)
    {
        _provider.Publish(key, url, clientCertificateMode);
    }

    /// <summary>Withdraws the endpoint and signals the reload; Kestrel drains the socket for up to its shutdown timeout, then unbinds.</summary>
    public void WithdrawEndpoint(string key)
    {
        _provider.Withdraw(key);
    }

    private sealed class PushableProvider : ConfigurationProvider
    {
        // Keys are provider-absolute: the root exposes the Kestrel section,
        // so an endpoint entry rides under "Kestrel:Endpoints:{key}:Url" and,
        // when set, "Kestrel:Endpoints:{key}:ClientCertificateMode". The
        // loader is handed the section and reads "Endpoints" relative to it,
        // which lands on exactly these keys.
        public void Publish(string key, string? url, string? clientCertificateMode)
        {
            Data[$"Kestrel:Endpoints:{key}:Url"] = url;
            if (clientCertificateMode is null)
                Data.Remove($"Kestrel:Endpoints:{key}:ClientCertificateMode");
            else
                Data[$"Kestrel:Endpoints:{key}:ClientCertificateMode"] = clientCertificateMode;
            OnReload();
        }

        public void Withdraw(string key)
        {
            Data.Remove($"Kestrel:Endpoints:{key}:Url");
            Data.Remove($"Kestrel:Endpoints:{key}:ClientCertificateMode");
            OnReload();
        }
    }
}
