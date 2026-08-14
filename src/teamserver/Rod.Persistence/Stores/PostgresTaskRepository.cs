using Microsoft.EntityFrameworkCore;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Tasks;
using Rod.Persistence.Configurations;
// The domain entity shares its name with System.Threading.Tasks.Task. Pin it
// here so the adapter's Task type wins; the BCL type is reached by its full
// name where the methods return it, exactly as the port does.
using Task = Rod.CoreState.Tasks.Task;

namespace Rod.Persistence.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="ITaskRepository"/> (ADR 0003). Each call
/// creates a short-lived <see cref="RodPersistenceDbContext"/> from the factory,
/// performs its work, and disposes the context; the context is never held across
/// calls so the singleton adapter stays safe for concurrent use.
/// </summary>
/// <remarks>
/// FIFO ordering (the in-memory adapter's global enqueue sequence) is reproduced
/// by the <c>enqueue_seq</c> column on <c>tasks</c>, a Postgres IDENTITY column
/// whose value is generated exactly once at INSERT and never touched on the
/// UPDATE path a dispatch/completion re-save takes. Two concurrent enqueues get
/// distinct, increasing values from the database without the adapter managing a
/// counter. <see cref="NextPendingAsync"/> and the listing methods order by that
/// column, so per-implant dispatch drains in enqueue order and the engagement /
/// implant histories read in the same order across restarts.
/// </remarks>
internal sealed class PostgresTaskRepository : ITaskRepository
{
    private readonly IDbContextFactory<RodPersistenceDbContext> _factory;

    public PostgresTaskRepository(IDbContextFactory<RodPersistenceDbContext> factory)
        => _factory = factory;

    public async System.Threading.Tasks.Task SaveAsync(Task task, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        // Insert-or-replace by id: a queued task is inserted (the IDENTITY column
        // assigns its enqueue sequence), and a dispatch/completion re-save updates
        // the lifecycle columns in place without touching enqueue_seq.
        var existing = await db.Tasks.FindAsync(new object?[] { task.Id }, cancellationToken);
        if (existing is null)
            db.Tasks.Add(task);
        else
            db.Entry(existing).CurrentValues.SetValues(task);

        await db.SaveChangesAsync(cancellationToken);
    }

    public async System.Threading.Tasks.Task<Task?> FindAsync(TaskId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
    }

    public async System.Threading.Tasks.Task<IReadOnlyList<Task>> ListByImplantAsync(
        ImplantId implant,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.Tasks
            .AsNoTracking()
            .Where(t => t.ImplantId == implant)
            .OrderBy(t => EF.Property<long>(t, TaskConfiguration.EnqueueSequenceShadow))
            .ToArrayAsync(cancellationToken);
    }

    public async System.Threading.Tasks.Task<IReadOnlyList<Task>> ListByEngagementAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.Tasks
            .AsNoTracking()
            .Where(t => t.EngagementId == engagement)
            .OrderBy(t => EF.Property<long>(t, TaskConfiguration.EnqueueSequenceShadow))
            .ToArrayAsync(cancellationToken);
    }

    public async System.Threading.Tasks.Task<TaskPage> ListByEngagementPageAsync(
        EngagementId engagement,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await PageAsync(
            db.Tasks.AsNoTracking().Where(t => t.EngagementId == engagement),
            limit, cursor, cancellationToken);
    }

    public async System.Threading.Tasks.Task<TaskPage> ListByImplantPageAsync(
        ImplantId implant,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await PageAsync(
            db.Tasks.AsNoTracking().Where(t => t.ImplantId == implant),
            limit, cursor, cancellationToken);
    }

    // Keyset pagination over the enqueue sequence: the newest window first (a
    // null cursor), each next cursor reading strictly older rows. The LIMIT
    // fetches one extra row so "is there a next page" needs no second query.
    private static async System.Threading.Tasks.Task<TaskPage> PageAsync(
        IQueryable<Task> scoped,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (cursor is not null)
        {
            if (!TaskPageCursor.TryDecode(cursor, out var after))
                throw new ArgumentException("Cursor is not a valid task page cursor.", nameof(cursor));
            scoped = scoped.Where(t => EF.Property<long>(t, TaskConfiguration.EnqueueSequenceShadow) < after);
        }

        // Project the enqueue sequence alongside the entity: the shadow value
        // must be read inside the query -- EF.Property refuses to read it off an
        // already-materialized instance.
        var newestFirst = await scoped
            .OrderByDescending(t => EF.Property<long>(t, TaskConfiguration.EnqueueSequenceShadow))
            .Take(limit + 1)
            .Select(t => new
            {
                t,
                Seq = EF.Property<long>(t, TaskConfiguration.EnqueueSequenceShadow),
            })
            .ToArrayAsync(cancellationToken);

        var hasMore = newestFirst.Length > limit;
        var items = newestFirst.Take(limit).Select(x => x.t).ToArray();
        Array.Reverse(items); // Oldest first within the page, matching the full listing.
        var next = hasMore
            ? TaskPageCursor.Encode(newestFirst[limit - 1].Seq)
            : null;
        return new TaskPage(items, next);
    }

    public async System.Threading.Tasks.Task<Task?> NextPendingAsync(
        ImplantId implant,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        // The oldest still-queued task for the implant by enqueue sequence; the
        // beacon drains these one at a time on each check-in.
        return await db.Tasks
            .AsNoTracking()
            .Where(t => t.ImplantId == implant && t.Status == Rod.CoreState.Tasks.TaskStatus.Queued)
            .OrderBy(t => EF.Property<long>(t, TaskConfiguration.EnqueueSequenceShadow))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async System.Threading.Tasks.Task<Task?> ClaimNextPendingAsync(
        ImplantId implant,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // FOR UPDATE SKIP LOCKED makes the claim atomic across concurrent
        // beacons: the subquery locks the oldest Queued row (or skips it when
        // another transaction already holds it), so two claims for one implant
        // can never select the same task. The outer query turns the lock into a
        // plain id; the entity is then loaded and transitioned inside the same
        // transaction so the Dispatched mark commits with the lock released
        // only after SaveChanges.
        var claimedId = await db.Database.SqlQuery<Guid>($"""
            SELECT task_id
            FROM tasks
            WHERE task_id = (
                SELECT task_id
                FROM tasks
                WHERE implant_id = {implant.Value} AND status = {(int)Rod.CoreState.Tasks.TaskStatus.Queued}
                ORDER BY enqueue_seq
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            )
            """).FirstOrDefaultAsync(cancellationToken);

        if (claimedId == Guid.Empty)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var task = await db.Tasks.FirstAsync(t => t.Id == new TaskId(claimedId), cancellationToken);
        task.MarkDispatched(at);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return task;
    }
}

