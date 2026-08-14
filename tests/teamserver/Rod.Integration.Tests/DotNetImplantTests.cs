using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Transport;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Roadmap  acceptance: the reference .NET implant checks in and tasks
/// end-to-end. A real teamserver (Kestrel mTLS beacon endpoint + plain-HTTP
/// operator/enroll API) is stood up, the reference .NET implant is published to a
/// temp dir and launched against it as a real subprocess (dotnet Rod.Implant.dll),
/// and the test drives the full slice the AC names: the implant enrolls over HTTP,
/// beacons over mTLS, completes the handshake, runs a dispatched shell.exec task,
/// and the operator sees the captured output plus the TaskCompleted audit event.
/// This is the same round-trip HandshakePresenceTests/TaskRoundTripTests prove
/// in-process, now driven by the actual .NET implant assembly.
/// </summary>
public class DotNetImplantTests
{
    /// <summary>
    /// Roadmap  acceptance: a parent implant derives a child on lateral.move
    /// that enrolls back, and the child's parentage is recorded server-side. The
    /// parent runs as a real subprocess; the operator tasks it lateral.move with a
    /// second (child) stager token in the arguments; the handler generates a fresh
    /// child keypair and enrolls a child naming the parent. The operator implant
    /// listing must then show the child with its ParentImplantId set to the parent.
    /// This is the implant-driven round-trip the AC names -- the in-process
    /// ChildEnrollmentHttpTests already prove the server-side parentage model.
    /// </summary>
    [DotNetFact]
    public async Task DotNetImplant_DerivesChildOnLateralMove_EndToEnd()
    {
        await using var env = await TestEnv.StartAsync();

        // One engagement, two single-use tokens: the parent redeems one at its own
        // enroll; the child redeems the second when the parent derives it. Mint both
        // against the same engagement up front.
        var (engagementId, parentToken, childToken) = await env.MintEngagementWithTwoTokensAsync();

        var implantSource = LocateImplantSource();
        var implantDir = PublishImplant(implantSource);
        var implantDll = Path.Combine(implantDir, "Rod.Implant.dll");
        var implantProc = StartImplant(implantDll, env, parentToken,
            sleep: TimeSpan.FromSeconds(1), jitter: TimeSpan.Zero);
        var stderr = new StringBuilder();
        implantProc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        implantProc.BeginErrorReadLine();
        using (implantProc)
        {
            try
            {
                var (_, parentId) = await WaitForImplantOnlineAsync(env, deadline: TimeSpan.FromSeconds(60), stderr);

                // Operator tasks the parent with lateral.move, passing the child's
                // stager token as the argument. The handler derives a child that
                // enrolls back naming the parent (architecture.md Sec 10.1).
                var issued = await env.Http.PostAsJsonAsync(
                    $"/engagements/{engagementId}/tasks",
                    new { ImplantId = parentId, Verb = "lateral.move", Arguments = childToken });
                issued.EnsureSuccessStatusCode();
                var issuedBody = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();
                Assert.NotNull(issuedBody);
                Assert.Equal("lateral.move", issuedBody!.Verb);

                // The handler enrolls the child and reports its id on the first line
                // of the task output. Poll until the task completes with a child id.
                string childId = "";
                await WaitUntilAsync(async () =>
                {
                    var fetched = await env.Http.GetFromJsonAsync<TaskBody>(
                        $"/engagements/{engagementId}/tasks/{issuedBody!.TaskId}");
                    if (fetched is not { Status: "Completed", Outcome: "Succeeded" })
                        return false;
                    var line = (fetched.Output ?? string.Empty).Trim();
                    childId = line.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    return childId.Length > 0;
                }, deadline: TimeSpan.FromSeconds(60));

                // The acceptance point: the child is recorded server-side with its
                // ParentImplantId set to the parent. Read it back through the operator
                // implant listing the way an operator would.
                await WaitUntilAsync(async () =>
                {
                    var listed = await env.Http.GetFromJsonAsync<ImplantEndpoints.ImplantResponse[]>(
                        $"/engagements/{engagementId}/implants");
                    var childRow = listed?.FirstOrDefault(i => i.ImplantId == childId);
                    return childRow is not null && childRow.ParentImplantId == parentId;
                }, deadline: TimeSpan.FromSeconds(30));
            }
            finally
            {
                if (!implantProc.HasExited)
                {
                    try { implantProc.Kill(entireProcessTree: true); } catch { }
                    implantProc.WaitForExit(5000);
                }
                try { if (Directory.Exists(implantDir)) Directory.Delete(implantDir, recursive: true); } catch { }
            }
        }
    }

    [DotNetFact]
    public async Task DotNetImplant_EnrollsBeaconsAndTasks_EndToEnd()
    {
        await using var env = await TestEnv.StartAsync();

        // Mint a stager token for a fresh engagement; the implant redeems it at
        // enroll (architecture.md Sec 9). The token resolves the engagement.
        var secret = await env.MintStagerTokenAsync();

        // Publish the reference implant once for the test into a temp dir, then run
        // it as a real subprocess: enroll over HTTP, beacon over mTLS. A short
        // sleep keeps the check-in prompt so the round-trip resolves quickly.
        var implantSource = LocateImplantSource();
        var implantDir = PublishImplant(implantSource);
        var implantDll = Path.Combine(implantDir, "Rod.Implant.dll");
        var implantProc = StartImplant(implantDll, env, secret,
            sleep: TimeSpan.FromSeconds(1), jitter: TimeSpan.Zero);
        // Drain stderr asynchronously so the pipe cannot block the implant, and so
        // a failure surfaces the implant's own diagnostics.
        var stderr = new StringBuilder();
        implantProc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        implantProc.BeginErrorReadLine();
        using (implantProc)
        {
            try
            {
                // Wait for the implant to enroll, then beacon: it appears online in
                // its engagement once the mTLS handshake completes. Presence is the
                // active-sessions projection ().
                var (engagementId, implantId) = await WaitForImplantOnlineAsync(env, deadline: TimeSpan.FromSeconds(60), stderr);

                // Operator tasks the implant over HTTP (shell.exec). A unique marker
                // in the command lets the assertion confirm the captured output is
                // genuinely this task's output, not a stale read.
                var marker = "rod-dotnet-marker-" + Guid.NewGuid().ToString("N")[..8];
                var issued = await env.Http.PostAsJsonAsync(
                    $"/engagements/{engagementId}/tasks",
                    new { ImplantId = implantId, Verb = "shell.exec", Arguments = $"echo {marker}" });
                issued.EnsureSuccessStatusCode();
                var issuedBody = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();
                Assert.NotNull(issuedBody);

                // The implant runs the task and writes the result upstream; the beacon
                // stream captures it and appends the audit event. Poll until the task
                // is completed with the marker in its output.
                await WaitUntilAsync(async () =>
                {
                    var fetched = await env.Http.GetFromJsonAsync<TaskBody>(
                        $"/engagements/{engagementId}/tasks/{issuedBody!.TaskId}");
                    return fetched is { Status: "Completed", Outcome: "Succeeded" }
                        && (fetched.Output ?? string.Empty).Contains(marker);
                }, deadline: TimeSpan.FromSeconds(60));

                // The audit trail carries the TaskCompleted event for this task
                // (architecture.md Sec 11). A task now produces a three-event arc
                // (): issued, dispatched, then completed.
                var fetchedFull = await env.Http.GetFromJsonAsync<TaskBody>(
                    $"/engagements/{engagementId}/tasks/{issuedBody!.TaskId}");
                Assert.NotNull(fetchedFull);
                Assert.Equal(3, fetchedFull!.Audit.Length);
                Assert.Equal("TaskIssued", fetchedFull.Audit[0].Kind);
                Assert.Equal("TaskDispatched", fetchedFull.Audit[1].Kind);
                Assert.Equal("TaskCompleted", fetchedFull.Audit[2].Kind);
                Assert.Equal("shell.exec", fetchedFull.Audit[2].Verb);
                Assert.Equal("Succeeded", fetchedFull.Audit[2].Outcome);

                //  acceptance: a recon task's scan results are captured as task
                // output against an authorized target (architecture.md Sec 10.3).
                // The mTLS beacon port the implant is connected to is a known-open
                // loopback port, so a portscan over a tight range around it reports
                // that port open in the captured output.
                var scanRange = TestSupport.PortScanRangeAround(env.MtlsPort);
                var reconIssued = await env.Http.PostAsJsonAsync(
                    $"/engagements/{engagementId}/tasks",
                    new { ImplantId = implantId, Verb = "recon.portscan", Arguments = $"127.0.0.1 {scanRange}" });
                reconIssued.EnsureSuccessStatusCode();
                var reconBody = await reconIssued.Content.ReadFromJsonAsync<TaskIssuedBody>();
                Assert.NotNull(reconBody);
                Assert.Equal("recon.portscan", reconBody!.Verb);

                await WaitUntilAsync(async () =>
                {
                    var fetched = await env.Http.GetFromJsonAsync<TaskBody>(
                        $"/engagements/{engagementId}/tasks/{reconBody!.TaskId}");
                    return fetched is { Status: "Completed", Outcome: "Succeeded" }
                        && (fetched.Output ?? string.Empty).Contains($"127.0.0.1:{env.MtlsPort} open");
                }, deadline: TimeSpan.FromSeconds(60));
            }
            finally
            {
                if (!implantProc.HasExited)
                {
                    try { implantProc.Kill(entireProcessTree: true); } catch { }
                    implantProc.WaitForExit(5000);
                }
                try { if (Directory.Exists(implantDir)) Directory.Delete(implantDir, recursive: true); } catch { }
            }
        }

        // The acceptance point is already proven above: the implant enrolled,
        // beacons over mTLS, ran a dispatched shell.exec task, and the teamserver
        // recorded a TaskCompleted audit event for it (read back through the
        // operator task endpoint). The audit trail is the durable record; the
        // implant process and its publish dir are disposable.
    }

    // Walks up from the test assembly to find the repo root (the directory holding
    // src/implant/dotnet alongside src/teamserver), then returns the implant
    // source tree. The build unit does the same resolution; the test mirrors it so
    // the subprocess runs the same source the build unit compiles.
    private static string LocateImplantSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "implant", "dotnet"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "teamserver")))
                return Path.Combine(dir.FullName, "src", "implant", "dotnet");
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the .NET implant source tree from the test assembly.");
    }

    // Publishes the reference implant into a temp dir once for the test. The
    // publish is framework-dependent (the test host already has dotnet), so the
    // output is small and the slice stays about the build path. A failed publish
    // throws so the failure is attributable rather than a silent subprocess exit.
    private static string PublishImplant(string implantSource)
    {
        var outDir = Path.Combine(Path.GetTempPath(), "rod-dotnet-implant-" + Guid.NewGuid().ToString("N"));
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = implantSource,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("publish");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Release");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outDir);
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add("/clp:NoSummary");
        using var publish = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet publish.");
        var pubOut = publish.StandardOutput.ReadToEnd();
        var pubErr = publish.StandardError.ReadToEnd();
        publish.WaitForExit();
        if (publish.ExitCode != 0)
            throw new InvalidOperationException($"dotnet publish failed (exit {publish.ExitCode}):\n{pubOut}\n{pubErr}");
        return outDir;
    }

    // Starts the reference implant as a real subprocess: `dotnet Rod.Implant.dll`.
    // The implant enrolls over HTTP and beacons over mTLS against the test server.
    // env.CACertFile is the dev CA PEM the implant trusts as the mTLS server
    // identity (the dev CA doubles as the server cert). stdout/stderr are captured
    // for diagnostics on failure but are not asserted -- the AC is the
    // teamserver-side outcome.
    private static Process StartImplant(string implantDll, TestEnv env, string token, TimeSpan sleep, TimeSpan jitter)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(implantDll);
        psi.ArgumentList.Add("-enroll-url");
        psi.ArgumentList.Add($"http://127.0.0.1:{env.HttpPort}/implants/enroll");
        psi.ArgumentList.Add("-beacon-url");
        psi.ArgumentList.Add($"127.0.0.1:{env.MtlsPort}");
        psi.ArgumentList.Add("-token");
        psi.ArgumentList.Add(token);
        psi.ArgumentList.Add("-ca-cert");
        psi.ArgumentList.Add(env.CACertFile);
        psi.ArgumentList.Add("-sleep");
        psi.ArgumentList.Add(ToGoDuration(sleep));
        psi.ArgumentList.Add("-jitter");
        psi.ArgumentList.Add(ToGoDuration(jitter));
        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start implant.");
    }

    // Formats a TimeSpan the way the implant's Go-style duration parser reads it:
    // an integer count of seconds with an "s" unit (e.g. "1s", "0s"). The test
    // only ever passes whole-second intervals, so seconds precision is exact here.
    private static string ToGoDuration(TimeSpan value)
        => $"{(long)value.TotalSeconds}s";

    // Polls the presence query until exactly one implant is online in some
    // engagement, then returns (engagementId, implantId). The engagement id is not
    // known ahead of time -- it is resolved from the stager token at enroll -- so
    // the test discovers it from the enrollment side via the presence listing.
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
            "The .NET implant did not appear online within the deadline. Implant stderr:\n" + stderr);
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
        public string Verb { get; set; } = "";
    }

    private sealed class TaskBody
    {
        public string Status { get; set; } = "";
        public string? Output { get; set; }
        public string? Outcome { get; set; }
        public AuditBody[] Audit { get; set; } = Array.Empty<AuditBody>();
    }

    private sealed class AuditBody
    {
        public string Kind { get; set; } = "";
        public string Verb { get; set; } = "";
        public string? Output { get; set; }
        public string Outcome { get; set; } = "";
    }

    private sealed class EngagementSummary
    {
        public string EngagementId { get; set; } = "";
    }

    /// <summary>
    /// A real Kestrel teamserver with the mTLS implant endpoint bound, plus a
    /// plain-HTTP operator/enroll API. The dev CA is exported to a temp PEM file
    /// the implant trusts as the mTLS server identity. Disposed to tear down.
    /// Mirrors the Go implant test's TestEnv.
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

            // Export the dev CA as a PEM file so the implant's beacon TLS client
            // pins it as the mTLS server identity (the dev CA doubles as the server
            // cert). The implant's -ca-cert reads a PEM file path; enroll is plain
            // HTTP so the pin only matters once the beacon stream opens, but it is
            // good hygiene to pin the teamserver identity explicitly.
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
                Name: "Operation .NET Slice"));
            createResponse.EnsureSuccessStatusCode();
            var created = await createResponse.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();

            return await MintStagerTokenAsync(created!.EngagementId);
        }

        // Mints a stager token for an existing engagement. Used by the lateral-move
        // round-trip test: the child derives from the parent, so both enroll into the
        // same engagement and each needs its own single-use token.
        public async Task<string> MintStagerTokenAsync(string engagementId)
        {
            var mintResponse = await Http.PostAsync($"/engagements/{engagementId}/stager-tokens", content: null);
            mintResponse.EnsureSuccessStatusCode();
            var token = await mintResponse.Content.ReadFromJsonAsync<EngagementEndpoints.StagerTokenResponse>();
            return token!.Secret;
        }

        // Creates an engagement and mints two single-use stager tokens for it -- the
        // shape the lateral-move round-trip needs (parent token + child token). Used
        // by DotNetImplant_DerivesChildOnLateralMove_EndToEnd.
        public async Task<(string EngagementId, string ParentToken, string ChildToken)> MintEngagementWithTwoTokensAsync()
        {
            var createResponse = await Http.PostAsJsonAsync("/engagements", new EngagementEndpoints.CreateEngagementRequest(
                Name: "Operation .NET Slice"));
            createResponse.EnsureSuccessStatusCode();
            var created = await createResponse.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
            var engagementId = created!.EngagementId;
            var parentToken = await MintStagerTokenAsync(engagementId);
            var childToken = await MintStagerTokenAsync(engagementId);
            return (engagementId, parentToken, childToken);
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
