using Rod.Implant.Internal;

namespace Rod.Implant.Tests;

// Pins the class-verb config channel: the build unit bakes the class's reduced
// verb set as the profile's comma-joined "verbs" key, BakedProfileSupport maps
// it onto ROD_VERBS, and Config.Parse reads that back into ClassVerbs for the
// beacon's advertised-set derivation (architecture.md Sec 5.2/5.3).
public class ConfigTests
{
    [Fact]
    public void ClassVerbs_FromEnv_AreCommaSplitAndTrimmed()
    {
        using var verbs = new EnvScope("ROD_VERBS", "shell.exec, recon.portscan ,");
        using var enroll = new EnvScope("ROD_ENROLL_URL", "http://127.0.0.1:9/implants/enroll");
        using var token = new EnvScope("ROD_STAGER_TOKEN", "test-token");
        var config = Config.Parse(Array.Empty<string>());
        Assert.Equal(new[] { "shell.exec", "recon.portscan" }, config.ClassVerbs);
    }

    [Fact]
    public void ClassVerbs_Unset_IsEmpty()
    {
        using var verbs = new EnvScope("ROD_VERBS", "");
        using var enroll = new EnvScope("ROD_ENROLL_URL", "http://127.0.0.1:9/implants/enroll");
        using var token = new EnvScope("ROD_STAGER_TOKEN", "test-token");
        var config = Config.Parse(Array.Empty<string>());
        Assert.Empty(config.ClassVerbs);
    }
}
