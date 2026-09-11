using System.Net;
using System.Net.Http.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.CoreState.Tasks;
using Rod.Transport.Endpoints;
using Rod.V1;
// The domain entity shares its name with the BCL Task, and both namespaces
// define a TaskOutcome; pin the BCL Task and reach the wire outcome by name.
using Task = System.Threading.Tasks.Task;
using TaskOutcome = Rod.V1.TaskOutcome;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance for the WebSocket beacon stream (architecture.md Sec 8, the
/// web posture's interactive tier): the same session the gRPC stream runs,
/// over a WebSocket on the plain-HTTP listener family, with the envelope's
/// own handshake and frame grammar. A from-scratch WebSocket implant -- no
/// gRPC library, just the socket and the protobuf messages -- enrolls,
/// handshakes, receives a pushed task the moment it is queued, reports its
/// result, and holds the live channel an operator types into
/// (architecture.md Sec 10.3).
/// </summary>
public class WebSocketBeaconRoundTripTests
{
    [Fact]
    public async Task FromScratchImplant_HandshakesReceivesPushedTaskingAndReports()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var secret = await MintStagerTokenAsync(client, engagementId);
            var implantId = await EnrollAsync(client, secret);

            using var implant = await WsImplant.ConnectAsync(host, implantId);
            var handshake = await implant.ReceiveHandshakeAsync();
            Assert.Equal(HandshakeStatus.Ok, handshake.Status);
            Assert.Equal(1, handshake.Version.Major);

            // The push shape: the task arrives on the open stream the moment
            // the operator queues it -- no poll, no waiting interval.
            var marker = "rod-ws-marker-" + Guid.NewGuid().ToString("N")[..8];
            var issued = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new { ImplantId = implantId, Verb = "shell.exec", Arguments = $"echo {marker}" });
            issued.EnsureSuccessStatusCode();
            var issuedBody = await issued.Content.ReadFromJsonAsync<IssuedBody>();

            var request = TaskRequest.Parser.ParseFrom(await implant.ReceiveSingleFrameAsync());
            Assert.Equal(issuedBody!.TaskId, request.TaskId);
            Assert.Equal("shell.exec", request.Verb);
            Assert.NotEmpty(request.Signature.ToByteArray());

            var completed = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks/{issuedBody.TaskId}/input",
                new { Data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("ignored")) });
            // A one-shot task takes no live input: the verb check refuses it,
            // not the transport.
            Assert.Equal(HttpStatusCode.UnprocessableEntity, completed.StatusCode);

            await implant.SendFramesAsync(new[] { Frames.Result(
                request.TaskId, TaskOutcome.Succeeded, marker) });

            var task = await WaitUntilAsync(async () =>
            {
                var fetched = await client.GetFromJsonAsync<TaskBody>(
                    $"/engagements/{engagementId}/tasks/{issuedBody.TaskId}");
                return fetched?.Status == "Completed" ? fetched : null;
            });
            Assert.Contains(marker, task!.Output);
        }
    }

    [Fact]
    public async Task FromScratchImplant_HoldsTheInteractiveChannel()
    {
        // The point of the stream on the web posture: the channel verbs run
        // live. An operator's typing reaches the implant as a ChannelInput
        // frame on this socket, and the implant's output streams back.
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var secret = await MintStagerTokenAsync(client, engagementId);
            var implantId = await EnrollAsync(client, secret);

            using var implant = await WsImplant.ConnectAsync(host, implantId);
            Assert.Equal(HandshakeStatus.Ok, (await implant.ReceiveHandshakeAsync()).Status);

            var issued = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new { ImplantId = implantId, Verb = "shell.interact", Arguments = "" });
            issued.EnsureSuccessStatusCode();
            var issuedBody = await issued.Content.ReadFromJsonAsync<IssuedBody>();
            Assert.NotNull(issuedBody);

            var request = TaskRequest.Parser.ParseFrom(await implant.ReceiveSingleFrameAsync());
            Assert.Equal(ChannelVerbs.ShellInteract, request.Verb);
            // The operator types; the frame crosses the stream.
            var typed = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks/{issuedBody!.TaskId}/input",
                new { Data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("whoami\n")) });
            typed.EnsureSuccessStatusCode();

            var input = ChannelInput.Parser.ParseFrom(await implant.ReceiveSingleFrameAsync(
                expectKind: FrameKind.ChannelInput));
            Assert.Equal(issuedBody.TaskId, input.TaskId);
            Assert.Equal("whoami\n", input.Data.ToStringUtf8());

            // The implant streams its shell's output back over the channel.
            await implant.SendFramesAsync(new[] { Frames.ChannelOut(
                request.TaskId, "root\n") });

            var task = await WaitUntilAsync(async () =>
            {
                var fetched = await client.GetFromJsonAsync<TaskBody>(
                    $"/engagements/{engagementId}/tasks/{issuedBody.TaskId}");
                return fetched?.Output?.Contains("root") == true ? fetched : null;
            });
            Assert.Equal("Dispatched", task!.Status);
        }
    }

    private static async Task<string> CreateEngagementAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Wss Stream"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        return created!.EngagementId;
    }

    private static async Task<string> MintStagerTokenAsync(HttpClient client, string engagementId)
    {
        var response = await client.PostAsync($"/engagements/{engagementId}/stager-tokens", content: null);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<EngagementEndpoints.StagerTokenResponse>();
        return token!.Secret;
    }

    private static async Task<string> EnrollAsync(HttpClient client, string secret)
    {
        var response = await client.PostAsJsonAsync("/implants/enroll",
            new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: secret, Class: null, PublicKey: null));
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
        public string? Output { get; set; }
    }

    // The from-scratch WebSocket implant: the envelope's varint frame codec
    // over the socket's messages, nothing else.
    private sealed class WsImplant : IDisposable
    {
        private readonly System.Net.WebSockets.WebSocket _ws;

        private WsImplant(System.Net.WebSockets.WebSocket ws) => _ws = ws;

        public static async Task<WsImplant> ConnectAsync(IHost host, string implantId)
        {
            var server = host.GetTestServer();
            var wsClient = server.CreateWebSocketClient();
            var ws = await wsClient.ConnectAsync(
                new Uri(server.BaseAddress, WebSocketBeaconEndpoints.Route), CancellationToken.None);
            var implant = new WsImplant(ws);

            // The implant speaks first: the handshake frame, plaintext -- the
            // lab posture on the cleartext test host.
            await implant.SendFramesAsync(new[] { new Frame
            {
                Payload = ByteString.CopyFrom(new HandshakeRequest
                {
                    Version = new ProtocolVersion { Major = 1 },
                    ImplantId = implantId,
                    Capabilities = { "shell.exec", ChannelVerbs.ShellInteract },
                }.ToByteArray()),
            } });
            return implant;
        }

        public async Task<HandshakeResponse> ReceiveHandshakeAsync()
            => HandshakeResponse.Parser.ParseFrom(await ReceiveSingleFrameAsync());

        public async Task<byte[]> ReceiveSingleFrameAsync(FrameKind expectKind = FrameKind.Unspecified)
        {
            var frames = Parse(await ReceiveMessageAsync());
            Assert.Single(frames);
            if (expectKind != FrameKind.Unspecified)
                Assert.Equal(expectKind, frames[0].Kind);
            return frames[0].Payload.ToByteArray();
        }

        public async Task SendFramesAsync(IReadOnlyList<Frame> frames)
        {
            var payload = Encode(frames);
            await _ws.SendAsync(
                payload, System.Net.WebSockets.WebSocketMessageType.Binary,
                endOfMessage: true, CancellationToken.None);
        }

        public void Dispose() => _ws.Dispose();

        private async Task<byte[]> ReceiveMessageAsync()
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (true)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var received = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                if (received.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                    throw new InvalidOperationException("The stream closed under the test.");
                message.Write(buffer, 0, received.Count);
                if (received.EndOfMessage)
                    return message.ToArray();
            }
        }

        // The envelope codec: the protobuf canonical delimited-stream shape --
        // an unsigned varint length before each marshaled Frame.

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

    // The upstream frame builders the from-scratch implant sends after its
    // handshake.
    private static class Frames
    {
        public static Frame Result(string taskId, TaskOutcome outcome, string output)
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
