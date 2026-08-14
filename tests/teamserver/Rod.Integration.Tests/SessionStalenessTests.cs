using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Sessions;
using Rod.Transport;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// The session staleness sweep acceptance (architecture.md Sec 10.3): a session
/// whose last-seen stamp is older than the configured threshold is closed and
/// the implant drops off the online roster -- the fix for a beacon stream that
/// dies silently and otherwise stays Active forever. Drives the hosted sweeper
/// directly (SweepOnceAsync) for determinism instead of racing the timer.
/// </summary>
public class SessionStalenessTests
{
    private static (HttpClient Client, IHost Host) CreateClient()
    {
        var (client, host, _) = AuthenticatedHost.Create(
            extendConfig: settings =>
            {
                settings["Sessions:Staleness:Threshold"] = "00:00:01";
                settings["Sessions:Staleness:SweepInterval"] = "01:00:00";
            });
        return (client, host);
    }

    private static async Task<EngagementId> CreateEngagementAsync(HttpClient client)
    {
        await AuthenticatedHost.LoginAsync(client);
        var response = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Nightwatch"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        return new EngagementId(Guid.Parse(created!.EngagementId));
    }

    private static async Task<Implant> EnrollAsync(IHost host, EngagementId engagement, DateTimeOffset at)
    {
        var implants = host.Services.GetRequiredService<IImplantRepository>();
        var implant = Implant.Enroll(ImplantId.New(), engagement, "key-stale", at.AddDays(30), ImplantClass.Stage2, at);
        await implants.SaveAsync(implant);
        return implant;
    }

    [Fact]
    public async Task Sweep_ClosesAStaleSession_AndDropsTheImplantOffTheRoster()
    {
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var engagement = await CreateEngagementAsync(client);
            var now = host.Services.GetRequiredService<TimeProvider>().GetUtcNow();
            var implant = await EnrollAsync(host, engagement, now.AddMinutes(-5));
            var sessions = host.Services.GetRequiredService<ISessionRegistry>();
            await sessions.OpenAsync(implant, new[] { "shell.exec" }, now.AddMinutes(-5));

            // The configured threshold is one second, so the five-minute-old
            // session is stale on the next pass. Drive the pass directly.
            var sweeper = host.Services.GetRequiredService<SessionStalenessSweeper>();
            var closed = await sweeper.SweepOnceAsync();

            var swept = Assert.Single(closed);
            Assert.Equal(implant.Id, swept.ImplantId);
            Assert.Equal(SessionStatus.Closed, swept.Status);

            // The implant dropped off the online roster: presence is empty and
            // the implant listing reads offline.
            var presence = await client.GetFromJsonAsync<PresenceEndpoints.PresenceRecordResponse[]>(
                $"/engagements/{engagement}/presence");
            Assert.Empty(presence!);

            var listed = await client.GetFromJsonAsync<ImplantEndpoints.ImplantResponse[]>(
                $"/engagements/{engagement}/implants");
            var row = Assert.Single(listed!);
            Assert.False(row.IsOnline);
        }
    }

    [Fact]
    public async Task Sweep_LeavesAFreshSessionAlone()
    {
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var engagement = await CreateEngagementAsync(client);
            var now = host.Services.GetRequiredService<TimeProvider>().GetUtcNow();
            var implant = await EnrollAsync(host, engagement, now);
            var sessions = host.Services.GetRequiredService<ISessionRegistry>();
            await sessions.OpenAsync(implant, new[] { "shell.exec" }, now);

            var sweeper = host.Services.GetRequiredService<SessionStalenessSweeper>();
            var closed = await sweeper.SweepOnceAsync();

            Assert.Empty(closed);
            var active = await sessions.GetActiveAsync(implant.Id);
            Assert.NotNull(active);
            Assert.Equal(SessionStatus.Active, active!.Status);

            var presence = await client.GetFromJsonAsync<PresenceEndpoints.PresenceRecordResponse[]>(
                $"/engagements/{engagement}/presence");
            Assert.Single(presence!);
        }
    }

    [Fact]
    public async Task MisconfiguredThreshold_FailsStartup()
    {
        // A present-but-unparseable threshold fails startup loudly: a silently
        // disabled sweep would leave dead sessions on the roster forever.
        Assert.Throws<InvalidOperationException>(() =>
            AuthenticatedHost.Create(
                extendConfig: settings => settings["Sessions:Staleness:Threshold"] = "not-a-timespan"));
    }
}
