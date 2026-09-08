using Rod.BuildPipeline.PayloadBuild;

namespace Rod.Build.Tests;

/// <summary>
/// Unit tests for the bake-time transport trim (architecture.md Sec 8): the
/// egress walk's URL shapes decide which check-in modules a build compiles,
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
        // The mainstream single-port shape: an http(s) enroll front with no
        // named beacon endpoint derives a schemed beacon URL, so the walk is
        // web-only and the stream module (the gRPC client) never compiles.
        var profile = new TransportProfile("https://c2.example.test/implants/enroll", "/beacon");

        var modules = TransportModuleSelection.Select(profile);

        Assert.Equal(CheckInModules.Web, modules);
        Assert.False(TransportModuleSelection.NeedsGrpcClient(modules));
    }

    [Fact]
    public void Select_AnMtlsBeaconAuthority_CompilesTheStreamOnly()
    {
        // The named-beacon shape: the parser bakes the mTLS socket as a bare
        // host:port, so the walk is stream-only and the web cycle never
        // compiles.
        var profile = new TransportProfile("https://c2.example.test/implants/enroll", "/beacon")
        {
            BeaconEndpoint = "c2.example.test:8443",
        };

        var modules = TransportModuleSelection.Select(profile);

        Assert.Equal(CheckInModules.Stream, modules);
        Assert.True(TransportModuleSelection.NeedsGrpcClient(modules));
    }

    [Fact]
    public void Select_AMixedWalk_KeepsBothClients()
    {
        // A stream primary with a web fallback crosses shapes mid-run when
        // the primary burns: both clients must compile or the walk strands
        // the artifact on the fallback's front.
        var profile = new TransportProfile("https://c2.example.test/implants/enroll", "/beacon")
        {
            BeaconEndpoint = "c2.example.test:8443",
            FallbackEndpoints = new[] { "https://backup.example.test/implants/enroll" },
        };

        var modules = TransportModuleSelection.Select(profile);

        Assert.Equal(CheckInModules.Web | CheckInModules.Stream, modules);
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

        var modules = TransportModuleSelection.Select(profile);

        Assert.Equal(CheckInModules.Web, modules);
    }

    [Fact]
    public void Select_AMisshapenSchemedBeaconEndpoint_IsTheWebCycle()
    {
        // The classifier follows the URL's own shape, not why it was named:
        // a schemed beacon endpoint runs the envelope cycle at run time, so
        // the trim must keep the web module for it (hand-built params can
        // carry one even though the parser bares every named beacon).
        var profile = new TransportProfile("https://c2.example.test/implants/enroll", "/beacon")
        {
            BeaconEndpoint = "https://front.example.test",
        };

        var modules = TransportModuleSelection.Select(profile);

        Assert.Equal(CheckInModules.Web, modules);
    }

    [Fact]
    public void Apply_WebOnly_RemovesTheStreamModuleWhole_AndWritesTheSelection()
    {
        var staging = StageModuleFiles();

        TransportModuleSelection.Apply(staging, CheckInModules.Web);

        // Whole source files out: the stream client and its factory leave the
        // compilation, and with them every Grpc reference the tree carried.
        Assert.False(File.Exists(Path.Combine(staging, "Internal", "Beacon.cs")));
        Assert.False(File.Exists(Path.Combine(staging, "Internal", "StreamCheckIn.cs")));
        Assert.True(File.Exists(Path.Combine(staging, "Internal", "EnvelopeBeacon.cs")));
        Assert.True(File.Exists(Path.Combine(staging, "Internal", "WebCheckIn.cs")));
        var selection = File.ReadAllText(Path.Combine(staging, "Internal", "TransportSelection.cs"));
        Assert.Contains("WebCheckIn.Create(setup)", selection);
        Assert.DoesNotContain("StreamCheckIn.Create(setup)", selection);
    }

    [Fact]
    public void Apply_StreamOnly_RemovesTheWebModuleWhole_AndWritesTheSelection()
    {
        var staging = StageModuleFiles();

        TransportModuleSelection.Apply(staging, CheckInModules.Stream);

        Assert.False(File.Exists(Path.Combine(staging, "Internal", "EnvelopeBeacon.cs")));
        Assert.False(File.Exists(Path.Combine(staging, "Internal", "WebCheckIn.cs")));
        Assert.True(File.Exists(Path.Combine(staging, "Internal", "Beacon.cs")));
        Assert.True(File.Exists(Path.Combine(staging, "Internal", "StreamCheckIn.cs")));
        var selection = File.ReadAllText(Path.Combine(staging, "Internal", "TransportSelection.cs"));
        Assert.Contains("StreamCheckIn.Create(setup)", selection);
        Assert.DoesNotContain("WebCheckIn.Create(setup)", selection);
    }

    [Fact]
    public void Apply_AShapeCrossingWalk_KeepsEveryModuleFile()
    {
        var staging = StageModuleFiles();

        TransportModuleSelection.Apply(staging, CheckInModules.Web | CheckInModules.Stream);

        foreach (var file in new[] { "Beacon.cs", "StreamCheckIn.cs", "EnvelopeBeacon.cs", "WebCheckIn.cs" })
            Assert.True(File.Exists(Path.Combine(staging, "Internal", file)), file + " must survive a both-module bake");
        var selection = File.ReadAllText(Path.Combine(staging, "Internal", "TransportSelection.cs"));
        Assert.Contains("WebCheckIn.Create(setup)", selection);
        Assert.Contains("StreamCheckIn.Create(setup)", selection);
    }

    [Fact]
    public void Apply_AnEmptySelectionIsRefused()
    {
        // A primary walk entry always has a shape, so an empty set is a
        // caller bug, not a profile -- refuse it rather than bake an
        // artifact that phones nowhere.
        var staging = StageModuleFiles();

        Assert.Throws<InvalidOperationException>(
            () => TransportModuleSelection.Apply(staging, CheckInModules.None));
    }

    // Stages a minimal implant tree: the four module files plus the
    // TransportSelection stub, the files Apply is contractually allowed to
    // touch.
    private string StageModuleFiles()
    {
        var staging = Path.Combine(_root, "staging", "dotnet");
        Directory.CreateDirectory(Path.Combine(staging, "Internal"));
        foreach (var file in new[] { "Beacon.cs", "StreamCheckIn.cs", "EnvelopeBeacon.cs", "WebCheckIn.cs", "TransportSelection.cs" })
            File.WriteAllText(Path.Combine(staging, "Internal", file), "// fixture");
        return staging;
    }
}
