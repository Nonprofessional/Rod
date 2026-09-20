using System.Text;
using Rod.Implant.Internal;

namespace Rod.Implant.Tests;

// Pins the bake's authority (architecture.md Sec 5.1): the profile baked at
// build time overrides whatever flags or the environment supplied, so a
// fielded artifact cannot be re-pointed or re-credentialed at run time; keys
// the bake omits keep their run-time values, so an unbaked dev binary stays
// fully configurable. The baked JSON reaches ApplyBaked here as the build
// unit's generated constant would deliver it: base64-URL of the profile
// document.
public class BakedProfilePrecedenceTests
{
    private static string Bake(object profile)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(
                System.Text.Json.JsonSerializer.Serialize(profile)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    // A config parsed against the run-time environment: an endpoint and a
    // credential the bake will have to beat.
    private static Config ParsedWithRuntimeDefaults()
    {
        using var enroll = new EnvScope("ROD_ENROLL_URL", "http://runtime.example.test/implants/enroll");
        using var token = new EnvScope("ROD_STAGER_TOKEN", "runtime-token");
        return Config.Parse(Array.Empty<string>());
    }

    [Fact]
    public void BakedOperationalKeys_OverrideTheRunTimeValues()
    {
        var config = ParsedWithRuntimeDefaults();

        // The environment points elsewhere entirely; the bake wins for every
        // operational key it carries.
        BakedProfileSupport.ApplyBaked(config, Bake(new Dictionary<string, object>
        {
            ["enrollURL"] = "https://baked.example.test/implants/enroll",
            ["beaconURL"] = "baked.example.test:5443",
            ["token"] = "baked-token",
            ["sleep"] = "5s",
            ["jitter"] = "2s",
            ["mode"] = "poll",
        }));

        Assert.Equal("https://baked.example.test/implants/enroll", config.EnrollURL);
        Assert.Equal("baked.example.test:5443", config.BeaconURL);
        Assert.Equal("baked-token", config.StagerToken);
        Assert.Equal(TimeSpan.FromSeconds(5), config.Sleep);
        Assert.Equal(TimeSpan.FromSeconds(2), config.Jitter);
        Assert.Equal("poll", config.Mode);
    }

    [Fact]
    public void KeysTheBakeOmits_KeepTheirRunTimeValues()
    {
        var config = ParsedWithRuntimeDefaults();

        BakedProfileSupport.ApplyBaked(config, Bake(new Dictionary<string, object>
        {
            ["sleep"] = "1s",
        }));

        // The bake carried only the cadence: the run-time endpoint and
        // credential stand -- the unbaked dev shape, one key at a time.
        Assert.Equal("http://runtime.example.test/implants/enroll", config.EnrollURL);
        Assert.Equal("runtime-token", config.StagerToken);
        Assert.Equal(TimeSpan.FromSeconds(1), config.Sleep);
    }

    [Fact]
    public void AnEmptyBakedKillDate_ClearsARunTimeFuse()
    {
        // The run-time fuse is in place before the parse, so the config
        // genuinely holds one for the bake to clear.
        using var fuse = new EnvScope("ROD_KILL_DATE", DateTimeOffset.UtcNow.AddDays(90).ToString("O"));
        var config = ParsedWithRuntimeDefaults();
        Assert.True(config.HasKillDate);

        // The pipeline emits an empty killDate for the open-ended shape; the
        // bake says open-ended, so the run-time fuse is gone.
        BakedProfileSupport.ApplyBaked(config, Bake(new Dictionary<string, object>
        {
            ["killDate"] = "",
        }));
        Assert.False(config.HasKillDate);
    }

    [Fact]
    public void MalformedBakedData_LeavesTheRunTimeConfigurationInForce()
    {
        var config = ParsedWithRuntimeDefaults();

        // Not valid base64-URL, not JSON: a bad bake must not crash the
        // implant or half-apply -- the run-time configuration stands.
        BakedProfileSupport.ApplyBaked(config, "%%%not-a-profile%%%");

        Assert.Equal("http://runtime.example.test/implants/enroll", config.EnrollURL);
        Assert.Equal("runtime-token", config.StagerToken);
    }
}
