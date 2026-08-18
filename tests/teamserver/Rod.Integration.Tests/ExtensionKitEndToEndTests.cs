using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.BuildPipeline.PayloadBuild;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.Tradecraft;
using Rod.Transport;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance for the tradecraft extension kit (architecture.md Sec 5.3,
/// Sec 10.2; extending/tradecraft.md), driven end to end against a real
/// teamserver. An out-of-tree evasion handler source is dropped into a
/// configured extension directory, the .NET build unit produces the artifact,
/// and the artifact runs as a real subprocess: it must advertise
/// <c>evasion.avoid</c> at handshake -- the contract-only verbs no class gates
/// ride along in the bake, so the roster reflects reality -- and a tasked
/// <c>evasion.avoid</c> must complete with the compiled-in handler's output.
/// The stand-in handler implements no evasion technique: it is plumbing
/// evidence for the seam, not tradecraft (the Sec 13 boundary keeps the real
/// thing out-of-tree).
/// </summary>
public class ExtensionKitEndToEndTests
{
    [DotNetFact]
    public async Task ExtensionBuiltArtifact_AdvertisesAndRunsTheOutOfTreeVerb_EndToEnd()
    {
        await using var env = await TestEnv.StartAsync();
        var secret = await env.MintStagerTokenAsync();

        // The extension directory: one out-of-tree handler source, exactly the
        // drop-in shape an operator maintains -- no fork of the implant tree.
        var extensionDir = Path.Combine(Path.GetTempPath(), "rod-ext-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extensionDir);
        var artifactPath = Path.Combine(Path.GetTempPath(), "rod-ext-e2e-artifact-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string marker = "rod-ext-marker";
            await File.WriteAllTextAsync(Path.Combine(extensionDir, "DemoAvoidHandler.cs"), """
                using Rod.Implant.Internal;
                using Rod.V1;

                namespace MyTradecraft.Evasion;

                // An out-of-tree stand-in for an evasion module's implant half: no
                // tradecraft, just a marker echo proving the verb dispatched to the
                // compiled-in handler.
                internal sealed class DemoAvoidHandler : ICapabilityHandler
                {
                    public string Verb => "evasion.avoid";

                    public HandlerResult Handle(string arguments)
                        => (TaskOutcome.Succeeded, "avoid-ack: " + arguments);
                }
                """);

            // The build unit resolves the reference implant tree itself; only the
            // extension directory and the live endpoint differ from a stock build.
            // The profile bakes the live enroll endpoint, a 1s check-in cadence,
            // and the class verb set plus the ungated contract-only verbs.
            var unit = new DotNetBuildUnit(extensionDir: extensionDir);
            var artifact = await unit.BuildAsync(new BuildParams(
                EngagementId.New(),
                OperatorId.New(),
                ImplantClass.Stage2,
                new TargetProfile(HostOperatingSystem, HostArchitecture),
                new TransportProfile($"http://127.0.0.1:{env.HttpPort}/implants/enroll", "/beacon"),
                new BeaconProfile(TimeSpan.FromSeconds(1), TimeSpan.Zero, DateTimeOffset.UtcNow.AddDays(1))));

            // The artifact is the self-contained single-file executable's bytes;
            // write them out and mark the file executable so it runs as a process.
            await File.WriteAllBytesAsync(artifactPath, artifact.Content);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(artifactPath,
                    System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite | System.IO.UnixFileMode.UserExecute);

            var implantProc = StartArtifact(artifactPath, env, secret);
            var stderr = new StringBuilder();
            implantProc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            implantProc.BeginErrorReadLine();
            using (implantProc)
            {
                try
                {
                    var (engagementId, implantId) = await WaitForImplantOnlineAsync(env, deadline: TimeSpan.FromSeconds(60), stderr);

                    // The acceptance point for the advertisement: the handshake
                    // roster shows the out-of-tree contract-only verb alongside the
                    // class verbs -- and still not the contract verbs it carries no
                    // handler for (evasion.unload), because the advertised set
                    // stays the baked verbs intersected with the compiled handlers.
                    var presence = await env.Http.GetFromJsonAsync<PresenceEndpoints.PresenceRecordResponse>(
                        $"/engagements/{engagementId}/presence/{implantId}");
                    Assert.NotNull(presence);
                    Assert.Contains("evasion.avoid", presence!.Capabilities);
                    Assert.Contains("shell.exec", presence.Capabilities);
                    Assert.DoesNotContain("evasion.unload", presence.Capabilities);

                    // The acceptance point for the dispatch: the registry-widened
                    // task gate admits the verb, the artifact runs the compiled-in
                    // handler, and the operator sees its output.
                    var issued = await env.Http.PostAsJsonAsync(
                        $"/engagements/{engagementId}/tasks",
                        new { ImplantId = implantId, Verb = "evasion.avoid", Arguments = marker });
                    issued.EnsureSuccessStatusCode();
                    var issuedBody = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();
                    Assert.NotNull(issuedBody);

                    await WaitUntilAsync(async () =>
                    {
                        var fetched = await env.Http.GetFromJsonAsync<TaskBody>(
                            $"/engagements/{engagementId}/tasks/{issuedBody!.TaskId}");
                        return fetched is { Status: "Completed", Outcome: "Succeeded" }
                            && (fetched.Output ?? string.Empty).Contains("avoid-ack: " + marker);
                    }, deadline: TimeSpan.FromSeconds(60));
                }
                finally
                {
                    if (!implantProc.HasExited)
                    {
                        try { implantProc.Kill(entireProcessTree: true); } catch { }
                        implantProc.WaitForExit(5000);
                    }
                }
            }
        }
        finally
        {
            try { if (Directory.Exists(extensionDir)) Directory.Delete(extensionDir, recursive: true); } catch { }
            try { if (File.Exists(artifactPath)) File.Delete(artifactPath); } catch { }
        }
    }

    // The build contract's Go-style os/arch pair for the host running the test:
    // the artifact has to execute here, so it builds for this machine.
    private static string HostOperatingSystem =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";

    private static string HostArchitecture =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            _ => "x86",
        };

    // Starts the built artifact as a real subprocess. The baked profile seeds
    // the endpoint and cadence; the flags carry what no bake holds (the token,
    // the mTLS beacon port, the CA pin) exactly the way a deployment would run
    // the artifact.
    private static Process StartArtifact(string artifactPath, TestEnv env, string token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = artifactPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-beacon-url");
        psi.ArgumentList.Add($"127.0.0.1:{env.MtlsPort}");
        psi.ArgumentList.Add("-token");
        psi.ArgumentList.Add(token);
        psi.ArgumentList.Add("-ca-cert");
        psi.ArgumentList.Add(env.CACertFile);
        psi.ArgumentList.Add("-sleep");
        psi.ArgumentList.Add("1s");
        psi.ArgumentList.Add("-jitter");
        psi.ArgumentList.Add("0s");
        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the built artifact.");
    }

    // Polls the presence query until exactly one implant is online in some
    // engagement, then returns (engagementId, implantId); the engagement is
    // resolved from the enrollment side, the same as the other subprocess
    // acceptance tests.
    private static async Task<(string EngagementId, string ImplantId)> WaitForImplantOnlineAsync(
        TestEnv env, TimeSpan deadline, StringBuilder stderr)
    {
        var end = DateTimeOffset.UtcNow + deadline;
        while (DateTimeOffset.UtcNow < end)
        {
            var engagements = await env.Http.GetFromJsonAsync<EngagementSummary[]>("/engagements")
                ?? Array.Empty<EngagementSummary>();
            foreach (var eng in engagements)
            {
                var presence = await env.Http.GetFromJsonAsync<PresenceEndpoints.PresenceRecordResponse[]>(
                    $"/engagements/{eng.EngagementId}/presence");
                if (presence is { Length: > 0 } online)
                {
                    return (eng.EngagementId, online[0].ImplantId);
                }
            }
            await Task.Delay(500);
        }
        throw new TimeoutException(
            "The extension-built artifact did not appear online within the deadline. Artifact stderr:\n" + stderr);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan deadline)
    {
        var end = DateTimeOffset.UtcNow + deadline;
        while (DateTimeOffset.UtcNow < end)
        {
            if (await condition())
                return;
            await Task.Delay(250);
        }
        throw new TimeoutException("Condition was not met within the deadline.");
    }

    // Minimal DTOs for the JSON round-trip; the transport owns the wire shape.

    private sealed class TaskIssuedBody
    {
        public string TaskId { get; set; } = "";
    }

    private sealed class TaskBody
    {
        public string Status { get; set; } = "";
        public string? Output { get; set; }
        public string? Outcome { get; set; }
    }

    private sealed class EngagementSummary
    {
        public string EngagementId { get; set; } = "";
    }

    /// <summary>
    /// A real Kestrel teamserver with the mTLS implant endpoint bound and the
    /// plain-HTTP operator/enroll API, composed with the tradecraft layer so the
    /// registry-backed task gate admits the contract-only verbs the placeholder
    /// holds -- the shape a deployment that tasks out-of-tree tradecraft runs.
    /// Standalone twin of the other subprocess tests' TestEnv.
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
                    configureServices: services => AuthenticatedHost.ComposeServices(
                        services, config, extra: inner => inner.AddRodTradecraft()),
                    mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints),
                    configuration: config)
                .ConfigureWebHost(webBuilder => webBuilder
                    .UseRodMtls(env.MtlsPort)
                    .ConfigureKestrel(kestrel => kestrel.ListenLocalhost(env.HttpPort)))
                .Build();
            await env.Host.StartAsync();

            // The dev CA as a PEM file the artifact trusts as the mTLS server
            // identity (the dev CA doubles as the server cert).
            var ca = env.Host.Services.GetRequiredService<Rod.CoreState.Pki.IImplantCertificateAuthority>().GetCaCertificate();
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
                Name: "Operation Extension Kit"));
            createResponse.EnsureSuccessStatusCode();
            var created = await createResponse.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();

            var mintResponse = await Http.PostAsync($"/engagements/{created!.EngagementId}/stager-tokens", content: null);
            mintResponse.EnsureSuccessStatusCode();
            var token = await mintResponse.Content.ReadFromJsonAsync<EngagementEndpoints.StagerTokenResponse>();
            return token!.Secret;
        }

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            try { if (CACertFile is not null && File.Exists(CACertFile)) File.Delete(CACertFile); } catch { }
            if (Host is not null)
                await Host.StopAsync();
            Host?.Dispose();
        }

        private static int GetFreeTcpPort()
        {
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
