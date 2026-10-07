using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Rod.Integration.Tests;

/// <summary>
/// A loopback webhook receiver: binds a local port and records every
/// pushed body verbatim. The lab half of the Sec 4.4 URL posture -- plain
/// http is accepted for loopback, which is what lets the suite be its own
/// far end.
/// </summary>
public sealed class LoopbackWebhook : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pump;

    public ConcurrentQueue<(string Path, string Body)> Requests { get; } = new();

    public string Url { get; }

    private LoopbackWebhook(int port)
    {
        Url = $"http://127.0.0.1:{port}/push";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _pump = PumpAsync(_stop.Token);
    }

    public static LoopbackWebhook Start()
    {
        // HttpListener cannot ask the stack for an ephemeral port; discover
        // a free one with a TCP probe and take it (the suite races nothing).
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new LoopbackWebhook(port);
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }

            using (context.Response)
            using (var reader = new StreamReader(context.Request.InputStream))
            {
                Requests.Enqueue((context.Request.Url!.AbsolutePath, await reader.ReadToEndAsync(cancellationToken)));
                context.Response.StatusCode = 200;
            }
        }
    }

    /// <summary>Waits until the receiver has seen the expected number of pushes.</summary>
    public async Task UntilAsync(int count)
    {
        for (var i = 0; i < 200; i++)
        {
            if (Requests.Count >= count)
                return;
            await Task.Delay(50);
        }

        Assert.Fail($"Expected {count} push(es); saw {Requests.Count}.");
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _pump.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }
}
