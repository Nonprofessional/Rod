using System.IO;

namespace Rod.Audit.Tests;

/// <summary>
/// The durable artifact store: the shared contract
/// (<see cref="ArtifactStoreContractTests"/>) against a temp directory, plus
/// the durability cases of its own -- artifacts round-trip through disk and
/// survive disposal and recreation of the store, so evidence linked to a task
/// outlives a teamserver restart alongside the audit trail
/// (architecture.md Sec 11).
/// </summary>
public class FileArtifactStoreTests : ArtifactStoreContractTests, IDisposable
{
    private readonly List<TempDir> _dirs = new();

    protected override IArtifactStore CreateStore()
    {
        var dir = new TempDir();
        _dirs.Add(dir);
        return new FileArtifactStore(Options(dir.Path));
    }

    public void Dispose()
    {
        foreach (var dir in _dirs)
            dir.Dispose();
    }

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
