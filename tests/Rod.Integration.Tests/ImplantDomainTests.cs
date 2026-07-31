using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Staging;

namespace Rod.Integration.Tests;

/// <summary>
/// Direct checks of the <see cref="Implant"/> entity invariants and the
/// stager-token redeem semantics (architecture.md Sec 5/9), complementing the
/// HTTP enrollment slice in <see cref="EnrollmentTests"/>. Redeem must consume
/// one use on success, and refuse unknown, expired, or spent tokens -- each with
/// a distinct <see cref="StagerTokenRedeemReason"/> the endpoint maps to a wire
/// status.
/// </summary>
public class ImplantDomainTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    // --- Implant entity ---

    [Fact]
    public void Enroll_RecordsAllFields()
    {
        var id = ImplantId.New();
        var engagement = EngagementId.New();

        var implant = Implant.Enroll(id, engagement, "key-abc", Now.AddDays(30), ImplantClass.Stage2, Now);

        Assert.Equal(id, implant.Id);
        Assert.Equal(engagement, implant.EngagementId);
        Assert.Equal("key-abc", implant.Key);
        Assert.Equal(Now.AddDays(30), implant.KillDate);
        Assert.Equal(ImplantClass.Stage2, implant.Class);
        Assert.Equal(Now, implant.CreatedAt);
    }

    [Fact]
    public void Enroll_RejectsBlankKey()
    {
        Assert.Throws<ArgumentException>(
            () => Implant.Enroll(ImplantId.New(), EngagementId.New(), "  ", Now.AddDays(1), ImplantClass.Stage2, Now));
    }

    [Fact]
    public void Enroll_RejectsKillDateAtOrBeforeCreation()
    {
        Assert.Throws<ArgumentException>(
            () => Implant.Enroll(ImplantId.New(), EngagementId.New(), "k", Now, ImplantClass.Stage2, Now));
        Assert.Throws<ArgumentException>(
            () => Implant.Enroll(ImplantId.New(), EngagementId.New(), "k", Now.AddSeconds(-1), ImplantClass.Stage2, Now));
    }

    // --- Stager token redeem ---

    [Fact]
    public async Task Redeem_ConsumesToken_AndSucceedsOnce()
    {
        var engagements = new InMemoryEngagementRepository();
        var owner = OperatorId.New();
        var engagement = Engagement.Create(EngagementId.New(), "Op A", owner, Now);
        await engagements.SaveAsync(engagement);

        var tokens = new InMemoryStagerTokenService(engagements);
        var minted = await tokens.MintAsync(engagement.Id, owner, Now);

        var first = await tokens.RedeemAsync(minted.Secret, Now.AddSeconds(1));
        Assert.Equal(engagement.Id, first.EngagementId);

        // Single-use default: a second redeem of the same secret is now unknown.
        var ex = await Assert.ThrowsAsync<StagerTokenRedeemException>(
            () => tokens.RedeemAsync(minted.Secret, Now.AddSeconds(2)));
        Assert.Equal(StagerTokenRedeemReason.Unknown, ex.Reason);
    }

    [Fact]
    public async Task Redeem_RefusesExpiredToken()
    {
        var engagements = new InMemoryEngagementRepository();
        var owner = OperatorId.New();
        var engagement = Engagement.Create(EngagementId.New(), "Op A", owner, Now);
        await engagements.SaveAsync(engagement);

        var tokens = new InMemoryStagerTokenService(engagements);
        var minted = await tokens.MintAsync(engagement.Id, owner, Now);

        var ex = await Assert.ThrowsAsync<StagerTokenRedeemException>(
            () => tokens.RedeemAsync(minted.Secret, minted.ExpiresAt.AddSeconds(1)));
        Assert.Equal(StagerTokenRedeemReason.Expired, ex.Reason);
    }

    [Fact]
    public async Task Redeem_RefusesWrongSecret()
    {
        var engagements = new InMemoryEngagementRepository();
        var owner = OperatorId.New();
        var engagement = Engagement.Create(EngagementId.New(), "Op A", owner, Now);
        await engagements.SaveAsync(engagement);

        var tokens = new InMemoryStagerTokenService(engagements);
        await tokens.MintAsync(engagement.Id, owner, Now);

        var ex = await Assert.ThrowsAsync<StagerTokenRedeemException>(
            () => tokens.RedeemAsync("not-a-real-secret", Now.AddSeconds(1)));
        Assert.Equal(StagerTokenRedeemReason.Unknown, ex.Reason);
    }
}
