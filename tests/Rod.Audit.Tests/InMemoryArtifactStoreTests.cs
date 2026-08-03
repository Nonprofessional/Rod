namespace Rod.Audit.Tests;

/// <summary>
/// The M2.3 acceptance check for the artifact store: artifacts are first-class
/// objects attached to tasks (architecture.md Sec 11). A task lists its own
/// artifacts; another task on the same engagement sees none of them; the
/// engagement view is scoped and oldest-first.
/// </summary>
public class InMemoryArtifactStoreTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static Artifact Artifact(
        Guid engagement, Guid task, int seed, string name = "out.txt", byte[]? content = null)
        => new(
            ArtifactId: Guid.NewGuid(),
            EngagementId: engagement,
            TaskId: task,
            OperatorId: Guid.NewGuid(),
            Name: name,
            ContentType: "text/plain",
            Content: content ?? new byte[] { (byte)seed },
            Size: content?.Length ?? 1,
            StoredAt: T0.AddSeconds(seed));

    [Fact]
    public async Task Save_AttachesAnArtifact_ToItsTask()
    {
        var store = new InMemoryArtifactStore();
        var engagement = Guid.NewGuid();
        var task = Guid.NewGuid();

        var saved = Artifact(engagement, task, 0, content: new byte[] { 1, 2, 3 });
        await store.SaveAsync(saved);

        var forTask = await store.ForTaskAsync(task);
        var only = Assert.Single(forTask);
        Assert.Equal(saved.ArtifactId, only.ArtifactId);
        Assert.Equal(saved.Content, only.Content);
        Assert.Equal(3, only.Size);

        var byId = await store.FindAsync(saved.ArtifactId);
        Assert.NotNull(byId);
        Assert.Equal(task, byId!.TaskId);
    }

    [Fact]
    public async Task ForTask_ListsOnlyThatTasksArtifacts_OldestFirst()
    {
        var store = new InMemoryArtifactStore();
        var engagement = Guid.NewGuid();
        var taskA = Guid.NewGuid();
        var taskB = Guid.NewGuid();

        // Two artifacts on task A (stored out of time order), one on task B.
        await store.SaveAsync(Artifact(engagement, taskA, 2));
        await store.SaveAsync(Artifact(engagement, taskB, 1));
        await store.SaveAsync(Artifact(engagement, taskA, 0));

        var forA = await store.ForTaskAsync(taskA);
        Assert.Equal(2, forA.Count);
        Assert.All(forA, a => Assert.Equal(taskA, a.TaskId));
        Assert.True(forA[0].StoredAt < forA[1].StoredAt);

        // Task B sees only its own artifact, never task A's.
        var forB = await store.ForTaskAsync(taskB);
        var only = Assert.Single(forB);
        Assert.Equal(taskB, only.TaskId);
    }

    [Fact]
    public async Task List_IsEngagementScoped_AndOldestFirst()
    {
        var store = new InMemoryArtifactStore();
        var engagementA = Guid.NewGuid();
        var engagementB = Guid.NewGuid();
        var task = Guid.NewGuid();

        await store.SaveAsync(Artifact(engagementA, task, 0));
        await store.SaveAsync(Artifact(engagementA, task, 1));
        await store.SaveAsync(Artifact(engagementB, task, 2));

        var a = await store.ListAsync(engagementA);
        Assert.Equal(2, a.Count);
        Assert.All(a, x => Assert.Equal(engagementA, x.EngagementId));
        Assert.True(a[0].StoredAt < a[1].StoredAt);

        // Engagement B's artifact is invisible from engagement A.
        Assert.Single(await store.ListAsync(engagementB));
    }

    [Fact]
    public async Task Find_ReturnsNull_ForUnknownId()
    {
        var store = new InMemoryArtifactStore();
        Assert.Null(await store.FindAsync(Guid.NewGuid()));
    }
}
