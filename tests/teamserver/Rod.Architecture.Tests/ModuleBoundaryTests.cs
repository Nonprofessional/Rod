using System.Text.Json;
using System.Xml.Linq;

namespace Rod.Architecture.Tests;

/// <summary>
/// Encodes the in-tree module host rules (architecture.md Sec 15.1, ADR-001):
/// reference modules live under <c>modules/</c> as separate assemblies that
/// reach the process through exactly the path an operator's external module
/// takes -- an assembly beside the binary plus an entry under
/// <c>Capabilities:Modules</c> -- so the tree eats its own dogfood and the
/// seams stay the public contract. Three rules must hold: a server module
/// references the contract project (<c>Rod.Capabilities</c>) and nothing else
/// in-house; no kernel project compile-references a module (the composition
/// root deploys modules, it never references them); and the shipped
/// configuration lists every in-tree module, so what ships, loads. A fourth
/// keeps the host honest: the tree carries at least one reference module, so
/// the in-tree path is exercised, not merely permitted.
/// </summary>
public class ModuleBoundaryTests
{
    private static readonly string RepoRoot = FindRepoRoot(new DirectoryInfo(AppContext.BaseDirectory));

    private static string FindRepoRoot(DirectoryInfo start)
    {
        DirectoryInfo? dir = start;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src"))
                && Directory.Exists(Path.Combine(dir.FullName, "tests")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the repo root from the test assembly.");
    }

    private static readonly string[] ServerModuleProjects = Directory
        .GetFiles(Path.Combine(RepoRoot, "modules"), "*.csproj", SearchOption.AllDirectories)
        .OrderBy(p => p)
        .ToArray();

    public static IEnumerable<object[]> ServerModuleProjectCases =>
        ServerModuleProjects.Select(p => new object[] { p });

    private static string[] ReadProjectReferences(string projectFile)
        => XDocument.Load(projectFile).Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => (string?)e.Attribute("Include")?.Value)
            .Where(include => include is not null)
            .Select(include => include!.Replace('\\', '/'))
            .ToArray();

    // A server module couples to the module seam and to nothing else in-house:
    // Rod.Capabilities carries the contract, and any wider reference would
    // weld the module into the kernel -- exactly what the module layer exists
    // to avoid.
    [Theory]
    [MemberData(nameof(ServerModuleProjectCases))]
    public void ServerModules_ReferenceTheContractOnly(string projectFile)
    {
        var project = Path.GetFileNameWithoutExtension(projectFile)!;
        var targets = ReadProjectReferences(projectFile)
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(t => t)
            .ToArray();

        var forbidden = targets.Where(t => t != "Rod.Capabilities").ToArray();
        Assert.True(forbidden.Length == 0,
            $"{project} references {string.Join(", ", forbidden)}; a server module may reference " +
            "Rod.Capabilities (the contract) and nothing else in-house.");
    }

    // The dogfood rule: in-tree modules load through the external path, so no
    // kernel project may reference one -- a compile-time edge would make the
    // module a layer participant and quietly retire the loading path the
    // ecosystem depends on. (ProjectReferenceTests would also fail the added
    // edge through its allowed-references matrix; this test names the rule.)
    [Fact]
    public void KernelProjects_NeverReferenceServerModules()
    {
        var violators = new List<string>();
        foreach (var projectFile in Directory.GetFiles(
                     Path.Combine(RepoRoot, "src", "teamserver"), "*.csproj", SearchOption.AllDirectories))
        {
            var project = Path.GetFileNameWithoutExtension(projectFile)!;
            foreach (var include in ReadProjectReferences(projectFile))
            {
                if (include.Contains("/modules/", StringComparison.Ordinal))
                    violators.Add($"{project} -> {include}");
            }
        }

        Assert.True(violators.Count == 0,
            "Kernel projects must not reference in-tree modules (they load through the " +
            "config-listed path): " + string.Join(", ", violators));
    }

    // What ships, loads: every module under modules/ is named in the shipped
    // appsettings.json so the in-tree set rides the operator's config path by
    // default -- and every named entry corresponds to a module that exists,
    // so a removed or renamed module leaves no stale entry behind that would
    // abort startup.
    [Fact]
    public void ShippedConfig_ListsExactlyTheInTreeServerModules()
    {
        var settingsPath = Path.Combine(
            RepoRoot, "src", "teamserver", "Rod.TeamServer", "appsettings.json");
        using var document = JsonDocument.Parse(
            File.ReadAllText(settingsPath),
            new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

        var listed = document.RootElement
            .GetProperty("Capabilities").GetProperty("Modules")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToList();

        // The assembly token follows the last comma, mirroring the loader's
        // own split of a "Namespace.Type, AssemblyName" entry.
        var listedAssemblies = listed
            .Select(entry => entry[(entry.LastIndexOf(',') + 1)..].Trim())
            .OrderBy(a => a)
            .ToList();
        var moduleAssemblies = ServerModuleProjects
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(a => a)
            .ToList();

        Assert.True(!listedAssemblies.Except(moduleAssemblies).Any()
                    && !moduleAssemblies.Except(listedAssemblies).Any(),
            "The shipped Capabilities:Modules list and the modules under modules/ disagree: " +
            $"shipped [{string.Join(", ", listedAssemblies)}] vs tree [{string.Join(", ", moduleAssemblies)}].");
    }

    // The host is real only while it hosts something: an empty modules/ tree
    // would pass every rule above while exercising none of them.
    [Fact]
    public void TheTree_CarriesAtLeastOneServerModule()
    {
        Assert.True(ServerModuleProjects.Length > 0,
            "modules/ holds no server module; the in-tree module host would be dead code.");
    }
}
