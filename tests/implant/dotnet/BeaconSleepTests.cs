using Rod.Implant.Internal;
using Rod.V1;

namespace Rod.Implant.Tests;

/// <summary>
/// The runtime cadence control (beacon.sleep): the live sleep/jitter pair a
/// fielded implant retunes without a rebuild -- the reference implant's
/// answer to Cobalt Strike's sleep. Pins the argument grammar (Go durations,
/// bare seconds, the interactive-as-poll zero), the was/now report the
/// operator reads back, and the swap semantics of the shared Cadence both
/// contact clients sleep on.
/// </summary>
public class BeaconSleepTests
{
    [Theory]
    [InlineData("10s", 10, 10)]         // Go duration, jitter kept
    [InlineData("30", 30, 10)]          // bare seconds, jitter kept
    [InlineData("1m30s", 90, 10)]       // compound Go duration, jitter kept
    [InlineData("10s 2s", 10, 2)]       // sleep + jitter
    [InlineData("0 0", 0, 0)]           // the interactive-as-poll posture
    [InlineData("500ms 100ms", 0.5, 0.1)]
    public void Set_AcceptsGoDurationsAndBareSeconds(string args, double sleepSeconds, double jitterSeconds)
    {
        var cadence = new Cadence(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));

        var (outcome, output) = BeaconSleep.Set(args, cadence);

        Assert.Equal(TaskOutcome.Succeeded, outcome);
        var (sleep, jitter) = cadence.Current;
        Assert.Equal(TimeSpan.FromSeconds(sleepSeconds), sleep);
        Assert.Equal(TimeSpan.FromSeconds(jitterSeconds), jitter);
        Assert.Contains("was 30s ± 10s", output);
    }

    [Fact]
    public void Set_WithoutAJitter_KeepsTheCurrentOne()
    {
        var cadence = new Cadence(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(7));

        var (outcome, _) = BeaconSleep.Set("5m", cadence);

        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Equal(TimeSpan.FromMinutes(5), cadence.Current.Sleep);
        Assert.Equal(TimeSpan.FromSeconds(7), cadence.Current.Jitter);
    }

    [Theory]
    [InlineData("")]
    [InlineData("10s 2s 1s")]
    [InlineData("banana")]
    [InlineData("-5s")]
    [InlineData("10s -2s")]
    public void Set_RefusesMalformedArguments(string args)
    {
        var cadence = new Cadence(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));

        var (outcome, output) = BeaconSleep.Set(args, cadence);

        Assert.Equal(TaskOutcome.Failed, outcome);
        // A refused change leaves the cadence untouched.
        Assert.Equal(TimeSpan.FromSeconds(30), cadence.Current.Sleep);
        Assert.Equal(TimeSpan.FromSeconds(10), cadence.Current.Jitter);
        Assert.Contains("beacon.sleep", output);
    }

    [Fact]
    public void Set_WithoutACadenceControl_RefusesCleanly()
    {
        var (outcome, output) = BeaconSleep.Set("10s", null);

        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("no cadence control", output);
    }

    [Fact]
    public void Registry_DispatchesBeaconSleep_ThroughTheDefaultSelection()
    {
        // The full reference registry carries the verb with its cadence, so
        // the dispatch path the beacon loop uses retunes the shared control.
        var cadence = new Cadence(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));
        var registry = HandlerRegistry.Default(enroll: null, cadence: cadence);

        var (outcome, output, _) = registry.Dispatch("beacon.sleep", "0 0");

        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Equal(TimeSpan.Zero, cadence.Current.Sleep);
        Assert.Equal(TimeSpan.Zero, cadence.Current.Jitter);
        Assert.Contains("back-to-back", output);
    }
}
