using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Implants;
using Rod.CoreState.Sessions;

namespace Rod.Integration.Tests;

/// <summary>
/// Direct checks of <see cref="HandshakeService"/> -- the use case that opens an
/// implant's session (, lifted to sessions in ). Without spinning
/// up TLS: the service refuses an unknown implant, an unsupported protocol
/// version, and a certificate-vs-engagement mismatch, and on success it opens a
/// session for the implant in its engagement with the advertised capabilities.
///
/// The mTLS identity check is parameterized on the certificate engagement id, so
/// these cases exercise it deterministically; the end-to-end mTLS handshake is
/// covered separately in <see cref="HandshakePresenceTests"/>.
/// </summary>
public class HandshakeServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static HandshakeService NewService(IImplantRepository? implants = null)
    {
        implants ??= new InMemoryImplantRepository();
        var sessions = new InMemorySessionRegistry();
        var clock = new FakeClock(Now);
        return new HandshakeService(implants, sessions, clock);
    }

    private static async Task<Implant> EnrollAsync(
        IImplantRepository implants,
        EngagementId? engagement = null,
        DateTimeOffset? createdAt = null)
    {
        var resolvedEngagement = engagement ?? EngagementId.New();
        var resolvedAt = createdAt ?? Now;
        var implant = Implant.Enroll(
            ImplantId.New(), resolvedEngagement, "key-abc",
            resolvedAt.AddDays(30), ImplantClass.Stage2, resolvedAt);
        await implants.SaveAsync(implant);
        return implant;
    }

    [Fact]
    public async Task Handshake_OpensSession_WhenIdentityMatches()
    {
        var implants = new InMemoryImplantRepository();
        var sessions = new InMemorySessionRegistry();
        var service = new HandshakeService(implants, sessions, new FakeClock(Now));

        var implant = await EnrollAsync(implants);

        var result = await service.HandshakeAsync(new HandshakeCommand(
            implant.Id, ProtocolVersions.Major, ProtocolVersions.Minor,
            new[] { "shell.exec" }, implant.EngagementId));

        Assert.Equal(implant.Id, result.ImplantId);
        Assert.Equal(implant.EngagementId, result.EngagementId);

        // Session opened, active for the implant, scoped to the engagement, with
        // advertised caps.
        var session = await sessions.GetActiveAsync(implant.Id);
        Assert.NotNull(session);
        Assert.Equal(result.SessionId, session!.Id);
        Assert.Equal(implant.EngagementId, session.EngagementId);
        Assert.Equal(new[] { "shell.exec" }, session.Capabilities);
        Assert.Equal(Now, session.LastSeenAt);

        var active = await sessions.ListActiveAsync(implant.EngagementId);
        Assert.Single(active, s => s.ImplantId == implant.Id);
    }

    [Fact]
    public async Task Handshake_RefusesUnknownImplant()
    {
        var service = NewService();

        var ex = await Assert.ThrowsAsync<HandshakeException>(() => service.HandshakeAsync(
            new HandshakeCommand(ImplantId.New(), 1, 0, Array.Empty<string>(), EngagementId.New())));

        Assert.Equal(HandshakeReason.UnknownImplant, ex.Reason);
    }

    [Fact]
    public async Task Handshake_RefusesVersionMismatch()
    {
        var implants = new InMemoryImplantRepository();
        var service = NewService(implants);
        var implant = await EnrollAsync(implants);

        // A future major version is incompatible.
        var ex = await Assert.ThrowsAsync<HandshakeException>(() => service.HandshakeAsync(
            new HandshakeCommand(implant.Id, 2, 0, Array.Empty<string>(), implant.EngagementId)));
        Assert.Equal(HandshakeReason.VersionMismatch, ex.Reason);
    }

    [Fact]
    public async Task Handshake_RefusesIdentityMismatch()
    {
        var implants = new InMemoryImplantRepository();
        var service = NewService(implants);
        var implant = await EnrollAsync(implants);

        // The certificate claims a different engagement than the implant is
        // enrolled in -- the mTLS identity check (architecture.md Sec 9).
        var ex = await Assert.ThrowsAsync<HandshakeException>(() => service.HandshakeAsync(
            new HandshakeCommand(implant.Id, 1, 0, Array.Empty<string>(), EngagementId.New())));
        Assert.Equal(HandshakeReason.IdentityMismatch, ex.Reason);
    }

    [Fact]
    public async Task Handshake_RefusesMissingCertificateBinding()
    {
        var implants = new InMemoryImplantRepository();
        var service = NewService(implants);
        var implant = await EnrollAsync(implants);

        // No certificate binding at all (null) is also an identity mismatch.
        var ex = await Assert.ThrowsAsync<HandshakeException>(() => service.HandshakeAsync(
            new HandshakeCommand(implant.Id, 1, 0, Array.Empty<string>(), CertificateEngagementId: null)));
        Assert.Equal(HandshakeReason.IdentityMismatch, ex.Reason);
    }

    [Fact]
    public async Task Handshake_RefusesExpiredKillDate()
    {
        var implants = new InMemoryImplantRepository();
        var engagement = EngagementId.New();

        // An implant whose baked-in kill date is in the past (architecture.md
        // Sec 7). Enroll it with a short window, then advance the wall clock past
        // it so the handshake's kill-date gate fires. The identity check passes
        // (matching engagement) so the refusal is specifically the kill date.
        var killDate = Now.AddSeconds(30);
        var implant = Implant.Enroll(
            ImplantId.New(), engagement, "key-abc", killDate, ImplantClass.Stage2, Now);
        await implants.SaveAsync(implant);

        var sessions = new InMemorySessionRegistry();
        var service = new HandshakeService(implants, sessions, new FakeClock(killDate.AddSeconds(1)));

        var ex = await Assert.ThrowsAsync<HandshakeException>(() => service.HandshakeAsync(
            new HandshakeCommand(implant.Id, 1, 0, Array.Empty<string>(), engagement)));
        Assert.Equal(HandshakeReason.KillDateExpired, ex.Reason);

        // No session was opened for the expired implant.
        Assert.Null(await sessions.GetActiveAsync(implant.Id));
    }

    [Fact]
    public async Task Handshake_OpensSession_WhenKillDateIsInFuture()
    {
        var implants = new InMemoryImplantRepository();
        var sessions = new InMemorySessionRegistry();
        var engagement = EngagementId.New();

        // The kill date is in the future at handshake time, so the gate passes
        // and a session opens normally -- the negative case for the check above.
        var implant = Implant.Enroll(
            ImplantId.New(), engagement, "key-abc", Now.AddDays(30), ImplantClass.Stage2, Now);
        await implants.SaveAsync(implant);

        var service = new HandshakeService(implants, sessions, new FakeClock(Now));

        var result = await service.HandshakeAsync(
            new HandshakeCommand(implant.Id, 1, 0, new[] { "shell.exec" }, engagement));

        Assert.Equal(implant.Id, result.ImplantId);
        Assert.NotNull(await sessions.GetActiveAsync(implant.Id));
    }

    [Fact]
    public async Task Handshake_RefusesRetiredImplant()
    {
        var implants = new InMemoryImplantRepository();
        var sessions = new InMemorySessionRegistry();
        var engagement = EngagementId.New();

        // A retired implant (architecture.md Sec 7). The kill date is in
        // the future and the engagement matches, so the refusal is specifically
        // the retirement -- a retired implant never gets a session again.
        var implant = Implant.Enroll(
            ImplantId.New(), engagement, "key-abc", Now.AddDays(30), ImplantClass.Stage2, Now);
        implant.Retire(Now);
        await implants.SaveAsync(implant);

        var service = new HandshakeService(implants, sessions, new FakeClock(Now));

        var ex = await Assert.ThrowsAsync<HandshakeException>(() => service.HandshakeAsync(
            new HandshakeCommand(implant.Id, 1, 0, Array.Empty<string>(), engagement)));
        Assert.Equal(HandshakeReason.ImplantRetired, ex.Reason);

        // No session was opened for the retired implant.
        Assert.Null(await sessions.GetActiveAsync(implant.Id));
    }

    private sealed class FakeClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
