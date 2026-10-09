using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Sessions;
using Rod.CoreState.Tasks;
using System.ComponentModel;
// The domain entity shares its name with System.Threading.Tasks.Task; the row
// projections below take the entity. The BCL type is not used by name here.
using Task = Rod.CoreState.Tasks.Task;

namespace Rod.Operators.Mcp;

/// <summary>
/// The MCP server's read-only toolset (architecture.md Sec 4, the operator
/// layer's agent surface): the same roster, task, and audit reads the operator
/// UI makes, exposed as standard MCP tools so an operator's own agent tooling
/// can drive the console's read side. Every tool is a read -- the write half
/// (task-issuing tools) is a separate item with its own explicit gate, so an
/// agent client can never act on an engagement through this surface, only
/// observe it. Engagement scoping is by construction: each tool takes an
/// engagement id and reads through the engagement-rooted repository methods,
/// never across engagements.
///
/// Results are compact JSON strings -- one object per row, the same field
/// names the operator API's responses carry -- so any MCP client (an LLM
/// agent or a plain script) consumes them without a bespoke schema.
/// </summary>
[McpServerToolType]
public sealed class RodReadTools
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IEngagementRepository _engagements;
    private readonly IImplantRepository _implants;
    private readonly ISessionRegistry _sessions;
    private readonly ITaskRepository _tasks;
    private readonly IAuditStore _audit;

    public RodReadTools(
        IEngagementRepository engagements,
        IImplantRepository implants,
        ISessionRegistry sessions,
        ITaskRepository tasks,
        IAuditStore audit)
    {
        _engagements = engagements;
        _implants = implants;
        _sessions = sessions;
        _tasks = tasks;
        _audit = audit;
    }

    [McpServerTool(Name = "list_engagements")]
    [Description("List every engagement on this teamserver: id, name, state, and creation time. The id scopes every other tool here.")]
    public async Task<string> ListEngagements(CancellationToken cancellationToken)
    {
        var rows = await _engagements.ListAsync(cancellationToken);
        return JsonSerializer.Serialize(rows.Select(e => new
        {
            engagementId = e.Id.ToString(),
            name = e.Name,
            state = e.IsRetired ? "retired" : e.IsClosed ? "frozen" : "open",
            createdAt = e.CreatedAt,
        }), Json);
    }

    [McpServerTool(Name = "list_implants")]
    [Description("List an engagement's implants: id, class, host details, liveness, and parentage.")]
    public async Task<string> ListImplants(
        [Description("The engagement id, as returned by list_engagements.")] string engagementId,
        CancellationToken cancellationToken)
    {
        var engagement = await ResolveEngagementAsync(engagementId, cancellationToken);
        var rows = await _implants.ListByEngagementAsync(engagement.Id, cancellationToken);
        return JsonSerializer.Serialize(rows.Select(i => new
        {
            implantId = i.Id.ToString(),
            cls = i.Class.ToString(),
            hostname = i.Hostname,
            os = i.Os,
            arch = i.Arch,
            username = i.Username,
            retired = i.IsRetired,
            lastSeenAt = i.LastSeenAt,
            parentImplantId = i.ParentImplantId?.ToString(),
            createdAt = i.CreatedAt,
        }), Json);
    }

    [McpServerTool(Name = "list_sessions")]
    [Description("List an engagement's active implant sessions -- the who-is-alive view.")]
    public async Task<string> ListSessions(
        [Description("The engagement id, as returned by list_engagements.")] string engagementId,
        CancellationToken cancellationToken)
    {
        var engagement = await ResolveEngagementAsync(engagementId, cancellationToken);
        var rows = await _sessions.ListActiveAsync(engagement.Id, cancellationToken);
        return JsonSerializer.Serialize(rows.Select(s => new
        {
            sessionId = s.Id.ToString(),
            implantId = s.ImplantId.ToString(),
            startedAt = s.StartedAt,
            lastSeenAt = s.LastSeenAt,
            carrier = s.LastCarrier,
            capabilities = s.Capabilities,
        }), Json);
    }

    [McpServerTool(Name = "list_tasks")]
    [Description("List one page of an engagement's task history, newest first. Walk pages with the returned cursor.")]
    public async Task<string> ListTasks(
        [Description("The engagement id, as returned by list_engagements.")] string engagementId,
        [Description("Page size, 1-100 (default 25).")] int? limit,
        CancellationToken cancellationToken)
    {
        var engagement = await ResolveEngagementAsync(engagementId, cancellationToken);
        var page = await _tasks.ListByEngagementPageAsync(
            engagement.Id, BoundLimit(limit), cursor: null, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            tasks = page.Items.Select(TaskRow).ToArray(),
            nextCursor = page.NextCursor,
        }, Json);
    }

    [McpServerTool(Name = "get_task")]
    [Description("Read one task in full: verb, arguments, status, outcome, and the captured output transcript.")]
    public async Task<string> GetTask(
        [Description("The engagement id, as returned by list_engagements.")] string engagementId,
        [Description("The task id, as returned by list_tasks.")] string taskId,
        CancellationToken cancellationToken)
    {
        var engagement = await ResolveEngagementAsync(engagementId, cancellationToken);
        if (!Guid.TryParse(taskId, out var taskValue))
            throw new McpException("The task id is not a valid identifier.");

        var task = await _tasks.FindAsync(new TaskId(taskValue), cancellationToken);
        if (task is null || task.EngagementId != engagement.Id)
            throw new McpException("No such task in this engagement.");

        return JsonSerializer.Serialize(TaskDetail(task), Json);
    }

    [McpServerTool(Name = "list_audit")]
    [Description("List one page of an engagement's audit trail, newest first -- every attributed operational fact. Walk pages with the returned cursor.")]
    public async Task<string> ListAudit(
        [Description("The engagement id, as returned by list_engagements.")] string engagementId,
        [Description("Page size, 1-100 (default 25).")] int? limit,
        CancellationToken cancellationToken)
    {
        var engagement = await ResolveEngagementAsync(engagementId, cancellationToken);
        var page = await _audit.ListPageAsync(
            engagement.Id.Value, BoundLimit(limit), cursor: null, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            events = page.Items.Select(e => new
            {
                at = e.At,
                kind = e.Kind.ToString(),
                verb = e.Verb,
                payload = e.Payload,
                output = e.Output,
                outcome = e.Outcome,
                implantId = e.ImplantId == Guid.Empty ? null : e.ImplantId.ToString(),
                taskId = e.TaskId == Guid.Empty ? null : e.TaskId.ToString(),
            }).ToArray(),
            nextCursor = page.NextCursor,
        }, Json);
    }

    private async Task<Engagement> ResolveEngagementAsync(string engagementId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var value))
            throw new McpException("The engagement id is not a valid identifier.");
        return await _engagements.FindAsync(new EngagementId(value), cancellationToken)
            ?? throw new McpException("No such engagement.");
    }

    private static int BoundLimit(int? limit)
        => Math.Clamp(limit ?? 25, 1, 100);

    private static object TaskRow(Task t) => new
    {
        taskId = t.Id.ToString(),
        implantId = t.ImplantId.ToString(),
        verb = t.Verb,
        status = t.Status.ToString(),
        createdAt = t.CreatedAt,
    };

    private static object TaskDetail(Task t) => new
    {
        taskId = t.Id.ToString(),
        engagementId = t.EngagementId.ToString(),
        implantId = t.ImplantId.ToString(),
        verb = t.Verb,
        arguments = t.Arguments,
        status = t.Status.ToString(),
        outcome = t.Outcome?.ToString(),
        output = t.Output,
        createdAt = t.CreatedAt,
        dispatchedAt = t.DispatchedAt,
        completedAt = t.CompletedAt,
    };
}
