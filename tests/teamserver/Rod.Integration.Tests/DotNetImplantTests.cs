using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Transport;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance: the reference .NET implant checks in and tasks
/// end-to-end. A real teamserver (Kestrel mTLS beacon endpoint + plain-HTTP
/// operator/enroll API) is stood up, the reference .NET implant is published to a
/// temp dir and launched against it as a real subprocess (dotnet Rod.Implant.dll),
/// and the test drives the full slice the acceptance criteria name: the implant enrolls over HTTP,
/// beacons over mTLS, completes the handshake, runs a dispatched shell.exec task,
/// and the operator sees the captured output plus the TaskCompleted audit event.
/// This is the same round-trip HandshakePresenceTests/TaskRoundTripTests prove
/// in-process, now driven by the actual .NET implant assembly.
/// </summary>
public class DotNetImplantTests
{
    /// <summary>
    /// Acceptance: a parent implant derives a child on lateral.move
    /// that enrolls back, and the child's parentage is recorded server-side. The
    /// parent runs as a real subprocess; the operator tasks it lateral.move with a
    /// second (child) stager token in the arguments; the handler generates a fresh
    /// child keypair and enrolls a child naming the parent. The operator implant
    /// listing must then show the child with its ParentImplantId set to the parent.
    /// This is the implant-driven round-trip the acceptance criteria name -- the in-process
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

    /// <summary>
    /// Acceptance: an operator's SOCKS-configured tool reaches arbitrary third
    /// hosts through one stage-2 tunnel task, every connection and its bytes
    /// attributed (architecture.md Sec 10.1 tunnel, Sec 14). The reference
    /// implant (a real subprocess) runs tunnel.socks; the operator binds the
    /// SOCKS relay onto it; two SOCKS clients CONNECT to two different third
    /// hosts through the one channel, exchange bytes, and the task's final
    /// summary records both destinations and the byte tallies -- the proxy's
    /// attributed record, next to the bind and close in the trail.
    /// </summary>
    [DotNetFact]
    public async Task DotNetImplant_SocksProxy_ReachesArbitraryHostsThroughOneTask_EndToEnd()
    {
        await using var env = await TestEnv.StartAsync();
        var secret = await env.MintStagerTokenAsync();

        var implantSource = LocateImplantSource();
        var implantDir = PublishImplant(implantSource);
        var implantDll = Path.Combine(implantDir, "Rod.Implant.dll");
        var implantProc = StartImplant(implantDll, env, secret,
            sleep: TimeSpan.FromSeconds(1), jitter: TimeSpan.Zero);
        var stderr = new StringBuilder();
        implantProc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        implantProc.BeginErrorReadLine();
        using (implantProc)
        {
            try
            {
                var (engagementId, implantId) = await WaitForImplantOnlineAsync(env, deadline: TimeSpan.FromSeconds(60), stderr);

                // Two third hosts, each reachable only from the implant's
                // vantage: the proxy's destinations are per connection, not
                // baked at task time.
                await using var thirdOne = EchoHost.Start();
                await using var thirdTwo = EchoHost.Start();

                var issued = await env.Http.PostAsJsonAsync(
                    $"/engagements/{engagementId}/tasks",
                    new { ImplantId = implantId, Verb = "tunnel.socks" });
                issued.EnsureSuccessStatusCode();
                var issuedBody = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();
                Assert.NotNull(issuedBody);

                await WaitUntilAsync(async () =>
                    (await env.Http.GetFromJsonAsync<TaskBody>(
                        $"/engagements/{engagementId}/tasks/{issuedBody!.TaskId}"))!.Status == "Dispatched",
                    deadline: TimeSpan.FromSeconds(30));

                var bound = await env.Http.PostAsJsonAsync(
                    $"/engagements/{engagementId}/tasks/{issuedBody!.TaskId}/relay",
                    new { });
                bound.EnsureSuccessStatusCode();
                var relay = await bound.Content.ReadFromJsonAsync<RelayBody>();
                Assert.NotNull(relay);
                Assert.True(relay!.Port > 0);

                // The unmodified-tool shape: a SOCKS client (what a browser or
                // proxychains speaks) CONNECTs each destination through the
                // one proxy port and exchanges bytes.
                var one = await SocksClient.ConnectAsync(relay.Port, "127.0.0.1", thirdOne.Port);
                await using (one)
                {
                    await one.SendAsync("ping");
                    Assert.Equal("ping", await one.ReceiveAsync());
                }
                var two = await SocksClient.ConnectAsync(relay.Port, "127.0.0.1", thirdTwo.Port);
                await using (two)
                {
                    await two.SendAsync("pong");
                    Assert.Equal("pong", await two.ReceiveAsync());
                }

                // eof closes the proxy with the task; the summary is the
                // record of what the proxy dialed and moved.
                var closed = await env.Http.PostAsJsonAsync(
                    $"/engagements/{engagementId}/tasks/{issuedBody.TaskId}/input",
                    new { Eof = true });
                closed.EnsureSuccessStatusCode();

                TaskBody? completed = null;
                await WaitUntilAsync(async () =>
                {
                    completed = await env.Http.GetFromJsonAsync<TaskBody>(
                        $"/engagements/{engagementId}/tasks/{issuedBody.TaskId}");
                    return completed is { Status: "Completed", Outcome: "Succeeded" };
                }, deadline: TimeSpan.FromSeconds(30));

                Assert.Contains("2 connections (0 refused)", completed!.Output);
                Assert.Contains("8 bytes up, 8 bytes down", completed.Output);
                Assert.Contains($"127.0.0.1:{thirdOne.Port}", completed.Output);
                Assert.Contains($"127.0.0.1:{thirdTwo.Port}", completed.Output);
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

    /// <summary>
    /// Acceptance: a <c>tunnel.forward</c> task issued to a pivot-child session
    /// executes on its parent and reaches the third host, attributed to the
    /// pivot session end to end (architecture.md Sec 5.2, Sec 14). The parent
    /// -- a real implant subprocess in stream mode -- derives a Pivot-class
    /// child via lateral.move; the child never connects, because no process
    /// exists to run it. When the operator tasks the child a tunnel, the
    /// parent's beacon stream claims the marked frame, verifies the signature
    /// against the child's id, opens the tunnel from its own vantage, and the
    /// operator's input posts ride the parent's stream to the tunnel. The
    /// task's whole record -- transcript and relay summary -- lands on the
    /// child.
    /// </summary>
    [DotNetFact]
    public async Task DotNetImplant_FrontsPivotChildTunneling_EndToEnd()
    {
        await using var env = await TestEnv.StartAsync();
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

                // The parent derives a Pivot-class child (architecture.md
                // Sec 5.2): an identity for a host that cannot run its own
                // implant, enrolled naming the parent and never connecting.
                var derived = await env.Http.PostAsJsonAsync(
                    $"/engagements/{engagementId}/tasks",
                    new { ImplantId = parentId, Verb = "lateral.move", Arguments = $"{childToken} Pivot" });
                derived.EnsureSuccessStatusCode();
                var derivedBody = await derived.Content.ReadFromJsonAsync<TaskIssuedBody>();
                Assert.NotNull(derivedBody);
                string childId = "";
                await WaitUntilAsync(async () =>
                {
                    var fetched = await env.Http.GetFromJsonAsync<TaskBody>(
                        $"/engagements/{engagementId}/tasks/{derivedBody!.TaskId}");
                    if (fetched is not { Status: "Completed", Outcome: "Succeeded" })
                        return false;
                    var line = (fetched.Output ?? string.Empty).Trim();
                    childId = line.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    return childId.Length > 0;
                }, deadline: TimeSpan.FromSeconds(60));

                // The third host: reachable only from the implant's vantage.
                await using var thirdHost = EchoHost.Start();

                // The operator tasks the child, never the parent. The child's
                // queue is claimed by the parent's stream through the fronting
                // claim, and the tunnel runs in the parent.
                var issued = await env.Http.PostAsJsonAsync(
                    $"/engagements/{engagementId}/tasks",
                    new { ImplantId = childId, Verb = "tunnel.forward", Arguments = $"127.0.0.1 {thirdHost.Port}" });
                issued.EnsureSuccessStatusCode();
                var issuedBody = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();
                Assert.NotNull(issuedBody);

                await WaitUntilAsync(async () =>
                    (await env.Http.GetFromJsonAsync<TaskBody>(
                        $"/engagements/{engagementId}/tasks/{issuedBody!.TaskId}"))!.Status == "Dispatched",
                    deadline: TimeSpan.FromSeconds(30));

                // The operator's input posts ride the fronting stream -- the
                // child has none of its own -- and the parent relays them to
                // the third host, whose echo lands on the child's transcript.
                var sent = await env.Http.PostAsJsonAsync(
                    $"/engagements/{engagementId}/tasks/{issuedBody!.TaskId}/input",
                    new { Data = Encoding.UTF8.GetBytes("ping") });
                sent.EnsureSuccessStatusCode();
                var closed = await env.Http.PostAsJsonAsync(
                    $"/engagements/{engagementId}/tasks/{issuedBody.TaskId}/input",
                    new { Eof = true });
                closed.EnsureSuccessStatusCode();

                TaskBody? completed = null;
                await WaitUntilAsync(async () =>
                {
                    completed = await env.Http.GetFromJsonAsync<TaskBody>(
                        $"/engagements/{engagementId}/tasks/{issuedBody.TaskId}");
                    return completed is { Status: "Completed", Outcome: "Succeeded" };
                }, deadline: TimeSpan.FromSeconds(30));

                // The whole record is the child's: the transcript carries the
                // relayed traffic and the summary, and the task itself belongs
                // to the pivot session the parent fronted.
                Assert.Contains("ping", completed!.Output);
                Assert.Contains("relayed 4 bytes up, 4 bytes down", completed.Output);
                Assert.Equal(childId, completed.ImplantId);
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
                // active-sessions projection.
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
                //: issued, dispatched, then completed.
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

    /// <summary>
    /// Poll-mode end to end: the reference implant runs with -mode poll, so each
    /// check-in drains queued tasking, closes the stream, and sleeps the beacon
    /// interval instead of holding a line open. The task must still round-trip,
    /// and across several check-in cycles the engagement trail holds exactly one
    /// SessionOpened record -- the session is reused per check-in, not churned.
    /// </summary>
    [DotNetFact]
    public async Task DotNetImplant_PollMode_TasksAcrossCheckIns_WithoutSessionChurn()
    {
        await using var env = await TestEnv.StartAsync();
        var secret = await env.MintStagerTokenAsync();

        var implantSource = LocateImplantSource();
        var implantDir = PublishImplant(implantSource);
        var implantDll = Path.Combine(implantDir, "Rod.Implant.dll");
        var implantProc = StartImplant(implantDll, env, secret,
            sleep: TimeSpan.FromSeconds(1), jitter: TimeSpan.Zero, mode: "poll");
        var stderr = new StringBuilder();
        implantProc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        implantProc.BeginErrorReadLine();
        using (implantProc)
        {
            try
            {
                var (engagementId, implantId) = await WaitForImplantOnlineAsync(env, deadline: TimeSpan.FromSeconds(60), stderr);

                var marker = "rod-poll-marker-" + Guid.NewGuid().ToString("N")[..8];
                var issued = await env.Http.PostAsJsonAsync(
                    $"/engagements/{engagementId}/tasks",
                    new { ImplantId = implantId, Verb = "shell.exec", Arguments = $"echo {marker}" });
                issued.EnsureSuccessStatusCode();
                var issuedBody = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();
                Assert.NotNull(issuedBody);

                // The next check-in drains the task and reports the result.
                await WaitUntilAsync(async () =>
                {
                    var fetched = await env.Http.GetFromJsonAsync<TaskBody>(
                        $"/engagements/{engagementId}/tasks/{issuedBody!.TaskId}");
                    return fetched is { Status: "Completed", Outcome: "Succeeded" }
                        && (fetched.Output ?? string.Empty).Contains(marker);
                }, deadline: TimeSpan.FromSeconds(60));

                // Several check-in cycles later (sleep is 1s), the trail holds
                // exactly one SessionOpened event: every check-in handshake
                // reused the implant's one live session.
                await Task.Delay(TimeSpan.FromSeconds(4));
                await AuthenticatedHost.LoginAsync(env.Http);
                var page = await env.Http.GetFromJsonAsync<AuditPageBody>(
                    $"/engagements/{engagementId}/audit?limit=200");
                Assert.NotNull(page);
                Assert.Single(page!.Items, e => e.Kind == "SessionOpened");
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

    /// <summary>
    /// Acceptance for staged uploads (architecture.md Sec 10, the per-verb
    /// typed arm), driven by the real implant: a 10 MiB file.push carries its
    /// payload as staged content -- the sha256 bound into the signed
    /// arguments, the bytes staged as a task-bound artifact -- and the implant
    /// demands and reassembles the chunk run over its beacon stream. The
    /// acceptance point is literal: the 10 MiB file lands whole on the target
    /// (this host), byte for byte.
    /// </summary>
    [DotNetFact]
    public async Task DotNetImplant_StagedFilePush_LandsTenMiBWhole()
    {
        await using var env = await TestEnv.StartAsync();
        var secret = await env.MintStagerTokenAsync();

        var implantSource = LocateImplantSource();
        var implantDir = PublishImplant(implantSource);
        var implantDll = Path.Combine(implantDir, "Rod.Implant.dll");
        var implantProc = StartImplant(implantDll, env, secret,
            sleep: TimeSpan.FromSeconds(1), jitter: TimeSpan.Zero);
        var stderr = new StringBuilder();
        implantProc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        implantProc.BeginErrorReadLine();
        using (implantProc)
        {
            try
            {
                var (engagementId, implantId) = await WaitForImplantOnlineAsync(env, deadline: TimeSpan.FromSeconds(60), stderr);

                var content = RandomNumberGenerator.GetBytes(10 * 1024 * 1024);
                var targetPath = Path.Combine(Path.GetTempPath(), "rod-e2e-staged-" + Guid.NewGuid().ToString("N") + ".bin");
                try
                {
                    var issued = await env.Http.PostAsJsonAsync(
                        $"/engagements/{engagementId}/tasks",
                        new { ImplantId = implantId, Verb = "file.push", Arguments = targetPath, Content = content });
                    issued.EnsureSuccessStatusCode();
                    var issuedBody = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();
                    Assert.NotNull(issuedBody);

                    await WaitUntilAsync(async () =>
                    {
                        var fetched = await env.Http.GetFromJsonAsync<TaskBody>(
                            $"/engagements/{engagementId}/tasks/{issuedBody!.TaskId}");
                        return fetched is { Status: "Completed", Outcome: "Succeeded" };
                    }, deadline: TimeSpan.FromSeconds(60));

                    // The file landed whole: every staged byte, in order, on disk.
                    Assert.True(File.Exists(targetPath), "the staged file was not written: " + stderr);
                    var landed = await File.ReadAllBytesAsync(targetPath);
                    Assert.Equal(content.Length, landed.Length);
                    Assert.Equal(content, landed);
                }
                finally
                {
                    try { File.Delete(targetPath); } catch { }
                }
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

    // Walks up from the test assembly to find the repo root (the directory holding
    // src/implant/dotnet alongside src/teamserver), then returns the implant
    // source tree. The build unit does the same resolution; the test mirrors it so
    // the subprocess runs the same source the build unit compiles.
    /// <summary>
    /// Acceptance: an operator types into a live shell on a connected implant
    /// (architecture.md Sec 10.3, the streaming task shape). The real implant
    /// holds a stream-mode check-in; the operator issues shell.interact with
    /// an initial command, watches its output land on the transcript while
    /// the task is still live, types a second command through the input route
    /// and sees it run, then closes stdin and the channel completes with the
    /// whole session as the task's record.
    /// </summary>
    [DotNetFact]
    public async Task DotNetImplant_InteractiveShell_EndToEnd()
    {
        await using var env = await TestEnv.StartAsync();
        var secret = await env.MintStagerTokenAsync();

        var implantSource = LocateImplantSource();
        var implantDir = PublishImplant(implantSource);
        var implantDll = Path.Combine(implantDir, "Rod.Implant.dll");
        var implantProc = StartImplant(implantDll, env, secret,
            sleep: TimeSpan.FromSeconds(1), jitter: TimeSpan.Zero);
        var stderr = new StringBuilder();
        implantProc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        implantProc.BeginErrorReadLine();
        using (implantProc)
        {
            try
            {
                var (engagementId, implantId) =
                    await WaitForImplantOnlineAsync(env, deadline: TimeSpan.FromSeconds(60), stderr);

                // Unique markers, command by command, so the assertions
                // confirm the transcript is genuinely this channel's.
                var initialMarker = "rod-shell-initial-" + Guid.NewGuid().ToString("N")[..8];
                var typedMarker = "rod-shell-typed-" + Guid.NewGuid().ToString("N")[..8];

                // Open the channel with an initial command. The channel goes
                // live: the initial command's output lands on the transcript
                // while the task is still Dispatched -- a live read, not a
                // completion-time capture.
                var issued = await env.Http.PostAsJsonAsync(
                    $"/engagements/{engagementId}/tasks",
                    new { ImplantId = implantId, Verb = "shell.interact", Arguments = $"echo {initialMarker}" });
                issued.EnsureSuccessStatusCode();
                var issuedBody = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();
                Assert.NotNull(issuedBody);

                await WaitUntilAsync(async () =>
                {
                    var fetched = await env.Http.GetFromJsonAsync<TaskBody>(
                        $"/engagements/{engagementId}/tasks/{issuedBody!.TaskId}");
                    return fetched is { Status: "Dispatched" }
                        && (fetched.Output ?? string.Empty).Contains(initialMarker);
                }, deadline: TimeSpan.FromSeconds(60));

                // The operator types: the input route carries the bytes down
                // the channel and the shell runs them.
                var typed = await env.Http.PostAsJsonAsync(
                    $"/engagements/{engagementId}/tasks/{issuedBody!.TaskId}/input",
                    new { Data = Encoding.UTF8.GetBytes($"echo {typedMarker}\n") });
                typed.EnsureSuccessStatusCode();

                await WaitUntilAsync(async () =>
                {
                    var fetched = await env.Http.GetFromJsonAsync<TaskBody>(
                        $"/engagements/{engagementId}/tasks/{issuedBody!.TaskId}");
                    return fetched is { Status: "Dispatched" }
                        && (fetched.Output ?? string.Empty).Contains(typedMarker);
                }, deadline: TimeSpan.FromSeconds(60));

                // Close stdin: the shell reads its EOF, exits, and the channel
                // completes with the whole session as its record.
                var closed = await env.Http.PostAsJsonAsync(
                    $"/engagements/{engagementId}/tasks/{issuedBody!.TaskId}/input",
                    new { Eof = true });
                closed.EnsureSuccessStatusCode();

                await WaitUntilAsync(async () =>
                {
                    var fetched = await env.Http.GetFromJsonAsync<TaskBody>(
                        $"/engagements/{engagementId}/tasks/{issuedBody!.TaskId}");
                    return fetched is { Status: "Completed", Outcome: "Succeeded" };
                }, deadline: TimeSpan.FromSeconds(60));

                var final = await env.Http.GetFromJsonAsync<TaskBody>(
                    $"/engagements/{engagementId}/tasks/{issuedBody!.TaskId}");
                Assert.NotNull(final);
                Assert.Contains(initialMarker, final!.Output);
                Assert.Contains(typedMarker, final.Output);
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
    // for diagnostics on failure but are not asserted -- the acceptance criterion is the
    // teamserver-side outcome.
    private static Process StartImplant(
        string implantDll, TestEnv env, string token, TimeSpan sleep, TimeSpan jitter, string mode = "stream")
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
        psi.ArgumentList.Add("-mode");
        psi.ArgumentList.Add(mode);
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

    private sealed class AuditPageBody
    {
        public AuditEventBody[] Items { get; set; } = Array.Empty<AuditEventBody>();
        public string? NextCursor { get; set; }
    }

    private sealed class AuditEventBody
    {
        public string Kind { get; set; } = string.Empty;
    }

    private sealed class TaskIssuedBody
    {
        public string TaskId { get; set; } = "";
        public string Verb { get; set; } = "";
    }

    private sealed class TaskBody
    {
        public string TaskId { get; set; } = "";
        public string ImplantId { get; set; } = "";
        public string Status { get; set; } = "";
        public string? Output { get; set; }
        public string? Outcome { get; set; }
        public AuditBody[] Audit { get; set; } = Array.Empty<AuditBody>();
    }

    private sealed class RelayBody
    {
        public string TaskId { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; }
    }

    /// <summary>
    /// The minimal SOCKS5 client a browser implements: no-auth greeting,
    /// CONNECT with a domain-shaped address, then a byte stream. Standing in
    /// for the operator's SOCKS-configured tooling.
    /// </summary>
    private sealed class SocksClient : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;

        private SocksClient(TcpClient client, NetworkStream stream)
        {
            _client = client;
            _stream = stream;
        }

        public static async Task<SocksClient> ConnectAsync(int proxyPort, string host, int port)
        {
            var tool = new TcpClient();
            await tool.ConnectAsync(IPAddress.Loopback, proxyPort);
            var stream = tool.GetStream();

            await stream.WriteAsync(new byte[] { 5, 1, 0 });
            var method = await ReadExactlyAsync(stream, 2);
            Assert.Equal(5, method[0]);
            Assert.Equal(0, method[1]);

            var name = Encoding.ASCII.GetBytes(host);
            var request = new List<byte> { 5, 1, 0, 3, (byte)name.Length };
            request.AddRange(name);
            request.Add((byte)(port >> 8));
            request.Add((byte)port);
            await stream.WriteAsync(request.ToArray());

            var reply = await ReadExactlyAsync(stream, 10);
            Assert.Equal(5, reply[0]);
            Assert.Equal(0, reply[1]); // the implant's dial result
            return new SocksClient(tool, stream);
        }

        public Task SendAsync(string text)
            => _stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

        public async Task<string> ReceiveAsync()
        {
            var buffer = new byte[16 * 1024];
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var read = await _stream.ReadAsync(buffer, deadline.Token);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }

        public async ValueTask DisposeAsync()
        {
            _stream.Dispose();
            _client.Dispose();
            await ValueTask.CompletedTask;
        }

        private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count)
        {
            var buffer = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var read = await stream.ReadAsync(buffer.AsMemory(offset), deadline.Token);
                if (read <= 0)
                    throw new IOException("the SOCKS peer closed early");
                offset += read;
            }
            return buffer;
        }
    }

    /// <summary>
    /// The third host of the fronting acceptance test: a loopback TCP listener
    /// that echoes every byte back until its peer half-closes, then ends its
    /// own side. Standing in for the network segment only the implant can
    /// reach.
    /// </summary>
    private sealed class EchoHost : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serve;

        private EchoHost(TcpListener listener, Task serve)
        {
            _listener = listener;
            _serve = serve;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        public static EchoHost Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var serve = ServeAsync(listener);
            return new EchoHost(listener, serve);
        }

        private static async Task ServeAsync(TcpListener listener)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync();
            }
            catch (SocketException)
            {
                return; // disposed before anything connected
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            using (socket)
            {
                var buffer = new byte[16 * 1024];
                while (true)
                {
                    var received = 0;
                    try
                    {
                        received = await socket.ReceiveAsync(buffer, SocketFlags.None);
                    }
                    catch (SocketException)
                    {
                        return; // the peer reset the connection
                    }
                    if (received <= 0)
                        return; // the peer half-closed; end our side too
                    var sent = 0;
                    while (sent < received)
                        sent += await socket.SendAsync(
                            buffer.AsMemory(sent, received - sent), SocketFlags.None);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _serve;
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
        }
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
    /// Standalone twin of the
    /// integration suite's TestEnv, reduced to what the subprocess tests need.
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
