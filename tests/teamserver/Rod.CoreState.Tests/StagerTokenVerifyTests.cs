using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;
using Rod.CoreState.Staging;
using Task = System.Threading.Tasks.Task;

namespace Rod.CoreState.Tests;

/// <summary>
/// The non-consuming verify on the stager token service (architecture.md
/// Sec 6): the pre-enrollment read a stage-1 stager's payload fetch performs.
/// It must accept exactly what redeem accepts -- a valid, unexpired token with
/// uses remaining -- and refuse exactly what redeem refuses, while leaving the
/// token whole: a fetch may never spend the deployment credential the stage-2
/// needs at its own enroll.
/// </summary>
public class StagerTokenVerifyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private sealed record Harness(InMemoryStagerTokenService Service, StagerToken Token);

    private static async Task<Harness> HarnessAsync()
    {
        var engagementId = EngagementId.New();
        var owner = OperatorId.New();
        var engagements = new InMemoryEngagementRepository();
        await engagements.SaveAsync(Engagement.Create(engagementId, "verify-test", owner, Now));

        var service = new InMemoryStagerTokenService(engagements);
        var token = await service.MintAsync(engagementId, owner, Now);
        return new Harness(service, token);
    }

    [Fact]
    public async Task Verify_AcceptsAndDoesNotConsume()
    {
        var h = await HarnessAsync();

        var first = await h.Service.VerifyAsync(h.Token.Secret, Now.AddMinutes(1));
        var second = await h.Service.VerifyAsync(h.Token.Secret, Now.AddMinutes(2));
        Assert.Equal(h.Token.EngagementId, first.EngagementId);
        Assert.Equal(h.Token.EngagementId, second.EngagementId);

        // The single use is still there for the enrollment that follows.
        var redeemed = await h.Service.RedeemAsync(h.Token.Secret, Now.AddMinutes(3));
        Assert.Equal(h.Token.EngagementId, redeemed.EngagementId);
    }

    [Fact]
    public async Task Verify_RefusesWhatRedeemRefuses()
    {
        var h = await HarnessAsync();

        await Assert.ThrowsAsync<StagerTokenRedeemException>(
            () => h.Service.VerifyAsync("not-a-token", Now));
        await Assert.ThrowsAsync<StagerTokenRedeemException>(
            () => h.Service.VerifyAsync(h.Token.Secret, Now.AddHours(2)));

        // A spent token is refused by verify too: fetch and enroll share the
        // same remaining-uses budget.
        await h.Service.RedeemAsync(h.Token.Secret, Now.AddMinutes(1));
        await Assert.ThrowsAsync<StagerTokenRedeemException>(
            () => h.Service.VerifyAsync(h.Token.Secret, Now.AddMinutes(2)));
    }
}
