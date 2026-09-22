using System.Net;
using System.Net.Http.Json;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.CoreState.Tasks;
using Rod.Transport.Channels;
using Rod.Transport.Endpoints;
using Rod.V1;
// The domain entity shares its name with the BCL Task, and both namespaces
// define a TaskOutcome; pin the BCL Task and the wire outcome by name.
using Task = System.Threading.Tasks.Task;
using TaskOutcome = Rod.V1.TaskOutcome;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance for the degraded channel discipline (architecture.md
/// Sec 10.3, the opt-in interactive tier over poll carriers): an implant
/// whose envelope contact advertises the discipline claims channel verbs,
/// operator input parks and rides the next contact as ChannelInput, the
/// implant's ChannelOutput batches the same way, and a channel the implant
/// stops collecting closes with a timeout instead of sitting Dispatched. An
/// implant that never opted in keeps the live-stream-only behavior.
/// </summary>
public class DegradedChannelRoundTripTests
{
    [Fact]
    public async Task OptedInEnvelopeImplant_RunsTheInteractiveChannelAcrossContacts()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var secret = await MintDeployTokenAsync(client, engagementId);
            var implantId = await EnrollAsync(client, secret);

            // The handshake advertises the discipline: the session record
            // carries it, the dispatch claim and the input park read it.
            using var implant = new PollImplant(host, implantId, advertiseDegraded: true);
            var handshake = await implant.ContactAsync();
            Assert.NotNull(handshake);
            Assert.Equal(HandshakeStatus.Ok, handshake!.Status);

            // A channel verb claims over the envelope under the opt-in.
            var issued = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new { ImplantId = implantId, Verb = "shell.interact", Arguments = "" });
            issued.EnsureSuccessStatusCode();
            var issuedBody = await issued.Content.ReadFromJsonAsync<IssuedBody>();
            Assert.NotNull(issuedBody);

            var task = TaskRequest.Parser.ParseFrom(await implant.NextFrameAsync());
            // The issuance response carries the id hyphen-less; the proto's
            // TaskId keeps the dashes.
            Assert.Equal(issuedBody!.TaskId, task.TaskId.Replace("-", ""));
            Assert.Equal(ChannelVerbs.ShellInteract, task.Verb);

            // The operator types while the implant sleeps its interval: the
            // input parks (200, not the live-stream 409) ...
            var typed = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks/{issuedBody.TaskId}/input",
                new { Data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("whoami\n")) });
            typed.EnsureSuccessStatusCode();

            // ... and rides the next contact as a ChannelInput frame.
            var input = ChannelInput.Parser.ParseFrom(await implant.NextFrameAsync());
            Assert.Equal(issuedBody.TaskId, input.TaskId.Replace("-", ""));
            Assert.Equal("whoami\n", input.Data.ToStringUtf8());

            // The implant's shell output batches upstream the same way, and
            // the final result closes the channel -- both ride the contact
            // after the one that collected the input.
            var marker = "rod-degraded-" + Guid.NewGuid().ToString("N")[..8];
            await implant.SendFramesAsync(
                Frames.ChannelOut(task.TaskId, marker),
                Frames.Result(task.TaskId, TaskOutcome.Succeeded, "session over"));
            Assert.Equal(HandshakeStatus.Ok, (await implant.ContactAsync())!.Status);

            var completed = await WaitUntilAsync(async () =>
            {
                var fetched = await client.GetFromJsonAsync<TaskBody>(
                    $"/engagements/{engagementId}/tasks/{issuedBody.TaskId}");
                return fetched?.Status == "Completed" ? fetched : null;
            });
            Assert.Contains(marker, completed!.Output);
        }
    }

    [Fact]
    public async Task AnImplantWithoutTheOptIn_KeepsTheLiveStreamOnlyBehavior()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var secret = await MintDeployTokenAsync(client, engagementId);
            var implantId = await EnrollAsync(client, secret);

            using var implant = new PollImplant(host, implantId, advertiseDegraded: false);
            Assert.Equal(HandshakeStatus.Ok, (await implant.ContactAsync())!.Status);

            var issued = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new { ImplantId = implantId, Verb = "shell.interact", Arguments = "" });
            issued.EnsureSuccessStatusCode();
            var issuedBody = await issued.Content.ReadFromJsonAsync<IssuedBody>();

            // The claim defers (the discipline is not advertised) and the
            // input refuses: nothing parks, exactly the behavior that
            // predates the tier.
            var next = await implant.ContactAsync();
            Assert.NotNull(next);
            var typed = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks/{issuedBody!.TaskId}/input",
                new { Data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("x")) });
            Assert.Equal(HttpStatusCode.Conflict, typed.StatusCode);

            var fetched = await client.GetFromJsonAsync<TaskBody>(
                $"/engagements/{engagementId}/tasks/{issuedBody.TaskId}");
            Assert.Equal("Queued", fetched!.Status);
        }
    }

    [Fact]
    public async Task AChannelTheImplantStopsCollecting_ClosesWithATimeout()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var secret = await MintDeployTokenAsync(client, engagementId);
            var implantId = await EnrollAsync(client, secret);

            using var implant = new PollImplant(host, implantId, advertiseDegraded: true);
            Assert.Equal(HandshakeStatus.Ok, (await implant.ContactAsync())!.Status);

            var issued = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new { ImplantId = implantId, Verb = "shell.interact", Arguments = "" });
            issued.EnsureSuccessStatusCode();
            var issuedBody = await issued.Content.ReadFromJsonAsync<IssuedBody>();
            var task = TaskRequest.Parser.ParseFrom(await implant.NextFrameAsync());

            // Input parks; the implant then vanishes (no further contacts).
            var typed = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks/{issuedBody!.TaskId}/input",
                new { Data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("stale\n")) });
            typed.EnsureSuccessStatusCode();

            // The sweep rides the next park attempt: with the deadline run
            // down, that attempt refuses AND the channel closes itself.
            var hub = host.Services.GetRequiredService<DegradedChannelHub>();
            hub.IdleTimeout = TimeSpan.FromMilliseconds(100);
            await Task.Delay(250);
            var refused = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks/{issuedBody.TaskId}/input",
                new { Data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("late\n")) });
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

            var closed = await WaitUntilAsync(async () =>
            {
                var fetched = await client.GetFromJsonAsync<TaskBody>(
                    $"/engagements/{engagementId}/tasks/{issuedBody.TaskId}");
                return fetched?.Status == "Completed" ? fetched : null;
            });
            Assert.Equal("Failed", closed!.Outcome);
            Assert.Contains("timed out", closed.Output);
        }
    }

    private static async Task<string> CreateEngagementAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Degraded Channel"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        return created!.EngagementId;
    }

    private static async Task<string> MintDeployTokenAsync(HttpClient client, string engagementId)
    {
        var response = await client.PostAsync($"/engagements/{engagementId}/deploy-tokens", content: null);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<EngagementEndpoints.DeployTokenResponse>();
        return token!.Secret;
    }

    private static async Task<string> EnrollAsync(HttpClient client, string secret)
    {
        var response = await client.PostAsJsonAsync("/implants/enroll",
            new EnrollmentEndpoints.EnrollRequest(DeployTokenSecret: secret, Class: null, PublicKey: null));
        response.EnsureSuccessStatusCode();
        var enrolled = await response.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();
        return enrolled!.ImplantId!;
    }

    private static async Task<T?> WaitUntilAsync<T>(Func<Task<T?>> probe, TimeSpan? timeout = null)
        where T : class
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var found = await probe();
            if (found is not null)
                return found;
            await Task.Delay(50);
        }
        throw new TimeoutException("The condition never held inside the window.");
    }

    private sealed class IssuedBody
    {
        public string TaskId { get; set; } = "";
    }

    private sealed class TaskBody
    {
        public string Status { get; set; } = "";
        public string? Outcome { get; set; }
        public string? Output { get; set; }
    }

    // The from-scratch envelope implant: the POST cycle with the varint frame
    // codec, the lab plaintext posture, and the degraded advertisement as its
    // only extra.
    private sealed class PollImplant : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _implantId;
        private readonly bool _advertiseDegraded;
        private readonly List<Frame> _inbound = new();

        public PollImplant(IHost host, string implantId, bool advertiseDegraded)
        {
            _http = host.GetTestServer().CreateClient();
            _implantId = implantId;
            _advertiseDegraded = advertiseDegraded;
        }

        public async Task<HandshakeResponse?> ContactAsync()
        {
            var frames = new List<Frame> { HandshakeFrame() };
            frames.AddRange(_inbound);
            _inbound.Clear();
            using var content = new ByteArrayContent(Encode(frames));
            content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var response = await _http.PostAsync("/implants/beacon", content);
            response.EnsureSuccessStatusCode();
            var inbound = Parse(await response.Content.ReadAsByteArrayAsync());
            if (inbound.Count == 0)
                return null;
            _inbound.AddRange(inbound.Skip(1));
            return HandshakeResponse.Parser.ParseFrom(inbound[0].Payload);
        }

        public async Task<byte[]> NextFrameAsync()
        {
            while (_inbound.Count == 0)
            {
                var handshake = await ContactAsync();
                Assert.NotNull(handshake);
                Assert.Equal(HandshakeStatus.Ok, handshake!.Status);
            }
            var frame = _inbound[0];
            _inbound.RemoveAt(0);
            return frame.Payload.ToByteArray();
        }

        public Task SendFramesAsync(params Frame[] frames)
        {
            _inbound.AddRange(frames);
            return Task.CompletedTask;
        }

        public void Dispose() => _http.Dispose();

        private Frame HandshakeFrame()
        {
            var handshake = new HandshakeRequest
            {
                Version = new ProtocolVersion { Major = 1 },
                ImplantId = _implantId,
                ReplayNonces = true,
            };
            handshake.Capabilities.Add("shell.exec");
            handshake.Capabilities.Add(ChannelVerbs.ShellInteract);
            if (_advertiseDegraded)
                handshake.Capabilities.Add(DegradedChannelHub.Capability);
            return new Frame { Payload = ByteString.CopyFrom(handshake.ToByteArray()) };
        }

        private static byte[] Encode(IReadOnlyList<Frame> frames)
        {
            var body = new MemoryStream();
            foreach (var frame in frames)
            {
                var marshaled = frame.ToByteArray();
                WriteVarint(body, marshaled.Length);
                body.Write(marshaled);
            }
            return body.ToArray();
        }

        private static List<Frame> Parse(byte[] body)
        {
            var frames = new List<Frame>();
            var position = 0;
            while (position < body.Length)
            {
                uint length = 0;
                var shift = 0;
                int delimiter;
                for (delimiter = 0; delimiter < 5; delimiter++)
                {
                    var b = body[position + delimiter];
                    length |= (uint)(b & 0x7f) << shift;
                    if ((b & 0x80) == 0)
                        break;
                    shift += 7;
                }
                position += delimiter + 1;
                frames.Add(Frame.Parser.ParseFrom(body, position, (int)length));
                position += (int)length;
            }
            return frames;
        }

        private static void WriteVarint(MemoryStream target, int value)
        {
            uint remaining = (uint)value;
            while (remaining >= 0x80)
            {
                target.WriteByte((byte)(remaining | 0x80));
                remaining >>= 7;
            }
            target.WriteByte((byte)remaining);
        }
    }

    private static class Frames
    {
        public static Frame Result(string taskId, Rod.V1.TaskOutcome outcome, string output)
            => new()
            {
                Kind = FrameKind.TaskResult,
                Payload = ByteString.CopyFrom(new TaskResult
                {
                    TaskId = taskId,
                    Outcome = outcome,
                    Output = output,
                }.ToByteArray()),
            };

        public static Frame ChannelOut(string taskId, string output)
            => new()
            {
                Kind = FrameKind.ChannelOutput,
                Payload = ByteString.CopyFrom(new ChannelOutput
                {
                    TaskId = taskId,
                    Data = ByteString.CopyFromUtf8(output),
                }.ToByteArray()),
            };
    }
}
