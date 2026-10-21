using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Snippets;

namespace Rod.Operators.Mcp;

/// <summary>
/// The MCP server's task-issuing toolset (architecture.md Sec 4, the agent
/// surface's write half): the same <see cref="TaskService"/> issuance path
/// the operator API's task endpoint drives, so every gate holds exactly as
/// if an operator typed the command -- per-class verb sets, ROE guardrails,
/// carrier claims, closed engagements, attribution to the token's operator
/// -- beside the token's scope floor: the write tools take the operate rank
/// the route gate gives every engagement-scoped write, held at the tool
/// level because the route table cannot see a tool call's name. This class
/// registers only when the deployment opens the write gate
/// (<c>Mcp:AllowWriteTools</c>); the read toolset never carries it, so an
/// agent client cannot act on an engagement by drift.
/// </summary>
[McpServerToolType]
public sealed class RodWriteTools
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IEngagementRepository _engagements;
    private readonly EngagementAccessResolver _access;
    private readonly TaskService _tasks;
    private readonly ITaskSnippetStore _snippets;
    private readonly IAuditStore _audit;
    private readonly IHttpContextAccessor _http;

    public RodWriteTools(
        IEngagementRepository engagements,
        EngagementAccessResolver access,
        TaskService tasks,
        ITaskSnippetStore snippets,
        IAuditStore audit,
        IHttpContextAccessor http)
    {
        _engagements = engagements;
        _access = access;
        _tasks = tasks;
        _snippets = snippets;
        _audit = audit;
        _http = http;
    }

    [McpServerTool(Name = "issue_task")]
    [Description("Issue one task to an engagement's implant, through the same issuance gates the console's task dialog applies (verb set, ROE, carriers, closed state). Returns the queued task's id.")]
    public async Task<CallToolResult> IssueTask(
        [Description("The engagement id, as returned by list_engagements.")] string engagementId,
        [Description("The implant to task, as returned by list_implants.")] string implantId,
        [Description("The task verb, e.g. shell.exec or fs.ls.")] string verb,
        [Description("The task arguments as one string (JSON where the verb's grammar says so); omit for verbs that take none.")] string? arguments = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var engagement = await ResolveWritableEngagementAsync(engagementId, cancellationToken);
            if (!Guid.TryParse(implantId, out var implantValue))
                throw new McpException("The implant id is not a valid identifier.");

            return Text(await IssueAsync(
                engagement, new ImplantId(implantValue), verb, arguments ?? string.Empty, cancellationToken));
        }
        catch (McpException ex)
        {
            return Refused(ex.Message);
        }
    }

    [McpServerTool(Name = "replay_snippet")]
    [Description("Replay a saved task snippet's steps as tasks to one implant, in order, through the same issuance gates as issue_task. Returns the queued tasks' ids in step order.")]
    public async Task<CallToolResult> ReplaySnippet(
        [Description("The engagement id, as returned by list_engagements.")] string engagementId,
        [Description("The snippet id to replay.")] string snippetId,
        [Description("The implant to receive every step, as returned by list_implants.")] string implantId,
        CancellationToken cancellationToken)
    {
        try
        {
            var engagement = await ResolveWritableEngagementAsync(engagementId, cancellationToken);
            if (!Guid.TryParse(snippetId, out var snippetValue))
                throw new McpException("The snippet id is not a valid identifier.");
            if (!Guid.TryParse(implantId, out var implantValue))
                throw new McpException("The implant id is not a valid identifier.");

            // A snippet holds no trigger and no target (architecture.md Sec 4.1):
            // replay is keyboard shorthand, and the implant named here is the
            // hand doing the typing. The store is id-keyed, so the engagement
            // check is the caller's -- a foreign snippet reads as no snippet.
            var snippet = await _snippets.FindAsync(new TaskSnippetId(snippetValue), cancellationToken);
            if (snippet is null || snippet.EngagementId != engagement.Id)
                throw new McpException("No such snippet in this engagement.");

            var implant = new ImplantId(implantValue);
            var issued = new List<string>(snippet.Steps.Count);
            foreach (var step in snippet.Steps)
                issued.Add(await IssueAsync(engagement, implant, step.Verb, step.Arguments, cancellationToken));

            return Text(JsonSerializer.Serialize(new { tasks = issued }, Json));
        }
        catch (McpException ex)
        {
            return Refused(ex.Message);
        }
    }

    // A refusal is an expected outcome, not a protocol fault: it rides the
    // result as isError carrying its reason, so the directing agent reads
    // why and can correct itself. A thrown McpException would arrive masked
    // -- the SDK answers a failed invocation with a generic invocation
    // error, not the tool's text.
    private static CallToolResult Refused(string message)
        => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    private static CallToolResult Text(string value)
        => new() { Content = [new TextContentBlock { Text = value }] };

    // One task through the real issuance path: the audit fact rides the same
    // onIssued callback the operator API's endpoint passes, so the trail is
    // identical whether an operator or their agent's token issued the task.
    private async Task<string> IssueAsync(
        Engagement engagement,
        ImplantId implant,
        string verb,
        string arguments,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(verb))
            throw new McpException("Task verb is required.");

        try
        {
            var issued = await _tasks.IssueAsync(
                new IssueTaskCommand(engagement.Id, implant, CurrentOperator(), verb, arguments),
                onIssued: (taskIssued, ct) => AppendIssuedAuditAsync(taskIssued, _audit, ct),
                cancellationToken: cancellationToken);
            return issued.TaskId.ToString();
        }
        catch (TaskRejectedException ex)
        {
            // The refusal reasons are the gates doing their job; they read as
            // the tool's error, the same text the console would surface.
            throw new McpException(ex.Message);
        }
    }

    // The write half of the read toolset's choke point: the credential's
    // floor and the operator's standing, together the same bar the operator
    // API's Write routes hold (the route gate's operate floor plus the
    // writer tier behind it).
    private async Task<Engagement> ResolveWritableEngagementAsync(string engagementId, CancellationToken cancellationToken)
    {
        RequireWriteScope();
        if (!Guid.TryParse(engagementId, out var value))
            throw new McpException("The engagement id is not a valid identifier.");
        var id = new EngagementId(value);
        var engagement = await _engagements.FindAsync(id, cancellationToken);
        if (engagement is null)
            throw new McpException("No such engagement.");

        var standing = await _access.ResolveAsync(id, CurrentOperator(), cancellationToken);
        if (!standing.EngagementExists
            || (standing.Level != EngagementAccessLevel.Writer && standing.Level != EngagementAccessLevel.Owner))
        {
            // A stranger's engagement and a reader's reach read the same: no
            // such engagement, the concealment the operator API keeps.
            throw new McpException("No such engagement.");
        }

        return engagement;
    }

    // The scope floor the route gate gives every engagement-scoped write,
    // held here because the route table cannot see it: MCP traffic is one
    // POST route carrying JSON-RPC, so the toolset is the only place that
    // knows a call writes. A cookie session carries no scope claim and
    // passes untouched -- the floor narrows, never widens.
    private void RequireWriteScope()
    {
        if (_http.HttpContext?.User.TryGetTokenScope() is { } capped && capped < OperatorTokenScope.Operate)
            throw new McpException(
                $"The API token's '{OperatorTokenScopes.ToName(capped)}' scope does not cover this action.");
    }

    private OperatorId CurrentOperator()
        => _http.HttpContext?.User.TryGetOperatorId()
            ?? throw new McpException("No operator session backs this tool call.");

    // The same issued-fact shape TaskEndpoints appends, kept here so the
    // toolset's audit rides the identical tuple -- an agent-issued task is
    // indistinguishable in the trail from an operator-typed one, by design.
    private static Task AppendIssuedAuditAsync(TaskIssued issued, IAuditStore audit, CancellationToken cancellationToken)
        => audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: issued.EngagementId.Value,
                operatorId: issued.IssuedBy.Value,
                implantId: issued.ImplantId.Value,
                taskId: issued.TaskId.Value,
                verb: issued.Verb,
                kind: AuditEventKind.TaskIssued,
                payload: issued.Arguments,
                output: null,
                outcome: issued.TaskId.ToString(),
                at: issued.CreatedAt),
            cancellationToken);
}
