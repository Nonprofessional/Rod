using System.Diagnostics;

namespace Rod.Conformance.Tests;

/// <summary>
/// Acceptance for the Tier 0 conformance harness (todo.md, implant reach).
/// The harness drives a candidate implant against a live teamserver and
/// reports pass/fail per contract clause; the acceptance criterion is
/// two-sided: pointing it at the reference implant passes every clause, and
/// pointing it at a deliberately broken one fails with the violated clause
/// named. The broken candidates are the harness's own minimal implant with
/// exactly one defect switched on, so a named failure is attributable.
/// </summary>
public class ConformanceHarnessTests
{
    [Fact]
    public async Task Harness_ReferenceImplant_PassesEveryClause()
    {
        if (!DotNetAvailable())
            return; // The reference implant needs the dotnet toolchain.

        await using var rig = await ConformanceRig.StartAsync();
        using var reference = ReferenceImplantCandidate.Publish();

        var report = await rig.RunAsync(reference);
        Assert.True(report.Failed.Count == 0, $"reference implant failed clauses:{Environment.NewLine}{report}");
    }

    [Fact]
    public async Task Harness_ConformingMinimalImplant_PassesEveryClause()
    {
        await using var rig = await ConformanceRig.StartAsync();
        using var candidate = new MinimalImplant(new ImplantDefects());

        var report = await rig.RunAsync(candidate);
        Assert.True(report.Failed.Count == 0,
            $"the conforming minimal implant failed clauses:{Environment.NewLine}{report}");
    }

    [Fact]
    public async Task Harness_ImplantWithoutVerification_FailsTheSignatureClause_Named()
    {
        await using var rig = await ConformanceRig.StartAsync();
        using var candidate = new MinimalImplant(new ImplantDefects(SkipSignatureVerification: true));

        var report = await rig.RunAsync(candidate);

        // The defect betrays exactly the signature clause; every other clause
        // still passes, which is what makes the named failure meaningful.
        var failed = Assert.Single(report.Failed);
        Assert.Equal(ConformanceRig.SignatureClause, failed.Clause);
        Assert.Contains("unsigned tasking", failed.Detail);
    }

    [Fact]
    public async Task Harness_ImplantSpeakingOutOfTurn_FailsTheHandshakeClause_Named()
    {
        await using var rig = await ConformanceRig.StartAsync();
        using var candidate = new MinimalImplant(new ImplantDefects(SpeakHandshakeSecond: true));

        var report = await rig.RunAsync(candidate);

        var named = report.Failed.FirstOrDefault(c => c.Clause == ConformanceRig.HandshakeClause);
        Assert.NotNull(named);
        Assert.Contains("handshake-first", named!.Detail);
    }

    [Fact]
    public async Task Harness_ImplantWithScrambledChunks_FailsTheChunkClause_Named()
    {
        await using var rig = await ConformanceRig.StartAsync();
        using var candidate = new MinimalImplant(new ImplantDefects(ScrambleChunkSequences: true));

        var report = await rig.RunAsync(candidate);

        var named = report.Failed.FirstOrDefault(c => c.Clause == ConformanceRig.ChunkClause);
        Assert.NotNull(named);
        Assert.Contains("sequence", named!.Detail);
    }

    // The reference-implant candidate drives the real dotnet toolchain; skip
    // (not fail) where dotnet is not reachable, the same rule the in-tree
    // build tests apply.
    private static bool DotNetAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("dotnet", "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
                return false;
            process.WaitForExit(15000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
