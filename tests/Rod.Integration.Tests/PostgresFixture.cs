using DotNet.Testcontainers.Builders;
using Testcontainers.PostgreSql;
using Xunit;

namespace Rod.Integration.Tests;

/// <summary>
/// An ephemeral PostgreSQL container for the M10.1 durability acceptance test
/// (ADR 0003). One container per test class; the connection string it exposes is
/// fed to <c>ConnectionStrings:Postgres</c> so the composition root swaps the
/// in-memory core-state ports for the Postgres-backed adapters.
/// </summary>
/// <remarks>
/// When Docker is not reachable the fixture throws on startup; the consuming
/// test class wraps that in a skip (not a failure) so CI without Docker stays
/// green. The container image is pinned to a major Postgres version so the
/// schema migrations are exercised against a stable, known engine.
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("rod")
        .WithUsername("rod")
        .WithPassword("rod")
        // The postgres image ships no HEALTHCHECK, so wait on a command that
        // succeeds only once Postgres accepts connections -- UntilContainerIsHealthy
        // would hang forever here.
        .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready", "-U", "rod"))
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public bool IsAvailable { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
            IsAvailable = true;
        }
        catch (Exception)
        {
            // Docker is not available in this environment. The consuming test
            // skips rather than fails; the rest of the suite is unaffected.
            await _container.DisposeAsync();
            IsAvailable = false;
        }
    }

    public Task DisposeAsync()
    {
        if (IsAvailable)
            return _container.DisposeAsync().AsTask();

        return Task.CompletedTask;
    }
}
