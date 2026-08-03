using Xunit;

namespace Rod.Build.Tests;

/// <summary>
/// A [Fact] that is skipped when the go toolchain is not on PATH. The Go build
/// unit compiles a real binary; skipping keeps the suite green without go while
/// exercising the real slice where go is present.
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
