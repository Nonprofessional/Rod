using Rod.Implant.Internal;

namespace Rod.Implant.Tests;

// Pins the class-verb config channel: the build unit bakes the class's reduced
// verb set as the profile's comma-joined "verbs" key, BakedProfileSupport maps
// it onto ROD_VERBS, and Config.Parse reads that back into ClassVerbs for the
// beacon's advertised-set derivation (architecture.md Sec 5.2/5.3).
public class ConfigTests
{
    [Fact]
    public void Mode_DefaultsToStream_AndAcceptsPollFromFlagOrEnv()
    {
        // Stream is the default; -mode poll and ROD_MODE=poll both select the
        // poll cadence; anything else is a usage error, not a silent default.
        using var enroll = new EnvScope("ROD_ENROLL_URL", "http://127.0.0.1:9/implants/enroll");
        using var token = new EnvScope("ROD_STAGER_TOKEN", "test-token");
        Assert.Equal("stream", Config.Parse(Array.Empty<string>()).Mode);

        using (new EnvScope("ROD_MODE", "poll"))
            Assert.Equal("poll", Config.Parse(Array.Empty<string>()).Mode);

        Assert.Equal("poll", Config.Parse(new[] { "-mode", "poll" }).Mode);

        Assert.Throws<ExitProgramException>(() => Config.Parse(new[] { "-mode", "loud" }));
    }

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

    [Fact]
    public void FallbackEnrollURLs_ReadTheBakedJsonArrayFromEnv()
    {
        // The bake emits the ordered list as a JSON array under
        // "fallbackEnrollURLs"; BakedProfileSupport passes it through verbatim,
        // so the env value here is exactly what a baked artifact supplies.
        using var enroll = new EnvScope("ROD_ENROLL_URL", "http://127.0.0.1:9/implants/enroll");
        using var token = new EnvScope("ROD_STAGER_TOKEN", "test-token");
        using var fallbacks = new EnvScope(
            "ROD_FALLBACK_ENROLL_URLS",
            "[\"https://alt1.example.test/implants/enroll\", \"https://alt2.example.test/implants/enroll\"]");
        var config = Config.Parse(Array.Empty<string>());
        Assert.Equal(
            new[] { "https://alt1.example.test/implants/enroll", "https://alt2.example.test/implants/enroll" },
            config.FallbackEnrollURLs);
    }

    [Fact]
    public void FallbackEnrollURLs_FlagIsCommaSeparated_AndWinsOverEnv()
    {
        using var enroll = new EnvScope("ROD_ENROLL_URL", "http://127.0.0.1:9/implants/enroll");
        using var token = new EnvScope("ROD_STAGER_TOKEN", "test-token");
        using var envList = new EnvScope(
            "ROD_FALLBACK_ENROLL_URLS", "[\"https://env.example.test/implants/enroll\"]");
        var config = Config.Parse(
            new[] { "-fallback-enroll-urls", "https://alt1.example.test/implants/enroll, https://alt2.example.test/implants/enroll," });
        Assert.Equal(
            new[] { "https://alt1.example.test/implants/enroll", "https://alt2.example.test/implants/enroll" },
            config.FallbackEnrollURLs);
    }

    [Fact]
    public void FallbackEnrollURLs_MalformedEnvYieldsEmpty()
    {
        // A bad bake must not break enroll: an unparseable list is no list.
        using var enroll = new EnvScope("ROD_ENROLL_URL", "http://127.0.0.1:9/implants/enroll");
        using var token = new EnvScope("ROD_STAGER_TOKEN", "test-token");
        using var bad = new EnvScope("ROD_FALLBACK_ENROLL_URLS", "not-json");
        Assert.Empty(Config.Parse(Array.Empty<string>()).FallbackEnrollURLs);
    }

    [Fact]
    public void ResolveEnrollUrl_AppliesTheProfiledPathToAnyEntry()
    {
        // Every entry of the egress walk resolves through the same profiled
        // enroll path, so a fallback enrolls against the malleable route the
        // profile bakes, not the teamserver's default.
        var profile = new TransportProfile { EnrollPath = "/api/v1/health" };
        Assert.Equal(
            "https://alt.example.test/api/v1/health",
            Config.ResolveEnrollUrl("https://alt.example.test/implants/enroll", profile));
        // An entry without the default suffix keeps its host; the path replaces
        // whatever trailing path it carried.
        Assert.Equal(
            "https://alt.example.test/api/v1/health",
            Config.ResolveEnrollUrl("https://alt.example.test/some/other/path", profile));
    }
}
