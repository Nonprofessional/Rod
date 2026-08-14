using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Sessions;

namespace Rod.CoreState.Tests;

/// <summary>
/// Round-trip checks of <see cref="InMemorySessionRegistry"/> -- the
/// core-state layer lift. Sessions open on connect, close on disconnect, and
/// survive in the per-implant history; an implant holds at most one active
/// session so a reconnect closes the prior one; engagement scoping never leaks
/// another engagement's sessions (architecture.md Sec 3, Sec 4.1).
/// </summary>
public class SessionRegistryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static Implant NewImplant(EngagementId engagement)
    {
        var implant = Implant.Enroll(
            ImplantId.New(), engagement, "key-abc",
            Now.AddDays(30), ImplantClass.Stage2, Now);
        // The registry only reads implant.Id / implant.EngagementId, so the
        // entity never needs to be persisted for these tests.
        return implant;
    }

    [Fact]
    public async Task Open_ListActive_Close_RoundTrips()
    {
        var registry = new InMemorySessionRegistry();
        var engagement = EngagementId.New();
        var implant = NewImplant(engagement);

        var opened = await registry.OpenAsync(implant, new[] { "shell.exec" }, Now);

        // Active immediately, scoped to the engagement, by implant.
        Assert.Equal(SessionStatus.Active, opened.Status);
        Assert.Single(await registry.ListActiveAsync(engagement));
        Assert.NotNull(await registry.GetActiveAsync(implant.Id));

        await registry.CloseAsync(opened.Id, Now.AddMinutes(1));

        // No longer active; the per-implant history still shows it.
        Assert.Empty(await registry.ListActiveAsync(engagement));
        Assert.Null(await registry.GetActiveAsync(implant.Id));
        var history = await registry.ListByImplantAsync(implant.Id);
        var closed = Assert.Single(history);
        Assert.Equal(SessionStatus.Closed, closed.Status);
    }

    [Fact]
    public async Task Reconnect_ClosesPriorActiveSession()
    {
        var registry = new InMemorySessionRegistry();
        var engagement = EngagementId.New();
        var implant = NewImplant(engagement);

        var first = await registry.OpenAsync(implant, new[] { "shell.exec" }, Now);
        var second = await registry.OpenAsync(implant, new[] { "recon.portscan" }, Now.AddSeconds(10));

        // Only the new session is active; the prior one was closed by the reconnect.
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(second.Id, (await registry.GetActiveAsync(implant.Id))!.Id);
        Assert.Single(await registry.ListActiveAsync(engagement));

        // Both sessions are in the implant's connection history, oldest first.
        var history = await registry.ListByImplantAsync(implant.Id);
        Assert.Equal(new[] { first.Id, second.Id }, history.Select(s => s.Id).ToArray());
        Assert.Equal(SessionStatus.Closed, history[0].Status);
        Assert.Equal(SessionStatus.Active, history[1].Status);
    }

    [Fact]
    public async Task Touch_AdvancesActiveSession_LastSeenAndCapabilities()
    {
        var registry = new InMemorySessionRegistry();
        var implant = NewImplant(EngagementId.New());

        await registry.OpenAsync(implant, new[] { "shell.exec" }, Now);

        await registry.TouchAsync(implant.Id, new[] { "file.push" }, Now.AddSeconds(30));

        var active = await registry.GetActiveAsync(implant.Id);
        Assert.NotNull(active);
        Assert.Equal(new[] { "file.push" }, active!.Capabilities);
        Assert.Equal(Now.AddSeconds(30), active.LastSeenAt);
    }

    [Fact]
    public async Task Touch_IsNoOp_WhenImplantHasNoActiveSession()
    {
        var registry = new InMemorySessionRegistry();

        // Touching an implant with no session must not throw -- a stray keepalive
        // after close should be harmless.
        await registry.TouchAsync(ImplantId.New(), Array.Empty<string>(), Now);
    }

    [Fact]
    public async Task Close_IsIdempotent()
    {
        var registry = new InMemorySessionRegistry();
        var implant = NewImplant(EngagementId.New());
        var session = await registry.OpenAsync(implant, Array.Empty<string>(), Now);

        await registry.CloseAsync(session.Id, Now);
        await registry.CloseAsync(session.Id, Now.AddSeconds(1)); // second close is a no-op

        var found = await registry.FindAsync(session.Id);
        Assert.NotNull(found);
        Assert.Equal(SessionStatus.Closed, found!.Status);
    }

    [Fact]
    public async Task ListActive_StaysScopedByEngagement()
    {
        var registry = new InMemorySessionRegistry();
        var engagementA = EngagementId.New();
        var engagementB = EngagementId.New();

        await registry.OpenAsync(NewImplant(engagementA), Array.Empty<string>(), Now);
        await registry.OpenAsync(NewImplant(engagementB), Array.Empty<string>(), Now);

        var aOnly = await registry.ListActiveAsync(engagementA);
        Assert.Single(aOnly);
        Assert.All(aOnly, s => Assert.Equal(engagementA, s.EngagementId));
    }
}
