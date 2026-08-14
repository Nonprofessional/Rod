using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;

namespace Rod.CoreState.Tests;

/// <summary>
/// Direct checks of the <see cref="Implant.Retire"/> aggregate behavior
/// (architecture.md Sec 7, ). An enrolled implant is live until retired;
/// retiring stamps <see cref="Implant.RetiredAt"/>, flips
/// <see cref="Implant.IsRetired"/>, and is idempotent so a second retire is a
/// no-op. The handshake and task gates that read <see cref="Implant.IsRetired"/>
/// are covered end-to-end by the integration tests; this is the entity-level
/// invariant check.
/// </summary>
public class ImplantRetirementTests
{
    private static readonly DateTimeOffset Created = DateTimeOffset.UnixEpoch;
    private static readonly DateTimeOffset KillDate = Created.AddDays(30);

    private static Implant Enroll(DateTimeOffset? createdAt = null)
        => Implant.Enroll(
            ImplantId.New(),
            EngagementId.New(),
            "key-abc",
            KillDate,
            ImplantClass.Stage2,
            createdAt ?? Created);

    [Fact]
    public void EnrolledImplant_IsNotRetired()
    {
        var implant = Enroll();

        Assert.False(implant.IsRetired);
        Assert.Null(implant.RetiredAt);
    }

    [Fact]
    public void Retire_StampsRetiredAt_AndFlipsIsRetired()
    {
        var implant = Enroll();
        var retiredAt = Created.AddHours(1);

        var changed = implant.Retire(retiredAt);

        Assert.True(changed);
        Assert.True(implant.IsRetired);
        Assert.Equal(retiredAt, implant.RetiredAt);
    }

    [Fact]
    public void Retire_IsIdempotent_OnAlreadyRetiredImplant()
    {
        var implant = Enroll();
        var firstAt = Created.AddHours(1);
        implant.Retire(firstAt);

        var secondAt = Created.AddHours(2);
        var changed = implant.Retire(secondAt);

        Assert.False(changed);
        Assert.True(implant.IsRetired);
        Assert.Equal(firstAt, implant.RetiredAt);
    }
}
