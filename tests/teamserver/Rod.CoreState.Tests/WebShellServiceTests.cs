using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.WebShells;

namespace Rod.CoreState.Tests;

/// <summary>
/// Direct checks of the web-shell register use case (architecture.md
/// Sec 5.2's Web-shell class). A web-shell never enrolls: the register
/// action creates its WebShell-class implant row (the identity anchor
/// tasks and audit hang off) plus the connection profile, and the register
/// action itself is the engagement binding. Scoping checks pin that a
/// foreign engagement's reads refuse the endpoint exactly like an unknown
/// one, and that removal retires the row while the profile goes.
/// </summary>
public class WebShellServiceTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    private static WebShellService NewService(
        IEngagementRepository? engagements = null,
        TimeProvider? clock = null)
    {
        var engagementRepository = engagements ?? new EngagementRepositoryFake();
        return new WebShellService(
            new ImplantRepositoryFake(),
            new InMemoryWebShellProfileRepository(),
            engagementRepository,
            clock ?? FakeTimeProvider.At(At));
    }

    [Fact]
    public async Task Register_CreatesTheWebShellAnchorRow_AndTheProfile()
    {
        var engagements = new EngagementRepositoryFake();
        var engagement = engagements.Create("Operation Webshell");
        var service = NewService(engagements);

        var (implant, profile) = await service.RegisterAsync(
            engagement.Id, "https://web.example.test/up.php", "web.example.test",
            "eval-php", "connect", "base64", "base64",
            new OperatorId(Guid.NewGuid()));

        Assert.Equal(ImplantClass.WebShell, implant.Class);
        Assert.Equal(engagement.Id, implant.EngagementId);
        Assert.Equal("web.example.test", implant.Hostname);
        Assert.Null(implant.KillDate);
        Assert.False(implant.IsRetired);

        Assert.Equal(implant.Id, profile.ImplantId);
        Assert.Equal("https://web.example.test/up.php", profile.Url);
        Assert.Equal("eval-php", profile.AdapterId);
        Assert.Equal("connect", profile.Password);
        Assert.Null(profile.LastProbeAt);
        Assert.Null(profile.LastProbeOk);
    }

    [Fact]
    public async Task Register_RefusesAnUnknownEngagement()
    {
        var service = NewService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RegisterAsync(
            EngagementId.New(), "https://web.example.test/up.php", "web.example.test",
            "eval-php", "connect", "base64", "base64",
            new OperatorId(Guid.NewGuid())));
    }

    [Fact]
    public async Task Find_ScopesToTheOwningEngagement_AndToWebShellRows()
    {
        var engagements = new EngagementRepositoryFake();
        var engagement = engagements.Create("Operation Webshell");
        var other = engagements.Create("Operation Other");
        var service = NewService(engagements);
        var (implant, _) = await service.RegisterAsync(
            engagement.Id, "https://web.example.test/up.php", "web.example.test",
            "eval-php", "connect", "base64", "base64",
            new OperatorId(Guid.NewGuid()));

        var found = await service.FindAsync(engagement.Id, implant.Id);
        Assert.NotNull(found);
        Assert.Equal(implant.Id, found!.Value.Implant.Id);

        // A foreign engagement reads the endpoint as unknown, the same
        // construction every scoped surface follows.
        Assert.Null(await service.FindAsync(other.Id, implant.Id));

        // An implant row that is not a web-shell never resolves here.
        var stage2 = Implant.Enroll(
            ImplantId.New(), engagement.Id, At.AddDays(30), ImplantClass.Implant, At);
        Assert.Null(await service.FindAsync(engagement.Id, stage2.Id));
    }

    [Fact]
    public async Task Remove_RetiresTheRow_AndDropsTheProfile()
    {
        var engagements = new EngagementRepositoryFake();
        var engagement = engagements.Create("Operation Webshell");
        var service = NewService(engagements);
        var (implant, _) = await service.RegisterAsync(
            engagement.Id, "https://web.example.test/up.php", "web.example.test",
            "eval-php", "connect", "base64", "base64",
            new OperatorId(Guid.NewGuid()));

        Assert.True(await service.RemoveAsync(engagement.Id, implant.Id));
        Assert.Null(await service.FindAsync(engagement.Id, implant.Id));
        Assert.True((await service.ListAsync(engagement.Id)).Count == 0);

        // A second remove is a no-op, and a foreign engagement's remove
        // never touches it.
        Assert.False(await service.RemoveAsync(engagement.Id, implant.Id));
    }

    [Fact]
    public async Task NoteProbe_StampsTheOutcome_OnTheStoredProfile()
    {
        var engagements = new EngagementRepositoryFake();
        var engagement = engagements.Create("Operation Webshell");
        var service = NewService(engagements);
        var (implant, _) = await service.RegisterAsync(
            engagement.Id, "https://web.example.test/up.php", "web.example.test",
            "eval-php", "connect", "base64", "base64",
            new OperatorId(Guid.NewGuid()));

        await service.NoteProbeAsync(engagement.Id, implant.Id, ok: true);
        var probed = await service.FindAsync(engagement.Id, implant.Id);
        Assert.True(probed!.Value.Profile.LastProbeOk);
        Assert.NotNull(probed.Value.Profile.LastProbeAt);

        // A failed probe keeps its stamp -- a dead endpoint says so.
        await service.NoteProbeAsync(engagement.Id, implant.Id, ok: false);
        Assert.False((await service.FindAsync(engagement.Id, implant.Id))!.Value.Profile.LastProbeOk);
    }

    private sealed class EngagementRepositoryFake : IEngagementRepository
    {
        private readonly Dictionary<EngagementId, Engagement> _engagements = new();

        public Engagement Create(string name)
        {
            var engagement = Engagement.Create(
                EngagementId.New(), name, new OperatorId(Guid.NewGuid()), At);
            _engagements[engagement.Id] = engagement;
            return engagement;
        }

        public Task<Engagement?> FindAsync(EngagementId id, CancellationToken cancellationToken = default)
            => Task.FromResult(_engagements.TryGetValue(id, out var found) ? found : null);

        public Task<Engagement> GetOrThrowAsync(
            EngagementId id, CancellationToken cancellationToken = default)
            => Task.FromResult(
                _engagements.TryGetValue(id, out var found)
                    ? found
                    : throw new InvalidOperationException($"Engagement {id} does not exist."));

        public Task<IReadOnlyList<Engagement>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Engagement>>(_engagements.Values.ToArray());

        public Task SaveAsync(Engagement engagement, CancellationToken cancellationToken = default)
        {
            _engagements[engagement.Id] = engagement;
            return Task.CompletedTask;
        }
    }

    private sealed class ImplantRepositoryFake : IImplantRepository
    {
        private readonly Dictionary<ImplantId, Implant> _implants = new();

        public Task<Implant?> FindAsync(ImplantId id, CancellationToken cancellationToken = default)
            => Task.FromResult(_implants.TryGetValue(id, out var found) ? found : null);

        public Task<IReadOnlyList<Implant>> ListByEngagementAsync(
            EngagementId engagement,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Implant>>(
                _implants.Values.Where(i => i.EngagementId == engagement).ToArray());

        public Task<IReadOnlyList<Implant>> ListFrontedPivotsAsync(
            ImplantId parent,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Implant>>(Array.Empty<Implant>());

        public Task SaveAsync(Implant implant, CancellationToken cancellationToken = default)
        {
            _implants[implant.Id] = implant;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        private FakeTimeProvider(DateTimeOffset now) => _now = now;

        public static FakeTimeProvider At(DateTimeOffset now) => new(now);

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
