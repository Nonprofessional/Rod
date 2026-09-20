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
/// A fact that runs only when a Rust toolchain (cargo) is on PATH -- the
/// twin of DotNetFact for the Rust reference implant's legs.
/// </summary>
public sealed class RustFactAttribute : FactAttribute
{
    public RustFactAttribute()
    {
        try
        {
            var probe = Process.Start(new ProcessStartInfo
            {
                FileName = "cargo",
                ArgumentList = { "--version" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            probe?.WaitForExit(5000);
            if (probe is null || probe.ExitCode != 0)
                Skip = "cargo is not usable on PATH";
        }
        catch
        {
            Skip = "cargo is not on PATH";
        }
    }
}

/// <summary>
/// Acceptance for the Rust reference implant (architecture.md Sec 12.2): the
/// reach implant must ride the same wire the .NET reference rides. The test
/// stands up a real teamserver, mints a deployment credential, builds the
/// Rust implant with cargo, and runs it in its dev shape (ROD_* environment)
/// against the anonymous enroll listener -- then the acceptance is the full
/// round trip: it enrolls, goes online, and a dispatched shell.exec completes
/// with its output. Cross-implant interoperability is the product; this is
/// its proof for the second language.
/// </summary>
public class RustImplantEndToEndTests
{
    [RustFact]
    public async Task RustImplant_Enrols_GoesOnline_AndRunsTasking()
    {
        var rustDir = FindRustTree()
            ?? throw new InvalidOperationException("the Rust implant tree was not found from the test assembly");
        // The dev-shape binary: cargo release build of the checked-in tree.
        var build = Process.Start(new ProcessStartInfo
        {
            FileName = "cargo",
            WorkingDirectory = rustDir,
            ArgumentList = { "build", "--release" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        var buildOutput = (await build!.StandardOutput.ReadToEndAsync())
            + await build.StandardError.ReadToEndAsync();
        build.WaitForExit(300_000);
        Assert.True(build.ExitCode == 0, $"cargo build failed:\n{buildOutput}");
        var binary = Path.Combine(rustDir, "target", "release", "rod-implant");
        Assert.True(File.Exists(binary), $"cargo reported success but {binary} is missing");

        await using var env = await TestEnv.StartAsync();
        await env.CreateEngagementAsync();

        // The deployment credential: a manual mint (the rotation shape), spent
        // by the dev-shape enroll below.
        var mint = await env.Http.PostAsync(
            $"/engagements/{env.EngagementId}/stager-tokens", content: null);
        mint.EnsureSuccessStatusCode();
        var token = await mint.Content.ReadFromJsonAsync<MintedToken>();
        Assert.False(string.IsNullOrEmpty(token?.Secret));

        var stderr = new StringBuilder();
        var start = new ProcessStartInfo
        {
            FileName = binary,
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        start.Environment["ROD_ENROLL_URL"] = $"http://127.0.0.1:{env.HttpPort}/implants/enroll";
        start.Environment["ROD_STAGER_TOKEN"] = token!.Secret;
        start.Environment["ROD_SLEEP"] = "1";
        start.Environment["ROD_JITTER"] = "0";
        start.Environment["ROD_ENVELOPE"] = "none";
        start.Environment["ROD_VERBS"] = "shell.exec,fs.list,file.pull,beacon.sleep";
        using var process = Process.Start(start);
        Assert.NotNull(process);
        process!.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        process.BeginErrorReadLine();

        try
        {
            // The first acceptance: the Rust implant enrolled and went live on
            // the roster, speaking the shared wire.
            var implantId = await WaitForOnlineAsync(env, TimeSpan.FromSeconds(60), stderr);
            Assert.False(string.IsNullOrEmpty(implantId));

            // The second acceptance: a dispatched task rides the wire down, the
            // Rust handler runs it, and the result rides back up.
            var marker = "rod-rust-smoke-" + Guid.NewGuid().ToString("N")[..8];
            var issued = await env.Http.PostAsJsonAsync(
                $"/engagements/{env.EngagementId}/tasks",
                new TaskEndpoints.IssueTaskRequest(implantId, "shell.exec", $"echo {marker}"));
            issued.EnsureSuccessStatusCode();
            var task = await issued.Content.ReadFromJsonAsync<TaskBody>();
            Assert.NotNull(task);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var read = await env.Http.GetFromJsonAsync<TaskBody>(
                    $"/engagements/{env.EngagementId}/tasks/{task!.TaskId}");
                if (read?.Status == "Completed")
                {
                    Assert.Equal("Succeeded", read.Outcome);
                    Assert.Contains(marker, read.Output);
                    return;
                }
                await Task.Delay(500);
            }
            Assert.Fail($"the dispatched task did not complete. Implant stderr:\n{stderr}");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                process.WaitForExit(5000);
            }
        }
    }

    [RustFact]
    public async Task RustImplant_BuildsThroughThePipeline_AndRunsFromTheBake()
    {
        await using var env = await TestEnv.StartAsync();
        await env.CreateEngagementAsync();

        // The full operable loop through the operator API: a language "rust"
        // build request bakes the profile (sealed contacts on, the default
        // posture) and mints the credential; the artifact is downloaded and
        // run in its fielded shape -- no arguments, no environment, the bake
        // is the configuration. The sealed contact path (AES-256-GCM bodies
        // under the per-artifact key, counter over the frames) is what this
        // leg proves beyond the dev-shape plaintext one.
        var enrollUrl = $"http://127.0.0.1:{env.HttpPort}/implants/enroll";
        var built = await env.Http.PostAsJsonAsync(
            $"/engagements/{env.EngagementId}/payloads",
            new PayloadEndpoints.BuildPayloadRequest(
                Language: "rust",
                Class: "Stage2",
                TargetOs: "linux",
                TargetArch: "amd64",
                Endpoint: enrollUrl,
                UriPath: null,
                SleepSeconds: 1.0,
                JitterSeconds: 0.0,
                KillDate: null,
                Mode: "poll"));
        Assert.True(built.IsSuccessStatusCode, await built.Content.ReadAsStringAsync());
        var artifact = await built.Content.ReadFromJsonAsync<ArtifactBody>();
        Assert.NotNull(artifact);

        var outDir = Path.Combine(Path.GetTempPath(), "rod-e2e-rust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var binaryPath = Path.Combine(outDir, "rod-implant");
        Process? process = null;
        try
        {
            using (var download = await env.Http.GetAsync(
                $"/engagements/{artifact!.EngagementId}/payloads/{artifact.ArtifactId}"))
            {
                download.EnsureSuccessStatusCode();
                await File.WriteAllBytesAsync(binaryPath, await download.Content.ReadAsByteArrayAsync());
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(binaryPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var stderr = new StringBuilder();
            process = Process.Start(new ProcessStartInfo
            {
                FileName = binaryPath,
                UseShellExecute = false,
                RedirectStandardError = true,
            });
            Assert.NotNull(process);
            process!.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            process.BeginErrorReadLine();

            var implantId = await WaitForOnlineAsync(env, TimeSpan.FromSeconds(90), stderr);
            Assert.False(string.IsNullOrEmpty(implantId));

            var marker = "rod-rust-baked-" + Guid.NewGuid().ToString("N")[..8];
            var issued = await env.Http.PostAsJsonAsync(
                $"/engagements/{env.EngagementId}/tasks",
                new TaskEndpoints.IssueTaskRequest(implantId, "shell.exec", $"echo {marker}"));
            issued.EnsureSuccessStatusCode();
            var task = await issued.Content.ReadFromJsonAsync<TaskBody>();

            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var read = await env.Http.GetFromJsonAsync<TaskBody>(
                    $"/engagements/{env.EngagementId}/tasks/{task!.TaskId}");
                if (read?.Status == "Completed")
                {
                    Assert.Equal("Succeeded", read.Outcome);
                    Assert.Contains(marker, read.Output);
                    return;
                }
                await Task.Delay(500);
            }
            Assert.Fail($"the dispatched task did not complete. Implant stderr:\n{stderr}");
        }
        finally
        {
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                process.WaitForExit(5000);
            }
            process?.Dispose();
            try { Directory.Delete(outDir, recursive: true); } catch { }
        }
    }

    private sealed record ArtifactBody
    {
        public string ArtifactId { get; set; } = "";
        public string EngagementId { get; set; } = "";
    }

    private static async Task<string> WaitForOnlineAsync(
        TestEnv env, TimeSpan deadline, StringBuilder stderr)
    {
        var end = DateTimeOffset.UtcNow + deadline;
        while (DateTimeOffset.UtcNow < end)
        {
            try
            {
                var implants = await env.Http.GetFromJsonAsync<ImplantEndpoints.ImplantResponse[]>(
                    $"/engagements/{env.EngagementId}/implants");
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
        throw new TimeoutException("the Rust implant did not appear online. Stderr:\n" + stderr);
    }

    private static string? FindRustTree()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "implant", "rust");
            if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(dir.FullName, "src", "teamserver")))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private sealed record MintedToken(string Secret);

    private sealed record TaskBody
    {
        public string TaskId { get; set; } = "";
        public string Status { get; set; } = "";
        public string? Outcome { get; set; }
        public string? Output { get; set; }
    }

    /// <summary>
    /// A real Kestrel teamserver with the plain-HTTP operator/enroll API and
    /// the mTLS implant endpoint, logged in; the StagerEndToEnd harness shape.
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
            var createResponse = await Http.PostAsJsonAsync("/engagements",
                new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Rust Slice"));
            createResponse.EnsureSuccessStatusCode();
            var created = await createResponse.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
            EngagementId = created!.EngagementId;
        }

        public string? EngagementId { get; private set; }

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            if (Host is not null)
                await Host.StopAsync();
            Host?.Dispose();
        }
    }
}
