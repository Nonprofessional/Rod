using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState.Application;
using Rod.CoreState.Live;

namespace Rod.Transport;

/// <summary>
/// The lost-response task expiry sweep (architecture.md Sec 10.3). A poll
/// carrier hands a task out once; when the TXT answer or contact response
/// carrying it is lost in transit, nothing re-delivers it, and the task
/// would hang Dispatched forever -- the operator's cancel only reaches
/// Queued, so even manual cleanup has no path. The sweeper runs
/// <see cref="TaskService.ExpireLostTasksAsync"/> on a fixed cadence, so a
/// lost task closes as failed with its audit arc complete instead of
/// hanging on the roster for the engagement's life.
/// </summary>
/// <remarks>
/// The expiry window is deliberately wide (a day): a non-channel task that
/// has not answered in twenty-four hours was carried by an answer that
/// never arrived, while a task that is genuinely still running is a channel
/// task, which the sweep never touches. A late real result that wins the
/// race spares the task; one that arrives after the expiry is dropped by
/// the first-wins rule, exactly like a duplicate. Each expiry lands on the
/// engagement trail as a system-attributed <see cref="AuditEventKind.TaskExpired"/>
/// fact and fans out as a TaskCompleted live event, so connected operators
/// watch the task go terminal. <see cref="SweepOnceAsync"/> is public so
/// tests drive a pass deterministically instead of racing the timer.
/// </remarks>
public sealed class TaskExpirySweeper : BackgroundService
{
    /// <summary>The built-in cadence: expire after a day of silence, check every 30 minutes.</summary>
    public static readonly TimeSpan ExpiryWindow = TimeSpan.FromHours(24);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(30);

    private readonly TaskService _tasks;
    private readonly IAuditStore _audit;
    private readonly ILiveEventBus _live;
    private readonly TimeProvider _clock;

    public TaskExpirySweeper(
        TaskService tasks,
        IAuditStore audit,
        ILiveEventBus live,
        TimeProvider clock)
    {
        _tasks = tasks;
        _audit = audit;
        _live = live;
        _clock = clock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first pass runs immediately at startup: a restarted teamserver
        // inherits the previous run's hanging tasks and should not wait a
        // full interval to start closing them.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // A failed pass costs one sweep, never the service: the next
                // pass re-reads the store.
            }

            await Task.Delay(SweepInterval, stoppingToken);
        }
    }

    /// <summary>
    /// Runs one sweep pass: expires every overage Dispatched task whose
    /// response never came back, recording each on the trail and fanning it
    /// to connected operators. Returns the expired tasks.
    /// </summary>
    public async Task<IReadOnlyList<TaskCompleted>> SweepOnceAsync(
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var expired = await _tasks.ExpireLostTasksAsync(ExpiryWindow, cancellationToken);

        foreach (var task in expired)
        {
            await _audit.AppendAsync(
                AuditEvent.Fact(
                    eventId: Guid.NewGuid(),
                    engagementId: task.EngagementId.Value,
                    operatorId: Guid.Empty,
                    implantId: task.ImplantId.Value,
                    taskId: task.TaskId.Value,
                    verb: task.Verb,
                    kind: AuditEventKind.TaskExpired,
                    payload: $"{task.Verb} {task.Arguments} expired: dispatched at {task.CompletedAt - ExpiryWindow:O}, no response",
                    output: task.Output,
                    outcome: task.TaskId.ToString(),
                    at: now),
                cancellationToken);
            await _live.PublishAsync(
                LiveEvent.TaskCompleted(
                    task.EngagementId,
                    task.IssuedBy,
                    task.ImplantId,
                    task.TaskId,
                    "expired: the carrying response was lost",
                    now),
                cancellationToken);
        }

        return expired;
    }
}
