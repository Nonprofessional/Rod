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

    private const string DohAnswerA =
        """{"Status":0,"Answer":[{"name":"asked.","type":1,"TTL":60,"data":"203.0.113.10"}]}""";

    /// <summary>
    /// Serves the stub services on one ephemeral loopback port until the
    /// returned stopper runs: <c>/domain/...</c> answers the RDAP shape
    /// (404 when the test plays a registry without the record), a
    /// <c>name=</c> query answers the DoH resolver shape, and anything
    /// else is the CT mirror's ask.
    /// </summary>
    private static (string BaseUrl, Action Stop) ServeStubServices(int rdapStatus = 200)
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
                    var url = ctx.Request.Url!;
                    var (status, body) = url.AbsolutePath.StartsWith("/domain/")
                        ? (rdapStatus, RdapAnswer)
                        : ctx.Request.QueryString["name"] is not null
                            ? (200, DohAnswerA)
                            : (200, CtAnswer);
                    var buffer = Encoding.UTF8.GetBytes(body);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = buffer.Length;
                    ctx.Response.StatusCode = status;
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

    /// <summary>
    /// Serves a whois server on an ephemeral loopback port: reads each
    /// query line, answers with the fixed record, closes -- port 43's
    /// whole conversation, one client at a time.
    /// </summary>
    private static (int Port, Action Stop) ServeWhois()
    {
        var listener = TcpListener.Create(0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var stopped = new TaskCompletionSource();
        var serve = Task.Run(async () =>
        {
            try
            {
                while (listener.Server.IsBound)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII);
                    await reader.ReadLineAsync();
                    var answer = Encoding.ASCII.GetBytes(
                        "Domain: example.com\r\nRegistrar: Example Registrar, Inc.\r\n");
                    await stream.WriteAsync(answer);
                    await stream.FlushAsync();
                }
            }
            catch (Exception) when (stopped.Task.IsCompleted)
            {
            }
        });
        return (port, () =>
        {
            stopped.TrySetResult();
            listener.Stop();
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

            foreach (var action in new[] { "rdap", "subdomains", "resolve", "portscan" })
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

    [Fact]
    public async Task Resolve_CapturesAddressBearingFindings_AndJoinsThePictureWithThem()
    {
        var (baseUrl, stop) = ServeStubServices();
        try
        {
            var (client, host, operatorId) = AuthenticatedHost.Create(extendConfig: settings =>
            {
                settings["Recon:DohBaseUrl"] = baseUrl;
            });
            using (host)
            using (client)
            {
                await AuthenticatedHost.LoginAsync(client);
                var engagement = await CreateEngagementAsync(client);

                var resolved = await RunOkAsync(client, engagement, "resolve",
                    new { Target = "www.example.com" });
                Assert.Equal("recon.resolve:www.example.com", resolved.GetProperty("name").GetString());
                Assert.Equal(1, resolved.GetProperty("findings").GetInt32());

                var stored = Assert.Single(await host.Services.GetRequiredService<IArtifactStore>()
                    .ListAsync(engagement));
                Assert.Equal(operatorId.Value, stored.OperatorId);

                var lookup = Assert.Single(
                    await host.Services.GetRequiredService<IAuditStore>().ListAsync(engagement),
                    e => e.Kind == AuditEventKind.ReconLookupCompleted);
                Assert.Equal("resolve;www.example.com", lookup.Payload);

                // The address rides the picture: the host is observed with
                // what it resolved to.
                var topology = await client.GetFromJsonAsync<JsonElement>(
                    $"/engagements/{engagement}/topology");
                var observation = Assert.Single(topology.GetProperty("observations").EnumerateArray());
                Assert.Equal("www.example.com", observation.GetProperty("host").GetString());
                Assert.Equal("203.0.113.10", observation.GetProperty("address").GetString());
            }
        }
        finally
        {
            stop();
        }
    }

    [Fact]
    public async Task Rdap_RegistryMiss_FallsBackToWhois_WhenAServerIsConfigured()
    {
        var (baseUrl, stopHttp) = ServeStubServices(rdapStatus: 404);
        var (whoisPort, stopWhois) = ServeWhois();
        try
        {
            var (client, host, _) = AuthenticatedHost.Create(extendConfig: settings =>
            {
                settings["Recon:RdapBaseUrl"] = baseUrl;
                settings["Recon:WhoisServer"] = $"127.0.0.1:{whoisPort}";
            });
            using (host)
            using (client)
            {
                await AuthenticatedHost.LoginAsync(client);
                var engagement = await CreateEngagementAsync(client);

                var answer = await RunOkAsync(client, engagement, "rdap", new { Target = "example.com" });
                Assert.Equal("recon.whois:example.com", answer.GetProperty("name").GetString());
                Assert.Equal("text/plain", answer.GetProperty("contentType").GetString());

                // One event covers the run, its lookup name carrying the
                // fallback.
                var lookup = Assert.Single(
                    await host.Services.GetRequiredService<IAuditStore>().ListAsync(engagement),
                    e => e.Kind == AuditEventKind.ReconLookupCompleted);
                Assert.Equal("rdap>whois;example.com", lookup.Payload);
            }
        }
        finally
        {
            stopHttp();
            stopWhois();
        }
    }

    [Fact]
    public async Task Resolve_ABulkAskOverTheCap_IsRefusedByName()
    {
        var (baseUrl, stop) = ServeStubServices();
        try
        {
            var (client, host, _) = AuthenticatedHost.Create(extendConfig: settings =>
            {
                settings["Recon:DohBaseUrl"] = baseUrl;
                settings["Recon:MaxResolveTargets"] = "1";
            });
            using (host)
            using (client)
            {
                await AuthenticatedHost.LoginAsync(client);
                var engagement = await CreateEngagementAsync(client);

                var response = await client.PostAsJsonAsync(
                    $"/engagements/{engagement}/recon:resolve",
                    new { Targets = new[] { "a.example.com", "b.example.com" } });
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

                var both = await client.PostAsJsonAsync(
                    $"/engagements/{engagement}/recon:resolve",
                    new { Target = "a.example.com", Targets = new[] { "b.example.com" } });
                Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
            }
        }
        finally
        {
            stop();
        }
    }
}
