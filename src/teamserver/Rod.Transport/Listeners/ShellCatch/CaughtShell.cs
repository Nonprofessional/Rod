using System.Net.Sockets;
using System.Net;
using System.Text;
using Rod.CoreState.ShellSessions;

namespace Rod.Transport.Listeners.ShellCatch;

/// <summary>
/// The runtime half of one caught shell: everything the socket pump and the
/// operator endpoints share that the durable <see cref="ShellSession"/>
/// entity deliberately does not hold (architecture.md Sec 8). The pump owns
/// the socket's read side and is the only writer to the output log; the
/// endpoints hold it through the <see cref="ShellCatchHub"/> and write the
/// input side. An operator close is a flag the pump honors -- shutting the
/// socket down here ends its read, and the flag is what makes the ending a
/// Close instead of a Lost.
///
/// Decoding is per-connection state: a shell emits a byte stream with no
/// framing, so an incremental decoder carries partial characters across
/// reads instead of mangling them at buffer edges. The v1 decoder is UTF-8
/// with replacement -- a cmd.exe code page can still mojibake, which the
/// fingerprint's Windows guess at least makes visible to the operator.
/// </summary>
public sealed class CaughtShell
{
    private readonly Socket _socket;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private volatile bool _closedByOperator;

    public CaughtShell(ShellSession session, Socket socket)
    {
        Session = session;
        _socket = socket;
        Output = new ShellOutputLog();
    }

    /// <summary>The durable entity; mutated through the session registry.</summary>
    public ShellSession Session { get; }

    /// <summary>The live output tail the console reads.</summary>
    public ShellOutputLog Output { get; }

    /// <summary>Whether an operator asked for this shell to close.</summary>
    public bool ClosedByOperator => _closedByOperator;

    /// <summary>The remote endpoint, "host:port", for display.</summary>
    public string RemoteAddress
        => _socket.RemoteEndPoint is IPEndPoint endpoint
            ? $"{endpoint.Address}:{endpoint.Port}"
            : Session.RemoteAddress;

    /// <summary>
    /// Writes one operator input down the socket: the text then a newline,
    /// the line discipline a dumb shell expects. Serialized across
    /// concurrent operator submissions; returns false when the write failed
    /// (the peer is gone and the pump will finish the ending).
    /// </summary>
    public async Task<bool> WriteInputAsync(string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\n");
        try
        {
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                await _socket.SendAsync(bytes, SocketFlags.None, cancellationToken);
                return true;
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (Exception ex) when (
            ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// The socket's read half, for the pump loop only. The socket stays
    /// private to this object so the write path is the only surface the
    /// operator routes reach.
    /// </summary>
    internal ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken cancellationToken)
        => _socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);

    /// <summary>
    /// Decodes raw socket bytes onto the output log. Kept here so the
    /// decoder state stays with the connection; returns the appended text.
    /// </summary>
    public string Decode(byte[] buffer, int count, DateTimeOffset at)
    {
        var chars = new char[Encoding.UTF8.GetMaxCharCount(count)];
        var decoded = _decoder.GetChars(buffer, 0, count, chars, 0);
        var text = new string(chars, 0, decoded);
        if (text.Length > 0)
            Output.Append(text, at);
        return text;
    }

    /// <summary>
    /// Marks the shell closed by an operator and unblocks the pump's read by
    /// shutting the socket down. Idempotent; the pump observes the flag and
    /// finishes the Close on its way out, so the ending is attributed
    /// correctly no matter which side the shutdown surfaced on.
    /// </summary>
    public void CloseByOperator()
    {
        _closedByOperator = true;
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // Already gone: the pump's read is failing on its own, which is
            // the same unblock this shutdown was for.
        }
    }
}
