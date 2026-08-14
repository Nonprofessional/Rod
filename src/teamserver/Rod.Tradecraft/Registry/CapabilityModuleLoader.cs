using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Configuration;
using Rod.Tradecraft.Modules;

namespace Rod.Tradecraft.Registry;

/// <summary>
/// Loads out-of-tree capability modules from the <c>Tradecraft:Modules</c>
/// configuration list (architecture.md Sec 10.2). Each entry is a
/// <c>Namespace.Type, AssemblyName</c> string naming a type that implements
/// <see cref="ICapabilityModule"/>: the assembly is resolved by name -- already
/// loaded, or present as <c>AssemblyName.dll</c> in the application directory --
/// and the type is instantiated and registered against the registry. Because
/// registration replaces the placeholder for the module's verb (last registration
/// wins), adding an out-of-tree module is a deploy-time config entry, never a
/// composition-root edit -- the acceptance point.
/// </summary>
/// <remarks>
/// <para>
/// The loader is deliberately bounded: it reads an explicit, operator-supplied
/// list of named types and resolves assemblies only by that list, never by
/// scanning arbitrary directories for anything implementing the contract. A
/// module therefore reaches the process exactly when an operator built it,
/// placed it next to the teamserver binary, and named it in config -- the
/// runtime attack surface stays bounded by explicit deploy-time inputs, matching
/// the "server gates and forwards, never executes" posture (architecture.md
/// Sec 10.2/10.3).
/// </para>
/// <para>
/// Failures are loud: a missing assembly, an unknown or non-module type, or a
/// constructor that throws aborts startup. A silently skipped module would leave
/// its verb served by the placeholder -- the exact "registered but not what the
/// operator deployed" state a red team cannot afford.
/// </para>
/// </remarks>
public static class CapabilityModuleLoader
{
    /// <summary>The configuration key the module list lives under.</summary>
    public const string ModulesSectionKey = "Tradecraft:Modules";

    /// <summary>
    /// Loads and registers every module listed under <c>Tradecraft:Modules</c> in
    /// <paramref name="configuration"/>. A missing section is an empty list -- the
    /// built-in placeholders stay in place, matching the pre-loading behavior of
    /// every host that does not configure modules.
    /// </summary>
    public static async Task LoadAsync(
        IConfiguration configuration,
        ICapabilityRegistry registry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(registry);

        var entries = configuration.GetSection(ModulesSectionKey).Get<string[]?>() ?? Array.Empty<string>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
                throw new InvalidOperationException(
                    $"An entry in '{ModulesSectionKey}' is empty; each entry must be a 'Namespace.Type, AssemblyName' string.");

            await LoadEntryAsync(entry, registry, cancellationToken);
        }
    }

    /// <summary>
    /// Loads one <c>Namespace.Type, AssemblyName</c> entry: resolves the assembly,
    /// instantiates the type, and registers the resulting module. Public so tests
    /// and tooling can exercise a single entry without building a whole
    /// configuration; the composition root calls <see cref="LoadAsync"/>.
    /// </summary>
    public static async Task LoadEntryAsync(
        string entry,
        ICapabilityRegistry registry,
        CancellationToken cancellationToken = default)
    {
        // Type names cannot contain commas; assembly names (and any version
        // suffixes) can, so the split is on the last comma.
        var comma = entry.LastIndexOf(',');
        if (comma <= 0 || comma == entry.Length - 1)
        {
            throw new InvalidOperationException(
                $"Module entry '{entry}' is not a 'Namespace.Type, AssemblyName' string.");
        }

        var typeName = entry[..comma].Trim();
        var assemblyName = entry[(comma + 1)..].Trim();
        var assembly = ResolveAssembly(assemblyName);
        var type = assembly.GetType(typeName, throwOnError: false)
            ?? throw new InvalidOperationException(
                $"Module entry '{entry}' names a type that does not exist in assembly '{assembly.GetName().Name}'.");
        if (!typeof(ICapabilityModule).IsAssignableFrom(type))
        {
            throw new InvalidOperationException(
                $"Module entry '{entry}' names '{type.FullName}', which does not implement {nameof(ICapabilityModule)}.");
        }

        var module = Activator.CreateInstance(type) as ICapabilityModule
            ?? throw new InvalidOperationException(
                $"Module entry '{entry}' names a type that could not be instantiated as an {nameof(ICapabilityModule)}.");

        await registry.RegisterAsync(module, cancellationToken);
    }

    // Resolves a module assembly by simple name: the default load context first
    // (a referenced or already-loaded assembly), then a same-named dll in the
    // application directory (an out-of-tree assembly placed next to the binary).
    // Any other location is refused -- the loader walks no directories.
    private static Assembly ResolveAssembly(string assemblyName)
    {
        try
        {
            return Assembly.Load(new AssemblyName(assemblyName));
        }
        catch (FileNotFoundException)
        {
            var candidate = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
            if (File.Exists(candidate))
                return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);

            throw new InvalidOperationException(
                $"Module assembly '{assemblyName}' was not found: place {assemblyName}.dll next to the " +
                "teamserver binary or reference the assembly from the composition root project.");
        }
    }
}
