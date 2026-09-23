using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;
using Rod.CoreState.Deployment;
using Task = System.Threading.Tasks.Task;

namespace Rod.CoreState.Tests;

/// <summary>
/// The batch-scoped mint: one token, several uses, a chosen window. Each
/// enroll spends one use; the token refuses the redeem past its last use and
/// anything past its expiry, verify never consumes, and a mint that names no
/// scope keeps the single-use, one-hour default.
/// </summary>
public class DeployTokenMultiUseTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private sealed record Harness(InMemoryDeployTokenService Service, EngagementId EngagementId, OperatorId Owner);

    private static async Task<Harness> HarnessAsync()
    {
        var engagementId = EngagementId.New();
        var owner = OperatorId.New();
        var engagements = new InMemoryEngagementRepository();
        await engagements.SaveAsync(Engagement.Create(engagementId, "multi-use-test", owner, Now));
        return new Harness(new InMemoryDeployTokenService(engagements), engagementId, owner);
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

        // The over-spend reads Spent, not Unknown: the spent row stays
        // resolvable so the refusal carries its attribution -- both stores
        // now share the one contract.
        var ex = await Assert.ThrowsAsync<DeployTokenRedeemException>(
            () => h.Service.RedeemAsync(token.Secret, Now.AddMinutes(3)));
        Assert.Equal(DeployTokenRedeemReason.Spent, ex.Reason);
        Assert.Equal(h.EngagementId, ex.EngagementId);
    }

    [Fact]
    public async Task Verify_Never_Consumes_A_Batch_Token()
    {
        var h = await HarnessAsync();
        var token = await h.Service.MintAsync(h.EngagementId, h.Owner, Now, maxUses: 1, lifetime: TimeSpan.FromHours(1));

        // The pre-enrollment read a fetch performs leaves the batch
        // credential whole for the implant's enroll, exactly as for a
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

        var ex = await Assert.ThrowsAsync<DeployTokenRedeemException>(
            () => h.Service.RedeemAsync(token.Secret, Now.AddMinutes(31)));
        Assert.Equal(DeployTokenRedeemReason.Expired, ex.Reason);
    }

    [Fact]
    public async Task Mint_Without_Scope_Keeps_The_Defaults()
    {
        var h = await HarnessAsync();
        var token = await h.Service.MintAsync(h.EngagementId, h.Owner, Now);

        Assert.Equal(1, token.MaxUses);
        Assert.Equal(Now.AddHours(1), token.ExpiresAt);
    }

    [Fact]
    public async Task Zero_MaxUses_Is_Unlimited_And_Never_Spends_Down()
    {
        var h = await HarnessAsync();
        var token = await h.Service.MintAsync(h.EngagementId, h.Owner, Now, maxUses: 0, lifetime: TimeSpan.FromHours(4));

        // The unlimited budget: every redeem succeeds, the state stays 0/0
        // ("not counted", never "spent"), and only the window or a revoke can
        // stop it.
        for (var i = 1; i <= 3; i++)
        {
            var redeemed = await h.Service.RedeemAsync(token.Secret, Now.AddMinutes(i));
            Assert.Equal(h.EngagementId, redeemed.EngagementId);
        }

        var state = await h.Service.FindAsync(token.Id);
        Assert.NotNull(state);
        Assert.Equal(0, state!.MaxUses);
        Assert.Equal(0, state.RemainingUses);

        // Verify reads the same way -- unlimited is not the spent shape.
        _ = await h.Service.VerifyAsync(token.Secret, Now.AddMinutes(4));

        // Past the window the credential still dies: unlimited counts uses,
        // not time.
        var ex = await Assert.ThrowsAsync<DeployTokenRedeemException>(
            () => h.Service.RedeemAsync(token.Secret, Now.AddHours(5)));
        Assert.Equal(DeployTokenRedeemReason.Expired, ex.Reason);
    }

    [Fact]
    public async Task State_Reads_The_Budget_By_Id_And_KeepsTheSpentPageAtZero()
    {
        var h = await HarnessAsync();
        var token = await h.Service.MintAsync(h.EngagementId, h.Owner, Now, maxUses: 3, lifetime: TimeSpan.FromHours(2));

        // The inspectable state is the ledger side of the credential: the
        // budget and the window, never the secret.
        var fresh = await h.Service.FindAsync(token.Id);
        Assert.NotNull(fresh);
        Assert.Equal(h.EngagementId, fresh!.EngagementId);
        Assert.Equal(h.Owner, fresh.IssuedBy);
        Assert.Equal(Now, fresh.IssuedAt);
        Assert.Equal(Now.AddHours(2), fresh.ExpiresAt);
        Assert.Equal(3, fresh.MaxUses);
        Assert.Equal(3, fresh.RemainingUses);

        // Each redeem moves the remaining count the next read reports.
        _ = await h.Service.RedeemAsync(token.Secret, Now.AddMinutes(1));
        var partlySpent = await h.Service.FindAsync(token.Id);
        Assert.NotNull(partlySpent);
        Assert.Equal(3, partlySpent!.MaxUses);
        Assert.Equal(2, partlySpent.RemainingUses);

        // The spent credential stays readable at zero remaining uses -- the
        // budget's ledger keeps its last page, and the payload library
        // renders zero as "no enrollments left." Only revocation reads gone.
        _ = await h.Service.RedeemAsync(token.Secret, Now.AddMinutes(2));
        _ = await h.Service.RedeemAsync(token.Secret, Now.AddMinutes(3));
        var spentState = await h.Service.FindAsync(token.Id);
        Assert.NotNull(spentState);
        Assert.Equal(0, spentState!.RemainingUses);

        Assert.Null(await h.Service.FindAsync(DeployTokenId.New()));
    }
}
