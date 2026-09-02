using Microsoft.Extensions.Configuration;

namespace Rod.Transport.Listeners;

/// <summary>
/// The push-only configuration source behind runtime listener management. The
/// Kestrel endpoint reloader binds and unbinds the endpoints under
/// <c>Kestrel:Endpoints</c> when the configuration section's reload token
/// fires, so a listener created after startup rides this provider: the
/// manager writes the endpoint's URL under the listener's id and pushes; a
/// delete removes the key and pushes. The provider holds nothing but those
/// endpoint entries -- the startup-configured listeners stay on the ordinary
/// code-bound path and are untouched by the reloader.
/// </summary>
internal sealed class DynamicEndpointsConfiguration
{
    private readonly PushableProvider _provider = new();

    /// <summary>The Kestrel section the manager writes endpoint entries into.</summary>
    public IConfiguration KestrelSection { get; }

    internal DynamicEndpointsConfiguration()
    {
        var root = new ConfigurationRoot(new IConfigurationProvider[] { _provider });
        KestrelSection = root.GetSection("Kestrel");
    }

    /// <summary>
    /// Publishes an endpoint URL under the listener's key and signals the
    /// reload; Kestrel binds the socket asynchronously (the reloader's own
    /// task), which is why the manager follows up with a port probe before
    /// reporting the listener as running.
    /// </summary>
    public void PublishEndpoint(string key, string url)
    {
        _provider.Publish(key, url);
    }

    /// <summary>Withdraws the endpoint and signals the reload; Kestrel drains the socket for up to its shutdown timeout, then unbinds.</summary>
    public void WithdrawEndpoint(string key)
    {
        _provider.Withdraw(key);
    }

    private sealed class PushableProvider : ConfigurationProvider
    {
        // Keys are provider-absolute: the root exposes the Kestrel section,
        // so an endpoint entry rides under "Kestrel:Endpoints:{key}:Url".
        // The loader is handed the section and reads "Endpoints" relative to
        // it, which lands on exactly these keys.
        public void Publish(string key, string? value)
        {
            Data[$"Kestrel:Endpoints:{key}:Url"] = value;
            OnReload();
        }

        public void Withdraw(string key)
        {
            Data.Remove($"Kestrel:Endpoints:{key}:Url");
            OnReload();
        }
    }
}
