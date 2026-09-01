using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Testcontainers.PostgreSql;
using Xunit;

namespace Rod.Integration.Tests;

/// <summary>
/// An ephemeral PostgreSQL container for the durability acceptance test
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

    /// <summary>
    /// What the container looks like right now, for failure messages: when a
    /// test loses its database mid-class, the state, exit code, and recent
    /// engine log say which failure it was. <c>Exited</c> with 137 is the OOM
    /// killer; <c>Running</c> with a "too many clients" or fork failure in the
    /// log is connection exhaustion; a clean log with <c>Running</c> points at
    /// the port mapping -- different problems, different fixes.
    /// </summary>
    public async Task<string> DescribeAsync()
    {
        try
        {
            var state = _container.State;
            var summary = state == TestcontainersStates.Exited
                ? $"state={state}, exitCode={await _container.GetExitCodeAsync()}"
                : $"state={state}";

            var logs = await _container.GetLogsAsync(
                since: DateTime.UtcNow.AddMinutes(-2), timestampsEnabled: true);
            var tail = (logs.Stdout + "\n" + logs.Stderr).Trim();
            if (tail.Length > 2000)
                tail = "..." + tail[^2000..];
            return $"{summary}; recent engine log: {tail}";
        }
        catch (Exception ex)
        {
            return $"state=unknown ({ex.GetType().Name}: {ex.Message})";
        }
    }
}

/// <summary>
/// Serializes the Postgres-backed suites. Each class still owns its container,
/// but one runs at a time: on a CI runner already carrying the parallel
/// build/conformance suites, two simultaneous engines squeezed the box enough
/// that new pooled connections died mid-handshake (the twice-repeated
/// durability flake), so the classes take turns instead.
/// </summary>
[CollectionDefinition("postgres")]
public sealed class PostgresCollection;
