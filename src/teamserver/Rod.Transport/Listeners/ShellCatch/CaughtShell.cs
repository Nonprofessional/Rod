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
/// reads instead of mangling them at buffer edges. The default decode is
/// UTF-8 with replacement; a shell that speaks a legacy code page (a cmd.exe
/// GBK box) switches the session's encoding from the console, and both
/// directions -- the decode and the input encode -- follow it.
/// </summary>
public sealed class CaughtShell
{
    private readonly Socket _socket;
    private readonly object _codecGate = new();
    private Encoding _encoding = Encoding.UTF8;
    private Decoder _decoder = Encoding.UTF8.GetDecoder();
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

    /// <summary>The wire name of the session's encoding ("utf-8" by default).</summary>
    public string EncodingName => ShellEncodings.NameOf(_encoding);

    /// <summary>
    /// Repoints the session's codec: output decodes and input encodes with the
    /// new encoding from here on. The decoder's partial-character state is
    /// connection-wide and belongs to the old encoding, so a switch mid
    /// multi-byte sequence drops that fragment -- bytes before the switch
    /// keep the decoding they were read under.
    /// </summary>
    public void SetEncoding(Encoding encoding)
    {
        lock (_codecGate)
        {
            _encoding = encoding;
            _decoder = encoding.GetDecoder();
        }
    }

    /// <summary>The remote endpoint, "host:port", for display.</summary>
    public string RemoteAddress
        => _socket.RemoteEndPoint is IPEndPoint endpoint
            ? $"{endpoint.Address}:{endpoint.Port}"
            : Session.RemoteAddress;

    /// <summary>
    /// Writes one operator input down the socket, verbatim: the caller sends
    /// exactly the bytes it means -- a command line arrives with its own
    /// newline, the interrupt byte arrives alone. Appending one here sent a
    /// second line after every command and a stray Enter after every ^C.
    /// Serialized across concurrent operator submissions; returns false when
    /// the write failed (the peer is gone and the pump will finish the
    /// ending).
    /// </summary>
    public async Task<bool> WriteInputAsync(string text, CancellationToken cancellationToken)
    {
        // Encode under the gate's snapshot: the encoding instance is itself
        // safe to read concurrently, so only the swap needs the lock.
        Encoding encoding;
        lock (_codecGate)
        {
            encoding = _encoding;
        }
        var bytes = encoding.GetBytes(text);
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
    /// The pump is the only caller, but an encoding switch can land between
    /// reads, so the decode and the swap share the codec gate.
    /// </summary>
    public string Decode(byte[] buffer, int count, DateTimeOffset at)
    {
        string text;
        lock (_codecGate)
        {
            var chars = new char[_encoding.GetMaxCharCount(count)];
            var decoded = _decoder.GetChars(buffer, 0, count, chars, 0);
            text = new string(chars, 0, decoded);
        }
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
