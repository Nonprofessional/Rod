using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Sessions;

namespace Rod.CoreState.Tests;

/// <summary>
/// Direct checks of the <see cref="Session"/> aggregate invariants
/// (architecture.md Sec 4.1, Sec 10.3) -- the M2.1 core-state layer lift. A
/// session opens Active, binds to its implant and engagement, carries the
/// advertised capabilities, advances its last-seen time on touch, and can only
/// be closed from Active.
/// </summary>
public class SessionAggregateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static Session Open(SessionId? id = null, DateTimeOffset? at = null)
        => Session.Open(
            id ?? SessionId.New(),
            ImplantId.New(),
            EngagementId.New(),
            new[] { "shell.exec" },
            at ?? Now);

    [Fact]
    public void Open_RecordsAllFields_AsActive()
    {
        var id = SessionId.New();
        var implant = ImplantId.New();
        var engagement = EngagementId.New();
        var caps = new[] { "shell.exec", "file.push" };

        var session = Session.Open(id, implant, engagement, caps, Now);

        Assert.Equal(id, session.Id);
        Assert.Equal(implant, session.ImplantId);
        Assert.Equal(engagement, session.EngagementId);
        Assert.Equal(caps, session.Capabilities);
        Assert.Equal(Now, session.StartedAt);
        Assert.Equal(Now, session.LastSeenAt);
        Assert.Null(session.EndedAt);
        Assert.Equal(SessionStatus.Active, session.Status);
    }

    [Fact]
    public void Open_AcceptsEmptyCapabilities()
    {
        var session = Session.Open(SessionId.New(), ImplantId.New(), EngagementId.New(), Array.Empty<string>(), Now);

        Assert.Empty(session.Capabilities);
    }

    [Fact]
    public void Touch_AdvancesLastSeen_AndRefreshesCapabilities()
    {
        var session = Open(at: Now);

        session.Touch(new[] { "recon.portscan" }, Now.AddSeconds(5));

        Assert.Equal(new[] { "recon.portscan" }, session.Capabilities);
        Assert.Equal(Now.AddSeconds(5), session.LastSeenAt);
        Assert.Equal(SessionStatus.Active, session.Status);
    }

    [Fact]
    public void Close_MarksSessionClosed_WithEndedAt()
    {
        var session = Open(at: Now);
        var endedAt = Now.AddMinutes(1);

        session.Close(endedAt);

        Assert.Equal(SessionStatus.Closed, session.Status);
        Assert.Equal(endedAt, session.EndedAt);
    }

    [Fact]
    public void Close_RefusesFromClosed()
    {
        var session = Open();
        session.Close(Now);

        Assert.Throws<InvalidOperationException>(() => session.Close(Now.AddSeconds(1)));
    }

    [Fact]
    public void Touch_RefusesFromClosed()
    {
        var session = Open();
        session.Close(Now);

        Assert.Throws<InvalidOperationException>(() => session.Touch(Array.Empty<string>(), Now.AddSeconds(1)));
    }
}
