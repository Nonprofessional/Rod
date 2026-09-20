using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.ShellSessions;

namespace Rod.CoreState.Tests;

/// <summary>
/// Direct checks of the <see cref="ShellSession"/> aggregate and its
/// in-memory registry (architecture.md Sec 8, the shellcatch transport).
/// The entity is the whole lifecycle of an accepted no-protocol connection:
/// it opens Live with an Unknown fingerprint, accumulates input/output
/// stamps, ends exactly once (Lost when the peer vanishes, Closed when an
/// operator ends it), and takes a single irreversible upgrade binding.
/// Registry-level checks pin that mutations resolve the stored entity and
/// tolerate the unknown-ended races the socket's two halves can produce.
/// </summary>
public class ShellSessionTests
{
    private static readonly DateTimeOffset Opened = DateTimeOffset.UnixEpoch;

    private static ShellSession LiveSession()
        => ShellSession.Open(
            ShellSessionId.New(),
            EngagementId.New(),
            Guid.NewGuid(),
            "10.9.8.7:44441",
            Opened);

    [Fact]
    public void Open_StartsLiveWithUnknownFingerprint()
    {
        var session = LiveSession();

        Assert.Equal(ShellSessionStatus.Live, session.Status);
        Assert.Equal(ShellOsGuess.Unknown, session.Os);
        Assert.Null(session.LastInputAt);
        Assert.Null(session.LastOutputAt);
        Assert.Null(session.EndedAt);
    }

    [Fact]
    public void MarkFingerprint_SetsGuessOnLiveSession()
    {
        var session = LiveSession();

        session.MarkFingerprint(ShellOsGuess.UnixShell);

        Assert.Equal(ShellOsGuess.UnixShell, session.Os);
    }

    [Fact]
    public void MarkFingerprint_IgnoresWeakerGuessAfterConcreteOne()
    {
        var session = LiveSession();
        session.MarkFingerprint(ShellOsGuess.WindowsPowerShell);

        session.MarkFingerprint(ShellOsGuess.Unknown);

        Assert.Equal(ShellOsGuess.WindowsPowerShell, session.Os);
    }

    [Fact]
    public void MarkLost_EndsSessionOnce_UnderRaceDuplicatesAreNoOps()
    {
        var session = LiveSession();
        var lostAt = Opened.AddMinutes(2);

        var first = session.MarkLost(lostAt);
        var second = session.MarkLost(lostAt.AddSeconds(1));
        var closed = session.Close(lostAt.AddSeconds(2));

        Assert.True(first);
        Assert.False(second);
        Assert.False(closed);
        Assert.Equal(ShellSessionStatus.Lost, session.Status);
        Assert.Equal(lostAt, session.EndedAt);
    }

    [Fact]
    public void Close_EndsSessionOnce_LostAfterCloseIsANoOp()
    {
        var session = LiveSession();
        var closedAt = Opened.AddMinutes(1);

        var closed = session.Close(closedAt);
        var lost = session.MarkLost(closedAt.AddSeconds(1));

        Assert.True(closed);
        Assert.False(lost);
        Assert.Equal(ShellSessionStatus.Closed, session.Status);
    }

    [Fact]
    public void NoteStamps_StopAccumulatingAfterEnd()
    {
        var session = LiveSession();
        session.NoteInput(Opened.AddSeconds(1));
        session.NoteOutput(Opened.AddSeconds(2));
        session.MarkLost(Opened.AddSeconds(3));

        session.NoteInput(Opened.AddSeconds(4));
        session.NoteOutput(Opened.AddSeconds(5));

        Assert.Equal(Opened.AddSeconds(1), session.LastInputAt);
        Assert.Equal(Opened.AddSeconds(2), session.LastOutputAt);
    }

    [Fact]
    public async Task Registry_MutationsResolveStoredEntity_AndUnknownIdsAreNoOps()
    {
        var registry = new InMemoryShellSessionRegistry();
        var engagement = EngagementId.New();
        var listenerId = Guid.NewGuid();

        var unknown = ShellSessionId.New();
        await registry.MarkFingerprintAsync(unknown, ShellOsGuess.UnixShell);
        await registry.NoteInputAsync(unknown, Opened);
        await registry.NoteOutputAsync(unknown, Opened);
        await registry.MarkLostAsync(unknown, Opened);
        await registry.CloseAsync(unknown, Opened);
        Assert.Null(await registry.FindAsync(unknown));

        var session = await registry.OpenAsync(engagement, listenerId, "10.9.8.7:44441", Opened);
        await registry.MarkFingerprintAsync(session.Id, ShellOsGuess.WindowsCmd);
        await registry.NoteOutputAsync(session.Id, Opened.AddSeconds(1));
        await registry.CloseAsync(session.Id, Opened.AddSeconds(2));

        var stored = await registry.FindAsync(session.Id);
        Assert.NotNull(stored);
        Assert.Equal(ShellOsGuess.WindowsCmd, stored!.Os);
        Assert.Equal(ShellSessionStatus.Closed, stored.Status);
        Assert.Equal(Opened.AddSeconds(1), stored.LastOutputAt);
    }

    [Fact]
    public async Task Registry_ListsByEngagement_OldestFirst_LiveAndEndedAlike()
    {
        var registry = new InMemoryShellSessionRegistry();
        var engagement = EngagementId.New();
        var other = EngagementId.New();
        var listenerId = Guid.NewGuid();

        var first = await registry.OpenAsync(engagement, listenerId, "10.0.0.1:1", Opened);
        await registry.OpenAsync(other, listenerId, "10.0.0.2:2", Opened.AddSeconds(1));
        var second = await registry.OpenAsync(engagement, listenerId, "10.0.0.3:3", Opened.AddSeconds(2));
        await registry.MarkLostAsync(first.Id, Opened.AddSeconds(3));

        var listed = await registry.ListByEngagementAsync(engagement);

        Assert.Equal(new[] { first.Id, second.Id }, listed.Select(s => s.Id));
        Assert.Contains(listed, s => s.Status == ShellSessionStatus.Lost);
        Assert.Contains(listed, s => s.Status == ShellSessionStatus.Live);
    }
}
