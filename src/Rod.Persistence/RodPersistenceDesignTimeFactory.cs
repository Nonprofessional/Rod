using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Rod.Persistence;

/// <summary>
/// Design-time factory that lets <c>dotnet ef migrations</c> build the
/// <see cref="RodPersistenceDbContext"/> without the host's full configuration
/// (ADR 0003). The EF tools discover this type and call it to construct a context
/// for model snapshotting and migration scaffolding; the connection string it
/// uses never needs to be live at design time -- migrations are generated from
/// the model, not a running database. It is never invoked by the running host.
/// </summary>
/// <remarks>
/// A real connection string is supplied from configuration at runtime; this
/// factory fills in a placeholder so the tools can construct the options.
/// </remarks>
internal sealed class RodPersistenceDesignTimeFactory : IDesignTimeDbContextFactory<RodPersistenceDbContext>
{
    public RodPersistenceDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RodPersistenceDbContext>()
            .UseNpgsql("Host=localhost;Database=rod;Username=rod;Password=rod")
            .Options;
        return new RodPersistenceDbContext(options);
    }
}
