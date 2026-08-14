using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Pki;
using Rod.CoreState.Staging;

namespace Rod.Integration.Tests;

/// <summary>
/// Direct checks of <see cref="EnrollmentService"/> -- the use case that redeems
/// a stager token and binds a new implant to its engagement (),
/// complementing the HTTP slice in <see cref="EnrollmentTests"/>. Without
/// spinning up a server: focuses on the per-implant material the service
/// generates (architecture.md Sec 7), which the HTTP test reads back only
/// indirectly through the certificate binding.
/// </summary>
public class EnrollmentServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    // Builds the service against the in-memory ports, the way the composition
    // root does. The engagements repo is shared with the stager-token service so
    // a minted token resolves to a real engagement; the dev CA is the same
    // self-signed root the transport layer trusts at mTLS termination -- here it
    // only has to issue a real leaf so the enroll path is exercised end to end.
    private static (EnrollmentService Service, IStagerTokenService Tokens, IEngagementRepository Engagements) NewService(
        IImplantRepository? implants = null,
        TimeProvider? clock = null)
    {
        var engagements = new InMemoryEngagementRepository();
        var tokens = new InMemoryStagerTokenService(engagements);
        implants ??= new InMemoryImplantRepository();
        var ca = new DevCertificateAuthority();
        var service = new EnrollmentService(engagements, tokens, implants, ca, clock ?? new FakeClock(Now));
        return (service, tokens, engagements);
    }

    // Mints a token for a fresh engagement and returns the secret an implant
    // redeems. Mirrors the HTTP mint flow without the round-trip. The owner is a
    // member of the engagement it mints for (required by the token service).
    private static async Task<string> MintTokenAsync(IEngagementRepository engagements, IStagerTokenService tokens)
    {
        var owner = OperatorId.New();
        var engagement = Engagement.Create(EngagementId.New(), "Op A", owner, Now);
        await engagements.SaveAsync(engagement);
        var minted = await tokens.MintAsync(engagement.Id, owner, Now);
        return minted.Secret;
    }

    [Fact]
    public async Task Enroll_GeneratesDistinctKeys_PerImplant()
    {
        // The per-implant key is generated at enrollment (architecture.md Sec 7:
        // unique per implant so compromising one does not compromise all). Two
        // enrolls must therefore carry different keys -- a regression to a shared
        // or constant key would fail this.
        var (service, tokens, engagements) = NewService();

        var first = await service.EnrollAsync(new EnrollCommand(await MintTokenAsync(engagements, tokens)));
        var second = await service.EnrollAsync(new EnrollCommand(await MintTokenAsync(engagements, tokens)));

        Assert.NotEqual(first.ImplantId, second.ImplantId);
        Assert.NotEmpty(first.Key);
        Assert.NotEmpty(second.Key);
        Assert.NotEqual(first.Key, second.Key);
    }

    [Fact]
    public async Task Enroll_RecordsTheKillDate_OnTheImplant()
    {
        // The kill date is set by the service (architecture.md Sec 7), recorded on
        // the implant and enforced later at handshake (architecture.md Sec 9) and
        // in the implant itself. This pins that the service produces one and the
        // implant entity carries it. The default window is 30 days from
        // enrollment; assert it lands there so a silent change is caught.
        var (service, tokens, engagements) = NewService();

        var result = await service.EnrollAsync(new EnrollCommand(await MintTokenAsync(engagements, tokens)));

        Assert.True(result.KillDate > Now);
        Assert.Equal(Now.AddDays(30), result.KillDate);
    }

    private sealed class FakeClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
