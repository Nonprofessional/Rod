using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;
using Rod.CoreState.Staging;
using Task = System.Threading.Tasks.Task;

namespace Rod.CoreState.Tests;

/// <summary>
/// The batch-scoped mint: one token, several uses, a chosen window. Each
/// enroll spends one use; the token refuses the redeem past its last use and
/// anything past its expiry, verify never consumes, and a mint that names no
/// scope keeps the single-use, one-hour default.
/// </summary>
public class StagerTokenMultiUseTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private sealed record Harness(InMemoryStagerTokenService Service, EngagementId EngagementId, OperatorId Owner);

    private static async Task<Harness> HarnessAsync()
    {
        var engagementId = EngagementId.New();
        var owner = OperatorId.New();
        var engagements = new InMemoryEngagementRepository();
        await engagements.SaveAsync(Engagement.Create(engagementId, "multi-use-test", owner, Now));
        return new Harness(new InMemoryStagerTokenService(engagements), engagementId, owner);
    }

    [Fact]
    public async Task Batch_Token_Redeems_Per_Use_And_Refuses_The_Extra()
    {
        var h = await HarnessAsync();
        var token = await h.Service.MintAsync(h.EngagementId, h.Owner, Now, maxUses: 2, lifetime: TimeSpan.FromHours(4));

        Assert.Equal(2, token.MaxUses);
        Assert.Equal(Now.AddHours(4), token.ExpiresAt);

        var first = await h.Service.RedeemAsync(token.Secret, Now.AddMinutes(1));
        var second = await h.Service.RedeemAsync(token.Secret, Now.AddMinutes(2));
        Assert.Equal(h.EngagementId, first.EngagementId);
        Assert.Equal(h.EngagementId, second.EngagementId);

        // The in-memory service deletes a token at zero remaining uses, so the
        // over-spend reads Unknown; the Postgres analogue keeps the row and
        // reports Spent (the deliberate durable difference, per its remarks).
        var ex = await Assert.ThrowsAsync<StagerTokenRedeemException>(
            () => h.Service.RedeemAsync(token.Secret, Now.AddMinutes(3)));
        Assert.Equal(StagerTokenRedeemReason.Unknown, ex.Reason);
    }

    [Fact]
    public async Task Verify_Never_Consumes_A_Batch_Token()
    {
        var h = await HarnessAsync();
        var token = await h.Service.MintAsync(h.EngagementId, h.Owner, Now, maxUses: 1, lifetime: TimeSpan.FromHours(1));

        // The pre-enrollment read a stage-1 stager performs leaves the batch
        // credential whole for the stage-2's enroll, exactly as for a
        // single-use token.
        _ = await h.Service.VerifyAsync(token.Secret, Now.AddSeconds(30));
        _ = await h.Service.VerifyAsync(token.Secret, Now.AddSeconds(60));

        var redeemed = await h.Service.RedeemAsync(token.Secret, Now.AddMinutes(2));
        Assert.Equal(h.EngagementId, redeemed.EngagementId);
    }

    [Fact]
    public async Task Batch_Token_Refuses_After_Its_Window()
    {
        var h = await HarnessAsync();
        var token = await h.Service.MintAsync(h.EngagementId, h.Owner, Now, maxUses: 5, lifetime: TimeSpan.FromMinutes(30));

        var ex = await Assert.ThrowsAsync<StagerTokenRedeemException>(
            () => h.Service.RedeemAsync(token.Secret, Now.AddMinutes(31)));
        Assert.Equal(StagerTokenRedeemReason.Expired, ex.Reason);
    }

    [Fact]
    public async Task Mint_Without_Scope_Keeps_The_Defaults()
    {
        var h = await HarnessAsync();
        var token = await h.Service.MintAsync(h.EngagementId, h.Owner, Now);

        Assert.Equal(1, token.MaxUses);
        Assert.Equal(Now.AddHours(1), token.ExpiresAt);
    }
}
