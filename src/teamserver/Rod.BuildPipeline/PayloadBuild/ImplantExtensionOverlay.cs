using System.Text;
using System.Text.RegularExpressions;

namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// One handler class discovered in the configured extension directory: the
/// namespace it declares and the class name. The generated registrations
/// reference the pair fully qualified.
/// </summary>
public sealed record ExtensionHandlerType(string Namespace, string Name)
{
    /// <summary>
    /// The C# reference the generated registrations instantiate. A class in
    /// the global namespace needs the explicit <c>global::</c> prefix -- the
    /// generated file itself sits inside a namespace, where a bare name would
    /// resolve against the wrong scope.
    /// </summary>
    public string FullName => Namespace.Length == 0 ? "global::" + Name : Namespace + "." + Name;
}

/// <summary>
/// The implant half of the tradecraft extension kit: overlays a configured
/// out-of-tree extension directory onto the per-build staging tree
/// (architecture.md Sec 5.3, Sec 6; extending/tradecraft.md). The directory's
/// .cs sources copy into the staging copy's Extensions/ folder and a generated
/// registrations file replaces the checked-in ExtensionRegistrations stub, so
/// dropping a handler source into the directory and building yields an
/// artifact that runs it -- no fork of the implant tree to maintain. Discovery
/// is a source scan: every top-level class whose base list names
/// <c>ICapabilityHandler</c> becomes one registration feeding
/// <c>HandlerRegistry.Default</c>'s <c>additional</c> seam.
/// </summary>
/// <remarks>
/// <para>
/// The scan is deliberately narrow. It is not a compiler: it reads
/// hand-written extension sources that follow the documented authoring shape
/// (a concrete, non-nested class with a parameterless constructor whose base
/// list names <c>ICapabilityHandler</c>). Anything outside that shape is left
/// alone -- it may be helper code -- and the genuinely broken cases fail loud
/// at the next step, because the generated registrations reference the
/// discovered names verbatim and <c>dotnet publish</c> refuses an abstract
/// class, a nested class, or a missing parameterless constructor with the
/// type's name in the diagnostic. An operator never gets a silently
/// unregistered handler for a shape the scanner understands but C# does not.
/// </para>
/// <para>
/// A configured directory that yields no handler at all is itself a loud
/// failure: an operator who pointed the build at an extension directory
/// believes their handlers are compiling in, and "registered but not what the
/// operator deployed" is the state a red team cannot afford -- the same rule
/// the server-side module loader applies to a bad <c>Tradecraft:Modules</c>
/// entry (architecture.md Sec 10.2).
/// </para>
/// </remarks>
public static class ImplantExtensionOverlay
{
    /// <summary>
    /// The file the generated registrations replace, relative to the implant
    /// tree's Extensions/ folder. The checked-in stub compiles empty; the
    /// overlay overwrites the copy in the staging tree.
    /// </summary>
    public const string RegistrationsFileName = "ExtensionRegistrations.cs";

    // A namespace declaration, file-scoped or block-scoped: the name is what
    // the generated registrations qualify the discovered class with. The last
    // declaration before a class match is the one in scope for it, which is
    // the single-namespace layout the authoring shape documents.
    private static readonly Regex NamespacePattern = new(
        @"namespace\s+([A-Za-z_][A-Za-z0-9_.]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A type declaration whose base list names the handler contract: the
    // authoring shape every extension handler follows. The gap between the
    // type name and the ":" stops at "{" or ";" so the base list cannot leak
    // past the declaration, and the token is word-bounded so
    // ICapabilityHandlerFoo does not match.
    private static readonly Regex HandlerClassPattern = new(
        @"(?:class|record|struct)\s+([A-Za-z_][A-Za-z0-9_]*)[^{;]*:\s*[^{;]*\bICapabilityHandler\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Applies the extension directory onto a staged implant tree: copies the
    /// .cs sources into <c>&lt;stagingDir&gt;/Extensions</c>, discovers the handler
    /// classes, and writes the generated registrations over the stub. Throws
    /// <see cref="InvalidOperationException"/> when the directory is missing or
    /// yields no handler -- both loudly, before any compile starts.
    /// </summary>
    public static void Apply(string extensionDir, string stagingDir)
    {
        ArgumentNullException.ThrowIfNull(extensionDir);
        ArgumentNullException.ThrowIfNull(stagingDir);
        if (!Directory.Exists(extensionDir))
            throw new InvalidOperationException(
                $"The configured implant extension directory '{extensionDir}' does not exist.");

        var extensionsDir = Path.Combine(stagingDir, "Extensions");
        CopySources(extensionDir, extensionsDir);

        var handlers = DiscoverHandlers(extensionDir);
        if (handlers.Count == 0)
            throw new InvalidOperationException(
                $"The configured implant extension directory '{extensionDir}' contains no handler: " +
                "each handler is a top-level class whose base list names ICapabilityHandler " +
                "(a concrete class with a parameterless constructor; see extending/tradecraft.md).");

        File.WriteAllText(
            Path.Combine(extensionsDir, RegistrationsFileName),
            RenderRegistrations(handlers));
    }

    /// <summary>
    /// Discovers every handler class in the extension directory, in a stable
    /// order: files sorted by path (ordinal), declarations in file order. The
    /// order is the registration order, and the registry's last-wins rule
    /// makes a duplicate verb deterministic rather than enumeration-order
    /// dependent.
    /// </summary>
    public static IReadOnlyList<ExtensionHandlerType> DiscoverHandlers(string extensionDir)
    {
        var root = Path.GetFullPath(extensionDir);
        var handlers = new List<ExtensionHandlerType>();
        foreach (var file in EnumerateSourceFiles(root))
        {
            var text = File.ReadAllText(file);
            var namespaceMatches = NamespacePattern.Matches(text);
            foreach (Match match in HandlerClassPattern.Matches(text))
            {
                var ns = "";
                for (var i = 0; i < namespaceMatches.Count; i++)
                {
                    if (namespaceMatches[i].Index < match.Index)
                        ns = namespaceMatches[i].Groups[1].Value;
                }
                handlers.Add(new ExtensionHandlerType(ns, match.Groups[1].Value));
            }
        }
        return handlers;
    }

    /// <summary>
    /// Copies the extension directory's .cs sources into the staging tree's
    /// Extensions/ folder, preserving relative subpaths. Only sources copy:
    /// the extension compiles into the implant project (an SDK-style project
    /// globs every .cs under it), so binaries, docs, and build output stay
    /// behind and bin/obj are skipped like the implant tree copy does.
    /// </summary>
    public static void CopySources(string extensionDir, string destinationDir)
    {
        var root = Path.GetFullPath(extensionDir);
        foreach (var file in EnumerateSourceFiles(root))
        {
            var relative = Path.GetRelativePath(root, file);
            var target = Path.Combine(destinationDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>
    /// Renders the generated ExtensionRegistrations source: the same static
    /// shape as the checked-in stub, with one <c>new</c> per discovered
    /// handler. The file replaces the stub in the staging copy, so the beacon
    /// wires the same <c>Handlers</c> list either way.
    /// </summary>
    public static string RenderRegistrations(IReadOnlyList<ExtensionHandlerType> handlers)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated> Generated by the Rod .NET build unit from the configured");
        sb.AppendLine("// extension directory (the tradecraft extension kit, extending/tradecraft.md).");
        sb.AppendLine("// One registration per discovered ICapabilityHandler implementation;");
        sb.AppendLine("// HandlerRegistry.Default appends them after the reference set.");
        sb.AppendLine("namespace Rod.Implant.Internal;");
        sb.AppendLine();
        sb.AppendLine("internal static class ExtensionRegistrations");
        sb.AppendLine("{");
        sb.AppendLine("    public static readonly IReadOnlyList<ICapabilityHandler> Handlers = new ICapabilityHandler[]");
        sb.AppendLine("    {");
        foreach (var handler in handlers)
        {
            sb.Append("        new ").Append(handler.FullName).AppendLine("(),");
        }
        sb.AppendLine("    };");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // Enumerates the extension directory's .cs files recursively, skipping
    // bin/obj, sorted by path so discovery -- and the registrations it
    // generates -- is stable across platforms and file systems.
    private static IEnumerable<string> EnumerateSourceFiles(string extensionDir)
    {
        var root = Path.GetFullPath(extensionDir);
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !UnderBuildOutput(Path.GetFullPath(path), root))
            .OrderBy(path => path, StringComparer.Ordinal);
    }

    // True when the file sits under a bin/ or obj/ folder inside the extension
    // root -- prior build output an operator's directory may carry, which must
    // not compile into the implant a second time.
    private static bool UnderBuildOutput(string file, string root)
    {
        for (var dir = Path.GetDirectoryName(file); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (string.Equals(dir, root, StringComparison.Ordinal))
                return false;
            if (Path.GetFileName(dir) is "bin" or "obj")
                return true;
        }
        return false;
    }
}
