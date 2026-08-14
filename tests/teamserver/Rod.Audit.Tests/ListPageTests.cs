namespace Rod.Audit.Tests;

/// <summary>
/// Paged reads of the in-memory audit and artifact stores (architecture.md
/// Sec 11): the newest window first, a cursor walking one page older,
/// oldest-first within each page, and the walk ending in a null cursor at the
/// beginning of the trail.
/// </summary>
public class ListPageTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static AuditEvent Fact(Guid engagement, int seed)
        => AuditEvent.Fact(
            eventId: Guid.NewGuid(),
            engagementId: engagement,
            operatorId: Guid.NewGuid(),
            implantId: Guid.NewGuid(),
            taskId: Guid.NewGuid(),
            verb: "shell.exec",
            kind: AuditEventKind.TaskCompleted,
            payload: $"arg-{seed}",
            output: "out",
            outcome: "Succeeded",
            at: T0.AddSeconds(seed));

    private static Artifact ArtifactOf(Guid engagement, Guid task, int seed)
        => new(
            ArtifactId: Guid.NewGuid(),
            EngagementId: engagement,
            TaskId: task,
            OperatorId: Guid.NewGuid(),
            Name: $"artifact-{seed}",
            ContentType: "text/plain",
            Content: new byte[] { (byte)seed },
            Size: 1,
            StoredAt: T0.AddSeconds(seed));

    [Fact]
    public async Task AuditPageWalk_CoversEveryEvent_Once_InOrder()
    {
        var store = new InMemoryAuditStore();
        var engagement = Guid.NewGuid();
        for (var i = 0; i < 8; i++)
            await store.AppendAsync(Fact(engagement, i));

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await store.ListPageAsync(engagement, limit: 3, cursor);

            // Oldest first within the page.
            Assert.Equal(
                page.Items.Select(e => e.EventId),
                page.Items.OrderBy(e => e.At).Select(e => e.EventId));
            seen.AddRange(page.Items.Select(e => e.EventId));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        // 3 + 3 + 2 pages, no duplicates, and the concatenation covers the
        // full trail (pages walk newest window first, so the set is what must
        // match the full listing, not the concatenation order).
        Assert.Equal(8, seen.Count);
        Assert.Equal(8, seen.Distinct().Count());
        var full = await store.ListAsync(engagement);
        Assert.Equal(
            full.Select(e => e.EventId).OrderBy(id => id).ToArray(),
            seen.OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task AuditPages_StayScopedByEngagement()
    {
        // Cross-engagement isolation holds on the paged read too
        // (architecture.md Sec 3/11): another engagement's events never leak in.
        var store = new InMemoryAuditStore();
        var engagementA = Guid.NewGuid();
        var engagementB = Guid.NewGuid();
        for (var i = 0; i < 3; i++)
            await store.AppendAsync(Fact(engagementA, i));
        for (var i = 0; i < 2; i++)
            await store.AppendAsync(Fact(engagementB, i));

        var page = await store.ListPageAsync(engagementA, limit: 10, cursor: null);

        Assert.Equal(3, page.Items.Count);
        Assert.All(page.Items, e => Assert.Equal(engagementA, e.EngagementId));
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task AuditGarbageCursor_Throws()
    {
        var store = new InMemoryAuditStore();
        var engagement = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.ListPageAsync(engagement, limit: 10, cursor: "not-a-cursor"));
    }

    [Fact]
    public async Task ArtifactPageWalk_CoversEveryArtifact_Once_InOrder()
    {
        var store = new InMemoryArtifactStore();
        var engagement = Guid.NewGuid();
        var task = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
            await store.SaveAsync(ArtifactOf(engagement, task, i));

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await store.ForTaskPageAsync(task, limit: 2, cursor);

            // Oldest first within the page.
            Assert.Equal(
                page.Items.Select(a => a.ArtifactId),
                page.Items.OrderBy(a => a.StoredAt).Select(a => a.ArtifactId));
            seen.AddRange(page.Items.Select(a => a.ArtifactId));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        // 2 + 2 + 1 pages, no duplicates, and the concatenation covers the
        // full set (pages walk newest window first, so the set is what must
        // match the full listing, not the concatenation order).
        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
        var full = await store.ForTaskAsync(task);
        Assert.Equal(
            full.Select(a => a.ArtifactId).OrderBy(id => id).ToArray(),
            seen.OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task ArtifactPages_StayScopedByTask()
    {
        // A page never mixes another task's artifacts: the task filter is the
        // scoping guard on the paged read too (architecture.md Sec 11).
        var store = new InMemoryArtifactStore();
        var engagement = Guid.NewGuid();
        var taskA = Guid.NewGuid();
        var taskB = Guid.NewGuid();
        for (var i = 0; i < 3; i++)
            await store.SaveAsync(ArtifactOf(engagement, taskA, i));
        await store.SaveAsync(ArtifactOf(engagement, taskB, 99));

        var page = await store.ForTaskPageAsync(taskA, limit: 10, cursor: null);

        Assert.Equal(3, page.Items.Count);
        Assert.All(page.Items, a => Assert.Equal(taskA, a.TaskId));
        Assert.Null(page.NextCursor);
    }
}
