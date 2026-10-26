using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore;
namespace Runner.Engine;

/// <summary>Explicit, single-operator upgrade from the initial schema. Run with workers stopped.</summary>
public static class SchemaUpgrade
{
    public static async Task ApplyFunctionsAsync(RunnerDb db, CancellationToken ct = default)
    {
        var pg = db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS \"__RunnerSchema\" (\"Version\" integer NOT NULL PRIMARY KEY)", ct);
        var hasResult = false;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = pg
                ? "SELECT column_name FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'Jobs' AND column_name = 'ResultJson'"
                : "SELECT name FROM pragma_table_info('Jobs') WHERE name = 'ResultJson'";
            hasResult = await command.ExecuteScalarAsync(ct) is not null;
        }
        if (!hasResult)
        {
            var suffix = pg ? "postgres" : "sqlite";
            var resource = $"Runner.Engine.Migrations.002-functions.{suffix}.sql";
            await using var stream = typeof(SchemaUpgrade).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException("Upgrade resource missing");
            using var reader = new StreamReader(stream);
            await db.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync(ct), ct);
        }
        // Verify the full new shape before recording success, including on freshly initialized databases.
        await db.Functions.AsNoTracking().OrderBy(x => x.Id).Take(1).ToListAsync(ct);
        await db.Jobs.AsNoTracking().OrderBy(x => x.Id).Select(x => x.ResultJson).Take(1).ToListAsync(ct);
        await db.Database.ExecuteSqlRawAsync("INSERT INTO \"__RunnerSchema\" (\"Version\") VALUES (2) ON CONFLICT (\"Version\") DO NOTHING", ct);
        await transaction.CommitAsync(ct);
    }
}
