using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Sessions;
using Rod.CoreState.Tasks;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The engagement's network picture (architecture.md Sec 11.2): assembled,
/// never stored. Hosts group from the implants' own enrollment hostnames,
/// pivot links from the parentage the child-enrollment path records, and
/// observed hosts and ports from completed recon task outputs parsed at read
/// time against the documented JSON-lines grammar (extending/tradecraft.md)
/// -- a line that does not parse is not a finding, so unparseable output
/// stays in the transcript instead of erroring the view. Chain state
/// surfaces the way every evidence projection holds it: a broken chain says
/// so rather than silently rendering.
///
/// Read-only by construction, like the report projections: everything here is
/// durable and attributed already, and the topology is a projection of it --
/// no host store, no edge store, nothing to keep consistent. Scoped by
/// engagement (architecture.md Sec 3): the engagement id in the path binds
/// every lookup.
/// </summary>
public static class TopologyEndpoints
{
    // The recon verbs whose output the projection parses. Transport cannot
    // reference the tradecraft layer that owns the names (the layer rule), so
    // the strings stand here with the capability catalog as their authority
    // and the grammar as the contract.
    private const string PortscanVerb = "recon.portscan";
    private const string HostenumVerb = "recon.hostenum";

    // A recon sweep against a /24 can print a lot of lines; past this the
    // parse stops and the rest stays in the transcript -- the read stays
    // bounded no matter what a handler printed.
    private const int MaxObservationLinesPerTask = 10_000;

    // The host kinds, in picture terms: a host the fleet occupies, one only
    // recon has seen, and one only a note or label names.
    internal const string KindEnrolled = "enrolled";
    internal const string KindObserved = "observed";
    internal const string KindNoted = "noted";

    // The link kinds. "pivot" is the parentage edge: the parent derived the
    // child, so the fleet's reach deepens along it.
    internal const string LinkKindPivot = "pivot";

    public static IEndpointRouteBuilder MapTopologyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/engagements/{engagementId}/topology", GetTopologyAsync)
            .RequireAuthorization(OperatorScopes.ReadPolicy)
            .WithName(nameof(GetTopologyAsync));
        return endpoints;
    }

    private static async Task<IResult> GetTopologyAsync(
        string engagementId,
        IEngagementRepository engagements,
        IImplantRepository implants,
        ISessionRegistry sessions,
        ITaskRepository tasks,
        IAuditStore audit,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        var engagement = await engagements.FindAsync(new EngagementId(engagementValue), cancellationToken);
        if (engagement is null)
            return Results.NotFound(new Problem("Engagement does not exist."));
        var engagementKey = new EngagementId(engagementValue);

        // The trail feeds the labels (and the noted hosts) and the integrity
        // stamp, the same read the report builder makes.
        var trail = await audit.ListAsync(engagementValue, cancellationToken);
        var chainBreak = AuditChain.VerifyTrail(trail);
        var facts = HostEndpoints.ParseHostFacts(trail);
        var labelsByHost = HostEndpoints.ReduceLabels(facts);

        var enrolled = await implants.ListByEngagementAsync(engagementKey, cancellationToken);
        var online = (await sessions.ListActiveAsync(engagementKey, cancellationToken))
            .Select(s => s.ImplantId)
            .ToHashSet();

        // The picture's hosts, assembled from the three sources. Kind records
        // the strongest claim: a host the fleet occupies beats one recon saw,
        // which beats one only a fact names. A host a note or label names
        // enters here even with no implant and no observation behind it.
        var hosts = new Dictionary<string, TopologyHost>(StringComparer.Ordinal);
        foreach (var host in facts.Select(f => f.Host).Distinct(StringComparer.Ordinal))
        {
            hosts[host] = new TopologyHost(
                host, KindNoted, null, null, [], 0,
                labelsByHost.GetValueOrDefault(host, []).Select(l => l.Label).ToArray());
        }

        var observations = await ParseObservationsAsync(tasks, engagementKey, cancellationToken);
        var observedAttributes = new Dictionary<string, (string? Os, string? Arch)>(StringComparer.Ordinal);
        foreach (var observation in observations)
        {
            if (!observedAttributes.TryGetValue(observation.Host, out var attrs))
                attrs = (null, null);
            observedAttributes[observation.Host] = (attrs.Os ?? observation.Os, attrs.Arch ?? observation.Arch);

            if (!hosts.ContainsKey(observation.Host))
            {
                hosts[observation.Host] = new TopologyHost(
                    observation.Host, KindObserved, observation.Os, observation.Arch, [], 0, []);
            }
        }

        // The host key an implant groups under: its normalized hostname, or
        // its own id when it reported none -- a standalone node rather than a
        // pinned-to-nothing entry.
        string HostKeyOf(Implant implant)
            => HostEndpoints.NormalizeHost(implant.Hostname) ?? implant.Id.ToString();

        var implantById = enrolled.ToDictionary(i => i.Id);
        foreach (var implant in enrolled.OrderBy(i => i.CreatedAt))
        {
            var key = HostKeyOf(implant);
            if (!hosts.TryGetValue(key, out var host))
            {
                host = new TopologyHost(key, KindObserved, null, null, [], 0, []);
                hosts[key] = host;
            }

            host = host with
            {
                Kind = KindEnrolled,
                ImplantIds = [.. host.ImplantIds, implant.Id.ToString()],
                Online = host.Online + (online.Contains(implant.Id) ? 1 : 0),
                Os = host.Os ?? implant.Os,
                Arch = host.Arch ?? implant.Arch,
            };
            hosts[key] = host;
        }

        // Fold hostenum attributes onto whatever entry the host already has:
        // recon's read of the OS fills what the enrollment did not report.
        foreach (var (host, attrs) in observedAttributes)
        {
            if (attrs is not { Os: null, Arch: null } && hosts.TryGetValue(host, out var entry))
                hosts[host] = entry with { Os = entry.Os ?? attrs.Os, Arch = entry.Arch ?? attrs.Arch };
        }

        // The pivot links: every child derivation is an edge from the parent
        // outward -- how the fleet's reach deepened into the environment. A
        // parent the engagement no longer resolves (deleted by hand) leaves
        // the child standing, not the picture broken.
        var links = new List<TopologyLink>();
        foreach (var implant in enrolled)
        {
            if (implant.ParentImplantId is not { } parent
                || !implantById.TryGetValue(parent, out var parentImplant))
            {
                continue;
            }

            links.Add(new TopologyLink(
                parentImplant.Id.ToString(),
                implant.Id.ToString(),
                HostKeyOf(parentImplant),
                HostKeyOf(implant),
                LinkKindPivot));
        }

        return Results.Ok(new TopologyResponse(
            engagementValue,
            [.. hosts.Values.OrderBy(h => h.Host, StringComparer.Ordinal)],
            [.. links],
            [.. observations],
            chainBreak is null,
            chainBreak?.ToString()));
    }

    // One recon finding parsed off a completed task's output. The grammar is
    // the documented JSON-lines contract: one object per line, "host"
    // required, the rest optional and unknown fields ignored.
    private static async Task<List<TopologyObservation>> ParseObservationsAsync(
        ITaskRepository tasks,
        EngagementId engagement,
        CancellationToken cancellationToken)
    {
        var observations = new List<TopologyObservation>();
        var history = await tasks.ListByEngagementAsync(engagement, cancellationToken);
        foreach (var task in history)
        {
            if (task.Status != Rod.CoreState.Tasks.TaskStatus.Completed
                || (task.Verb != PortscanVerb && task.Verb != HostenumVerb)
                || string.IsNullOrEmpty(task.Output))
            {
                continue;
            }

            var parsed = 0;
            foreach (var line in task.Output.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                if (parsed >= MaxObservationLinesPerTask)
                    break;

                Finding? finding;
                try
                {
                    finding = ParseFinding(line);
                }
                catch (JsonException)
                {
                    continue; // Not a finding this view can read; the transcript keeps it.
                }

                if (finding is null)
                    continue;

                parsed++;
                observations.Add(new TopologyObservation(
                    finding.Value.Host,
                    finding.Value.Port,
                    finding.Value.State,
                    finding.Value.Service,
                    finding.Value.Address,
                    finding.Value.Os,
                    finding.Value.Arch,
                    task.Id.ToString(),
                    task.ImplantId.ToString(),
                    task.Verb,
                    task.CompletedAt ?? task.CreatedAt));
            }
        }

        return observations;
    }

    private readonly record struct Finding(
        string Host,
        int? Port,
        string? State,
        string? Service,
        string? Address,
        string? Os,
        string? Arch);

    private static Finding? ParseFinding(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (!root.TryGetProperty("host", out var hostElement)
            || hostElement.ValueKind != JsonValueKind.String
            || hostElement.GetString() is not { Length: > 0 } host)
        {
            return null;
        }

        int? port = null;
        if (root.TryGetProperty("port", out var portElement)
            && portElement.ValueKind == JsonValueKind.Number
            && portElement.TryGetInt32(out var portValue))
        {
            port = portValue;
        }

        string? StringProperty(string name)
            => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;

        // The hostenum grammar carries an addresses array; the observation
        // keeps one address slot, so the findings read as a comma join -- the
        // display shape, not a data loss (the transcript holds the source).
        string? address = null;
        if (root.TryGetProperty("addresses", out var addresses)
            && addresses.ValueKind == JsonValueKind.Array)
        {
            address = string.Join(
                ",",
                addresses.EnumerateArray()
                    .Where(a => a.ValueKind == JsonValueKind.String)
                    .Select(a => a.GetString()));
        }

        return new Finding(
            host,
            port,
            StringProperty("state"),
            StringProperty("service"),
            address,
            StringProperty("os"),
            StringProperty("arch"));
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    // One host in the picture: the key it groups under (the normalized
    // hostname, or the implant id when unattributed), the strongest claim on
    // it (enrolled / observed / noted), what the sources reported about it,
    // and the fleet's occupancy.
    public sealed record TopologyHost(
        string Host,
        string Kind,
        string? Os,
        string? Arch,
        string[] ImplantIds,
        int Online,
        string[] Labels);

    // One edge in the picture: a parent deriving a child -- the pivot link,
    // directed outward from the parent, how the fleet's reach deepened.
    public sealed record TopologyLink(
        string FromImplantId,
        string ToImplantId,
        string FromHost,
        string ToHost,
        string Kind);

    // One recon finding, attributed to the task that produced it: a port
    // observation (port, state, service) or a host observation (address, os,
    // arch as far as the grammar carried them).
    public sealed record TopologyObservation(
        string Host,
        int? Port,
        string? State,
        string? Service,
        string? Address,
        string? Os,
        string? Arch,
        string TaskId,
        string ImplantId,
        string Verb,
        DateTimeOffset At);

    // The whole picture: hosts, links, and observations over the projection's
    // own integrity stamp.
    public sealed record TopologyResponse(
        Guid EngagementId,
        TopologyHost[] Hosts,
        TopologyLink[] Links,
        TopologyObservation[] Observations,
        bool ChainVerified,
        string? ChainBreak);
}
