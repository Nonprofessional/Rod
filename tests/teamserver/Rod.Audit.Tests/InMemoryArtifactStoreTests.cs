namespace Rod.Audit.Tests;

/// <summary>
/// The in-memory adapter on the shared artifact-store contract
/// (<see cref="ArtifactStoreContractTests"/>).
/// </summary>
public class InMemoryArtifactStoreTests : ArtifactStoreContractTests
{
    protected override IArtifactStore CreateStore() => new InMemoryArtifactStore();
}
