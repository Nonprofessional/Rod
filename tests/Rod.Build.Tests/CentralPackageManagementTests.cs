using System.Xml.Linq;

namespace Rod.Build.Tests;

/// <summary>
/// Guards the central package management (CPM) rule from roadmap M0.1: every
/// <c>PackageReference</c> must be versionless in the project file, with the
/// version resolved from <c>Directory.Packages.props</c>. Catches a regression
/// where a template or edit re-introduces a <c>Version="..."</c> attribute.
/// </summary>
public class CentralPackageManagementTests
{
    private static readonly string RepoRoot =
        // Assembly runs from <repo>/tests/Rod.Build.Tests/bin/...
        new DirectoryInfo(AppContext.BaseDirectory)
            .Parent!.Parent!.Parent!.Parent!.Parent!.FullName;

    private static readonly IReadOnlyList<string> ProjectFiles = Directory.GetFiles(
        RepoRoot, "*.csproj", SearchOption.AllDirectories).OrderBy(p => p).ToArray();

    public static IEnumerable<object[]> ProjectFileCases =>
        ProjectFiles.Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(ProjectFileCases))]
    public void PackageReferences_AreVersionless(string projectFile)
    {
        var doc = XDocument.Load(projectFile);
        var offenders = doc.Descendants()
            .Where(e => (string?)e.Name.LocalName == "PackageReference")
            .Where(e => e.Attribute("Version") is not null)
            .Select(e => $"{e.Attribute("Include")?.Value}@{e.Attribute("Version")?.Value}")
            .ToArray();

        Assert.False(offenders.Length != 0,
            $"{Path.GetRelativePath(RepoRoot, projectFile)} has versioned PackageReference(s); " +
            "move them to Directory.Packages.props: " + string.Join(", ", offenders));
    }
}
