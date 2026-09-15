using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Pki;
using Rod.CoreState.ShellSessions;
using Rod.CoreState.Staging;

namespace Rod.Integration.Tests;

/// <summary>
/// Direct checks of the upgrade lineage (architecture.md Sec 8): a stager
/// token minted by a caught shell's upgrade render carries the session it
/// belongs to, and the enrollment that redeems it closes the loop both
/// ways -- the implant's origin points at the shell, the shell's record
/// points at the implant. An ordinary mint carries no origin and binds
/// nothing, and an origin from a foreign engagement is refused silently
/// (provenance never crosses engagements).
/// </summary>
public class ShellUpgradeLineageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task Enroll_BindsTheLineageBothWays_WhenTheTokenCarriesAnOrigin()
    {
        var engagements = new InMemoryEngagementRepository();
        var tokens = new InMemoryStagerTokenService(engagements);
        var implants = new InMemoryImplantRepository();
        var shells = new InMemoryShellSessionRegistry();
        var service = new EnrollmentService(
            engagements, tokens, implants, new DevCertificateAuthority(), new FakeClock(Now), shells);

        var owner = OperatorId.New();
        var engagement = Engagement.Create(EngagementId.New(), "Op Lineage", owner, Now);
        await engagements.SaveAsync(engagement);

        var shell = await shells.OpenAsync(engagement.Id, Guid.NewGuid(), "10.9.8.7:44441", Now);
        var minted = await tokens.MintAsync(
            engagement.Id, owner, Now, originShellSession: shell.Id);

        var enrolled = await service.EnrollAsync(new EnrollCommand(minted.Secret));

        // Both ends of the lineage point at each other.
        var stored = await implants.FindAsync(enrolled.ImplantId);
        Assert.NotNull(stored);
        Assert.Equal(shell.Id, stored!.OriginShellSessionId);

        var bound = await shells.FindAsync(shell.Id);
        Assert.NotNull(bound);
        Assert.Equal(enrolled.ImplantId, bound!.UpgradedImplantId);
    }

    [Fact]
    public async Task Enroll_BindsNothing_WhenTheMintCarriedNoOrigin()
    {
        var engagements = new InMemoryEngagementRepository();
        var tokens = new InMemoryStagerTokenService(engagements);
        var implants = new InMemoryImplantRepository();
        var shells = new InMemoryShellSessionRegistry();
        var service = new EnrollmentService(
            engagements, tokens, implants, new DevCertificateAuthority(), new FakeClock(Now), shells);

        var owner = OperatorId.New();
        var engagement = Engagement.Create(EngagementId.New(), "Op Plain", owner, Now);
        await engagements.SaveAsync(engagement);
        var shell = await shells.OpenAsync(engagement.Id, Guid.NewGuid(), "10.9.8.7:44442", Now);
        var minted = await tokens.MintAsync(engagement.Id, owner, Now);

        var enrolled = await service.EnrollAsync(new EnrollCommand(minted.Secret));

        var stored = await implants.FindAsync(enrolled.ImplantId);
        Assert.Null(stored!.OriginShellSessionId);
        Assert.Null((await shells.FindAsync(shell.Id))!.UpgradedImplantId);
    }

    [Fact]
    public async Task Enroll_RefusesAForeignOrigin_Silently()
    {
        var engagements = new InMemoryEngagementRepository();
        var tokens = new InMemoryStagerTokenService(engagements);
        var implants = new InMemoryImplantRepository();
        var shells = new InMemoryShellSessionRegistry();
        var service = new EnrollmentService(
            engagements, tokens, implants, new DevCertificateAuthority(), new FakeClock(Now), shells);

        var owner = OperatorId.New();
        var engagement = Engagement.Create(EngagementId.New(), "Op Home", owner, Now);
        await engagements.SaveAsync(engagement);
        var foreign = Engagement.Create(EngagementId.New(), "Op Foreign", owner, Now);
        await engagements.SaveAsync(foreign);

        // The shell belongs to the foreign engagement; the token redeems into
        // the home one. Provenance never crosses engagements, so the bind is
        // refused and the enrollment itself stands.
        var shell = await shells.OpenAsync(foreign.Id, Guid.NewGuid(), "10.9.8.7:44443", Now);
        var minted = await tokens.MintAsync(
            engagement.Id, owner, Now, originShellSession: shell.Id);

        var enrolled = await service.EnrollAsync(new EnrollCommand(minted.Secret));

        var stored = await implants.FindAsync(enrolled.ImplantId);
        Assert.Null(stored!.OriginShellSessionId);
        Assert.Null((await shells.FindAsync(shell.Id))!.UpgradedImplantId);
    }
}
