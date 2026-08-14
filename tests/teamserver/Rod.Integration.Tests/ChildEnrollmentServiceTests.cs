using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Pki;
using Rod.CoreState.Staging;

namespace Rod.Integration.Tests;

/// <summary>
/// Direct checks of <see cref="EnrollmentService"/>'s child-enrollment path
/// (architecture.md Sec 5.2, ) -- the acceptance point: a child
/// implant enrols from a parent within scope, with parentage linkage recorded.
/// Complements the top-level enroll checks in <see cref="EnrollmentServiceTests"/>
/// and the HTTP slice that follows. Drives the service against the in-memory ports
/// the way the composition root does, focusing on the parent resolution and
/// scope/liveness rules a child derivation requires.
/// </summary>
public class ChildEnrollmentServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private sealed class FakeClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    // Builds the service against the in-memory ports, mirroring
    // EnrollmentServiceTests.NewService. The engagements repo is shared with the
    // stager-token service so a minted token resolves to a real engagement.
    private static (EnrollmentService Service, IStagerTokenService Tokens, IEngagementRepository Engagements, IImplantRepository Implants) NewService(
        TimeProvider? clock = null)
    {
        var engagements = new InMemoryEngagementRepository();
        var tokens = new InMemoryStagerTokenService(engagements);
        var implants = new InMemoryImplantRepository();
        var ca = new DevCertificateAuthority();
        var service = new EnrollmentService(engagements, tokens, implants, ca, clock ?? new FakeClock(Now));
        return (service, tokens, engagements, implants);
    }

    // Mints a token for a fresh engagement and returns (secret, engagement). The
    // owner is a member of the engagement it mints for (required by the token
    // service).
    private static async Task<(string Secret, EngagementId Engagement)> MintTokenAsync(
        IEngagementRepository engagements, IStagerTokenService tokens)
    {
        var owner = OperatorId.New();
        var engagement = Engagement.Create(EngagementId.New(), "Op A", owner, Now);
        await engagements.SaveAsync(engagement);
        var minted = await tokens.MintAsync(engagement.Id, owner, Now);
        return (minted.Secret, engagement.Id);
    }

    // Enrolls a parent into an engagement directly through the registry so a child
    // enrollment has a real parent to derive from. Mirrors the helper the gating
    // tests use to seed an implant.
    private static async Task<Implant> EnrollParentAsync(
        IImplantRepository implants, EngagementId engagement)
    {
        var parent = Implant.Enroll(
            ImplantId.New(), engagement, "key-parent", Now.AddDays(30), ImplantClass.Stage2, Now);
        await implants.SaveAsync(parent);
        return parent;
    }

    [Fact]
    public async Task EnrolChild_RecordsTheParent_AndBindsTheSameEngagement()
    {
        // The  acceptance point: a child enrols from a parent within scope,
        // with parentage recorded. The child's engagement is the redeemed token's
        // engagement (which equals the parent's), and the result carries the parent.
        var (service, tokens, engagements, implants) = NewService();
        var (secret, engagement) = await MintTokenAsync(engagements, tokens);
        var parent = await EnrollParentAsync(implants, engagement);

        var result = await service.EnrollAsync(new EnrollCommand(secret, ParentImplantId: parent.Id));

        Assert.Equal(engagement, result.EngagementId);
        Assert.Equal(parent.Id, result.ParentImplantId);
        // The recorded implant carries the parent linkage.
        var child = await implants.FindAsync(result.ImplantId);
        Assert.NotNull(child);
        Assert.Equal(parent.Id, child!.ParentImplantId);
        Assert.Equal(engagement, child.EngagementId);
    }

    [Fact]
    public async Task EnrolChild_RefusesUnknownParent()
    {
        // A child must derive from a real parent; an unknown parent id refuses the
        // enroll before the child is recorded.
        var (service, tokens, engagements, implants) = NewService();
        var (secret, engagement) = await MintTokenAsync(engagements, tokens);
        var bogusParent = ImplantId.New();

        var ex = await Assert.ThrowsAsync<InvalidParentImplantException>(
            () => service.EnrollAsync(new EnrollCommand(secret, ParentImplantId: bogusParent)));

        Assert.Equal(InvalidParentImplantReason.Unknown, ex.Reason);
        // Nothing was enrolled.
        Assert.Empty(await implants.ListByEngagementAsync(engagement));
    }

    [Fact]
    public async Task EnrolChild_RefusesForeignEngagementParent()
    {
        // The child enrols into the same engagement as its parent
        // (architecture.md Sec 3). A parent in another engagement is refused --
        // the child cannot be grafted across the engagement boundary.
        var (service, tokens, engagements, implants) = NewService();
        var (secret, _) = await MintTokenAsync(engagements, tokens);
        // A parent enrolled into a *different* engagement.
        var foreignParent = await EnrollParentAsync(implants, EngagementId.New());

        var ex = await Assert.ThrowsAsync<InvalidParentImplantException>(
            () => service.EnrollAsync(new EnrollCommand(secret, ParentImplantId: foreignParent.Id)));

        Assert.Equal(InvalidParentImplantReason.EngagementMismatch, ex.Reason);
    }

    [Fact]
    public async Task EnrolChild_RefusesRetiredParent()
    {
        // A retired implant is out of operation (architecture.md Sec 7, ) and
        // cannot derive children; the enroll is refused before the child is
        // recorded.
        var (service, tokens, engagements, implants) = NewService();
        var (secret, engagement) = await MintTokenAsync(engagements, tokens);
        var parent = await EnrollParentAsync(implants, engagement);
        parent.Retire(Now.AddSeconds(1));
        await implants.SaveAsync(parent);

        var ex = await Assert.ThrowsAsync<InvalidParentImplantException>(
            () => service.EnrollAsync(new EnrollCommand(secret, ParentImplantId: parent.Id)));

        Assert.Equal(InvalidParentImplantReason.Retired, ex.Reason);
    }

    [Fact]
    public async Task Enroll_WithoutParent_RecordsNullParent()
    {
        // A top-level enroll (no parent) still records a null parent, and the
        // result's ParentImplantId reflects that. Guards a regression that silently
        // defaulted the parent to something non-null.
        var (service, tokens, engagements, _) = NewService();
        var (secret, _) = await MintTokenAsync(engagements, tokens);

        var result = await service.EnrollAsync(new EnrollCommand(secret));

        Assert.Null(result.ParentImplantId);
    }
}
