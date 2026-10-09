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
    /// </summary>
    public sealed record Result(
        bool Succeeded,
        Guid? ArtifactId,
        string? Name,
        string? ContentType,
        long Size,
        int Findings,
        string Summary,
        string? Reason);

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
                return Failed($"the registry has no record for '{domain}'");
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
