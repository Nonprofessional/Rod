using System.Net;
using System.Net.Sockets;

namespace Rod.Implant.Tests;

// Shared fixtures for the reference-implant xUnit suite: a self-cleaning temp
// directory, an environment-variable scope that restores the prior value, and a
// loopback TCP listener that gives the recon verbs a deterministically open port
// without a network dependency. These mirror the t.TempDir / t.Setenv helpers
// and startLoopbackListener in the Go reference implant's tests.

/// <summary>
/// A self-cleaning temp directory. The path is exposed via <see cref="Path"/>;
/// the directory and its contents are deleted on dispose. The xUnit analog of
/// Go's <c>t.TempDir</c>.
/// </summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    private TempDir(string path) => Path = path;

    public static TempDir Create()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "Rod.Implant.Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TempDir(path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* best effort; the OS reaps temp on cleanup */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}

/// <summary>
/// Sets an environment variable for the lifetime of a block and restores the
/// prior value (or removes it) on dispose. The xUnit analog of Go's
/// <c>t.Setenv</c>.
/// </summary>
internal sealed class EnvScope : IDisposable
{
    private readonly string _name;
    private readonly string? _previous;
    private readonly bool _hadPrevious;

    public EnvScope(string name, string value)
    {
        _name = name;
        _previous = Environment.GetEnvironmentVariable(name);
        _hadPrevious = _previous is not null;
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
        => Environment.SetEnvironmentVariable(_name, _hadPrevious ? _previous : null);
}

/// <summary>
/// Opens a TCP listener on 127.0.0.1 with a kernel-chosen port and keeps it
/// bound so a recon verb observes a deterministically open port without a
/// network dependency. Inbound connections are accepted and immediately closed
/// so the service probe reads a clean end-of-stream (Read returns 0) and
/// reports the port as open with no banner, matching the documented "open"
/// line. Dispose stops the listener.
/// </summary>
internal sealed class LoopbackListener : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }

    private LoopbackListener(TcpListener listener, int port)
    {
        _listener = listener;
        Port = port;
    }

    public static LoopbackListener Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var lb = new LoopbackListener(listener, port);
        lb.AcceptAndCloseLoop();
        return lb;
    }

    // Drains the accept queue by accepting each inbound connection and closing
    // it at once. Without this the probe would block on a banner read until its
    // timeout; the graceful close lets Read return 0 (open, no banner).
    private void AcceptAndCloseLoop()
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient accepted;
                try { accepted = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
                catch (SocketException) { return; } // listener stopped
                accepted.Dispose();
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}

