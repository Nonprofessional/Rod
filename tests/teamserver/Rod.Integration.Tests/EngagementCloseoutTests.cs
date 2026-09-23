using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance: the engagement close-out (architecture.md Sec 2 step 10, Sec 11)
/// -- freeze the engagement, export the evidence package, verify it against the
/// chain, then retire. A frozen engagement accepts no new tasking and no new
/// deployments; the exported package carries the trail, the artifacts, and the
/// report, and re-verifies offline (here: through the same verifier the
/// <c>--verify-evidence</c> CLI runs, against the returned bytes with no store
/// or host involved).
/// </summary>
public class EngagementCloseoutTests
{
    [Fact]
    public async Task The_Full_Close_Out_Path()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var implant = await EnrollImplantAsync(host, engagementId);

            // Some operational history, so the exported trail is a real one.
            var issued = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new TaskEndpoints.IssueTaskRequest(implant.Id.ToString(), "shell.exec", "whoami"));
            Assert.Equal(HttpStatusCode.Created, issued.StatusCode);

            // The close-out starts from the frozen state: an open engagement
            // has a growing trail, so its evidence is not final yet.
            var earlyExport = await client.PostAsync($"/engagements/{engagementId}:evidence-package", null);
            Assert.Equal(HttpStatusCode.Conflict, earlyExport.StatusCode);

            var frozen = await client.PostAsync($"/engagements/{engagementId}:freeze", null);
            Assert.Equal(HttpStatusCode.OK, frozen.StatusCode);
            var frozenBody = await frozen.Content.ReadFromJsonAsync<CloseoutEndpoints.EngagementClosedResponse>();
            Assert.Equal(engagementId, frozenBody!.EngagementId);

            // A frozen engagement accepts no new tasking and no new deployments.
            var refusedTask = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new TaskEndpoints.IssueTaskRequest(implant.Id.ToString(), "shell.exec", "id"));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refusedTask.StatusCode);
            var refusedMint = await client.PostAsync($"/engagements/{engagementId}/deploy-tokens", null);
            Assert.Equal(HttpStatusCode.Conflict, refusedMint.StatusCode);

            // The export: one ZIP carrying the trail, the artifacts, and the
            // report, downloadable by the closing operator.
            var exported = await client.PostAsync($"/engagements/{engagementId}:evidence-package", null);
            Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
            Assert.Equal("application/zip", exported.Content.Headers.ContentType?.MediaType);
            var package = await exported.Content.ReadAsByteArrayAsync();

            // The offline verification: the same check the CLI runs on a host
            // with no Rod infrastructure. No store, no host -- just the bytes.
            var verification = await EvidencePackage.VerifyAsync(new MemoryStream(package));
            Assert.True(verification.Verified, verification.Failure);
            Assert.Equal(Guid.Parse(engagementId), verification.Manifest!.Header.EngagementId);
            Assert.True(verification.Manifest.EventCount >= 3);
            Assert.Contains("report.json", verification.Manifest.Files.Select(f => f.Path));
            Assert.Contains("report.md", verification.Manifest.Files.Select(f => f.Path));

            // The live trail carries its own close-out story, and the exported
            // package's trail ends one event short of it -- the export event is
            // written after the package is built (a later re-export carries it).
            var audit = host.Services.GetRequiredService<IAuditStore>();
            var trail = await audit.ListAsync(Guid.Parse(engagementId));
            Assert.Contains(trail, e => e.Kind == AuditEventKind.EngagementCreated);
            Assert.Contains(trail, e => e.Kind == AuditEventKind.TaskIssued);
            Assert.Contains(trail, e => e.Kind == AuditEventKind.EngagementFrozen);
            Assert.Contains(trail, e => e.Kind == AuditEventKind.EvidenceExported);
            Assert.Equal(verification.Manifest.EventCount + 1, trail.Count);

            // Retiring completes the close-out, terminal: it requires the freeze
            // (open engagements are refused above), and nothing closes again.
            var retired = await client.PostAsync($"/engagements/{engagementId}:retire", null);
            Assert.Equal(HttpStatusCode.OK, retired.StatusCode);
            var retiredAgain = await client.PostAsync($"/engagements/{engagementId}:retire", null);
            Assert.Equal(HttpStatusCode.Conflict, retiredAgain.StatusCode);
            var frozenAgain = await client.PostAsync($"/engagements/{engagementId}:freeze", null);
            Assert.Equal(HttpStatusCode.Conflict, frozenAgain.StatusCode);

            // The state is visible to operators: the engagement list reads back
            // the close-out timestamps.
            var list = await client.GetFromJsonAsync<List<EngagementEndpoints.EngagementResponse>>("/engagements");
            var entry = Assert.Single(list!, e => e.EngagementId == engagementId);
            Assert.True(entry.FrozenAt.HasValue);
            Assert.True(entry.RetiredAt.HasValue);
        }
    }

    [Fact]
    public async Task Retire_Refuses_An_Open_Engagement()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);

            // The close-out cannot skip the evidence export: retiring an open
            // engagement is refused by the aggregate.
            var response = await client.PostAsync($"/engagements/{engagementId}:retire", null);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
    }

    [Fact]
    public async Task Unfreeze_Recovers_A_Mistaken_Freeze()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var implant = await EnrollImplantAsync(host, engagementId);

            var frozen = await client.PostAsync($"/engagements/{engagementId}:freeze", null);
            Assert.Equal(HttpStatusCode.OK, frozen.StatusCode);

            // The recovery: the engagement reopens, and tasking and deployments
            // resume -- the gates the freeze closed read open again.
            var unfrozen = await client.PostAsync($"/engagements/{engagementId}:unfreeze", null);
            Assert.Equal(HttpStatusCode.OK, unfrozen.StatusCode);
            var reopened = await unfrozen.Content.ReadFromJsonAsync<CloseoutEndpoints.EngagementReopenedResponse>();
            Assert.Equal(engagementId, reopened!.EngagementId);

            var issued = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new TaskEndpoints.IssueTaskRequest(implant.Id.ToString(), "shell.exec", "id"));
            Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
            var minted = await client.PostAsync($"/engagements/{engagementId}/deploy-tokens", null);
            Assert.Equal(HttpStatusCode.OK, minted.StatusCode);

            // Both events stay in the trail: the mistaken freeze is part of the
            // story, not erased by the recovery.
            var audit = host.Services.GetRequiredService<IAuditStore>();
            var trail = await audit.ListAsync(Guid.Parse(engagementId));
            Assert.Contains(trail, e => e.Kind == AuditEventKind.EngagementFrozen);
            Assert.Contains(trail, e => e.Kind == AuditEventKind.EngagementUnfrozen);

            // The guard rails hold: unfreezing an open engagement conflicts, and
            // retirement still requires a freeze first.
            var unfrozenAgain = await client.PostAsync($"/engagements/{engagementId}:unfreeze", null);
            Assert.Equal(HttpStatusCode.Conflict, unfrozenAgain.StatusCode);
            var retired = await client.PostAsync($"/engagements/{engagementId}:retire", null);
            Assert.Equal(HttpStatusCode.Conflict, retired.StatusCode);

            var list = await client.GetFromJsonAsync<List<EngagementEndpoints.EngagementResponse>>("/engagements");
            var entry = Assert.Single(list!, e => e.EngagementId == engagementId);
            Assert.False(entry.FrozenAt.HasValue);
            Assert.False(entry.RetiredAt.HasValue);
        }
    }

    [Fact]
    public async Task Unfreeze_Refuses_A_Retired_Engagement()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);

            await client.PostAsync($"/engagements/{engagementId}:freeze", null);
            await client.PostAsync($"/engagements/{engagementId}:retire", null);

            // Retirement is terminal: the record is sealed as evidence and the
            // close-out cannot be walked back past it.
            var response = await client.PostAsync($"/engagements/{engagementId}:unfreeze", null);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
    }

    private static async Task<string> CreateEngagementAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/engagements", new EngagementEndpoints.CreateEngagementRequest(Name: "close-out walk"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        return created!.EngagementId;
    }

    // Enrolls an implant directly through the registry so the task gate
    // has a class to read; the endpoint path does not require the implant to be
    // connected -- issuance is gated, not dispatch.
    private static async Task<Implant> EnrollImplantAsync(IHost host, string engagementId)
    {
        var implants = host.Services.GetRequiredService<IImplantRepository>();
        var clock = host.Services.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();
        var implant = Implant.Enroll(
            ImplantId.New(), new EngagementId(Guid.Parse(engagementId)), now.AddDays(30), ImplantClass.Implant, now);
        await implants.SaveAsync(implant);
        return implant;
    }
}
