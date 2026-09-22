using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Deployment;

namespace Rod.Integration.Tests;

/// <summary>
/// Direct checks of the <see cref="Implant"/> entity invariants and the
/// deploy-token redeem semantics (architecture.md Sec 5/9), complementing the
/// HTTP enrollment slice in <see cref="EnrollmentTests"/>. Redeem must consume
/// one use on success, and refuse unknown, expired, or spent tokens -- each with
/// a distinct <see cref="DeployTokenRedeemReason"/> the endpoint maps to a wire
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

        var implant = Implant.Enroll(id, engagement, Now.AddDays(30), ImplantClass.Implant, Now);

        Assert.Equal(id, implant.Id);
        Assert.Equal(engagement, implant.EngagementId);
        Assert.Equal(Now.AddDays(30), implant.KillDate);
        Assert.Equal(ImplantClass.Implant, implant.Class);
        Assert.Equal(Now, implant.CreatedAt);
    }


    [Fact]
    public void Enroll_RejectsKillDateAtOrBeforeCreation()
    {
        Assert.Throws<ArgumentException>(
            () => Implant.Enroll(ImplantId.New(), EngagementId.New(), Now, ImplantClass.Implant, Now));
        Assert.Throws<ArgumentException>(
            () => Implant.Enroll(ImplantId.New(), EngagementId.New(), Now.AddSeconds(-1), ImplantClass.Implant, Now));
    }

    // --- Deploy token redeem ---

    [Fact]
    public async Task Redeem_ConsumesToken_AndSucceedsOnce()
    {
        var engagements = new InMemoryEngagementRepository();
        var owner = OperatorId.New();
        var engagement = Engagement.Create(EngagementId.New(), "Op A", owner, Now);
        await engagements.SaveAsync(engagement);

        var tokens = new InMemoryDeployTokenService(engagements);
        var minted = await tokens.MintAsync(engagement.Id, owner, Now);

        var first = await tokens.RedeemAsync(minted.Secret, Now.AddSeconds(1));
        Assert.Equal(engagement.Id, first.EngagementId);

        // Single-use default: a second redeem of the same secret refuses as
        // spent -- the row stays resolvable, so the refusal says why.
        var ex = await Assert.ThrowsAsync<DeployTokenRedeemException>(
            () => tokens.RedeemAsync(minted.Secret, Now.AddSeconds(2)));
        Assert.Equal(DeployTokenRedeemReason.Spent, ex.Reason);
    }

    [Fact]
    public async Task Redeem_RefusesExpiredToken()
    {
        var engagements = new InMemoryEngagementRepository();
        var owner = OperatorId.New();
        var engagement = Engagement.Create(EngagementId.New(), "Op A", owner, Now);
        await engagements.SaveAsync(engagement);

        var tokens = new InMemoryDeployTokenService(engagements);
        var minted = await tokens.MintAsync(engagement.Id, owner, Now);

        var ex = await Assert.ThrowsAsync<DeployTokenRedeemException>(
            () => tokens.RedeemAsync(minted.Secret, minted.ExpiresAt.AddSeconds(1)));
        Assert.Equal(DeployTokenRedeemReason.Expired, ex.Reason);
    }

    [Fact]
    public async Task Redeem_RefusesWrongSecret()
    {
        var engagements = new InMemoryEngagementRepository();
        var owner = OperatorId.New();
        var engagement = Engagement.Create(EngagementId.New(), "Op A", owner, Now);
        await engagements.SaveAsync(engagement);

        var tokens = new InMemoryDeployTokenService(engagements);
        await tokens.MintAsync(engagement.Id, owner, Now);

        var ex = await Assert.ThrowsAsync<DeployTokenRedeemException>(
            () => tokens.RedeemAsync("not-a-real-secret", Now.AddSeconds(1)));
        Assert.Equal(DeployTokenRedeemReason.Unknown, ex.Reason);
    }
}
