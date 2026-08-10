using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rod.Audit;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Sessions;
using Rod.CoreState.Staging;
using Rod.CoreState.Tasks;
using Rod.Persistence.Stores;

namespace Rod.Persistence;

/// <summary>
/// Composition-root wiring for the durable PostgreSQL store (ADR 0003, roadmap
/// M10.1). When the <c>ConnectionStrings:Postgres</c> section is present, this
/// registers the EF Core <see cref="RodPersistenceDbContext"/> against the Npgsql
/// provider and <b>replaces</b> the in-memory port registrations added by
/// <c>AddRodTransport</c> with the Postgres-backed adapters, for whichever stores
/// have a durable implementation.
/// </summary>
/// <remarks>
/// The replace pattern mirrors <c>Rod.Operators</c> swapping
/// <c>NullLiveEventBus</c> for <c>InMemoryLiveEventBus</c>: the inner-layer host
/// registers a default, and an outer layer wired at the composition root replaces
/// it. Stores whose durable adapter is not yet implemented keep their in-memory
/// registration untouched, so the opt-in is incremental and a partial rollout is
/// safe. With the connection string absent, this method registers nothing and
/// every existing test stays on the in-memory path unchanged.
/// </remarks>
public static class RodPersistenceHost
{
    /// <summary>
    /// The configuration key the durable adapters are selected by. Mirrors the
    /// <c>Audit:DataDirectory</c> precedent: presence opts in, absence keeps the
    /// in-memory defaults.
    /// </summary>
    public const string ConnectionStringKey = "ConnectionStrings:Postgres";

    public static IServiceCollection AddRodPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration[ConnectionStringKey];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // No connection string -> the in-memory adapters registered by
            // AddRodTransport stay in place. Nothing to do.
            return services;
        }

        // A DbContext is not safe for concurrent use, but the application
        // services that hold these ports (EngagementService, etc.) are registered
        // as singletons and the teamserver fans task/beacon work across threads.
        // Registering a factory (a singleton-safe source of short-lived contexts)
        // lets each adapter own its per-operation context without the
        // captive-dependency hazard a scoped DbContext under a singleton would
        // create. AddDbContextFactory also pools contexts by default.
        services.AddDbContextFactory<RodPersistenceDbContext>(options =>
            options.UseNpgsql(connectionString));

        // Replace the in-memory core-state ports whose durable adapters are
        // implemented. Each adapter is a singleton that resolves the factory and
        // creates a fresh context per call, matching the singleton lifetime of the
        // application services that consume the ports. All eight core-state and
        // audit/artifact ports are now Postgres-backed when this extension runs:
        // operators, engagements, implants, sessions, tasks, stager tokens, audit,
        // and artifacts.
        services.Replace(ServiceDescriptor.Singleton<IOperatorRepository, PostgresOperatorRepository>());
        services.Replace(ServiceDescriptor.Singleton<IEngagementRepository, PostgresEngagementRepository>());
        services.Replace(ServiceDescriptor.Singleton<IImplantRepository, PostgresImplantRepository>());
        services.Replace(ServiceDescriptor.Singleton<ISessionRegistry, PostgresSessionRegistry>());
        services.Replace(ServiceDescriptor.Singleton<ITaskRepository, PostgresTaskRepository>());
        services.Replace(ServiceDescriptor.Singleton<IStagerTokenService, PostgresStagerTokenService>());
        services.Replace(ServiceDescriptor.Singleton<IAuditStore, PostgresAuditStore>());
        services.Replace(ServiceDescriptor.Singleton<IArtifactStore, PostgresArtifactStore>());

        return services;
    }
}
