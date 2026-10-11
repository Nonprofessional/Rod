using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Rod.Integration.Tests;

/// <summary>
/// A minimal loopback SMTP receiver for the campaign send engine's tests --
/// the SMTP twin of <see cref="LoopbackWebhook"/>. Speaks just enough of the
/// protocol for MailKit's client to deliver plain, unauthenticated messages:
/// greeting, EHLO with no extensions, MAIL/RCPT/DATA/QUIT. Every accepted
/// message is captured whole (envelope and raw MIME text) for assertions.
/// </summary>
internal sealed class LoopbackSmtp : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _acceptLoop;
    private readonly List<ReceivedMessage> _messages = new();
    private readonly object _lock = new();

    private LoopbackSmtp(TcpListener listener)
    {
        _listener = listener;
        _acceptLoop = Task.Run(() => AcceptAsync(_stopping.Token));
    }

    public static async Task<LoopbackSmtp> StartAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, TestSupport.GetFreeTcpPort());
        listener.Start(1);
        var smtp = new LoopbackSmtp(listener);
        // The first accept is warm by the time the caller sends, so a
        // connect right after this returns never races the loop.
        await Task.Yield();
        return smtp;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The accepted messages, in arrival order.</summary>
    public IReadOnlyList<ReceivedMessage> Messages
    {
        get
        {
            lock (_lock)
            {
                return _messages.ToArray();
            }
        }
    }

    private async Task AcceptAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(stopping);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            _ = Task.Run(() => ServeAsync(client, stopping));
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stopping)
    {
        using var _ = client;
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
            NewLine = "\r\n",
        };

        await writer.WriteLineAsync("220 loopback ESMTP rod-test");
        string? from = null;
        var rcpts = new List<string>();
        while (true)
        {
            var line = await reader.ReadLineAsync(stopping);
            if (line is null)
                return;

            if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase))
            {
                // No extensions: the client stays on the sequential plain
                // protocol (no PIPELINING, no STARTTLS, no AUTH offered).
                await writer.WriteLineAsync("250-loopback");
                await writer.WriteLineAsync("250 OK");
            }
            else if (line.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase))
            {
                from = line["MAIL FROM:".Length..].Trim();
                await writer.WriteLineAsync("250 ok");
            }
            else if (line.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase))
            {
                rcpts.Add(line["RCPT TO:".Length..].Trim());
                await writer.WriteLineAsync("250 ok");
            }
            else if (line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("354 go");
                var body = new StringBuilder();
                while (true)
                {
                    var data = await reader.ReadLineAsync(stopping);
                    if (data is null)
                        return;
                    if (data == ".")
                        break;
                    // Dot-stuffing per RFC 5321: a leading double dot is one.
                    body.AppendLine(data.StartsWith("..") ? data[1..] : data);
                }
                await writer.WriteLineAsync("250 accepted");
                lock (_lock)
                {
                    _messages.Add(new ReceivedMessage(
                        from ?? "", rcpts.ToArray(), body.ToString()));
                }
            }
            else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("221 bye");
                return;
            }
            else if (line.StartsWith("RSET", StringComparison.OrdinalIgnoreCase))
            {
                from = null;
                rcpts.Clear();
                await writer.WriteLineAsync("250 ok");
            }
            else
            {
                // Something this receiver does not speak; answer the
                // permissive default so the client's view stays coherent.
                await writer.WriteLineAsync("250 ok");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch
        {
            // The loop exits with the listener; nothing to surface.
        }
    }

    /// <summary>One accepted message: the envelope and the raw MIME text.</summary>
    public sealed record ReceivedMessage(string From, string[] RcptTo, string Data);
}
