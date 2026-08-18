using System.Reflection;
using System.Runtime.Loader;

namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// Loads out-of-tree payload transforms from the <c>Build:Transforms</c>
/// configuration list (architecture.md Sec 6, the transform seam). Each entry
/// is a <c>Namespace.Type, AssemblyName</c> string naming a type that
/// implements <see cref="IPayloadTransform"/>: the assembly is resolved by
/// name -- already loaded, or present as <c>AssemblyName.dll</c> in the
/// application directory -- and the type is instantiated. The listed order is
/// the application order, so an operator composes the chain by composing the
/// list. Adding a transform is a deploy-time config entry, never a
/// composition-root edit -- the same explicit-list shape as
/// <c>Tradecraft:Modules</c>.
/// </summary>
/// <remarks>
/// <para>
/// The loader is deliberately bounded: it reads an explicit, operator-supplied
/// list of named types and resolves assemblies only by that list, never by
/// scanning directories for implementers. A transform reaches the process
/// exactly when an operator built it, placed it next to the teamserver
/// binary, and named it in config.
/// </para>
/// <para>
/// Failures are loud: a missing assembly, an unknown or non-transform type, or
/// a constructor that throws aborts startup. A silently skipped transform
/// would leave an operator believing wrapped bytes are stored when the raw
/// build output is -- the exact confusion a build trail must never permit.
/// </para>
/// </remarks>
public static class PayloadTransformLoader
{
    /// <summary>The configuration key the transform list lives under.</summary>
    public const string TransformsSectionKey = "Build:Transforms";

    /// <summary>
    /// Loads every transform named in <paramref name="entries"/>, in order. An
    /// empty list yields the empty chain (the seam, the default for every
    /// host that does not configure transforms).
    /// </summary>
    public static IReadOnlyList<IPayloadTransform> Load(IReadOnlyList<string?> entries)
    {
        var transforms = new List<IPayloadTransform>(entries.Count);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
                throw new InvalidOperationException(
                    $"An entry in '{TransformsSectionKey}' is empty; each entry must be a 'Namespace.Type, AssemblyName' string.");
            transforms.Add(LoadEntry(entry!));
        }
        return transforms;
    }

    /// <summary>
    /// Loads one <c>Namespace.Type, AssemblyName</c> entry: resolves the
    /// assembly, instantiates the type, and returns the transform. Public so
    /// tests and tooling can exercise a single entry.
    /// </summary>
    public static IPayloadTransform LoadEntry(string entry)
    {
        // Type names cannot contain commas; assembly names (and any version
        // suffixes) can, so the split is on the last comma.
        var comma = entry.LastIndexOf(',');
        if (comma <= 0 || comma == entry.Length - 1)
            throw new InvalidOperationException(
                $"Transform entry '{entry}' is not a 'Namespace.Type, AssemblyName' string.");

        var typeName = entry[..comma].Trim();
        var assemblyName = entry[(comma + 1)..].Trim();
        var assembly = ResolveAssembly(assemblyName);
        var type = assembly.GetType(typeName, throwOnError: false)
            ?? throw new InvalidOperationException(
                $"Transform entry '{entry}' names a type that does not exist in assembly '{assembly.GetName().Name}'.");
        if (!typeof(IPayloadTransform).IsAssignableFrom(type))
            throw new InvalidOperationException(
                $"Transform entry '{entry}' names '{type.FullName}', which does not implement {nameof(IPayloadTransform)}.");

        return Activator.CreateInstance(type) as IPayloadTransform
            ?? throw new InvalidOperationException(
                $"Transform entry '{entry}' names a type that could not be instantiated as an {nameof(IPayloadTransform)}.");
    }

    // Resolves a transform assembly by simple name: the default load context
    // first (a referenced or already-loaded assembly), then a same-named dll
    // in the application directory (an out-of-tree assembly placed next to the
    // binary). Any other location is refused -- the loader walks no directories.
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
                $"Transform assembly '{assemblyName}' was not found: place {assemblyName}.dll next to the " +
                "teamserver binary or reference the assembly from the composition root project.");
        }
    }
}
