using Xunit;

namespace Rod.Build.Tests;

/// <summary>
/// A [Fact] that is skipped when the dotnet SDK is not on PATH. The .NET build
/// unit publishes a real implant assembly; skipping keeps the suite green without
/// dotnet while exercising the real slice where dotnet is present.
/// </summary>
public sealed class DotNetFactAttribute : FactAttribute
{
    public const string SkipReason = ".NET SDK not available on PATH; run with dotnet installed to exercise the real .NET implant build.";

    public DotNetFactAttribute()
    {
        if (!TestSupport.DotNetAvailable())
            Skip = SkipReason;
    }
}
