using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Rod.Audit;

namespace Rod.Operators.Workbench;

/// <summary>
/// The external recon workbench's use cases (architecture.md Sec 11.4): the
/// pre-foothold lookups and the scan whose findings land as task-less
/// engagement artifacts. This type runs the egress and captures the evidence;
/// the gates around it -- engagement resolution, the closed-engagement
/// refusal, the scan's ROE target scope -- live in the endpoint, which also
/// owns the audit writes (the composition rule every store holds here). It
/// never throws for egress problems, it reports them: the outcome is data
/// for the trail, exactly the webhook-pusher and LLM-client discipline.
/// </summary>
public sealed class ReconWorkbenchService
{
    /// <summary>
    /// One workbench run: the artifact its findings landed as on success,
    /// the readable reason on failure. The counts ride both arms -- the
    /// trail's one-line summary names what the run found either way.
    /// <see cref="RegistryMiss"/> marks the RDAP answer "no record for
    /// this domain" so the route can fall back to whois without matching
    /// on the reason's wording.
    /// </summary>
    public sealed record Result(
        bool Succeeded,
        Guid? ArtifactId,
        string? Name,
        string? ContentType,
        long Size,
        int Findings,
        string Summary,
        string? Reason,
        bool RegistryMiss = false);

    private readonly IOptions<ReconWorkbenchOptions> _options;
    private readonly IHttpClientFactory _clients;
    private readonly IArtifactStore _artifacts;
    private readonly TimeProvider _clock;
    private HttpClient? _lookupClient;

    public ReconWorkbenchService(
        IOptions<ReconWorkbenchOptions> options,
        IHttpClientFactory clients,
        IArtifactStore artifacts,
        TimeProvider clock)
    {
        _options = options;
        _clients = clients;
        _artifacts = artifacts;
        _clock = clock;
    }

    public bool RdapConfigured => _options.Value.RdapConfigured;
    public bool CtConfigured => _options.Value.CtConfigured;
    public bool DohConfigured => _options.Value.DohConfigured;
    public bool WhoisConfigured => _options.Value.WhoisConfigured;
    public bool ScanConfigured => _options.Value.ScanConfigured;

    // The lazy-client discipline (the webhook pusher's): registering the
    // workbench must not activate an HTTP handler pipeline in hosts that
    // never run a lookup.
    private HttpClient LookupClient
        => _lookupClient ??= _clients.CreateClient("recon");

    // The recon grammar's property casing (extending/tradecraft.md): every
    // finding line the workbench writes -- and the registration record's
    // vocabulary beside it -- serializes camelCase, the shape the topology
    // projection parses and an operator reads.
    private static readonly JsonSerializerOptions FindingJson =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private static readonly JsonSerializerOptions RecordJson =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// Queries the configured RDAP service for a domain's registration
    /// record and captures the normalized record as a JSON artifact named
    /// <c>recon.rdap:{domain}</c>. A domain the registry has no record for
    /// is a failed lookup with the reason named, not an empty finding --
    /// "no such domain" is an answer about the egress, not evidence.
    /// </summary>
    public async Task<Result> RdapLookupAsync(
        Guid engagementId,
        Guid operatorId,
        string domain,
        CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        try
        {
            using var response = await LookupClient.GetAsync(
                $"{options.RdapBaseUrl!.TrimEnd('/')}/domain/{Uri.EscapeDataString(domain)}",
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Missed($"the registry has no record for '{domain}'");
            if (!response.IsSuccessStatusCode)
                return Failed($"the RDAP service answered {(int)response.StatusCode}");

            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var (record, findings) = NormalizeRdap(domain, doc.RootElement);
            return await CapturedAsync(
                engagementId, operatorId, $"recon.rdap:{domain}", "application/json",
                JsonSerializer.Serialize(record, RecordJson),
                findings,
                $"{domain}: {(findings > 0 ? $"{findings} nameserver(s), registrar on record" : "record parsed, no nameservers on record")}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed($"the request exceeded the {options.RequestTimeoutSeconds}s budget");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException or JsonException)
        {
            return Failed(FirstLine(ex.Message));
        }
    }

    /// <summary>
    /// Queries the configured certificate-transparency mirror for names
    /// under a domain and captures the deduplicated set as a JSON-lines
    /// artifact named <c>recon.subdomains:{domain}</c> -- one
    /// <c>{"host":name}</c> per line, the documented recon grammar, so the
    /// topology projection parses the findings like any recon output. A
    /// wildcard certificate names no concrete host, so its literal is not
    /// a finding.
    /// </summary>
    public async Task<Result> EnumerateSubdomainsAsync(
        Guid engagementId,
        Guid operatorId,
        string domain,
        CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        try
        {
            using var response = await LookupClient.GetAsync(
                $"{options.CtBaseUrl!.TrimEnd('/')}/?q=%25.{Uri.EscapeDataString(domain)}&output=json",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                return Failed($"the CT mirror answered {(int)response.StatusCode}");

            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var names = CollectSubdomainNames(doc.RootElement, domain, options.MaxSubdomainNames);
            var lines = names.Select(n => JsonSerializer.Serialize(new FindingLine(n), FindingJson));
            return await CapturedAsync(
                engagementId, operatorId, $"recon.subdomains:{domain}", "application/x-ndjson",
                string.Join("\n", lines) + "\n",
                names.Count,
                $"{domain}: {names.Count} distinct name(s) in certificate transparency");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed($"the request exceeded the {options.RequestTimeoutSeconds}s budget");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException or JsonException)
        {
            return Failed(FirstLine(ex.Message));
        }
    }

    /// <summary>
    /// Resolves names through the configured DNS-over-HTTPS resolver and
    /// captures the answers as JSON-lines findings -- one
    /// <c>{"host":name,"addresses":[...]}</c> per name that answered, the
    /// hostenum shape, so the topology projection carries the addresses.
    /// An IP target asks for its PTR name instead: the discovered name is
    /// the finding, the address its evidence. A single target that answers
    /// nothing is a failed run with the negative named; a bulk run counts
    /// its misses in the summary and records only the names that live --
    /// the census's follow-up ("which of the enumerated names live").
    /// </summary>
    public async Task<Result> ResolveAsync(
        Guid engagementId,
        Guid operatorId,
        IReadOnlyList<string> targets,
        CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        try
        {
            var answered = new ConcurrentBag<(int Index, string Line, string Name)>();
            using var limiter = new SemaphoreSlim(Math.Max(1, options.ResolveConcurrency));
            await Task.WhenAll(targets.Select(async (target, index) =>
            {
                await limiter.WaitAsync(cancellationToken);
                try
                {
                    if (await ResolveOneAsync(target, options, cancellationToken) is { } line)
                        answered.Add((index, line, target));
                }
                finally
                {
                    limiter.Release();
                }
            }));

            if (answered.IsEmpty)
            {
                var target = targets[0];
                return Failed(IPAddress.TryParse(target, out _)
                    ? $"no PTR record for '{target}'"
                    : $"no address records for '{target}'");
            }

            var ordered = answered.OrderBy(a => a.Index).ToArray();
            var name = targets.Count == 1
                ? $"recon.resolve:{targets[0]}"
                : $"recon.resolve:{targets.Count}-names";
            return await CapturedAsync(
                engagementId, operatorId, name, "application/x-ndjson",
                string.Join("\n", ordered.Select(a => a.Line)) + "\n",
                ordered.Length,
                targets.Count == 1
                    ? $"{targets[0]}: resolved"
                    : $"{ordered.Length} of {targets.Count} names resolved");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed($"the resolution exceeded the {options.RequestTimeoutSeconds}s budget");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException or JsonException)
        {
            return Failed(FirstLine(ex.Message));
        }
    }

    /// <summary>
    /// Asks the configured whois server (port 43, the protocol the RDAP
    /// world replaced) for a domain's record and captures the answer
    /// verbatim as a text artifact named <c>recon.whois:{domain}</c>. One
    /// query, no referral chasing: a server that hands back a referral
    /// names it in the captured text, and an operator chasing one re-runs
    /// against that server.
    /// </summary>
    public async Task<Result> WhoisLookupAsync(
        Guid engagementId,
        Guid operatorId,
        string domain,
        CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        try
        {
            var (host, port) = ParseWhoisEndpoint(options.WhoisServer!);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.RequestTimeoutSeconds)));
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, budget.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(domain + "\r\n"), budget.Token);
            using var answer = new MemoryStream();
            await stream.CopyToAsync(answer, budget.Token);
            var text = Encoding.UTF8.GetString(answer.ToArray()).Trim();
            if (text.Length == 0)
                return Failed("the whois server returned an empty answer");

            return await CapturedAsync(
                engagementId, operatorId, $"recon.whois:{domain}", "text/plain",
                text + "\n",
                0,
                $"{domain}: whois answer captured ({text.Length} chars)");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed($"the whois request exceeded the {options.RequestTimeoutSeconds}s budget");
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException or FormatException)
        {
            return Failed(FirstLine(ex.Message));
        }
    }

    /// <summary>
    /// TCP-connect scans a host from the teamserver itself (the one shipped
    /// scan origin) and captures the open ports as JSON-lines findings named
    /// <c>recon.portscan:{host}:{ports}</c>, the same recon grammar the
    /// implant-side verb prints. Closed and filtered ports are not findings
    /// -- the artifact records what answered, and the count in the summary
    /// names the scan's yield.
    /// </summary>
    public async Task<Result> ScanAsync(
        Guid engagementId,
        Guid operatorId,
        string host,
        IReadOnlyList<int> ports,
        string portsLabel,
        CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        try
        {
            var open = await ConnectScanAsync(host, ports, options, cancellationToken);
            var lines = open.Select(p => JsonSerializer.Serialize(new PortFindingLine(host, p), FindingJson));
            return await CapturedAsync(
                engagementId, operatorId, $"recon.portscan:{host}:{portsLabel}", "application/x-ndjson",
                string.Join("\n", lines) + (open.Count > 0 ? "\n" : string.Empty),
                open.Count,
                $"{host} [{portsLabel}]: {open.Count} port(s) open");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed($"the scan exceeded its budget");
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            return Failed(FirstLine(ex.Message));
        }
    }

    // One bounded sweep: every port gets the same connect budget, the
    // concurrency cap bounds the sweep's footprint, and a port that refuses,
    // times out, or otherwise fails to complete a handshake is closed or
    // filtered -- the scan's answer for it is silence.
    private static async Task<List<int>> ConnectScanAsync(
        string host,
        IReadOnlyList<int> ports,
        ReconWorkbenchOptions options,
        CancellationToken cancellationToken)
    {
        var open = new ConcurrentBag<int>();
        using var limiter = new SemaphoreSlim(Math.Max(1, options.ScanConcurrency));
        var connectTimeout = TimeSpan.FromMilliseconds(Math.Max(100, options.ScanConnectTimeoutMilliseconds));
        await Task.WhenAll(ports.Select(async port =>
        {
            await limiter.WaitAsync(cancellationToken);
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(connectTimeout);
                using var client = new TcpClient();
                try
                {
                    await client.ConnectAsync(host, port, budget.Token);
                    open.Add(port);
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                {
                    // Refused or timed out: not open, not a finding.
                }
            }
            finally
            {
                limiter.Release();
            }
        }));
        return open.OrderBy(p => p).ToList();
    }

    private async Task<Result> CapturedAsync(
        Guid engagementId,
        Guid operatorId,
        string name,
        string contentType,
        string content,
        int findings,
        string summary)
    {
        var artifactId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes(content);
        await _artifacts.SaveAsync(
            new Artifact(
                ArtifactId: artifactId,
                EngagementId: engagementId,
                TaskId: null,
                OperatorId: operatorId,
                Name: name,
                ContentType: contentType,
                Content: bytes,
                Size: bytes.Length,
                StoredAt: _clock.GetUtcNow()));
        return new Result(
            true, artifactId, name, contentType, bytes.Length, findings, summary, null);
    }

    private static Result Failed(string reason)
        => new(false, null, null, null, 0, 0, string.Empty, reason);

    private static Result Missed(string reason)
        => new(false, null, null, null, 0, 0, string.Empty, reason, RegistryMiss: true);

    // One name through the resolver. Returns the grammar line when the name
    // answered, null when it did not -- the caller turns the null into a
    // named negative (single) or a counted miss (bulk). A recursive
    // resolver answers a CNAME chain with the alias and the target's
    // address records in the same section, so one A query and one AAAA
    // query cover the chain and the alias rides along.
    private async Task<string?> ResolveOneAsync(
        string target, ReconWorkbenchOptions options, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(target, out var address))
        {
            var ptr = (await QueryDohAsync(PtrName(address), "PTR", options, cancellationToken))
                .Where(a => a.Type == DohRecordTypePtr)
                .Select(a => a.Data.TrimEnd('.'))
                .FirstOrDefault();
            return ptr is { Length: > 0 } name
                ? JsonSerializer.Serialize(
                    new AddressFindingLine(name, [address.ToString()]), FindingJson)
                : null;
        }

        var answers = new List<(int Type, string Data)>();
        foreach (var typeLabel in new[] { "A", "AAAA" })
        {
            answers.AddRange(await QueryDohAsync(target, typeLabel, options, cancellationToken));
        }

        var addresses = answers
            .Where(a => a.Type is DohRecordTypeA or DohRecordTypeAaaa)
            .Select(a => a.Data)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (addresses.Length == 0)
            return null;

        var cname = answers
            .Where(a => a.Type == DohRecordTypeCname)
            .Select(a => a.Data.TrimEnd('.'))
            .FirstOrDefault();
        return JsonSerializer.Serialize(
            new AddressFindingLine(target, addresses, cname), FindingJson);
    }

    // One DoH query: GET {base}?name=&type= with the JSON accept, the
    // shape Google's and Cloudflare's resolvers both serve. The answer
    // section comes back whole, typed, in order -- the caller filters what
    // counts for its question.
    private async Task<List<(int Type, string Data)>> QueryDohAsync(
        string name,
        string typeLabel,
        ReconWorkbenchOptions options,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{options.DohBaseUrl!.TrimEnd('/')}?name={Uri.EscapeDataString(name)}&type={typeLabel}");
        request.Headers.Accept.ParseAdd("application/dns-json");
        using var response = await LookupClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"the resolver answered {(int)response.StatusCode}");

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var root = doc.RootElement;
        if (root.TryGetProperty("Status", out var status)
            && status.ValueKind == JsonValueKind.Number
            && status.GetInt32() != 0)
        {
            return []; // NXDOMAIN and its kin: the resolver's "no".
        }

        var answers = new List<(int Type, string Data)>();
        if (root.TryGetProperty("Answer", out var section) && section.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in section.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("type", out var kind)
                    && kind.ValueKind == JsonValueKind.Number
                    && entry.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.String
                    && data.GetString() is { Length: > 0 } value)
                {
                    answers.Add((kind.GetInt32(), value));
                }
            }
        }

        return answers;
    }

    // The DoH JSON answer's record types the resolver half reads.
    private const int DohRecordTypeA = 1;
    private const int DohRecordTypeCname = 5;
    private const int DohRecordTypeAaaa = 28;
    private const int DohRecordTypePtr = 12;

    // The reverse-query name for a PTR lookup: the address's bytes (or
    // nibbles) read backward under the in-addr/ip6 arpa zones.
    private static string PtrName(IPAddress address)
    {
        const string Hex = "0123456789abcdef";
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return $"{bytes[3]}.{bytes[2]}.{bytes[1]}.{bytes[0]}.in-addr.arpa";

        var labels = new StringBuilder();
        for (var i = bytes.Length - 1; i >= 0; i--)
        {
            labels.Append(Hex[bytes[i] & 0x0f]).Append('.');
            labels.Append(Hex[bytes[i] >> 4]).Append('.');
        }
        return labels.ToString() + "ip6.arpa";
    }

    private static (string Host, int Port) ParseWhoisEndpoint(string configured)
    {
        var separator = configured.LastIndexOf(':');
        return separator < 0
            ? (configured.Trim(), 43)
            : (configured[..separator].Trim(), int.Parse(configured[(separator + 1)..]));
    }

    // The RDAP record the artifact keeps: the registration facts an operator
    // aims a first implant by, in the shape the registry's own JSON carries
    // minus what normalization cannot vouch for.
    private static (RdapRecord Record, int Nameservers) NormalizeRdap(string domain, JsonElement root)
    {
        string? StringProperty(JsonElement element, string name)
            => element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        var status = root.TryGetProperty("status", out var statusElement)
            && statusElement.ValueKind == JsonValueKind.Array
            ? statusElement.EnumerateArray()
                .Where(s => s.ValueKind == JsonValueKind.String)
                .Select(s => s.GetString()!)
                .ToArray()
            : [];

        var events = root.TryGetProperty("events", out var eventsElement)
            && eventsElement.ValueKind == JsonValueKind.Array
            ? eventsElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Object)
                .Select(e => (Action: StringProperty(e, "eventAction"), Date: StringProperty(e, "eventDate")))
                .Where(e => e.Action is not null && e.Date is not null)
                .ToArray()
            : [];
        string? EventDate(string action)
            => events.FirstOrDefault(e => string.Equals(e.Action, action, StringComparison.OrdinalIgnoreCase)).Date;

        var nameservers = root.TryGetProperty("nameservers", out var nsElement)
            && nsElement.ValueKind == JsonValueKind.Array
            ? nsElement.EnumerateArray()
                .Select(ns => StringProperty(ns, "ldhName"))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : [];

        var registrar = root.TryGetProperty("entities", out var entitiesElement)
            && entitiesElement.ValueKind == JsonValueKind.Array
            ? entitiesElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Object)
                .Where(e => e.TryGetProperty("roles", out var roles)
                    && roles.ValueKind == JsonValueKind.Array
                    && roles.EnumerateArray().Any(r => r.ValueKind == JsonValueKind.String
                        && string.Equals(r.GetString(), "registrar", StringComparison.OrdinalIgnoreCase)))
                .Select(e => RegistrarName(e))
                .FirstOrDefault(n => n is not null)
            : null;

        var delegationSigned = root.TryGetProperty("secureDNS", out var dnsElement)
            && dnsElement.ValueKind == JsonValueKind.Object
            && dnsElement.TryGetProperty("delegationSigned", out var signed)
            && signed.ValueKind == JsonValueKind.True;

        return (new RdapRecord(
            domain,
            registrar,
            status,
            EventDate("registration"),
            EventDate("expiration"),
            EventDate("last changed"),
            nameservers,
            delegationSigned), nameservers.Length);
    }

    // A registrar entity's display name lives in its vcard array: the second
    // element is the jCard property list, and each property is the four-slot
    // [name, parameters, type, value] -- the "fn" property's value is the
    // name.
    private static string? RegistrarName(JsonElement entity)
    {
        if (!entity.TryGetProperty("vcardArray", out var vcard)
            || vcard.ValueKind != JsonValueKind.Array
            || vcard.GetArrayLength() < 2
            || vcard[1] is not { ValueKind: JsonValueKind.Array } properties)
        {
            return null;
        }

        foreach (var property in properties.EnumerateArray())
        {
            if (property.ValueKind == JsonValueKind.Array
                && property.GetArrayLength() >= 4
                && property[0].ValueKind == JsonValueKind.String
                && string.Equals(property[0].GetString(), "fn", StringComparison.Ordinal)
                && property[3].ValueKind == JsonValueKind.String
                && property[3].GetString() is { Length: > 0 } name)
            {
                return name;
            }
        }

        return null;
    }

    // The CT mirror's rows: each carries a name_value of one or more
    // newline-separated names. Keep exactly the names at or under the
    // queried domain, case-folded distinct, capped, ordinal-sorted -- the
    // artifact reads as a stable census.
    private static List<string> CollectSubdomainNames(JsonElement root, string domain, int cap)
    {
        var domainKey = domain.ToLowerInvariant();
        var names = new SortedSet<string>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Array)
            return [];

        foreach (var row in root.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object
                || !row.TryGetProperty("name_value", out var nameValue)
                || nameValue.ValueKind != JsonValueKind.String
                || nameValue.GetString() is not { } raw)
            {
                continue;
            }

            foreach (var candidate in raw.Split('\n'))
            {
                var name = candidate.Trim().TrimEnd('.').ToLowerInvariant();
                if (name.Length == 0 || name.StartsWith("*."))
                    continue; // A wildcard certificate names no concrete host.
                if (!name.EndsWith('.' + domainKey, StringComparison.Ordinal) && name != domainKey)
                    continue;
                names.Add(name);
                if (names.Count >= cap)
                    return names.ToList();
            }
        }

        return names.ToList();
    }

    private static string FirstLine(string text)
    {
        var separator = text.IndexOf('\n');
        return separator is > 0 and var stop ? text[..stop].Trim() : text.Trim();
    }

    // The documented recon grammar's shapes (extending/tradecraft.md): a
    // host line and a port line. Unknown fields are ignored by readers;
    // these carry exactly what the grammar promises.
    private sealed record FindingLine(string Host);

    private sealed record PortFindingLine(string Host, int Port, string State = "open");

    // The hostenum shape: a name and the addresses it answered with, the
    // alias along for the trail when one rode the chain.
    private sealed record AddressFindingLine(
        string Host,
        string[] Addresses,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? Cname = null);

    // The normalized registration record. Property names follow the RDAP
    // vocabulary an operator already reads (ldhName's status/events names)
    // so the artifact and the registry's own answer stay legible side by
    // side.
    private sealed record RdapRecord(
        string Domain,
        string? Registrar,
        string[] Status,
        string? Registered,
        string? Expires,
        string? LastChanged,
        string[] Nameservers,
        bool DelegationSigned);
}
