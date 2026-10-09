using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.Transport.Endpoints;
using Task = System.Threading.Tasks.Task;

namespace Rod.Integration.Tests;

/// <summary>
/// The external recon workbench acceptance (architecture.md Sec 11.4): with
/// each half's service configured, an operator runs an RDAP lookup and a
/// CT-log subdomain enumeration against a named engagement target and the
/// findings land as task-less engagement artifacts with every attempt in
/// the audit trail; the scan lands its open ports the same way when the
/// target is inside the engagement's ROE target scope, and a target outside
/// it is refused with the refusal in the trail. Unconfigured halves answer
/// 503 naming the configuration section, a closed engagement is refused,
/// and the findings join the topology projection -- findings-as-artifacts
/// is the seam. The external services are stub loopback listeners so the
/// egress contract is exercised without network dependency; the scan dials
/// a real loopback socket.
/// </summary>
public class ReconWorkbenchTests
{
    private const string RdapAnswer = """
        {
          "objectClassName": "domain",
          "ldhName": "EXAMPLE.COM",
          "status": ["client transfer prohibited"],
          "events": [
            { "eventAction": "registration", "eventDate": "2015-01-01T00:00:00Z" }
          ],
          "entities": [
            { "roles": ["registrar"], "vcardArray": ["vcard", [
              ["version", {}, "text", "4.0"],
              ["fn", {}, "text", "Example Registrar, Inc."]
            ]] }
          ],
          "nameservers": [ { "ldhName": "NS1.EXAMPLE.COM" } ],
          "secureDNS": { "delegationSigned": true }
        }
        """;

    private const string CtAnswer =
        """[{"name_value":"example.com\nwww.example.com","common_name":"example.com"}]""";

    /// <summary>
    /// Serves both stub services on one ephemeral loopback port until the
    /// returned stopper runs: <c>/domain/...</c> answers the RDAP shape,
    /// the query string names the CT mirror's ask.
    /// </summary>
    private static (string BaseUrl, Action Stop) ServeStubServices()
    {
        using var probe = TcpListener.Create(0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        var stopped = new TaskCompletionSource();
        var serve = Task.Run(async () =>
        {
            try
            {
                while (server.IsListening)
                {
                    var ctx = await server.GetContextAsync();
                    using var _ = ctx.Response;
                    var (status, body) = ctx.Request.Url!.AbsolutePath.StartsWith("/domain/")
                        ? (200, RdapAnswer)
                        : (200, CtAnswer);
                    var buffer = Encoding.UTF8.GetBytes(body);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = buffer.Length;
                    await ctx.Response.OutputStream.WriteAsync(buffer);
                }
            }
            catch (Exception) when (stopped.Task.IsCompleted)
            {
            }
        });
        return ($"http://127.0.0.1:{port}", () =>
        {
            stopped.TrySetResult();
            server.Stop();
        }
        );
    }

    private static async Task<Guid> CreateEngagementAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Paperoppida"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        return Guid.Parse(created!.EngagementId);
    }

    private static async Task<JsonElement> RunOkAsync(
        HttpClient client, Guid engagement, string action, object body)
    {
        var response = await client.PostAsJsonAsync($"/engagements/{engagement}/recon:{action}", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task PassiveLookups_LandFindingsAsArtifacts_AndEveryAttemptInTheTrail()
    {
        var (baseUrl, stop) = ServeStubServices();
        try
        {
            var (client, host, operatorId) = AuthenticatedHost.Create(extendConfig: settings =>
            {
                settings["Recon:RdapBaseUrl"] = baseUrl;
                settings["Recon:CtBaseUrl"] = baseUrl;
                settings["Recon:RequestTimeoutSeconds"] = "30";
            });
            using (host)
            using (client)
            {
                await AuthenticatedHost.LoginAsync(client);
                var engagement = await CreateEngagementAsync(client);

                var rdap = await RunOkAsync(client, engagement, "rdap", new { Target = "example.com" });
                Assert.Equal("recon.rdap:example.com", rdap.GetProperty("name").GetString());
                Assert.Equal("application/json", rdap.GetProperty("contentType").GetString());

                var names = await RunOkAsync(client, engagement, "subdomains", new { Target = "example.com" });
                Assert.Equal("application/x-ndjson", names.GetProperty("contentType").GetString());
                Assert.Equal(2, names.GetProperty("findings").GetInt32());

                // The findings are task-less artifacts under the acting
                // operator's attribution -- pre-foothold there is no task.
                var artifacts = host.Services.GetRequiredService<IArtifactStore>();
                var stored = await artifacts.ListAsync(engagement);
                Assert.Equal(2, stored.Count);
                Assert.All(stored, a => Assert.Null(a.TaskId));
                Assert.All(stored, a => Assert.Equal(operatorId.Value, a.OperatorId));

                // The bytes read back through the ordinary artifact route,
                // and the census speaks the recon grammar.
                var censusArtifact = Assert.Single(stored, a => a.Name == "recon.subdomains:example.com");
                var retrieved = await client.GetAsync($"/engagements/{engagement}/artifacts/{censusArtifact.ArtifactId:N}");
                Assert.Equal(HttpStatusCode.OK, retrieved.StatusCode);
                var census = await retrieved.Content.ReadAsStringAsync();
                Assert.Contains("\"host\":\"www.example.com\"", census);

                // Every attempt lands in the trail, the outcome the artifact
                // id -- the egress is the act.
                var audit = host.Services.GetRequiredService<IAuditStore>();
                var events = await audit.ListAsync(engagement);
                var lookups = events.Where(e => e.Kind == AuditEventKind.ReconLookupCompleted).ToArray();
                Assert.Equal(2, lookups.Length);
                Assert.All(lookups, e => Assert.Equal(operatorId.Value, e.OperatorId));
                Assert.Contains(lookups, e => e.Outcome == rdap.GetProperty("artifactId").GetString());
                Assert.Contains(lookups, e => e.Payload.StartsWith("subdomains;example.com"));
            }
        }
        finally
        {
            stop();
        }
    }

    [Fact]
    public async Task Scan_InsideTheRoeTargetScope_LandsFindings_OutsideIsRefusedInTheTrail()
    {
        // The scan's target: one listening loopback socket, and a port that
        // is closed by scan time beside it.
        var listener = TcpListener.Create(0);
        listener.Start();
        var openPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var probe = TcpListener.Create(0);
        probe.Start();
        var closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var accept = Task.Run(async () =>
        {
            while (listener.Server.IsBound)
            {
                try
                {
                    using var _ = await listener.AcceptTcpClientAsync();
                }
                catch (Exception)
                {
                    return; // The listener stopped between accepts.
                }
            }
        });
        try
        {
            var (client, host, operatorId) = AuthenticatedHost.Create(extendConfig: settings =>
            {
                settings["Recon:ScanOrigin"] = "Teamserver";
            });
            using (host)
            using (client)
            {
                await AuthenticatedHost.LoginAsync(client);
                var engagement = await CreateEngagementAsync(client);

                // Scope the engagement to a range the loopback target is
                // outside of, then ask for the scan: the refusal lands in
                // the trail before any connection opens.
                var roe = await client.PutAsJsonAsync($"/engagements/{engagement}/roe",
                    new { PermittedTargets = new[] { "203.0.113.0/24" } });
                Assert.Equal(HttpStatusCode.OK, roe.StatusCode);

                var refused = await client.PostAsJsonAsync(
                    $"/engagements/{engagement}/recon:portscan",
                    new { Target = "127.0.0.1", Ports = $"{openPort}" });
                Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
                var refusalProblem = await refused.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Contains("permitted targets", refusalProblem.GetProperty("error").GetString());

                var audit = host.Services.GetRequiredService<IAuditStore>();
                var refusedEvent = Assert.Single(
                    await audit.ListAsync(engagement), e => e.Kind == AuditEventKind.ReconScanRefused);
                Assert.Contains("permitted targets", refusedEvent.Outcome);
                Assert.DoesNotContain(
                    await host.Services.GetRequiredService<IArtifactStore>().ListAsync(engagement),
                    a => a.Name.StartsWith("recon.portscan:"));

                // Now scope the engagement to the loopback target and ask
                // again: the scan runs, the open port is a finding, and the
                // run is in the trail.
                roe = await client.PutAsJsonAsync($"/engagements/{engagement}/roe",
                    new { PermittedTargets = new[] { "127.0.0.1/32", "203.0.113.0/24" } });
                Assert.Equal(HttpStatusCode.OK, roe.StatusCode);

                var scanned = await RunOkAsync(client, engagement, "portscan",
                    new { Target = "127.0.0.1", Ports = $"{openPort},{closedPort}" });
                Assert.Equal($"recon.portscan:127.0.0.1:{openPort},{closedPort}",
                    scanned.GetProperty("name").GetString());
                Assert.Equal(1, scanned.GetProperty("findings").GetInt32());

                var scanArtifactId = scanned.GetProperty("artifactId").GetString();
                var scannedEvent = Assert.Single(
                    await audit.ListAsync(engagement), e => e.Kind == AuditEventKind.ReconScanCompleted);
                Assert.Equal(scanArtifactId, scannedEvent.Outcome);
                Assert.Contains("127.0.0.1", scannedEvent.Payload);
            }
        }
        finally
        {
            listener.Stop();
            await accept;
        }
    }

    [Fact]
    public async Task Findings_JoinTheTopologyProjection_AsObservedHosts()
    {
        var (baseUrl, stop) = ServeStubServices();
        try
        {
            var (client, host, _) = AuthenticatedHost.Create(extendConfig: settings =>
            {
                settings["Recon:CtBaseUrl"] = baseUrl;
            });
            using (host)
            using (client)
            {
                await AuthenticatedHost.LoginAsync(client);
                var engagement = await CreateEngagementAsync(client);

                await RunOkAsync(client, engagement, "subdomains", new { Target = "example.com" });

                var topology = await client.GetFromJsonAsync<JsonElement>(
                    $"/engagements/{engagement}/topology");
                var hosts = topology.GetProperty("hosts");
                var observed = Assert.Single(
                    hosts.EnumerateArray(), h => h.GetProperty("host").GetString() == "www.example.com");
                Assert.Equal("observed", observed.GetProperty("kind").GetString());

                var observation = Assert.Single(
                    topology.GetProperty("observations").EnumerateArray(),
                    o => o.GetProperty("host").GetString() == "www.example.com");
                Assert.Equal("recon.subdomains", observation.GetProperty("verb").GetString());
                Assert.NotNull(observation.GetProperty("artifactId").GetString());
                Assert.Null(observation.GetProperty("taskId").GetString());
            }
        }
        finally
        {
            stop();
        }
    }

    [Fact]
    public async Task UnconfiguredHalves_Answer503NamingTheSection_AndLeaveNoTrail()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (host)
        using (client)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagement = await CreateEngagementAsync(client);

            foreach (var action in new[] { "rdap", "subdomains", "portscan" })
            {
                var response = await client.PostAsJsonAsync(
                    $"/engagements/{engagement}/recon:{action}", new { Target = "example.com" });
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Contains("Recon", problem.GetProperty("error").GetString());
            }

            var audit = host.Services.GetRequiredService<IAuditStore>();
            var kinds = (await audit.ListAsync(engagement)).Select(e => e.Kind).ToArray();
            Assert.DoesNotContain(AuditEventKind.ReconLookupCompleted, kinds);
            Assert.DoesNotContain(AuditEventKind.ReconScanCompleted, kinds);
        }
    }

    [Fact]
    public async Task AClosedEngagement_AcceptsNoWorkbenchFindings()
    {
        var (baseUrl, stop) = ServeStubServices();
        try
        {
            var (client, host, _) = AuthenticatedHost.Create(extendConfig: settings =>
            {
                settings["Recon:RdapBaseUrl"] = baseUrl;
            });
            using (host)
            using (client)
            {
                await AuthenticatedHost.LoginAsync(client);
                var engagement = await CreateEngagementAsync(client);

                // Freeze through the repository the freeze route itself uses;
                // the workbench's refusal is the assertion.
                var engagements = host.Services.GetRequiredService<IEngagementRepository>();
                var loaded = await engagements.FindAsync(new EngagementId(engagement));
                loaded!.Freeze(DateTimeOffset.UtcNow);
                await engagements.SaveAsync(loaded);

                var response = await client.PostAsJsonAsync(
                    $"/engagements/{engagement}/recon:rdap", new { Target = "example.com" });
                Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            }
        }
        finally
        {
            stop();
        }
    }

    [Fact]
    public async Task MalformedRequests_AreRefusedByName()
    {
        var (client, host, _) = AuthenticatedHost.Create(extendConfig: settings =>
        {
            settings["Recon:RdapBaseUrl"] = "http://127.0.0.1:9";
            settings["Recon:ScanOrigin"] = "Teamserver";
        });
        using (host)
        using (client)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagement = await CreateEngagementAsync(client);

            var notADomain = await client.PostAsJsonAsync(
                $"/engagements/{engagement}/recon:rdap", new { Target = "not a domain" });
            Assert.Equal(HttpStatusCode.BadRequest, notADomain.StatusCode);

            var badPorts = await client.PostAsJsonAsync(
                $"/engagements/{engagement}/recon:portscan",
                new { Target = "127.0.0.1", Ports = "70000" });
            Assert.Equal(HttpStatusCode.BadRequest, badPorts.StatusCode);
        }
    }
}
