using Rod.BuildPipeline.PayloadBuild;

namespace Rod.Build.Tests;

/// <summary>
/// The wire names of the artifact format axis (architecture.md Sec 6): one
/// mapping shared by the build request parser, the recorder, and the library
/// view, so these tests pin the exact spellings an operator types and the
/// responses echo.
/// </summary>
public class ArtifactFormatTests
{
    [Theory]
    [InlineData(null, ArtifactFormat.SingleFileExe)]
    [InlineData("", ArtifactFormat.SingleFileExe)]
    [InlineData("exe", ArtifactFormat.SingleFileExe)]
    [InlineData("EXE", ArtifactFormat.SingleFileExe)]
    [InlineData(" exe-trimmed ", ArtifactFormat.TrimmedExe)]
    [InlineData("aot", ArtifactFormat.NativeAot)]
    [InlineData("DLL", ArtifactFormat.Dll)]
    public void TryParse_AcceptsTheWireNames(string? wire, ArtifactFormat expected)
    {
        Assert.True(ArtifactFormats.TryParse(wire, out var format));
        Assert.Equal(expected, format);
    }

    [Theory]
    [InlineData("native")]
    [InlineData("single-file-exe")]
    [InlineData("exe_trimmed")]
    [InlineData("position-independent")]
    public void TryParse_RefusesUnknownSpellings(string wire)
    {
        Assert.False(ArtifactFormats.TryParse(wire, out _));
    }

    [Fact]
    public void Name_EveryFormatRoundTripsThroughItsWireName()
    {
        foreach (ArtifactFormat format in Enum.GetValues(typeof(ArtifactFormat)))
        {
            var wire = ArtifactFormats.Name(format);
            Assert.True(ArtifactFormats.TryParse(wire, out var parsed), wire);
            Assert.Equal(format, parsed);
        }
    }
}
