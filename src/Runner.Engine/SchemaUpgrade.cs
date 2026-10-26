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
    public static async Task ApplyOperationsAsync(RunnerDb db, CancellationToken ct = default)
    {
        await ApplyFunctionsAsync(db, ct);
        var pg = db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = pg
            ? "SELECT table_name FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = 'Attempts'"
            : "SELECT name FROM sqlite_master WHERE type='table' AND name='Attempts'";
        if (await command.ExecuteScalarAsync(ct) is null)
        {
            var resource = $"Runner.Engine.Migrations.003-operations.{(pg ? "postgres" : "sqlite")}.sql";
            await using var stream = typeof(SchemaUpgrade).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException("Upgrade resource missing");
            using var reader = new StreamReader(stream);
            await db.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync(ct), ct);
        }
        await db.Attempts.AsNoTracking().OrderBy(x => x.Id).Take(1).ToListAsync(ct);
        await db.JobLogs.AsNoTracking().OrderBy(x => x.Id).Take(1).ToListAsync(ct);
        await db.Alerts.AsNoTracking().OrderBy(x => x.Id).Take(1).ToListAsync(ct);
        await db.Database.ExecuteSqlRawAsync("INSERT INTO \"__RunnerSchema\" (\"Version\") VALUES (3) ON CONFLICT (\"Version\") DO NOTHING", ct);
        await transaction.CommitAsync(ct);
    }

}
