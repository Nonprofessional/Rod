using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;
using Xunit;

namespace Rod.Integration.Tests;

/// <summary>
/// Direct checks of the <see cref="Engagement"/> aggregate (architecture.md
/// Sec 3), complementing the HTTP slice in <see cref="EngagementHttpTests"/>.
/// An engagement records the operator who created it as its owner for
/// accountability; access is not role-gated.
/// </summary>
public class EngagementDomainTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Fact]
    public void Create_RecordsOwnerAndName()
    {
        var owner = OperatorId.New();

        var engagement = Engagement.Create(EngagementId.New(), "Op A", owner, Now);

        Assert.Equal(owner, engagement.OwnerId);
        Assert.Equal("Op A", engagement.Name);
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
