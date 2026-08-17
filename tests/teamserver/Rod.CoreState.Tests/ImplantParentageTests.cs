using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;

namespace Rod.CoreState.Tests;

/// <summary>
/// Direct checks of the <see cref="Implant"/> parentage model (architecture.md
/// Sec 5.2). A top-level implant enrolled from a stager token has
/// no parent; a child derived from a parent records the parent's id. The shared
/// key/kill-date validation applies to both factories. The engagement-scope
/// check (parent and child in the same engagement) lives in the enrollment use
/// case, not the entity -- this is the entity-level invariant check.
/// </summary>
public class ImplantParentageTests
{
    private static readonly DateTimeOffset Created = DateTimeOffset.UnixEpoch;
    private static readonly DateTimeOffset KillDate = Created.AddDays(30);

    [Fact]
    public void Enroll_RecordsNoParent()
    {
        // A top-level implant (enrolled from a stager token) has no parent.
        var implant = Implant.Enroll(
            ImplantId.New(), EngagementId.New(), KillDate, ImplantClass.Stage2, Created);

        Assert.Null(implant.ParentImplantId);
    }

    [Fact]
    public void EnrollChild_RecordsTheParent()
    {
        // A child derived from a parent records the parent's id verbatim.
        var parent = ImplantId.New();
        var child = Implant.EnrollChild(
            ImplantId.New(), EngagementId.New(), KillDate, ImplantClass.Stage2, Created, parentImplantId: parent);

        Assert.Equal(parent, child.ParentImplantId);
    }

    [Fact]
    public void EnrollChild_WithNullParent_MatchesEnroll()
    {
        // Enroll delegates to EnrollChild with a null parent, so the two produce
        // an equivalent parentage -- a caller driving child enrollment with no
        // parent lands the same shape as a top-level enroll.
        var id = ImplantId.New();
        var engagement = EngagementId.New();

        var topLevel = Implant.Enroll(id, engagement, KillDate, ImplantClass.Stage2, Created);
        var asChild = Implant.EnrollChild(id, engagement, KillDate, ImplantClass.Stage2, Created, parentImplantId: null);

        Assert.Null(asChild.ParentImplantId);
        Assert.Equal(topLevel.ParentImplantId, asChild.ParentImplantId);
    }

    [Fact]
    public void EnrollChild_RejectsKillDateAtOrBeforeCreation()
    {
        Assert.Throws<ArgumentException>(
            () => Implant.EnrollChild(
                ImplantId.New(), EngagementId.New(), Created, ImplantClass.Stage2, Created, parentImplantId: ImplantId.New()));
        Assert.Throws<ArgumentException>(
            () => Implant.EnrollChild(
                ImplantId.New(), EngagementId.New(), Created.AddSeconds(-1), ImplantClass.Stage2, Created, parentImplantId: ImplantId.New()));
    }

    [Fact]
    public void EnrollChild_RejectsDefaultParentId()
    {
        // A default (Guid.Empty) parent id is never a real implant; recording it
        // would silently lose the linkage, so the factory refuses it. A null
        // parent stays valid (the top-level shape).
        Assert.Throws<ArgumentException>(
            () => Implant.EnrollChild(
                ImplantId.New(), EngagementId.New(), KillDate, ImplantClass.Stage2, Created, parentImplantId: default(ImplantId)));
    }
}
