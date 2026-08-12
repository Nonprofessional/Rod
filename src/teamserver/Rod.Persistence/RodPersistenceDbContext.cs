using Microsoft.EntityFrameworkCore;
using Rod.Audit;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Sessions;
using Rod.Persistence.Configurations;
using Task = Rod.CoreState.Tasks.Task;

namespace Rod.Persistence;

/// <summary>
/// The durable store's EF Core <see cref="DbContext"/> for teamserver state and
/// the audit trail (ADR 0003). Every aggregate and audit/artifact entity is
/// mapped here through its <see cref="IEntityTypeConfiguration{TEntity}"/> in
/// <c>Configurations/</c>; the migrations and the Postgres-backed adapters share
/// this one context.
/// </summary>
/// <remarks>
/// The context holds no domain logic: it is the persistence detail the ports
/// hide. Concurrency lives at the adapters (stager-token redeem, task FIFO), not
/// on the entities, so the domain model stays persistence-ignorant.
/// </remarks>
public sealed class RodPersistenceDbContext : DbContext
{
    public RodPersistenceDbContext(DbContextOptions<RodPersistenceDbContext> options)
        : base(options)
    {
    }

    public DbSet<Operator> Operators => Set<Operator>();
    internal DbSet<StoredOperatorCredential> OperatorCredentials => Set<StoredOperatorCredential>();
    public DbSet<Engagement> Engagements => Set<Engagement>();
    public DbSet<Implant> Implants => Set<Implant>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Task> Tasks => Set<Task>();
    internal DbSet<StoredStagerToken> StagerTokens => Set<StoredStagerToken>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Apply every IEntityTypeConfiguration<> in this assembly (the
        // Configurations/ classes). This keeps OnModelCreating a single line:
        // adding an entity means adding a configuration class, nothing here.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RodPersistenceDbContext).Assembly);

        // All core-state enum columns store as int by convention (the audit hash
        // canonical form already uses (int)Kind), so nothing here overrides that.
    }
}
