using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState.Pki;
using Rod.Transport;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance for the stage-1 stager output class (architecture.md Sec 6):
/// building a stager yields a runnable stage-1 that pulls its stage-2 and
/// enrols. The test stands up a real teamserver, builds a stage-2 implant and
/// then a stager referencing it through the operator build API, downloads the
/// stager executable, and runs it as a real subprocess with the deployment
/// credential. The stager fetches the stage-2 over the anonymous listener
/// (token verified, not spent), verifies the baked sha256, executes the
/// fetched artifact -- and the stage-2 spends the token at enroll and appears
/// on the roster.
/// </summary>
public class StagerEndToEndTests
{
    [DotNetFact]
    public async Task Stager_FetchesStage2_AndTheStage2_Enrols()
    {
        await using var env = await TestEnv.StartAsync();
        var secret = await env.MintStagerTokenAsync();

        // Build the stage-2 first: a linux/amd64 single-file implant baked for
        // this teamserver's enroll endpoint, sleeping at a 1s beacon cadence.
        var enrollUrl = $"http://127.0.0.1:{env.HttpPort}/implants/enroll";
        var stage2 = await env.BuildAsync(new
        {
            Class = "Stage2",
            TargetOs = "linux",
            TargetArch = "amd64",
            Endpoint = enrollUrl,
            SleepSeconds = 1.0,
            JitterSeconds = 0.0,
        });
        Assert.Equal("Stage2", stage2.Class);

        // Then the stager: the minimal loader with the stage-2's id and
        // fingerprint baked in as its fetch reference.
        var stager = await env.BuildAsync(new
        {
            Class = "Stager",
            TargetOs = "linux",
            TargetArch = "amd64",
            Endpoint = enrollUrl,
            Stage2PayloadId = stage2.ArtifactId,
        });
        Assert.Equal("Stager", stager.Class);

        // Download the stager executable and run it as the operator would drop
        // it on a target: a bare binary plus the deployment credential. The
        // beacon address is passed explicitly because the test topology splits
        // the plain-HTTP enroll listener from the mTLS beacon port.
        var outDir = Path.Combine(Path.GetTempPath(), "rod-e2e-stager-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var stagerPath = Path.Combine(outDir, "Rod.Stager");
        {
            using var download = await env.Http.GetAsync(
                $"/engagements/{stager.EngagementId}/payloads/{stager.ArtifactId}");
            download.EnsureSuccessStatusCode();
            await File.WriteAllBytesAsync(stagerPath, await download.Content.ReadAsByteArrayAsync());
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(stagerPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var stderr = new StringBuilder();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = stagerPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            // The loader takes the token at run time, never baked: it forwards
            // the credential (and the beacon address and CA pin the test
            // topology needs) to the stage-2 through the process environment.
            Environment =
            {
                ["ROD_STAGER_TOKEN"] = secret,
                ["ROD_BEACON_URL"] = $"127.0.0.1:{env.MtlsPort}",
                ["ROD_CA_CERT"] = env.CACertFile,
            },
        });
        Assert.NotNull(process);
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        process.BeginErrorReadLine();

        try
        {
            // The acceptance point: the stage-2 enrolled through the stager's
            // fetch-and-run, and it is live on the roster. The loader's own
            // exit is not the AC (it waits on the stage-2, which runs until
            // killed); the enrolled, online stage-2 is.
            var implantId = await WaitForStage2OnlineAsync(env, stager.EngagementId, TimeSpan.FromSeconds(60), stderr);
            Assert.False(string.IsNullOrEmpty(implantId));
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                process.WaitForExit(5000);
            }
            try { Directory.Delete(outDir, recursive: true); } catch { }
        }
    }

    // Polls the engagement's implant listing until a Stage-2 is enrolled and
    // online -- the state the stager's fetch-and-run exists to produce.
    private static async Task<string> WaitForStage2OnlineAsync(
        TestEnv env, string engagementId, TimeSpan deadline, StringBuilder stderr)
    {
        var end = DateTimeOffset.UtcNow + deadline;
        while (DateTimeOffset.UtcNow < end)
        {
            try
            {
                var implants = await env.Http.GetFromJsonAsync<ImplantEndpoints.ImplantResponse[]>(
                    $"/engagements/{engagementId}/implants");
                var online = implants?.FirstOrDefault(i => i.Class == "Stage2" && i.IsOnline);
                if (online is not null)
                    return online.ImplantId;
            }
            catch (HttpRequestException)
            {
                // The listing read races the enrollment; retry.
            }
            await Task.Delay(500);
        }
        throw new TimeoutException(
            "The stage-2 enrolled by the stager did not appear online. Stager stderr:\n" + stderr);
    }

    private sealed class BuildBody
    {
        public string ArtifactId { get; set; } = "";
        public string EngagementId { get; set; } = "";
        public string Class { get; set; } = "";
    }

    /// <summary>
    /// A real Kestrel teamserver with the mTLS implant endpoint bound, plus a
    /// plain-HTTP operator/enroll API, logged in and with the dev CA exported
    /// for the beacon mTLS pin. Mirrors the DotNetImplantTests harness.
    /// </summary>
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
        public int MtlsPort { get; private set; }
        public int HttpPort { get; private set; }
        public string CACertFile { get; private set; } = null!;

        public static async Task<TestEnv> StartAsync()
        {
            var env = new TestEnv();
            env.MtlsPort = GetFreeTcpPort();
            env.HttpPort = GetFreeTcpPort();

            var config = AuthenticatedHost.BuildConfig();
            env.Host = TransportHost.CreateHostBuilder(
                    configureServices: services => AuthenticatedHost.ComposeServices(services, config),
                    mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints),
                    configuration: config)
                .ConfigureWebHost(webBuilder => webBuilder
                    .UseRodMtls(env.MtlsPort)
                    .ConfigureKestrel(kestrel => kestrel.ListenLocalhost(env.HttpPort)))
                .Build();
            await env.Host.StartAsync();

            var ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>().GetCaCertificate();
            env.CACertFile = Path.Combine(Path.GetTempPath(), "rod-test-ca-" + Guid.NewGuid().ToString("N") + ".pem");
            var caPem = "-----BEGIN CERTIFICATE-----\n"
                + Convert.ToBase64String(ca.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert),
                    Base64FormattingOptions.InsertLineBreaks)
                + "\n-----END CERTIFICATE-----\n";
            await File.WriteAllTextAsync(env.CACertFile, caPem);

            env.Http = new HttpClient(new CookieHandler(new HttpClientHandler()))
            {
                BaseAddress = new Uri($"http://127.0.0.1:{env.HttpPort}"),
            };
            await AuthenticatedHost.LoginAsync(env.Http);
            return env;
        }

        public async Task<string> MintStagerTokenAsync()
        {
            var createResponse = await Http.PostAsJsonAsync("/engagements", new EngagementEndpoints.CreateEngagementRequest(
                Name: "Operation Stager Slice"));
            createResponse.EnsureSuccessStatusCode();
            var created = await createResponse.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
            EngagementId = created!.EngagementId;

            var mintResponse = await Http.PostAsync($"/engagements/{EngagementId}/stager-tokens", content: null);
            mintResponse.EnsureSuccessStatusCode();
            var token = await mintResponse.Content.ReadFromJsonAsync<EngagementEndpoints.StagerTokenResponse>();
            return token!.Secret;
        }

        public string? EngagementId { get; private set; }

        public async Task<BuildBody> BuildAsync(object request)
        {
            Assert.NotNull(EngagementId);
            var response = await Http.PostAsJsonAsync($"/engagements/{EngagementId}/payloads", request);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<BuildBody>();
            Assert.NotNull(body);
            return body!;
        }

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            try { if (CACertFile is not null && File.Exists(CACertFile)) File.Delete(CACertFile); } catch { }
            if (Host is not null)
                await Host.StopAsync();
            Host?.Dispose();
        }
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
