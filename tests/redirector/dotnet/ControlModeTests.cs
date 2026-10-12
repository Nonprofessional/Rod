using System.Net;
using System.Net.Sockets;
using Rod.Redirector;

namespace Rod.Redirector.Tests;

/// <summary>
/// The enrolled mode's local halves: the rule-driven forwarder set
/// reconciles pushed rules (start, stop, restart-on-change), and the node
/// identity persists exactly the pair enrollment minted.
/// </summary>
public class ControlModeTests
{
    private static async Task<int> GetFreePortAsync()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static ForwardingRuleWire TcpRule(string id, int listenPort, int upstreamPort)
        => new(id, "tcp", "127.0.0.1", listenPort, "127.0.0.1", upstreamPort, null);

    [Fact]
    public async Task ForwarderSet_ReconcilesPushedRules()
    {
        var upstreamA = await GetFreePortAsync();
        var upstreamB = await GetFreePortAsync();
        var frontA = await GetFreePortAsync();
        var frontB = await GetFreePortAsync();

        await using var set = new ForwarderSet(TextWriter.Null);
        await set.ApplyAsync(new[]
        {
            TcpRule("a", frontA, upstreamA),
            TcpRule("b", frontB, upstreamB),
        });
        Assert.Equal(2, set.RunningCount);
        await AssertPortAcceptsAsync(frontA);
        await AssertPortAcceptsAsync(frontB);

        // A re-push of the same rules touches nothing.
        await set.ApplyAsync(new[]
        {
            TcpRule("a", frontA, upstreamA),
            TcpRule("b", frontB, upstreamB),
        });
        Assert.Equal(2, set.RunningCount);

        // A changed rule restarts on its own bind; a gone rule stops; a rule
        // the build cannot carry skips without hurting the rest.
        var frontA2 = await GetFreePortAsync();
        await set.ApplyAsync(new[]
        {
            TcpRule("a", frontA2, upstreamB),
            new ForwardingRuleWire("u", "udp", "127.0.0.1", await GetFreePortAsync(), "127.0.0.1", 53, null),
        });
        Assert.Equal(1, set.RunningCount);
        await AssertPortAcceptsAsync(frontA2);
        await AssertPortRefusesAsync(frontA);
        await AssertPortRefusesAsync(frontB);
    }

    [Fact]
    public async Task NodeState_RoundTripsAndToleratesCorruption()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rod-node-state-{Guid.NewGuid():N}.json");
        try
        {
            Assert.Null(NodeState.Load(path));

            new NodeState("node-1", "secret-1").Save(path);
            var loaded = NodeState.Load(path);
            Assert.Equal("node-1", loaded!.NodeId);
            Assert.Equal("secret-1", loaded.NodeSecret);

            File.WriteAllText(path, "{ not json");
            Assert.Null(NodeState.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Config_EnrolledModeRejectsManualFlagsAndBadUrls()
    {
        var mixed = Assert.Throws<ExitProgramException>(
            () => RedirectorConfig.Parse(["-control", "ws://x/redirectors/control", "-listen", "0.0.0.0:443"]));
        Assert.Equal(2, mixed.ExitCode);

        var badScheme = Assert.Throws<ExitProgramException>(
            () => RedirectorConfig.Parse(["-control", "http://x/redirectors/control"]));
        Assert.Equal(2, badScheme.ExitCode);

        var enrolled = RedirectorConfig.Parse(
        [
            "-control", "wss://teamserver:5080/redirectors/control",
            "-credential", "c", "-state", "s.json",
        ]);
        Assert.True(enrolled.HasControl);
        Assert.Equal("s.json", enrolled.StatePath);

        var manual = RedirectorConfig.Parse(["-listen", "127.0.0.1:8080", "-upstream", "10.0.0.5:8443"]);
        Assert.False(manual.HasControl);
    }

    private static async Task AssertPortAcceptsAsync(int port)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        Assert.True(client.Connected);
    }

    private static async Task AssertPortRefusesAsync(int port)
    {
        // A stopped listener refuses new connections; give the unbind a
        // moment to land before asking.
        await Task.Delay(200);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port);
            Assert.Fail($"Port {port} still accepts connections.");
        }
        catch (SocketException)
        {
        }
    }
}
