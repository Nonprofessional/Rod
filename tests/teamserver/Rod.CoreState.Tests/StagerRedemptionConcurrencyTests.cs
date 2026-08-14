using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;
using Rod.CoreState.Staging;
using Task = System.Threading.Tasks.Task;

namespace Rod.CoreState.Tests;

/// <summary>
/// Multi-threaded hammer tests for <see cref="InMemoryStagerTokenService"/>
/// redeem atomicity. Redeem guards check-then-consume with a lock so a
/// single-use token cannot be redeemed twice; these tests drive real threads
/// into it.
/// </summary>
public class StagerRedemptionConcurrencyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static (InMemoryEngagementRepository Engagements, InMemoryStagerTokenService Tokens) NewService()
    {
        var engagements = new InMemoryEngagementRepository();
        var tokens = new InMemoryStagerTokenService(engagements);
        return (engagements, tokens);
    }

    private static async Task<StagerToken> MintAsync(
        InMemoryEngagementRepository engagements,
        InMemoryStagerTokenService tokens,
        string name)
    {
        var owner = OperatorId.New();
        var engagement = Engagement.Create(EngagementId.New(), name, owner, Now);
        await engagements.SaveAsync(engagement);
        return await tokens.MintAsync(engagement.Id, owner, Now);
    }

    [Fact]
    public async Task ConcurrentRedeems_OfOneToken_SucceedExactlyOnce()
    {
        var (engagements, tokens) = NewService();
        var minted = await MintAsync(engagements, tokens, "Op A");

        const int redeemers = 16;
        var gate = new ManualResetEventSlim(initialState: false);
        var pending = Enumerable.Range(0, redeemers).Select(_ => Task.Run(async () =>
        {
            gate.Wait();
            try
            {
                var redeemed = await tokens.RedeemAsync(minted.Secret, Now.AddMinutes(1));
                return new Outcome(redeemed, Failure: null);
            }
            catch (StagerTokenRedeemException ex)
            {
                return new Outcome(Redeemed: null, ex.Reason);
            }
        })).ToArray();

        // Release the redeemers together so they genuinely race for the lock.
        gate.Set();
        var outcomes = await Task.WhenAll(pending);

        // One winner consumes the single use; every loser observes the token
        // gone. Whether a loser sees Spent or Unknown depends on whether the
        // winner's removal beat its lookup, but a second success never happens.
        var winner = Assert.Single(outcomes, o => o.Redeemed is not null);
        Assert.Equal(minted.Id, winner.Redeemed!.Id);
        Assert.Equal(minted.EngagementId, winner.Redeemed.EngagementId);
        Assert.Equal(redeemers - 1, outcomes.Count(o =>
            o.Failure is StagerTokenRedeemReason.Spent or StagerTokenRedeemReason.Unknown));
    }

    [Fact]
    public async Task ConcurrentRedeems_OfDistinctTokens_AllSucceed()
    {
        var (engagements, tokens) = NewService();

        const int tokenCount = 32;
        var minted = new StagerToken[tokenCount];
        for (var i = 0; i < tokenCount; i++)
            minted[i] = await MintAsync(engagements, tokens, $"Op {i}");

        // The shared redeem lock must serialize without rejecting valid tokens:
        // each token redeems exactly once, concurrently, and is consumed.
        var gate = new ManualResetEventSlim(initialState: false);
        var pending = minted.Select(m => Task.Run(async () =>
        {
            gate.Wait();
            return await tokens.RedeemAsync(m.Secret, Now.AddMinutes(1));
        })).ToArray();

        gate.Set();
        var redeemed = await Task.WhenAll(pending);

        Assert.Equal(tokenCount, redeemed.Select(r => r.Id).Distinct().Count());

        foreach (var m in minted)
        {
            var ex = await Assert.ThrowsAsync<StagerTokenRedeemException>(
                () => tokens.RedeemAsync(m.Secret, Now.AddMinutes(2)));
            Assert.True(ex.Reason is StagerTokenRedeemReason.Spent or StagerTokenRedeemReason.Unknown);
        }
    }

    private sealed record Outcome(RedeemedStagerToken? Redeemed, StagerTokenRedeemReason? Failure);
}
