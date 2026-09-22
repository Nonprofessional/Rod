using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.Transport;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance: each implant class enrolls and is gated to its
/// reduced verb set. Drives the operator-facing task endpoint against an implant
/// of each class through the in-memory TestServer -- a verb inside the class's
/// set is accepted (201), a verb outside it is refused (422) before the task is
/// queued. The class's reduced set (architecture.md Sec 5.2) is enforced at task
/// issuance in core state and mapped to a wire status by the transport endpoint.
/// </summary>
public class ImplantClassGatingTests
{
    private static (HttpClient Client, IHost Host) CreateClient()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        return (client, host);
    }

    private static async Task<string> CreateEngagementAsync(HttpClient client)
    {
        await AuthenticatedHost.LoginAsync(client);
        var response = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Smokeshow"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        return created!.EngagementId;
    }

    // Enrolls an implant of the given class directly through the registry so the
    // task-issuance gate has a class to read. The endpoint path does not require
    // the implant to be connected -- issuance is gated, not dispatch.
    private static async Task<Implant> EnrollAsync(IHost host, EngagementId engagement, ImplantClass @class)
    {
        var implants = host.Services.GetRequiredService<IImplantRepository>();
        var clock = host.Services.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();
        var implant = Implant.Enroll(ImplantId.New(), engagement, now.AddDays(30), @class, now);
        await implants.SaveAsync(implant);
        return implant;
    }

    [Theory]
    [InlineData(ImplantClass.WebShell, "shell.exec", HttpStatusCode.Created)]
    [InlineData(ImplantClass.Ephemeral, "shell.exec", HttpStatusCode.Created)]
    [InlineData(ImplantClass.Ephemeral, "file.push", HttpStatusCode.UnprocessableEntity)]
    [InlineData(ImplantClass.Pivot, "shell.exec", HttpStatusCode.UnprocessableEntity)]
    [InlineData(ImplantClass.Pivot, "file.pull", HttpStatusCode.UnprocessableEntity)]
    [InlineData(ImplantClass.Implant, "shell.exec", HttpStatusCode.Created)]
    [InlineData(ImplantClass.Implant, "file.pull", HttpStatusCode.Created)]
    [InlineData(ImplantClass.Implant, "fs.list", HttpStatusCode.Created)]
    [InlineData(ImplantClass.Pivot, "fs.list", HttpStatusCode.UnprocessableEntity)]
    [InlineData(ImplantClass.Implant, "recon.portscan", HttpStatusCode.Created)]
    [InlineData(ImplantClass.Implant, "recon.hostenum", HttpStatusCode.Created)]
    [InlineData(ImplantClass.Implant, "recon.service", HttpStatusCode.Created)]
    [InlineData(ImplantClass.WebShell, "recon.service", HttpStatusCode.UnprocessableEntity)]
    [InlineData(ImplantClass.Implant, "lateral.move", HttpStatusCode.Created)]
    [InlineData(ImplantClass.Implant, "lateral.token", HttpStatusCode.Created)]
    [InlineData(ImplantClass.Implant, "lateral.exec_remote", HttpStatusCode.Created)]
    [InlineData(ImplantClass.WebShell, "lateral.token", HttpStatusCode.UnprocessableEntity)]
    [InlineData(ImplantClass.Implant, "persist.install", HttpStatusCode.Created)]
    [InlineData(ImplantClass.Implant, "persist.remove", HttpStatusCode.Created)]
    [InlineData(ImplantClass.Implant, "persist.list", HttpStatusCode.Created)]
    [InlineData(ImplantClass.WebShell, "persist.list", HttpStatusCode.UnprocessableEntity)]
    [InlineData(ImplantClass.Implant, "collect.cred", HttpStatusCode.Created)]
    [InlineData(ImplantClass.Implant, "collect.keylog", HttpStatusCode.Created)]
    [InlineData(ImplantClass.Implant, "exfil.push", HttpStatusCode.Created)]
    [InlineData(ImplantClass.Implant, "exfil.stage", HttpStatusCode.Created)]
    [InlineData(ImplantClass.WebShell, "exfil.push", HttpStatusCode.UnprocessableEntity)]
    public async Task TaskEndpoint_GatesOnTheImplantClassVerbSet(
        ImplantClass @class, string verb, HttpStatusCode expected)
    {
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var engagementId = await CreateEngagementAsync(client);
            var implant = await EnrollAsync(host, new EngagementId(Guid.Parse(engagementId)), @class);

            var response = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new TaskEndpoints.IssueTaskRequest(
                    ImplantId: implant.Id.ToString(),
                    Verb: verb,
                    Arguments: "arg"));

            Assert.Equal(expected, response.StatusCode);
        }
    }
}
