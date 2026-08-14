using Rod.Implant.Internal;
using Rod.V1;

namespace Rod.Implant.Tests;

// LateralTests ports lateral_test.go from the Go reference implant to xUnit,
// covering the lateral.* dispatch surface that does not need a live enroll
// endpoint: the lateral.move argument parser, the disabled-bundle refusal, the
// lateral.token platform branch, the lateral.exec_remote parser, and the
// dispatch routing. The end-to-end enroll round-trip is exercised by the
// implant-driven integration test, which runs the reference implant as a
// subprocess; reproducing that leaf+CA dance here would duplicate the
// teamserver's enroll handler for a single handler unit test.
public class LateralTests
{
    private static HandlerRegistry NewRegistry() => HandlerRegistry.Default();

    // A registry whose lateral.move handler derivation is enabled, so the parser
    // is reached rather than the disabled-bundle refusal. Profile is a default
    // TransportProfile; it is never used because every test fails at the parser.
    private static HandlerRegistry NewRegistryWithEnroll()
        => HandlerRegistry.Default(new EnrollBundle
        {
            Url = "http://127.0.0.1:9/enroll",
            ParentId = "parent-1",
            Profile = new TransportProfile(),
        });

    [Fact]
    public void LateralMove_DisabledBundle_FailsWithCause()
    {
        // A registry built without an enroll bundle cannot derive children; the
        // handler reports the cause rather than enrolling against an empty
        // endpoint.
        var registry = NewRegistry();
        var (outcome, output, _) = registry.Dispatch("lateral.move", "child-token");
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("not available", output);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a b c")]
    public void LateralMove_MalformedArgs_FailsWithCause(string args)
    {
        // A bundle with a URL enables derivation, but the argument still must
        // carry a token. Empty or over-long arguments are refused before any key
        // is generated.
        var registry = NewRegistryWithEnroll();
        var (outcome, output, _) = registry.Dispatch("lateral.move", args);
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("lateral.move expects", output);
    }

    [Fact]
    public void LateralToken_NonWindows_RefusesWithCause()
    {
        // Documents the platform contract on a non-Windows test host. Windows
        // hosts exercise the whoami path directly (covered by the build).
        if (OperatingSystem.IsWindows()) return;

        var registry = NewRegistry();
        var (outcome, output, _) = registry.Dispatch("lateral.token", "");
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("lateral.token", output);
        Assert.Contains("Windows", output);
    }

    [Fact]
    public void LateralExecRemote_MalformedArgs_FailsWithCause()
    {
        var registry = NewRegistry();
        foreach (var args in new[] { "", "   ", "single-host" })
        {
            var (outcome, output, _) = registry.Dispatch("lateral.exec_remote", args);
            Assert.Equal(TaskOutcome.Failed, outcome);
            Assert.Contains("lateral.exec_remote expects", output);
        }
    }

    // The class field is null when the single-token form is used or when parse
    // fails (the .NET handler leaves the out null until a second field is
    // present); the table below reflects that .NET semantics, the equivalent of
    // the Go implant's empty-string class.
    [Theory]
    [InlineData("", "", null, false)]
    [InlineData("   ", "", null, false)]
    [InlineData("tok", "tok", null, true)]
    [InlineData("  tok  ", "tok", null, true)]
    [InlineData("tok stage2", "tok", "stage2", true)]
    [InlineData("tok a b", "", null, false)]
    public void TryParseArgs_Routes_Token_And_Class(
        string input, string token, string? klass, bool ok)
    {
        var result = Lateral.TryParseArgs(input, out var t, out var c);
        Assert.Equal(ok, result);
        Assert.Equal(token, t);
        Assert.Equal(klass, c);
    }

    [Theory]
    [InlineData("", "", "", false)]
    [InlineData("   ", "", "", false)]
    [InlineData("host", "", "", false)]
    [InlineData("host cmd", "host", "cmd", true)]
    [InlineData("  host   cmd  ", "host", "cmd", true)]
    [InlineData("host cmd with args", "host", "cmd with args", true)]
    public void TryParseExecRemoteArgs_Routes_Host_And_Command(
        string input, string host, string command, bool ok)
    {
        var result = Lateral.TryParseExecRemoteArgs(input, out var h, out var cmd);
        Assert.Equal(ok, result);
        Assert.Equal(host, h);
        Assert.Equal(command, cmd);
    }
}
