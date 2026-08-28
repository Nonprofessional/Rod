using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Rod.Persistence;

/// <summary>
/// The startup schema-integrity check for the durable PostgreSQL store
/// ([architecture.md](docs/architecture.md) Sec 9, the same fail-loudly
/// posture as the CA and listener validation): every table the EF model maps
/// must exist and carry its primary key constraint before the teamserver
/// serves traffic. A database restored from a lossy dump or hand-mangled into
/// a constraint-less shape otherwise boots apparently healthy and dies at the
/// first task dispatch -- the replay-nonce reservation's ON CONFLICT needs the
/// nonce table's primary key, and every other table silently loses its
/// uniqueness guarantee. The check is one catalog round trip per boot.
/// </summary>
public static class PostgresSchemaGuard
{
    /// <summary>
    /// Verifies the connected database against the EF model: each mapped table
    /// present, each present table carrying a primary key constraint. Throws
    /// <see cref="InvalidOperationException"/> naming every offending table so
    /// the operator sees the whole repair list, not the first.
    /// </summary>
    public static async Task VerifyAsync(
        IDbContextFactory<RodPersistenceDbContext> factory,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var mapped = db.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // One catalog read answers both questions: which tables exist, and
        // which of those carry a primary key. Partitioned ('p') relations are
        // included alongside plain tables so a future partitioned table does
        // not dodge the check.
        var present = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT c.relname,
                       EXISTS (SELECT 1 FROM pg_constraint k
                               WHERE k.conrelid = c.oid AND k.contype = 'p') AS has_pk
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                present[reader.GetString(0)] = reader.GetBoolean(1);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        var missing = new List<string>();
        var keyless = new List<string>();
        foreach (var table in mapped)
        {
            if (!present.TryGetValue(table, out var hasPrimaryKey))
                missing.Add(table);
            else if (!hasPrimaryKey)
                keyless.Add(table);
        }

        var problems = new List<string>(2);
        if (missing.Count > 0)
            problems.Add(
                "tables the model maps are absent from the database (apply the schema with "
                + "`dotnet ef database update`): " + string.Join(", ", missing));
        if (keyless.Count > 0)
            problems.Add(
                "tables present without their primary key constraint (a lossy restore or manual "
                + "schema edit dropped them; re-apply the schema or repair the constraints): "
                + string.Join(", ", keyless));
        if (problems.Count > 0)
            throw new InvalidOperationException(
                "The PostgreSQL schema does not match the persistence model. " + string.Join("; ", problems));
    }
}
