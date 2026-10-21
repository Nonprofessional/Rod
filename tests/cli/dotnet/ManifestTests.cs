using Rod.Cli.Manifest;

namespace Rod.Cli.Tests;

public class ManifestTests
{
    private const string Valid = """
        [module]
        name = "demo-sweep"
        version = "1.2.0"
        entry = "server"
        license = "MIT"
        description = "A demonstration module."
        homepage = "https://example.test/demo-sweep"

        [compatibility]
        teamserver = "1.0.0"

        [[compatibility.targets]]
        implant-class = "implant"
        platform = "linux"
        arch = "amd64"

        [opsec]
        lands-on-disk = true
        notes = "Stages one file."

        [attack]
        techniques = ["t1082", "T1059.003"]

        [package]
        include = ["src/**"]
        exclude = ["**/bin/**"]
        """;

    [Fact]
    public void ACompleteManifest_ParsesIntoTheModel()
    {
        var errors = ManifestToml.Load(Valid, out var manifest);

        Assert.Empty(errors);
        Assert.NotNull(manifest);
        Assert.Equal("demo-sweep", manifest!.Name);
        Assert.Equal(new SemVer(1, 2, 0, null), manifest.Version);
        Assert.Equal(ModuleEntryKind.Server, manifest.Entry);
        Assert.Equal("MIT", manifest.License);
        Assert.Equal(new SemVer(1, 0, 0, null), manifest.TeamserverMin);
        var target = Assert.Single(manifest.Targets);
        Assert.Equal("implant", target.ImplantClass);
        Assert.True(manifest.Opsec.LandsOnDisk);
        // Techniques normalize to their canonical uppercase shape.
        Assert.Equal(["T1082", "T1059.003"], manifest.Techniques);
        Assert.NotNull(manifest.Package);
    }

    [Fact]
    public void MissingRequiredFields_AreEachNamed()
    {
        var errors = ManifestToml.Load("[module]\nname = \"x\"\n", out var manifest);

        Assert.Null(manifest);
        Assert.Contains(errors, e => e.Contains("module.version"));
        Assert.Contains(errors, e => e.Contains("module.entry"));
        Assert.Contains(errors, e => e.Contains("module.license"));
        Assert.Contains(errors, e => e.Contains("compatibility.teamserver"));
        Assert.Contains(errors, e => e.Contains("opsec.lands-on-disk"));
    }

    [Theory]
    [InlineData("name = \"Demo_Sweep\"", "kebab-case")]
    [InlineData("version = \"1.0\"", "SemVer")]
    [InlineData("entry = \"sidecar\"", "server, implant, or build-unit")]
    public void InvalidValues_FailValidation(string line, string expectedFragment)
    {
        // Each variant omits the field under test from the base document so
        // the inline copy is its only (invalid) declaration.
        var omit = line.Split(' ')[0];
        var toml = $"""
            [module]
            name = "demo-sweep"
            version = "1.0.0"
            entry = "server"
            license = "MIT"
            {line}

            [compatibility]
            teamserver = "1.0.0"

            [opsec]
            lands-on-disk = true
            """.Replace($"name = \"demo-sweep\"\n", omit == "name" ? "" : "name = \"demo-sweep\"\n")
               .Replace($"version = \"1.0.0\"\n", omit == "version" ? "" : "version = \"1.0.0\"\n")
               .Replace($"entry = \"server\"\n", omit == "entry" ? "" : "entry = \"server\"\n");
        var errors = ManifestToml.Load(toml, out var manifest);
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains(expectedFragment));
    }

    [Fact]
    public void AMultilineField_IsRejected_BeforeItCanPoisonTheCanonicalForm()
    {
        var toml = Valid.Replace("\"A demonstration module.\"", "\"\"\"\nline one\nline two\n\"\"\"");
        var errors = ManifestToml.Load(toml, out _);
        Assert.Contains(errors, e => e.Contains("single line"));
    }

    [Fact]
    public void SyntaxErrors_ReportTheParserMessage()
    {
        var errors = ManifestToml.Load("[module]\nname = \n", out var manifest);
        Assert.Null(manifest);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void UnknownKeysAreIgnored_SoNewerManifestsStillParse()
    {
        var toml = Valid.Replace("[opsec]", "future-field = 42\n[opsec]");
        var errors = ManifestToml.Load(toml, out var manifest);
        Assert.Empty(errors);
        Assert.NotNull(manifest);
    }
}

public class CanonicalFormTests
{
    private static ModuleManifest Manifest() => new()
    {
        Name = "demo-sweep",
        Version = new SemVer(1, 2, 0, null),
        Entry = ModuleEntryKind.Server,
        License = "MIT",
        Description = "A demonstration module.",
        Homepage = "https://example.test",
        TeamserverMin = new SemVer(1, 0, 0, null),
        Targets = [new TargetMatrix("implant", "linux", "amd64")],
        Opsec = new OpsecMetadata(true, "Stages one file."),
        Techniques = ["T1082"],
    };

    [Fact]
    public void EveryFieldAndEveryPayloadDigest_Participates()
    {
        var payload = new Dictionary<string, string> { ["src/a.cs"] = "aa", ["src/b.cs"] = "bb" };
        var baseline = CanonicalForm.Build(Manifest(), payload);

        // Each metadata field edit changes the signed bytes.
        var edits = new[]
        {
            Manifest() with { Name = "other" },
            Manifest() with { Version = new SemVer(1, 2, 1, null) },
            Manifest() with { License = "Apache-2.0" },
            Manifest() with { Description = "Different." },
            Manifest() with { TeamserverMin = new SemVer(1, 1, 0, null) },
            Manifest() with { Opsec = new OpsecMetadata(false, "Stages one file.") },
            Manifest() with { Techniques = ["T1005"] },
            Manifest() with { Targets = [new TargetMatrix("implant", "windows", "amd64")] },
        };
        foreach (var edited in edits)
            Assert.NotEqual(baseline, CanonicalForm.Build(edited, payload));

        // Each payload digest edit changes the signed bytes.
        Assert.NotEqual(baseline, CanonicalForm.Build(Manifest(), new Dictionary<string, string> { ["src/a.cs"] = "cc", ["src/b.cs"] = "bb" }));
        Assert.NotEqual(baseline, CanonicalForm.Build(Manifest(), new Dictionary<string, string> { ["src/a.cs"] = "aa" }));
    }

    [Fact]
    public void PayloadOrdering_IsPathSorted_NotEnumerationAccident()
    {
        var first = CanonicalForm.Build(Manifest(), new Dictionary<string, string>
        {
            ["z/last.cs"] = "1",
            ["a/first.cs"] = "2",
        });
        var second = CanonicalForm.Build(Manifest(), new Dictionary<string, string>
        {
            ["a/first.cs"] = "2",
            ["z/last.cs"] = "1",
        });
        Assert.Equal(first, second);
    }
    [Fact]
    public void TheForm_CarriesItsVersionLineAndNoTrailingNewline()
    {
        var form = CanonicalForm.Build(Manifest(), new Dictionary<string, string>());
        Assert.StartsWith("rod-module-manifest-v1\n", form);
        Assert.False(form.EndsWith('\n'));
        Assert.StartsWith("name=demo-sweep\n", form[(CanonicalForm.VersionLine.Length + 1)..]);
    }
}
