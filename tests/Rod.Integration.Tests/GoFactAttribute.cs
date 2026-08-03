using Xunit;

namespace Rod.Integration.Tests;

/// <summary>
/// A [Fact] that is skipped when the go toolchain is not on PATH. The M3.2 Go
/// build unit and the end-to-end Go implant test compile a real binary; skipping
/// (rather than failing) keeps the suite green in environments without go while
/// exercising the real slice where go is present. The skip reason is fixed at
/// discovery time so it surfaces as a skipped test in the runner output.
/// </summary>
public sealed class GoFactAttribute : FactAttribute
{
    public const string SkipReason = "Go toolchain not available on PATH; run with go installed to exercise the real Go implant build.";

    public GoFactAttribute()
    {
        if (!TestSupport.GoAvailable())
            Skip = SkipReason;
    }
}
