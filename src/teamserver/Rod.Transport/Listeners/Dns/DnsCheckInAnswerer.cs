using Microsoft.Extensions.Logging;

namespace Rod.Transport.Listeners.Dns;

// The DNS check-in answer core (architecture.md Sec 8), shared by the two
// carriages that speak the DNS grammar: the UDP listener's datagrams and
// the DoH route's HTTP bodies (RFC 8484). The wire grammar lives in
// DnsCheckInNames and the contract doc; the tasking/presence composition
// lives in DnsBeaconBridge. The carriage changes; the answer never does.

/// <summary>
/// Answers one DNS wire query under a zone: a poll or result chunk under
/// the zone gets the check-in treatment; anything else in the zone is
/// NXDOMAIN, the shape a resolver expects for an unknown name, so the zone
/// does not advertise what it is; a query for another zone entirely is
/// REFUSED (rcode 5) -- this listener is not an open resolver.
/// </summary>
internal sealed class DnsCheckInAnswerer
{
    private readonly string _zone;
    private readonly DnsBeaconBridge _bridge;
    private readonly ILogger _logger;
    private readonly string _listenerName;

    public DnsCheckInAnswerer(string zone, DnsBeaconBridge bridge, ILogger logger, string listenerName)
    {
        _zone = zone.TrimEnd('.').ToLowerInvariant();
        _bridge = bridge;
        _logger = logger;
        _listenerName = listenerName;
    }

    /// <summary>
    /// Builds the response wire message for one query. Fully async: the
    /// bridge calls (a task claim, an audit append) are awaited, never
    /// blocked on -- a blocked answer thread is a thread pool thread, and
    /// enough of those is exactly the starvation the Kestrel heartbeat
    /// warns about.
    /// </summary>
    public async Task<byte[]> AnswerAsync(byte[] query, CancellationToken cancellationToken)
    {
        var parsed = DnsCodec.ParseQuery(query);
        if (parsed?.Question is not { } question)
            return EmptyResponse(parsed?.Id ?? 0, responseCode: 1); // FORMERR

        var name = question.Name.ToLowerInvariant();

        // Only TXT check-ins under our zone; no recursion, no other records.
        if (!name.EndsWith(_zone, StringComparison.Ordinal))
            return EmptyResponse(parsed.Id, responseCode: 5); // REFUSED: not our zone

        if (question.Type != DnsCodec.TxtType)
            return EmptyResponse(parsed.Id, responseCode: 3); // NXDOMAIN: TXT only

        var response = new DnsMessage
        {
            Id = parsed.Id,
            IsResponse = true,
            Question = question,
            ResponseCode = 0,
        };

        try
        {
            if (DnsCheckInNames.TryParsePoll(name, _zone) is { } poll)
            {
                var marshaled = await _bridge.PollAsync(poll.Implant, cancellationToken);
                if (marshaled is not null)
                    response.Answers.Add(TxtAnswer(name, DnsCheckInNames.Encode(marshaled)));
            }
            else if (DnsCheckInNames.TryParseResult(name, _zone) is { } chunk)
            {
                await _bridge.ResultChunkAsync(
                    chunk.Implant, chunk.Task, chunk.Outcome, chunk.Sequence, chunk.Terminal, chunk.Chunk,
                    cancellationToken);
            }
            else
            {
                response.ResponseCode = 3; // NXDOMAIN: in-zone but not a check-in
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DNS listener {Name} failed a check-in for {Question}.", _listenerName, question.Name);
            return EmptyResponse(parsed.Id, responseCode: 2); // SERVFAIL
        }

        return DnsCodec.EncodeResponse(response);
    }

    private static DnsTxtAnswer TxtAnswer(string name, string encoded)
    {
        // Split the base32 payload into TXT strings of at most 200 chars: the
        // record's strings concatenate back into one payload on the implant
        // side, and the split keeps any single string well under the 255-byte
        // TXT limit with the EDNS0 budget in mind.
        var strings = new List<string>();
        for (var offset = 0; offset < encoded.Length; offset += 200)
            strings.Add(encoded.Substring(offset, Math.Min(200, encoded.Length - offset)));
        return new DnsTxtAnswer(name, strings);
    }

    private static byte[] EmptyResponse(ushort id, ushort responseCode)
        => DnsCodec.EncodeResponse(new DnsMessage { Id = id, IsResponse = true, ResponseCode = responseCode });
}
