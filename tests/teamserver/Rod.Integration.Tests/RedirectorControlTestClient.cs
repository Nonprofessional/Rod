using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.CoreState.Redirectors;

namespace Rod.Integration.Tests;

/// <summary>
/// The test-side redirector: a control-channel WebSocket client speaking the
/// same JSON grammar the binary's control client speaks, for driving an
/// enrolled node from integration tests.
/// </summary>
internal static class RedirectorControlTestClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static WebSocket Connect(IHost host)
    {
        var server = host.Services.GetRequiredService<IServer>() as TestServer
            ?? throw new InvalidOperationException("TestServer was not registered.");
        return server.CreateWebSocketClient()
            .ConnectAsync(new Uri("http://localhost/redirectors/control"), CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    public static Task SendAsync(WebSocket socket, object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public static async Task<JsonElement> ReceiveAsync(WebSocket socket)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var received = await socket.ReceiveAsync(chunk, CancellationToken.None);
            if (received.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("The socket closed before a message arrived.");
            buffer.Write(chunk, 0, received.Count);
            if (received.EndOfMessage)
                return JsonSerializer.Deserialize<JsonElement>(buffer.ToArray());
        }
    }

    /// <summary>
    /// Enrolls a node against a freshly minted credential and returns the
    /// open control socket with the enrolled identity.
    /// </summary>
    public static async Task<(WebSocket Socket, RedirectorNodeId NodeId, string NodeSecret)> EnrollAsync(
        IHost host,
        IRedirectorFleet fleet,
        OperatorId operatorId,
        string name = "edge-test")
    {
        var credential = await fleet.MintCredentialAsync(name, operatorId, DateTimeOffset.UtcNow);
        var socket = Connect(host);
        await SendAsync(socket, new { type = "enroll", credential = credential.Secret });
        var enrolled = await ReceiveAsync(socket);
        if (enrolled.GetProperty("type").GetString() != "enrolled")
            throw new InvalidOperationException($"Enrollment was refused: {enrolled}");
        return (socket,
            RedirectorNodeId.TryParse(enrolled.GetProperty("nodeId").GetString(), out var nodeId)
                ? nodeId
                : throw new InvalidOperationException("The enrolled response carried no node id."),
            enrolled.GetProperty("nodeSecret").GetString()!);
    }
}
