using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;
using Xunit;

namespace Rod.Integration.Tests;

/// <summary>
/// Direct checks of the <see cref="Engagement"/> aggregate invariants
/// (architecture.md Sec 3), complementing the HTTP slice in
/// <see cref="EngagementHttpTests"/>. The engagement always has exactly one
/// owner: creation seeds it, an extra owner is refused, and the owner cannot be
/// removed or demoted.
/// </summary>
public class EngagementDomainTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Fact]
    public void Create_RegistersOwner_AsTheSingleOwnerMember()
    {
        var owner = OperatorId.New();

        var engagement = Engagement.Create(EngagementId.New(), "Op A", owner, Now);

        var membership = Assert.Single(engagement.Members);
        Assert.Equal(owner, membership.OperatorId);
        Assert.Equal(Role.Owner, membership.Role);
        Assert.Equal(owner, engagement.OwnerId);
    }

    [Fact]
    public void AddMember_RefusesOwnerRole()
    {
        var owner = OperatorId.New();
        var engagement = Engagement.Create(EngagementId.New(), "Op A", owner, Now);

        Assert.Throws<EngagementDomainException>(
            () => engagement.AddMember(OperatorId.New(), Role.Owner, Now));
    }

    [Fact]
    public void AddMember_RefusesTheOwner_WhoIsAlreadyPresent()
    {
        var owner = OperatorId.New();
        var engagement = Engagement.Create(EngagementId.New(), "Op A", owner, Now);

        Assert.Throws<EngagementDomainException>(
            () => engagement.AddMember(owner, Role.Observer, Now));
    }

    [Fact]
    public void AddMember_RefusesDuplicateOperator()
    {
        var owner = OperatorId.New();
        var other = OperatorId.New();
        var engagement = Engagement.Create(EngagementId.New(), "Op A", owner, Now);
        engagement.AddMember(other, Role.Observer, Now);

        Assert.Throws<EngagementDomainException>(
            () => engagement.AddMember(other, Role.Operator, Now));
    }

    [Fact]
    public void RemoveMember_RefusesToRemoveTheOwner()
    {
        var owner = OperatorId.New();
        var engagement = Engagement.Create(EngagementId.New(), "Op A", owner, Now);

        Assert.Throws<EngagementDomainException>(() => engagement.RemoveMember(owner));
    }

    [Fact]
    public void ChangeMemberRole_RefusesToChangeTheOwner()
    {
        var owner = OperatorId.New();
        var engagement = Engagement.Create(EngagementId.New(), "Op A", owner, Now);

        Assert.Throws<EngagementDomainException>(
            () => engagement.ChangeMemberRole(owner, Role.Operator, Now));
    }

    [Fact]
    public void ChangeMemberRole_RefusesToPromoteToOwner()
    {
        var owner = OperatorId.New();
        var other = OperatorId.New();
        var engagement = Engagement.Create(EngagementId.New(), "Op A", owner, Now);
        engagement.AddMember(other, Role.Observer, Now);

        Assert.Throws<EngagementDomainException>(
            () => engagement.ChangeMemberRole(other, Role.Owner, Now));
    }

    [Fact]
    public void Create_RejectsBlankName()
    {
        Assert.Throws<ArgumentException>(
            () => Engagement.Create(EngagementId.New(), "  ", OperatorId.New(), Now));
    }

    [Fact]
    public void Operator_Register_RejectsBlankHandle()
    {
        Assert.Throws<ArgumentException>(
            () => Operator.Register(OperatorId.New(), "", "Display", Now));
    }
}
