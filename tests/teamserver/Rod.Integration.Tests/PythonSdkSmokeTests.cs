using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using Google.Protobuf;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState.Implants;
using Rod.Transport;
using Rod.V1;

namespace Rod.Integration.Tests;

/// <summary>
/// The SDK acceptance smoke (architecture.md Sec 15.3, ADR-006's AC):
/// <c>sdk/python/smoke.py</c> drives the operator API through the official
/// Python SDK -- login, create an engagement, issue a task, collect the
/// artifact -- against this real-socket teamserver while the rig plays the
/// implant side on the beacon stream. The rig owns everything environmental
/// (the enrolled implant, the beacon that completes the task and streams the
/// exfil chunk, the operate-scoped token); the smoke owns the arc the AC
/// names. Skips with a notice when no python3 is on PATH -- the smoke is a
/// cross-language acceptance, not a reason to fail a .NET-only checkout.
/// </summary>
public class PythonSdkSmokeTests
{
    [PythonFact]
    public async Task ThePythonSdk_CarriesTheAcceptanceArc()
    {
        var python = PythonFactAttribute.Python
            ?? throw new InvalidOperationException("the fact attribute skipped instead");

        await using var env = await SmokeEnv.StartAsync();
        var implant = await EngagementSetup.EnrollOwnedImplantAsync(env.Host);

        // The beacon that completes whatever the SDK tasks: result first,
        // then the terminal exfil chunk carrying the loot bytes.
        var loot = Encoding.UTF8.GetBytes("python sdk smoke loot\n");
        using var beacon = await WsBeaconClient.ConnectAsync(env.HttpPort, implant.Id.ToString());
        Assert.Equal(HandshakeStatus.Ok, (await beacon.ReceiveHandshakeAsync()).Status);

        var beaconWork = Task.Run(async () =>
        {
            var request = TaskRequest.Parser.ParseFrom(await beacon.ReceiveSingleFrameAsync());
            await beacon.SendFramesAsync([ResultFrame(new TaskResult
            {
                TaskId = request.TaskId,
                Outcome = TaskOutcome.Succeeded,
                Output = "pushed loot.txt",
            })]);
            await beacon.SendFramesAsync([new Frame
            {
                Kind = FrameKind.ExfilChunk,
                Payload = ByteString.CopyFrom(new ExfilChunk
                {
                    TaskId = request.TaskId,
                    Name = "loot.txt",
                    ContentType = "text/plain",
                    Sequence = 1,
                    Terminal = true,
                    Data = ByteString.CopyFrom(loot),
                }.ToByteArray()),
            }]);
        });

        var result = await RunSmoke(python, env, implant);
        // The beacon side should be long done; awaiting surfaces any fault
        // under the same failure instead of hanging the suite.
        await beaconWork.WaitAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("smoke-ok:", result.Stdout, StringComparison.Ordinal);
        // The rig's cookie session is separate from the smoke's: the artifact
        // the SDK collected is visible to the engagement's owner too.
        Assert.DoesNotContain("Traceback", result.Stdout, StringComparison.Ordinal);
    }

    private static Frame ResultFrame(TaskResult result)
        => new()
        {
            Payload = ByteString.CopyFrom(result.ToByteArray()),
            Kind = FrameKind.TaskResult,
        };

    private sealed record SmokeResult(int ExitCode, string Stdout, string Stderr);

    private static async Task<SmokeResult> RunSmoke(string python, SmokeEnv env, Implant implant)
    {
        var smokePath = FindRepoFile(["sdk", "python", "smoke.py"]);
        var arguments =
            $"{Quote(smokePath)} --url http://127.0.0.1:{env.HttpPort}" +
            $" --handle {AuthenticatedHost.Handle} --password {AuthenticatedHost.Password}" +
            $" --token {Quote(env.TokenSecret)}" +
            $" --engagement {implant.EngagementId} --implant {implant.Id}";
        var info = new ProcessStartInfo
        {
            FileName = python,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(info)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            Assert.Fail($"smoke.py exited {process.ExitCode}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        return new SmokeResult(process.ExitCode, stdout, stderr);
    }

    private static string Quote(string value) => $"\"{value}\"";

    private static string FindRepoFile(params string[] relative)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine([dir.FullName, .. relative]);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Could not locate {string.Join('/', relative)} from the test assembly.");
    }

    /// <summary>
    /// A real Kestrel teamserver on a free port with the operator API, the
    /// logged-in console session, and an operate-scoped API token the smoke
    /// drives the tasking through -- the ExfilRoundTripTests harness shape
    /// plus the mint.
    /// </summary>
    private sealed class SmokeEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
        public int HttpPort { get; private set; }
        public string TokenSecret { get; private set; } = "";

        public static async Task<SmokeEnv> StartAsync()
        {
            var env = new SmokeEnv();
            env.HttpPort = TestSupport.GetFreeTcpPort();

            var config = AuthenticatedHost.BuildConfig();
            env.Host = TransportHost.CreateHostBuilder(
                    configureServices: services => AuthenticatedHost.ComposeServices(services, config),
                    mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints),
                    configuration: config)
                .ConfigureWebHost(webBuilder => webBuilder
                    .ConfigureKestrel(kestrel => kestrel.ListenLocalhost(env.HttpPort)))
                .Build();
            await env.Host.StartAsync();

            env.Http = new HttpClient(new CookieHandler(new HttpClientHandler()))
            {
                BaseAddress = new Uri($"http://127.0.0.1:{env.HttpPort}"),
            };
            await AuthenticatedHost.LoginAsync(env.Http);

            // The token is the session operator's own: /operators/me, never
            // the roster (whose first row is the credential-less automation
            // engine account, a member that owns nothing).
            var me = await env.Http.GetFromJsonAsync<OperatorBody>("/operators/me");
            var minted = await env.Http.PostAsJsonAsync(
                $"/operators/{me!.Id}/tokens", new { scope = "operate" });
            minted.EnsureSuccessStatusCode();
            var token = await minted.Content.ReadFromJsonAsync<TokenBody>();
            env.TokenSecret = token!.Token!;
            return env;
        }

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            if (Host is not null)
                await Host.StopAsync();
            Host?.Dispose();
        }

        private sealed class OperatorBody
        {
            public string Id { get; set; } = "";
        }

        private sealed class TokenBody
        {
            public string? Token { get; set; }
        }
    }
}

    /// <summary>
    /// Gates the smoke on a usable python3: skipped (not failed) when the
    /// interpreter is absent, the same posture the Rust e2e keeps for cargo.
    /// </summary>
internal sealed class PythonFactAttribute : FactAttribute
    {
        public static string? Python { get; }

        static PythonFactAttribute()
        {
            foreach (var candidate in new[] { "python3", "python" })
            {
                try
                {
                    var probe = Process.Start(new ProcessStartInfo
                    {
                        FileName = candidate,
                        ArgumentList = { "--version" },
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    });
                    probe?.WaitForExit(5000);
                    if (probe is not null && probe.ExitCode == 0)
                    {
                        Python = candidate;
                        break;
                    }
                }
                catch
                {
                    // Not on PATH; the next candidate.
                }
            }
        }

        public PythonFactAttribute()
        {
            if (Python is null)
                Skip = "python3 is not on PATH; the SDK smoke needs it";
        }
    }
