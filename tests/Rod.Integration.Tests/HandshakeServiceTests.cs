using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Implants;
using Rod.CoreState.Presence;

namespace Rod.Integration.Tests;

/// <summary>
/// Direct checks of <see cref="HandshakeService"/> -- the use case that gates an
/// implant's presence (roadmap M1.3). Without spinning up TLS: the service
/// refuses an unknown implant, an unsupported protocol version, and a
/// certificate-vs-engagement mismatch, and on success it records the implant
/// online in its engagement with the advertised capabilities.
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
        var presence = new InMemoryPresenceRegistry();
        var clock = new FakeClock(Now);
        return new HandshakeService(implants, presence, clock);
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
    public async Task Handshake_RecordsOnline_WhenIdentityMatches()
    {
        var implants = new InMemoryImplantRepository();
        var presence = new InMemoryPresenceRegistry();
        var service = new HandshakeService(implants, presence, new FakeClock(Now));

        var implant = await EnrollAsync(implants);

        var result = await service.HandshakeAsync(new HandshakeCommand(
            implant.Id, ProtocolVersions.Major, ProtocolVersions.Minor,
            new[] { "shell.exec" }, implant.EngagementId));

        Assert.Equal(implant.Id, result.ImplantId);
        Assert.Equal(implant.EngagementId, result.EngagementId);

        // Presence recorded, scoped to the engagement, with advertised caps.
        var record = await presence.FindAsync(implant.Id);
        Assert.NotNull(record);
        Assert.Equal(implant.EngagementId, record!.EngagementId);
        Assert.Equal(new[] { "shell.exec" }, record.Capabilities);
        Assert.Equal(Now, record.LastSeenAt);

        var online = await presence.ListOnlineAsync(implant.EngagementId);
        Assert.Single(online, r => r.ImplantId == implant.Id);
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

    private sealed class FakeClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
