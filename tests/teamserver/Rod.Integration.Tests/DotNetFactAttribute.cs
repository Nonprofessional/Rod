using Xunit;

namespace Rod.Integration.Tests;

/// <summary>
/// A [Fact] that is skipped when the dotnet SDK is not on PATH. The  .NET
/// build unit and the end-to-end .NET implant test publish a real assembly;
/// skipping (rather than failing) keeps the suite green in environments without
/// dotnet while exercising the real slice where dotnet is present. The skip
/// reason is fixed at discovery time so it surfaces as a skipped test in the
/// runner output.
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
