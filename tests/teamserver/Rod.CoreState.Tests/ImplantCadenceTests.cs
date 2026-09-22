using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;

namespace Rod.CoreState.Tests;

/// <summary>
/// Direct checks of the <see cref="Implant"/> cadence record: the sleep/jitter
/// pair an enrollment reports and every changed handshake advertisement
/// replaces. The pair moves together -- a handshake that advertises carries
/// both halves -- and a handshake that carries nothing keeps whatever the
/// record held, so a pre-field client never erases an enrolled cadence.
/// </summary>
public class ImplantCadenceTests
{
    private static readonly DateTimeOffset Created = DateTimeOffset.UnixEpoch;
    private static readonly DateTimeOffset KillDate = Created.AddDays(30);

    [Fact]
    public void EnrollChild_RecordsTheReportedCadence()
    {
        var implant = Implant.EnrollChild(
            ImplantId.New(), EngagementId.New(), KillDate, ImplantClass.Implant, Created,
            sleepSeconds: 30, jitterSeconds: 10);

        Assert.Equal(30, implant.SleepSeconds);
        Assert.Equal(10, implant.JitterSeconds);
    }

    [Fact]
    public void EnrollChild_LeavesCadenceNullWhenUnreported()
    {
        var implant = Implant.EnrollChild(
            ImplantId.New(), EngagementId.New(), KillDate, ImplantClass.Implant, Created);

        Assert.Null(implant.SleepSeconds);
        Assert.Null(implant.JitterSeconds);
    }

    [Fact]
    public void NoteCadence_ReplacesThePairAndReportsTheChange()
    {
        var implant = Implant.EnrollChild(
            ImplantId.New(), EngagementId.New(), KillDate, ImplantClass.Implant, Created,
            sleepSeconds: 30, jitterSeconds: 10);

        // A beacon.sleep retune: the next handshake advertises the new pair.
        Assert.True(implant.NoteCadence(5, 1));
        Assert.Equal(5, implant.SleepSeconds);
        Assert.Equal(1, implant.JitterSeconds);

        // Re-advertising the same pair changes nothing -- a poll cadence
        // repeats it every contact, and only a retune writes.
        Assert.False(implant.NoteCadence(5, 1));
        Assert.Equal(5, implant.SleepSeconds);
    }

    [Fact]
    public void NoteCadence_WithNothingAdvertised_KeepsTheRecord()
    {
        var implant = Implant.EnrollChild(
            ImplantId.New(), EngagementId.New(), KillDate, ImplantClass.Implant, Created,
            sleepSeconds: 30, jitterSeconds: 10);

        // A handshake from a client that predates the advertisement carries
        // no pair at all; the enrolled cadence survives it.
        Assert.False(implant.NoteCadence(null, null));
        Assert.Equal(30, implant.SleepSeconds);
        Assert.Equal(10, implant.JitterSeconds);
    }

    [Fact]
    public void NoteCadence_AcceptsTheBackToBackPosture()
    {
        // Zero sleep is the near-interactive posture, not "not reported" --
        // the record must hold it as a value.
        var implant = Implant.EnrollChild(
            ImplantId.New(), EngagementId.New(), KillDate, ImplantClass.Implant, Created);

        Assert.True(implant.NoteCadence(0, 0));
        Assert.Equal(0, implant.SleepSeconds);
        Assert.Equal(0, implant.JitterSeconds);
    }
}
