using Rod.BuildPipeline.PayloadBuild;

namespace Rod.Build.Tests;

/// <summary>
/// Unit tests for the bake-time transport trim (architecture.md Sec 8): the
/// egress walk's URL shapes decide which contact modules a build compiles,
/// so an artifact carries exactly the transports it can dial. The selection
/// rule is pinned against every walk shape (web, stream, and the
/// shape-crossing mixed walk fallbacks can build), and the staging rewrite
/// is pinned file by file -- the unselected modules' sources leave the
/// compilation whole and the generated selection names only the compiled
/// factories.
/// </summary>
public class TransportModuleSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rod-transport-trim-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Select_AWebFrontWithoutABeaconSplit_CompilesTheWebCycleOnly()
    {
        // The mainstream single-port shape on the poll cadence: an http(s)
        // enroll front with no named beacon endpoint derives a schemed beacon
        // URL, so the walk is web-only and neither stream module (the gRPC
        // client, the WebSocket client) ever compiles.
        var profile = new TransportProfile("https://c2.example.test/implants/enroll", "/beacon");

        var modules = TransportModuleSelection.Select(profile, ContactModes.Poll);

        Assert.Equal(ContactModules.Web, modules);
        Assert.False(TransportModuleSelection.NeedsGrpcClient(modules));
    }

    [Fact]
    public void Select_AWebFrontOnStreamMode_CompilesTheWebSocketStreamOnly()
    {
        // The web posture's interactive tier: the same schemed walk, with the
        // baked mode holding the connection open -- the WebSocket client
        // compiles and neither the POST cycle nor the gRPC client does.
        var profile = new TransportProfile("https://c2.example.test/implants/enroll", "/beacon");

        var modules = TransportModuleSelection.Select(profile, ContactModes.Stream);

        Assert.Equal(ContactModules.WebSocket, modules);
        Assert.False(TransportModuleSelection.NeedsGrpcClient(modules));
    }

    [Fact]
    public void Select_AnMtlsBeaconAuthority_CompilesTheStreamOnly()
    {
        // The named-beacon shape: the parser bakes the mTLS socket as a bare
        // host:port, so the walk is stream-only and the mode never picks a
        // web client.
        var profile = new TransportProfile("https://c2.example.test/implants/enroll", "/beacon")
        {
            BeaconEndpoint = "c2.example.test:8443",
        };

        var modules = TransportModuleSelection.Select(profile, ContactModes.Stream);

        Assert.Equal(ContactModules.Stream, modules);
        Assert.True(TransportModuleSelection.NeedsGrpcClient(modules));
    }

    [Fact]
    public void Select_AQuicBeacon_CompilesTheQuicStreamOnly()
    {
        // The QUIC beacon's dial shape: the parser bakes the quic-schemed
        // endpoint, so the walk compiles the QUIC stream client and nothing
        // else -- no gRPC client rides a quic-only artifact.
        var profile = new TransportProfile("https://c2.example.test/implants/enroll", "/beacon")
        {
            BeaconEndpoint = "quic://c2.example.test:443",
        };

        var modules = TransportModuleSelection.Select(profile, ContactModes.Stream);

        Assert.Equal(ContactModules.Quic, modules);
        Assert.False(TransportModuleSelection.NeedsGrpcClient(modules));
    }

    [Fact]
    public void Select_AQuicEnrollFront_CompilesTheQuicModule()
    {
        // Enrollment over QUIC (architecture.md Sec 8): a quic-schemed enroll
        // endpoint runs the frame exchange, which rides the QUIC module's
        // dial -- so the module compiles even when the beacon names another
        // front's socket (the split shape) or derives from the same entry.
        var derived = new TransportProfile("quic://c2.example.test:443", "/beacon");
        Assert.Equal(
            ContactModules.Quic,
            TransportModuleSelection.Select(derived, ContactModes.Stream));

        var split = new TransportProfile("quic://c2.example.test:443", "/beacon")
        {
            BeaconEndpoint = "c2.example.test:8443",
        };
        Assert.Equal(
            ContactModules.Quic | ContactModules.Stream,
            TransportModuleSelection.Select(split, ContactModes.Stream));

        // The enroll shape claims its module for fallbacks too: a web primary
        // with a quic fallback keeps both clients.
        var withQuicFallback = new TransportProfile("https://c2.example.test/implants/enroll", "/beacon")
        {
            FallbackEndpoints = new[] { "quic://backup.example.test:443" },
        };
        Assert.Equal(
            ContactModules.Web | ContactModules.Quic,
            TransportModuleSelection.Select(withQuicFallback, ContactModes.Poll));
    }

    [Fact]
    public void Select_ADnsBeacon_CompilesTheDnsClientOnly()
    {
        // The DNS pairing shape: the enroll front stays web while the named
        // beacon is the dns-schemed dial, so the walk compiles the TXT
        // carrier's client and nothing else, whatever the mode.
        var profile = new TransportProfile("https://c2.example.test/implants/enroll", "/beacon")
        {
            BeaconEndpoint = "dns://10.9.8.7:53/c2.example.test",
        };

        var modules = TransportModuleSelection.Select(profile, ContactModes.Stream);

        Assert.Equal(ContactModules.Dns, modules);
        Assert.False(TransportModuleSelection.NeedsGrpcClient(modules));
    }

    [Fact]
    public void Select_ADohBeacon_CompilesTheDnsClientOnly()
    {
        // The DoH carriage: the same grammar over HTTPS, so the dns module
        // serves a doh-schemed dial exactly like a dns-schemed one.
        var profile = new TransportProfile("https://c2.example.test/implants/enroll", "/beacon")
        {
            BeaconEndpoint = "doh://10.9.8.7:443/c2.example.test",
        };

        var modules = TransportModuleSelection.Select(profile, ContactModes.Poll);

        Assert.Equal(ContactModules.Dns, modules);
    }

    [Fact]
    public void Select_ADnsBeaconWithWebFallbacks_KeepsTheFallbackClient()
    {
        // A dns primary with web fallbacks crosses shapes when the resolver
        // burns: the DNS client and the fallback's client must both compile
        // or the walk strands the artifact on the fallback's front.
        var profile = new TransportProfile("https://c2.example.test/implants/enroll", "/beacon")
        {
            BeaconEndpoint = "dns://10.9.8.7:53/c2.example.test",
            FallbackEndpoints = new[] { "https://backup.example.test/implants/enroll" },
        };

        var modules = TransportModuleSelection.Select(profile, ContactModes.Poll);

        Assert.Equal(ContactModules.Dns | ContactModules.Web, modules);
        Assert.False(TransportModuleSelection.NeedsGrpcClient(modules));
    }

    [Fact]
    public void Select_AQuicPrimaryWithWebFallbacks_KeepsTheFallbackClient()
    {
        // A quic primary with web fallbacks crosses shapes when the primary
        // burns: the QUIC client and the fallback's client must both compile
        // or the walk strands the artifact on the fallback's front.
        var profile = new TransportProfile("https://c2.example.test/implants/enroll", "/beacon")
        {
            BeaconEndpoint = "quic://c2.example.test:443",
            FallbackEndpoints = new[] { "https://backup.example.test/implants/enroll" },
        };

        var modules = TransportModuleSelection.Select(profile, ContactModes.Poll);

        Assert.Equal(ContactModules.Quic | ContactModules.Web, modules);
        Assert.False(TransportModuleSelection.NeedsGrpcClient(modules));
    }

    [Fact]
    public void Select_AMixedWalk_KeepsEveryClientItCanDial()
    {
        // A stream primary with a web fallback crosses shapes mid-run when
        // the primary burns: the gRPC client and the fallback's client must
        // both compile or the walk strands the artifact on the fallback's
        // front -- on a poll bake that fallback client is the POST cycle.
        var profile = new TransportProfile("https://c2.example.test/implants/enroll", "/beacon")
        {
            BeaconEndpoint = "c2.example.test:8443",
            FallbackEndpoints = new[] { "https://backup.example.test/implants/enroll" },
        };

        var modules = TransportModuleSelection.Select(profile, ContactModes.Poll);

        Assert.Equal(ContactModules.Web | ContactModules.Stream, modules);
        Assert.True(TransportModuleSelection.NeedsGrpcClient(modules));
    }

    [Fact]
    public void Select_AMixedWalkOnStreamMode_KeepsTheWebSocketFallback()
    {
        // The same crossing walk on a stream bake: the fallback's schemed URL
        // dials the WebSocket client when the walk reaches it.
        var profile = new TransportProfile("https://c2.example.test/implants/enroll", "/beacon")
        {
            BeaconEndpoint = "c2.example.test:8443",
            FallbackEndpoints = new[] { "https://backup.example.test/implants/enroll" },
        };

        var modules = TransportModuleSelection.Select(profile, ContactModes.Stream);

        Assert.Equal(ContactModules.WebSocket | ContactModules.Stream, modules);
        Assert.True(TransportModuleSelection.NeedsGrpcClient(modules));
    }

    [Fact]
    public void Select_WebFallbacksBehindAWebPrimary_StaysWebOnly()
    {
        // Fallback entries derive schemed beacon URLs like their primary, so
        // a web walk stays web no matter how many fronts it names -- and the
        // gRPC client still never compiles.
        var profile = new TransportProfile("http://c2.example.test/implants/enroll", "/beacon")
        {
            FallbackEndpoints = new[]
            {
                "https://backup.example.test/implants/enroll",
                "http://127.0.0.1:5080/implants/enroll",
            },
        };

        var modules = TransportModuleSelection.Select(profile, ContactModes.Poll);

        Assert.Equal(ContactModules.Web, modules);
    }

    [Fact]
    public void Select_AMisshapenSchemedBeaconEndpoint_IsTheWebCycle()
    {
        // The classifier follows the URL's own shape, not why it was named:
        // a schemed beacon endpoint runs a web client at run time, so the
        // trim must keep the mode's web module for it (hand-built params can
        // carry one even though the parser bares every named beacon).
        var profile = new TransportProfile("https://c2.example.test/implants/enroll", "/beacon")
        {
            BeaconEndpoint = "https://front.example.test",
        };

        var modules = TransportModuleSelection.Select(profile, ContactModes.Poll);

        Assert.Equal(ContactModules.Web, modules);
    }

    [Fact]
    public void Registry_EveryModulesFiles_ExistInTheImplantTree()
    {
        // A descriptor naming a file the tree does not carry would delete
        // nothing, compile nothing, and fail the build far from the cause;
        // pin the contract where the registry and the tree meet.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "implant", "dotnet")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var tree = Path.Combine(dir!.FullName, "src", "implant", "dotnet");
        foreach (var descriptor in ContactModuleRegistry.All)
            foreach (var file in descriptor.Files)
                Assert.True(File.Exists(Path.Combine(tree, file)), $"the {descriptor.Module} descriptor names a file the implant tree does not carry: {file}");
    }

    [Fact]
    public void Apply_WebOnly_RemovesTheStreamModuleWhole_AndWritesTheSelection()
    {
        var staging = StageModuleFiles();

        TransportModuleSelection.Apply(staging, ContactModules.Web);

        // Whole source files out: the stream client and its factory leave the
        // compilation, and with them every Grpc reference the tree carried.
        Assert.False(File.Exists(Path.Combine(staging, "Internal", "Beacon.cs")));
        Assert.False(File.Exists(Path.Combine(staging, "Internal", "StreamContact.cs")));
        Assert.True(File.Exists(Path.Combine(staging, "Internal", "EnvelopeBeacon.cs")));
        Assert.True(File.Exists(Path.Combine(staging, "Internal", "WebContact.cs")));
        var selection = File.ReadAllText(Path.Combine(staging, "Internal", "TransportSelection.cs"));
        Assert.Contains("WebContact.Create(setup)", selection);
        Assert.DoesNotContain("StreamContact.Create(setup)", selection);
    }

    [Fact]
    public void Apply_StreamOnly_RemovesTheWebModuleWhole_AndWritesTheSelection()
    {
        var staging = StageModuleFiles();

        TransportModuleSelection.Apply(staging, ContactModules.Stream);

        Assert.False(File.Exists(Path.Combine(staging, "Internal", "EnvelopeBeacon.cs")));
        Assert.False(File.Exists(Path.Combine(staging, "Internal", "WebContact.cs")));
        Assert.True(File.Exists(Path.Combine(staging, "Internal", "Beacon.cs")));
        Assert.True(File.Exists(Path.Combine(staging, "Internal", "StreamContact.cs")));
        var selection = File.ReadAllText(Path.Combine(staging, "Internal", "TransportSelection.cs"));
        Assert.Contains("StreamContact.Create(setup)", selection);
        Assert.DoesNotContain("WebContact.Create(setup)", selection);
    }

    [Fact]
    public void Apply_AShapeCrossingWalk_KeepsEveryModuleFile()
    {
        var staging = StageModuleFiles();

        TransportModuleSelection.Apply(staging, ContactModules.Web | ContactModules.Stream);

        foreach (var file in new[] { "Beacon.cs", "StreamContact.cs", "EnvelopeBeacon.cs", "WebContact.cs" })
            Assert.True(File.Exists(Path.Combine(staging, "Internal", file)), file + " must survive a both-module bake");
        var selection = File.ReadAllText(Path.Combine(staging, "Internal", "TransportSelection.cs"));
        Assert.Contains("WebContact.Create(setup)", selection);
        Assert.Contains("StreamContact.Create(setup)", selection);
    }

    [Fact]
    public void Apply_QuicOnly_RemovesEveryOtherModuleWhole_AndWritesTheSelection()
    {
        var staging = StageModuleFiles();

        TransportModuleSelection.Apply(staging, ContactModules.Quic);

        // Whole source files out: only the QUIC client survives, and with no
        // stream-shaped entry the gRPC client leaves too.
        Assert.True(File.Exists(Path.Combine(staging, "Internal", "QuicContact.cs")));
        Assert.False(File.Exists(Path.Combine(staging, "Internal", "Beacon.cs")));
        Assert.False(File.Exists(Path.Combine(staging, "Internal", "WsBeacon.cs")));
        Assert.False(File.Exists(Path.Combine(staging, "Internal", "WebContact.cs")));
        var selection = File.ReadAllText(Path.Combine(staging, "Internal", "TransportSelection.cs"));
        Assert.Contains("QuicContact.Create(setup)", selection);
        Assert.DoesNotContain("StreamContact.Create(setup)", selection);
        Assert.DoesNotContain("WebContact.Create(setup)", selection);
    }

    [Fact]
    public void Apply_AnEmptySelectionIsRefused()
    {
        // A primary walk entry always has a shape, so an empty set is a
        // caller bug, not a profile -- refuse it rather than bake an
        // artifact that phones nowhere.
        var staging = StageModuleFiles();

        Assert.Throws<InvalidOperationException>(
            () => TransportModuleSelection.Apply(staging, ContactModules.None));
    }

    // Stages a minimal implant tree: the module files plus the
    // TransportSelection stub, the files Apply is contractually allowed to
    // touch.
    private string StageModuleFiles()
    {
        var staging = Path.Combine(_root, "staging", "dotnet");
        Directory.CreateDirectory(Path.Combine(staging, "Internal"));
        foreach (var file in new[]
                 {
                     "Beacon.cs", "StreamContact.cs", "EnvelopeBeacon.cs", "WebContact.cs",
                     "WsBeacon.cs", "QuicContact.cs", "TransportSelection.cs",
                 })
            File.WriteAllText(Path.Combine(staging, "Internal", file), "// fixture");
        return staging;
    }
}
