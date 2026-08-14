using System.IO;

namespace Rod.Audit.Tests;

/// <summary>
/// The  acceptance check for the durable artifact store: artifacts attached
/// to tasks round-trip through disk and survive disposal and recreation of the
/// store, so evidence linked to a task outlives a teamserver restart alongside
/// the audit trail. Mirrors <see cref="InMemoryArtifactStoreTests"/> against a
/// temp directory, then adds the reload case (architecture.md Sec 11).
/// </summary>
public class FileArtifactStoreTests
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

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "rod-artifact-test-" + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private static AuditPersistenceOptions Options(string dir)
        => new() { DataDirectory = dir };

    [Fact]
    public async Task Save_AttachesAnArtifact_ToItsTask()
    {
        using var dir = new TempDir();
        var store = new FileArtifactStore(Options(dir.Path));
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
        using var dir = new TempDir();
        var store = new FileArtifactStore(Options(dir.Path));
        var engagement = Guid.NewGuid();
        var taskA = Guid.NewGuid();
        var taskB = Guid.NewGuid();

        await store.SaveAsync(Artifact(engagement, taskA, 2));
        await store.SaveAsync(Artifact(engagement, taskB, 1));
        await store.SaveAsync(Artifact(engagement, taskA, 0));

        var forA = await store.ForTaskAsync(taskA);
        Assert.Equal(2, forA.Count);
        Assert.All(forA, a => Assert.Equal(taskA, a.TaskId));
        Assert.True(forA[0].StoredAt < forA[1].StoredAt);

        var forB = await store.ForTaskAsync(taskB);
        var only = Assert.Single(forB);
        Assert.Equal(taskB, only.TaskId);
    }

    [Fact]
    public async Task List_IsEngagementScoped_AndOldestFirst()
    {
        using var dir = new TempDir();
        var store = new FileArtifactStore(Options(dir.Path));
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

        Assert.Single(await store.ListAsync(engagementB));
    }

    [Fact]
    public async Task Find_ReturnsNull_ForUnknownId()
    {
        using var dir = new TempDir();
        var store = new FileArtifactStore(Options(dir.Path));
        Assert.Null(await store.FindAsync(Guid.NewGuid()));
    }

    // The  property: a fresh store over the same directory recovers the
    // metadata index from artifacts.jsonl and serves a previously-stored
    // artifact with its exact bytes. Evidence survives the teardown.
    [Fact]
    public async Task Reload_ReturnsAPreviouslyStoredArtifact_WithExactBytes()
    {
        using var dir = new TempDir();
        var engagement = Guid.NewGuid();
        var task = Guid.NewGuid();
        var content = new byte[] { 9, 8, 7, 6, 5 };
        var saved = Artifact(engagement, task, 0, name: "loot.bin", content: content);

        // The store flushes each save, so there is nothing to dispose between
        // instances -- constructing a new one over the same dir is the restart.
        {
            var storeA = new FileArtifactStore(Options(dir.Path));
            await storeA.SaveAsync(saved);
        }

        // A brand-new instance over the same directory: no in-memory index. It
        // recovers the metadata and reads the bytes back from the blob.
        var storeB = new FileArtifactStore(Options(dir.Path));
        var byId = await storeB.FindAsync(saved.ArtifactId);
        Assert.NotNull(byId);
        Assert.Equal(saved.Name, byId!.Name);
        Assert.Equal(saved.ContentType, byId.ContentType);
        Assert.Equal(content, byId.Content);
        Assert.Equal(content.Length, byId.Size);

        var forTask = await storeB.ForTaskAsync(task);
        var only = Assert.Single(forTask);
        Assert.Equal(content, only.Content);
    }

    // An artifact stored before the restart is still scoped correctly after it:
    // engagement and task isolation survive the teardown.
    [Fact]
    public async Task Reload_KeepsArtifacts_EngagementScoped()
    {
        using var dir = new TempDir();
        var engagementA = Guid.NewGuid();
        var engagementB = Guid.NewGuid();
        var task = Guid.NewGuid();

        {
            var storeA = new FileArtifactStore(Options(dir.Path));
            await storeA.SaveAsync(Artifact(engagementA, task, 0));
            await storeA.SaveAsync(Artifact(engagementB, task, 1));
        }

        var storeB = new FileArtifactStore(Options(dir.Path));
        Assert.Single(await storeB.ListAsync(engagementA));
        Assert.Single(await storeB.ListAsync(engagementB));
    }

    // An empty directory (no artifacts.jsonl yet) reads as an empty store.
    [Fact]
    public async Task EmptyDirectory_ReadsAsAnEmptyStore()
    {
        using var dir = new TempDir();
        var store = new FileArtifactStore(Options(dir.Path));

        Assert.Empty(await store.ListAsync(Guid.NewGuid()));
        Assert.Empty(await store.ForTaskAsync(Guid.NewGuid()));
        Assert.Null(await store.FindAsync(Guid.NewGuid()));
    }
}
