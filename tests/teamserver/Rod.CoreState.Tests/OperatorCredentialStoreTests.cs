using Rod.CoreState.Operators;

namespace Rod.CoreState.Tests;

/// <summary>
/// Checks of the operator credential store's revocation path (architecture.md
/// Sec 9 -- certificate revocation, operator half). Revocation deletes the
/// stored verifier; login reads the hash fresh on every attempt, so a revoked
/// credential fails the next authentication with no restart and no cached
/// state to clear.
/// </summary>
public class OperatorCredentialStoreTests
{
    [Fact]
    public async Task RevokedCredential_HasNoVerifier_AndRevocationIsIdempotent()
    {
        var store = new InMemoryOperatorCredentialStore();
        var op = OperatorId.New();
        await store.SetHashAsync(op, "hash-1");

        await store.RevokeAsync(op);

        Assert.Null(await store.FindHashAsync(op, CancellationToken.None));
        // Revoking again (no verifier present) succeeds.
        await store.RevokeAsync(op);
        Assert.Null(await store.FindHashAsync(op, CancellationToken.None));
    }

    [Fact]
    public async Task ReprovisionedCredential_LogsInAgain()
    {
        var store = new InMemoryOperatorCredentialStore();
        var op = OperatorId.New();

        await store.SetHashAsync(op, "hash-1");
        await store.RevokeAsync(op);
        await store.SetHashAsync(op, "hash-2");

        Assert.Equal("hash-2", await store.FindHashAsync(op, CancellationToken.None));
    }
}
