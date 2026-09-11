using Rod.Transport.Listeners;
using Rod.Transport.Listeners.Providers;

namespace Rod.Integration.Tests;

/// <summary>
/// Checks of the transport provider registry: the in-tree six register under
/// their wire names with the postures their transports bind (the HTTP family
/// as one Kestrel shape under three TLS postures), and a provider arriving
/// later registers the same way -- with a conflicting re-registration refused
/// rather than silently replacing a live transport's bind behavior.
/// </summary>
public class TransportProvidersTests
{
    [Theory]
    [InlineData("http", "http", null)]
    [InlineData("https", "https", null)]
    [InlineData("mtls", "https", "AllowCertificate")]
    public void Find_ServesTheHttpFamilyUnderItsTlsPosture(string transport, string scheme, string? certMode)
    {
        var provider = TransportProviders.Find(transport);

        var kestrel = Assert.IsType<KestrelEndpointProvider>(provider);
        Assert.Equal(transport, kestrel.Transport);
        Assert.Equal(scheme, kestrel.Posture.Scheme);
        Assert.Equal(certMode, kestrel.Posture.ClientCertificateMode);
    }

    [Theory]
    [InlineData("dns")]
    [InlineData("smb")]
    [InlineData("tcp")]
    public void Find_ServesTheSocketOwningFamily(string transport)
    {
        var provider = TransportProviders.Find(transport);

        var hosted = Assert.IsType<HostedServiceTransportProvider>(provider);
        Assert.Equal(transport, hosted.Transport);
    }

    [Fact]
    public void Find_ReturnsNullForAnUnregisteredName()
    {
        Assert.Null(TransportProviders.Find("carrier-not-registered"));
    }

    [Theory]
    [InlineData("http", false)]
    [InlineData("https", false)]
    [InlineData("mtls", true)]
    [InlineData("dns", false)]
    [InlineData("smb", false)]
    [InlineData("tcp", false)]
    public void Carriers_DeclareTheNativeChannelTruthPerTransport(string transport, bool servesNative)
    {
        // The build parser's beacon rule reads this: only the transport whose
        // carriers include the beacon stream may be named as a build's beacon.
        var provider = TransportProviders.Find(transport);

        Assert.NotNull(provider);
        Assert.Equal(servesNative, provider!.ServesNativeChannel);
        if (servesNative)
            Assert.Contains(provider.Carriers, c => c == "beacon-stream");
    }

    [Fact]
    public void Register_DeclaresATransportTheCoreDoesNotKnow()
    {
        var provider = new StubTransportProvider("stub-test-transport");

        TransportProviders.Register(provider);

        Assert.Same(provider, TransportProviders.Find("stub-test-transport"));
    }

    [Fact]
    public void Register_RejectsAConflictingProviderUnderALiveName()
    {
        TransportProviders.Register(new StubTransportProvider("conflicting-test-transport"));

        Assert.Throws<InvalidOperationException>(
            () => TransportProviders.Register(new StubTransportProvider("CONFLICTING-TEST-TRANSPORT")));
    }

    [Fact]
    public void Register_TreatsTheSameInstanceAgainAsANoOp()
    {
        var provider = new StubTransportProvider("idempotent-test-transport");
        TransportProviders.Register(provider);

        TransportProviders.Register(provider);

        Assert.Same(provider, TransportProviders.Find("idempotent-test-transport"));
    }

    private sealed class StubTransportProvider(string transport) : ITransportProvider
    {
        public string Transport { get; } = transport;

        public IReadOnlyList<string> Carriers => Array.Empty<string>();

        public bool ServesNativeChannel => false;

        public string PublicEndpointScheme => "https";

        public bool AcceptsPublicEndpoint(string text) => true;

        public string DescribePublicEndpointRule(string got) => $"No rule; a registry stub never binds ({got}).";

        public void Validate(ListenerConfig config)
        {
        }

        public void ReserveBind(ListenerConfig config)
        {
        }

        public Task<BoundListener> BindAsync(TransportBindContext context, CancellationToken cancellationToken)
            => throw new NotSupportedException("A registry stub never binds.");
    }
}
