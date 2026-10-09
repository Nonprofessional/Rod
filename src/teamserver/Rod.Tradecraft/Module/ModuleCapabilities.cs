using Rod.Tradecraft.Capabilities;

namespace Rod.Tradecraft.Module;

/// <summary>
/// The module family (architecture.md Sec 5.4): the implant-side plugin
/// seam's own verbs -- the machinery that widens a deployed artifact's
/// runtime verb set with C-ABI capability modules delivered over the
/// staged task arm. The verbs a module brings arrive under the standard
/// categories (recon, lateral, persist, collect, exfil) or a paired
/// out-of-tree descriptor; this family holds the loading machinery
/// itself.
/// </summary>
/// <remarks>
/// Like every registered verb these are descriptors only: the server gates
/// and forwards, and execution runs on the implant's plugin table. All
/// three are gated to the Implant class at task issuance (module support
/// is the long-haul class's, Sec 5.2), and <see cref="ModuleLoad"/>
/// carries the <c>executes-code</c> attribute: loading a module is running
/// new code on the target, so the automation engine never fires it
/// unattended (Sec 10.4).
/// </remarks>
public static class ModuleCapabilities
{
    /// <summary>
    /// Load one module: the bytes ride the task's staged content, their
    /// sha256 bound into the signed tasking tuple, and the implant stages
    /// them the way the launcher one-liners stage a payload -- a memfd and
    /// <c>dlopen</c> on Linux, a manual PE map on Windows.
    /// </summary>
    public const string ModuleLoad = "module.load";

    /// <summary>
    /// Retract one module, best-effort: the verb routes drop immediately,
    /// and the library dies when the dispatch holding it ends.
    /// </summary>
    public const string ModuleUnload = "module.unload";

    /// <summary>Report the loaded modules and the verbs they registered.</summary>
    public const string ModuleList = "module.list";

    // Loading a module is executing new code on the target: the attribute
    // both badges the picker and keeps automation from firing it unattended
    // (architecture.md Sec 7, Sec 10.4).
    private static readonly IReadOnlyDictionary<string, string> ExecutesCode =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["executes-code"] = "true",
        };

    /// <summary>
    /// Descriptors for the module family, in declared order. The composition
    /// root registers these so the registry lists the family.
    /// </summary>
    public static readonly CapabilityDescriptor[] All =
    {
        CapabilityDescriptor.Of(ModuleLoad, CapabilityCategory.Module, "1.0", ExecutesCode),
        CapabilityDescriptor.Of(ModuleUnload, CapabilityCategory.Module, "1.0"),
        CapabilityDescriptor.Of(ModuleList, CapabilityCategory.Module, "1.0"),
    };

    /// <summary>Every module-family verb string, in declared order.</summary>
    public static readonly string[] Verbs =
    {
        ModuleLoad, ModuleUnload, ModuleList,
    };
}
