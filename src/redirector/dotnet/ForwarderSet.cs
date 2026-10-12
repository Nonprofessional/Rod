using System.Net;
using System.Net.Sockets;

namespace Rod.Redirector;

/// <summary>
/// The rule-driven fleet of forwarders (architecture.md Sec 8): one
/// <see cref="Forwarder"/> per rule the control plane pushes, reconciled
/// against the previous set -- new rules start, gone rules stop, changed
/// rules restart. Each forwarder is the same opaque L4 splice the manual
/// mode runs; the set only decides which sockets are open.
/// </summary>
internal sealed class ForwarderSet : IAsyncDisposable
{
    private readonly TextWriter _log;
    private readonly Dictionary<string, Running> _running = [];

    private sealed record Running(Forwarder Forwarder, Task Run, CancellationTokenSource Stop, ForwardingRuleWire Rule);

    public ForwarderSet(TextWriter log)
    {
        _log = log;
    }

    public int RunningCount => _running.Count;

    /// <summary>
    /// Reconciles the running set against the pushed rules. Idempotent: a
    /// rule whose id and shape match one already running is left alone.
    /// Transports this binary cannot serve log and skip -- the set stays
    /// useful the day the control plane grows a carriage this build predates.
    /// </summary>
    public async Task ApplyAsync(IReadOnlyList<ForwardingRuleWire> rules)
    {
        var wanted = new Dictionary<string, ForwardingRuleWire>();
        foreach (var rule in rules)
        {
            if (!string.Equals(rule.Transport, "tcp", StringComparison.OrdinalIgnoreCase))
            {
                _ = _log.WriteLineAsync($"rod-redirector: rule {rule.RuleId} names transport '{rule.Transport}', which this build does not carry; skipped");
                continue;
            }

            wanted[rule.RuleId] = rule;
        }

        // Stop the gone and the changed first, so a relocated listen port
        // never collides with its own previous bind.
        foreach (var (id, running) in _running.ToList())
        {
            if (wanted.TryGetValue(id, out var rule) && running.Rule == rule)
                continue;

            _running.Remove(id);
            await StopAsync(id, running);
        }

        foreach (var (id, rule) in wanted)
        {
            if (_running.ContainsKey(id))
                continue;

            var forwarder = new Forwarder(
                new IPEndPoint(ParseListenAddress(rule.ListenHost), rule.ListenPort),
                rule.UpstreamHost,
                rule.UpstreamPort,
                CidrAllowList.Parse(SplitAllow(rule.AllowList)),
                _log);
            var stop = new CancellationTokenSource();
            try
            {
                forwarder.Start();
            }
            catch (SocketException ex)
            {
                // A bind that fails (the port is taken, the address is not
                // this host's) kills that rule alone; the rest of the set and
                // the control channel stay up, and the next push retries.
                stop.Dispose();
                _ = _log.WriteLineAsync($"rod-redirector: rule {id} could not bind {rule.ListenHost}:{rule.ListenPort}: {ex.Message}");
                continue;
            }

            var run = Task.Run(() => forwarder.RunAsync(stop.Token));
            _running[id] = new Running(forwarder, run, stop, rule);
        }
    }

    /// <summary>Stops every forwarder and drains their accept loops.</summary>
    public async Task StopAllAsync()
    {
        foreach (var (id, running) in _running.ToList())
        {
            _running.Remove(id);
            await StopAsync(id, running);
        }
    }

    public async ValueTask DisposeAsync() => await StopAllAsync();

    private async Task StopAsync(string id, Running running)
    {
        running.Stop.Cancel();
        try
        {
            await running.Run;
        }
        catch (OperationCanceledException)
        {
            // The accept loop's clean exit.
        }

        running.Stop.Dispose();
    }

    private static string[]? SplitAllow(string? allowList)
        => string.IsNullOrWhiteSpace(allowList) ? null : allowList.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static IPAddress ParseListenAddress(string host)
        => host is "*" or "0.0.0.0" ? IPAddress.Any
            : host is "::" or "::0" ? IPAddress.IPv6Any
            : IPAddress.Parse(host.TrimStart('[').TrimEnd(']'));
}
