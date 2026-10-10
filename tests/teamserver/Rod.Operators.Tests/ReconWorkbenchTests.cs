using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Rod.Audit;
using Rod.Operators.Workbench;
using Task = System.Threading.Tasks.Task;

namespace Rod.Operators.Tests;

/// <summary>
/// Checks of the recon workbench's run-and-capture path (architecture.md
/// Sec 11.4): an RDAP answer normalizes into a JSON artifact, a CT mirror's
/// rows become a deduplicated grammar-shaped name census, the scan reports
/// exactly the ports that answered, and every egress problem reports as a
/// failed result instead of throwing. The far end is a recording handler;
/// the scan dials real loopback sockets.
/// </summary>
public class ReconWorkbenchTests
{
    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// The far end of the lookups: answers each recorded request with the
    /// queued response (status plus body), so a test scripts a whole
    /// exchange without a socket. Resolution requests queue per queried
    /// name -- the answer section routes by the DoH query's <c>name</c>
    /// parameter, so concurrent bulk tests carry no ordering assumptions.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> Requests = new();
        private readonly ConcurrentQueue<(int Status, string Body)> _answers = new();
        private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _answersByName = new();

        public void Enqueue(int status, string body) => _answers.Enqueue((status, body));

        public void EnqueueResolution(string name, string body)
            => _answersByName.GetOrAdd(name, _ => new ConcurrentQueue<string>()).Enqueue(body);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!);
            var name = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query).Get("name");
            if (name is not null
                && _answersByName.TryGetValue(name, out var queue)
                && queue.TryDequeue(out var body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                });
            }

            var (status, generic) = _answers.TryDequeue(out var answer) ? answer : (200, "{}");
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(generic, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Rig
    {
        public readonly FakeTime Clock = new();
        public readonly InMemoryArtifactStore Artifacts = new();
        public readonly ScriptedHandler Http = new();
        public readonly ReconWorkbenchOptions Options = new();
        public readonly ReconWorkbenchService Service;
        public readonly Guid Engagement = Guid.NewGuid();

        public Rig()
        {
            Options.RdapBaseUrl = "https://rdap-stub.test";
            Options.CtBaseUrl = "https://ct-stub.test";
            Options.DohBaseUrl = "https://doh-stub.test/resolve";
            Service = new ReconWorkbenchService(
                Microsoft.Extensions.Options.Options.Create(Options),
                new SingleClientFactory(new HttpClient(Http)),
                Artifacts,
                Clock);
        }

        public async Task<Artifact> SingleArtifactOf(ReconWorkbenchService.Result result)
            => Assert.Single(await Artifacts.ListAsync(Engagement), a => a.ArtifactId == result.ArtifactId);

        public async Task<int> ArtifactCount()
            => (await Artifacts.ListAsync(Engagement)).Count;
    }

    private const string RdapAnswer = """
        {
          "objectClassName": "domain",
          "ldhName": "EXAMPLE.COM",
          "status": ["client transfer prohibited"],
          "events": [
            { "eventAction": "registration", "eventDate": "2015-01-01T00:00:00Z" },
            { "eventAction": "expiration", "eventDate": "2027-01-01T00:00:00Z" }
          ],
          "entities": [
            { "roles": ["registrar"], "vcardArray": ["vcard", [
              ["version", {}, "text", "4.0"],
              ["fn", {}, "text", "Example Registrar, Inc."]
            ]] }
          ],
          "nameservers": [
            { "ldhName": "NS1.EXAMPLE.COM" },
            { "ldhName": "ns2.example.com" }
          ],
          "secureDNS": { "delegationSigned": true }
        }
        """;

    private const string CtAnswer = """
        [
          { "name_value": "example.com\nwww.example.com", "common_name": "example.com" },
          { "name_value": "WWW.EXAMPLE.COM.", "common_name": "example.com" },
          { "name_value": "*.example.com", "common_name": "example.com" },
          { "name_value": "api.example.com\nother.org", "common_name": "api.example.com" },
          { "name_value": "api.example.com", "common_name": "api.example.com" }
        ]
        """;

    [Fact]
    public async Task RdapLookup_NormalizesTheRecord_IntoATaskLessArtifact()
    {
        var rig = new Rig();
        rig.Http.Enqueue(200, RdapAnswer);

        var result = await rig.Service.RdapLookupAsync(rig.Engagement, Guid.NewGuid(), "example.com");

        Assert.True(result.Succeeded, result.Reason);
        Assert.Equal("recon.rdap:example.com", result.Name);
        Assert.Equal("application/json", result.ContentType);

        var artifact = await rig.SingleArtifactOf(result);
        Assert.Null(artifact.TaskId);
        var content = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(artifact.Content)).RootElement;
        Assert.Equal("Example Registrar, Inc.", content.GetProperty("registrar").GetString());
        Assert.Equal("2015-01-01T00:00:00Z", content.GetProperty("registered").GetString());
        Assert.Equal(["ns1.example.com", "ns2.example.com"],
            content.GetProperty("nameservers").EnumerateArray().Select(n => n.GetString()));
        Assert.True(content.GetProperty("delegationSigned").GetBoolean());
    }

    [Fact]
    public async Task RdapLookup_AFriendlyNotFound_IsAFailedRunWithTheReason()
    {
        var rig = new Rig();
        rig.Http.Enqueue(404, """{ "errorCode": 404, "title": "not found" }""");

        var result = await rig.Service.RdapLookupAsync(rig.Engagement, Guid.NewGuid(), "missing.test");

        Assert.False(result.Succeeded);
        Assert.Contains("no record", result.Reason);
        Assert.Equal(0, await rig.ArtifactCount());
    }

    [Fact]
    public async Task RdapLookup_AServiceError_IsAFailedRunWithTheStatus()
    {
        var rig = new Rig();
        rig.Http.Enqueue(503, "{}");

        var result = await rig.Service.RdapLookupAsync(rig.Engagement, Guid.NewGuid(), "example.com");

        Assert.False(result.Succeeded);
        Assert.Contains("503", result.Reason);
    }

    [Fact]
    public async Task Subdomains_DedupeFilterAndCapture_TheGrammarShapedCensus()
    {
        var rig = new Rig();
        rig.Http.Enqueue(200, CtAnswer);

        var result = await rig.Service.EnumerateSubdomainsAsync(rig.Engagement, Guid.NewGuid(), "example.com");

        Assert.True(result.Succeeded, result.Reason);
        Assert.Equal("recon.subdomains:example.com", result.Name);
        Assert.Equal("application/x-ndjson", result.ContentType);

        // Case-folded distinct, trailing dots dropped, wildcards skipped
        // (they name no concrete host), foreign names filtered -- exactly
        // the names at or under the target.
        var hosts = System.Text.Encoding.UTF8.GetString(
                (await rig.SingleArtifactOf(result)).Content)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("host").GetString())
            .ToArray();
        Assert.Equal(new[] { "api.example.com", "example.com", "www.example.com" }, hosts);
    }

    [Fact]
    public async Task Subdomains_TheNameCap_BoundsTheArtifact()
    {
        var rig = new Rig();
        rig.Options.MaxSubdomainNames = 2;
        rig.Http.Enqueue(200, CtAnswer);

        var result = await rig.Service.EnumerateSubdomainsAsync(rig.Engagement, Guid.NewGuid(), "example.com");

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Findings);
    }

    [Fact]
    public async Task Subdomains_AMirrorError_IsAFailedRunWithTheStatus()
    {
        var rig = new Rig();
        rig.Http.Enqueue(500, "{}");

        var result = await rig.Service.EnumerateSubdomainsAsync(rig.Engagement, Guid.NewGuid(), "example.com");

        Assert.False(result.Succeeded);
        Assert.Contains("500", result.Reason);
    }

    [Fact]
    public async Task Scan_ReportsExactlyThePortsThatAnswered()
    {
        // One listening socket is the open port; a bind-then-stop socket
        // names a port that is closed by scan time.
        var listener = TcpListener.Create(0);
        listener.Start();
        var openPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var probe = TcpListener.Create(0);
        probe.Start();
        var closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        try
        {
            var rig = new Rig();
            var accept = Task.Run(async () =>
            {
                using var _ = await listener.AcceptTcpClientAsync();
            });

            var result = await rig.Service.ScanAsync(
                rig.Engagement, Guid.NewGuid(), "127.0.0.1", [openPort, closedPort], "test");

            Assert.True(result.Succeeded, result.Reason);
            Assert.Equal($"recon.portscan:127.0.0.1:test", result.Name);
            var lines = System.Text.Encoding.UTF8.GetString(
                    (await rig.SingleArtifactOf(result)).Content)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement)
                .ToArray();
            var finding = Assert.Single(lines);
            Assert.Equal(openPort, finding.GetProperty("port").GetInt32());
            Assert.Equal("open", finding.GetProperty("state").GetString());
            await accept;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Lookups_RideTheConfiguredService_ExactlyOncePerRun()
    {
        var rig = new Rig();
        rig.Http.Enqueue(200, RdapAnswer);

        await rig.Service.RdapLookupAsync(rig.Engagement, Guid.NewGuid(), "example.com");

        var request = Assert.Single(rig.Http.Requests);
        Assert.Equal("https://rdap-stub.test/domain/example.com", request.ToString());
    }

    private const string DohAnswerAWithCname =
        """{"Status":0,"Answer":[{"name":"www.example.com.","type":5,"TTL":300,"data":"cdn.example.com."},{"name":"cdn.example.com.","type":1,"TTL":60,"data":"203.0.113.10"}]}""";

    private const string DohAnswerAaaa =
        """{"Status":0,"Answer":[{"name":"www.example.com.","type":28,"TTL":60,"data":"2001:db8::1"}]}""";

    private const string DohAnswerNxdomain =
        """{"Status":3,"Comment":"NXDOMAIN"}""";

    [Fact]
    public async Task Resolve_SingleName_CollectsAddressesAndTheAlias()
    {
        var rig = new Rig();
        rig.Http.EnqueueResolution("www.example.com", DohAnswerAWithCname);
        rig.Http.EnqueueResolution("www.example.com", DohAnswerAaaa);

        var result = await rig.Service.ResolveAsync(rig.Engagement, Guid.NewGuid(), ["www.example.com"]);

        Assert.True(result.Succeeded, result.Reason);
        Assert.Equal("recon.resolve:www.example.com", result.Name);
        var line = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(
            (await rig.SingleArtifactOf(result)).Content)).RootElement;
        Assert.Equal("www.example.com", line.GetProperty("host").GetString());
        Assert.Equal(["203.0.113.10", "2001:db8::1"],
            line.GetProperty("addresses").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("cdn.example.com", line.GetProperty("cname").GetString());
    }

    [Fact]
    public async Task Resolve_ANegativeSingle_IsAFailedRunWithTheNegativeNamed()
    {
        var rig = new Rig();
        rig.Http.EnqueueResolution("missing.example.com", DohAnswerNxdomain);
        rig.Http.EnqueueResolution("missing.example.com", DohAnswerNxdomain);

        var result = await rig.Service.ResolveAsync(rig.Engagement, Guid.NewGuid(), ["missing.example.com"]);

        Assert.False(result.Succeeded);
        Assert.Contains("no address records", result.Reason);
        Assert.Equal(0, await rig.ArtifactCount());
    }

    [Fact]
    public async Task Resolve_AnIpTarget_RecordsItsPtrName()
    {
        var rig = new Rig();
        rig.Http.EnqueueResolution("10.113.0.203.in-addr.arpa",
            """{"Status":0,"Answer":[{"name":"10.113.0.203.in-addr.arpa.","type":12,"TTL":300,"data":"web01.example.com."}]}""");

        var result = await rig.Service.ResolveAsync(rig.Engagement, Guid.NewGuid(), ["203.0.113.10"]);

        Assert.True(result.Succeeded, result.Reason);
        var line = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(
            (await rig.SingleArtifactOf(result)).Content)).RootElement;
        Assert.Equal("web01.example.com", line.GetProperty("host").GetString());
        Assert.Equal(["203.0.113.10"],
            line.GetProperty("addresses").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task Resolve_Bulk_RecordsTheNamesThatLive_AndCountsTheMisses()
    {
        var rig = new Rig();
        rig.Http.EnqueueResolution("www.example.com", DohAnswerAWithCname);
        rig.Http.EnqueueResolution("www.example.com", DohAnswerAaaa);
        rig.Http.EnqueueResolution("missing.example.com", DohAnswerNxdomain);
        rig.Http.EnqueueResolution("missing.example.com", DohAnswerNxdomain);

        var result = await rig.Service.ResolveAsync(
            rig.Engagement, Guid.NewGuid(), ["www.example.com", "missing.example.com"]);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Findings);
        Assert.Equal("recon.resolve:2-names", result.Name);
        Assert.Contains("1 of 2 names resolved", result.Summary);
    }

    [Fact]
    public async Task Whois_CapturesTheAnswerVerbatim()
    {
        // A loopback whois server: one line in, the fixed record back.
        var listener = TcpListener.Create(0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, System.Text.Encoding.ASCII);
            await reader.ReadLineAsync();
            var answer = System.Text.Encoding.ASCII.GetBytes("Domain: example.com\r\nRegistrar: Example Registrar\r\n");
            await stream.WriteAsync(answer);
            await stream.FlushAsync();
        });
        try
        {
            var rig = new Rig();
            rig.Options.WhoisServer = $"127.0.0.1:{port}";

            var result = await rig.Service.WhoisLookupAsync(rig.Engagement, Guid.NewGuid(), "example.com");

            Assert.True(result.Succeeded, result.Reason);
            Assert.Equal("recon.whois:example.com", result.Name);
            Assert.Equal("text/plain", result.ContentType);
            Assert.Contains("Registrar: Example Registrar",
                System.Text.Encoding.UTF8.GetString((await rig.SingleArtifactOf(result)).Content));
            await accepted;
        }
        finally
        {
            listener.Stop();
        }
    }
}
