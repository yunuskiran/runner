using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore;
namespace Runner.Engine;

/// <summary>Explicit, single-operator upgrade from the initial schema. Run with workers stopped.</summary>
public static class SchemaUpgrade
{
    public static async Task ApplyFunctionsAsync(RunnerDb db, CancellationToken ct = default)
    {
        var pg = db.Database.IsNpgsql();
        var mssql = db.Database.IsSqlServer();
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync(mssql
            ? "IF OBJECT_ID(N'dbo.__RunnerSchema', N'U') IS NULL CREATE TABLE [dbo].[__RunnerSchema] ([Version] int NOT NULL PRIMARY KEY)"
            : "CREATE TABLE IF NOT EXISTS \"__RunnerSchema\" (\"Version\" integer NOT NULL PRIMARY KEY)", ct);
        var hasResult = false;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = mssql
                ? "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Jobs') AND name = N'ResultJson'"
                : pg
                ? "SELECT column_name FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'Jobs' AND column_name = 'ResultJson'"
                : "SELECT name FROM pragma_table_info('Jobs') WHERE name = 'ResultJson'";
            hasResult = await command.ExecuteScalarAsync(ct) is not null;
        }
        if (!hasResult)
        {
            var suffix = mssql ? "mssql" : pg ? "postgres" : "sqlite";
            var resource = $"Runner.Engine.Migrations.002-functions.{suffix}.sql";
            await using var stream = typeof(SchemaUpgrade).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException("Upgrade resource missing");
            using var reader = new StreamReader(stream);
            await db.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync(ct), ct);
        }
        // Verify the full new shape before recording success, including on freshly initialized databases.
        await db.Functions.AsNoTracking().OrderBy(x => x.Id).Take(1).ToListAsync(ct);
        await db.Jobs.AsNoTracking().OrderBy(x => x.Id).Select(x => x.ResultJson).Take(1).ToListAsync(ct);
        await RecordVersionAsync(db, 2, ct);
        await transaction.CommitAsync(ct);
    }
    public static async Task ApplyOperationsAsync(RunnerDb db, CancellationToken ct = default)
    {
        await ApplyFunctionsAsync(db, ct);
        var pg = db.Database.IsNpgsql();
        var mssql = db.Database.IsSqlServer();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = mssql
            ? "SELECT name FROM sys.tables WHERE schema_id = SCHEMA_ID(N'dbo') AND name = N'Attempts'"
            : pg
            ? "SELECT table_name FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = 'Attempts'"
            : "SELECT name FROM sqlite_master WHERE type='table' AND name='Attempts'";
        if (await command.ExecuteScalarAsync(ct) is null)
        {
            var resource = $"Runner.Engine.Migrations.003-operations.{(mssql ? "mssql" : pg ? "postgres" : "sqlite")}.sql";
            await using var stream = typeof(SchemaUpgrade).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException("Upgrade resource missing");
            using var reader = new StreamReader(stream);
            await db.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync(ct), ct);
        }
        await db.Attempts.AsNoTracking().OrderBy(x => x.Id).Take(1).ToListAsync(ct);
        await db.JobLogs.AsNoTracking().OrderBy(x => x.Id).Take(1).ToListAsync(ct);
        await db.Alerts.AsNoTracking().OrderBy(x => x.Id).Take(1).ToListAsync(ct);
        await RecordVersionAsync(db, 3, ct);
        await transaction.CommitAsync(ct);
    }

    public static async Task ApplyPackagesAsync(RunnerDb db, CancellationToken ct = default)
    {
        await ApplyOperationsAsync(db, ct);
        var pg = db.Database.IsNpgsql();
        var mssql = db.Database.IsSqlServer();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = mssql
            ? "SELECT name FROM sys.tables WHERE schema_id = SCHEMA_ID(N'dbo') AND name = N'PluginPackages'"
            : pg
            ? "SELECT table_name FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = 'PluginPackages'"
            : "SELECT name FROM sqlite_master WHERE type='table' AND name='PluginPackages'";
        if (await command.ExecuteScalarAsync(ct) is null)
        {
            var resource = $"Runner.Engine.Migrations.004-packages.{(mssql ? "mssql" : pg ? "postgres" : "sqlite")}.sql";
            await using var stream = typeof(SchemaUpgrade).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException("Upgrade resource missing");
            using var reader = new StreamReader(stream);
            await db.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync(ct), ct);
        }
        await db.PluginPackages.AsNoTracking().Select(x => x.PackageId).OrderBy(x => x).Take(1).ToListAsync(ct);
        await db.PackageChanges.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.PackageId, x.Version, x.PreviousVersion, x.Enabled, x.Revision, x.TimestampUtc, x.Actor }).Take(1).ToListAsync(ct);
        await db.PluginChannels.AsNoTracking().OrderBy(x => x.PackageId).Select(x => new { x.PackageId, x.ActiveVersion, x.Enabled, x.Revision, x.ChangedUtc }).Take(1).ToListAsync(ct);
        await db.Jobs.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.PackageId, x.PackageVersion }).Take(1).ToListAsync(ct);
        await db.Schedules.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.PackageId, x.PackageVersion }).Take(1).ToListAsync(ct);
        await RecordVersionAsync(db, 4, ct);
        await transaction.CommitAsync(ct);
    }


    public static async Task ApplyConcurrencyAsync(RunnerDb db, CancellationToken ct = default)
    {
        await ApplyPackagesAsync(db, ct);
        var mssql = db.Database.IsSqlServer(); var pg = db.Database.IsNpgsql();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = mssql
            ? "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.PluginChannels') AND name = N'ExecutionMode'"
            : pg ? "SELECT column_name FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'PluginChannels' AND column_name = 'ExecutionMode'"
            : "SELECT name FROM pragma_table_info('PluginChannels') WHERE name = 'ExecutionMode'";
        if (await command.ExecuteScalarAsync(ct) is null)
        {
            var provider = mssql ? "mssql" : pg ? "postgres" : "sqlite";
            await using var stream = typeof(SchemaUpgrade).Assembly.GetManifestResourceStream($"Runner.Engine.Migrations.005-concurrency.{provider}.sql")
                ?? throw new InvalidOperationException("Upgrade resource missing");
            using var reader = new StreamReader(stream);
            await db.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync(ct), ct);
        }
        await db.PluginChannels.AsNoTracking().OrderBy(x => x.PackageId).Select(x => new { x.PackageId, x.ExecutionMode, x.ExecutionToken, x.ExecutionLeaseUtc }).Take(1).ToListAsync(ct);
        await db.PluginPackages.AsNoTracking().OrderBy(x => x.PackageId).Select(x => x.DefaultExecutionMode).Take(1).ToListAsync(ct);
        await db.PackageChanges.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.ExecutionMode, x.PreviousExecutionMode }).Take(1).ToListAsync(ct);
        await RecordVersionAsync(db, 5, ct); await transaction.CommitAsync(ct);
    }

    public static async Task ApplyPlatformAsync(RunnerDb db, CancellationToken ct = default)
    {
        await ApplyConcurrencyAsync(db, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = tx.GetDbTransaction();
        command.CommandText = db.Database.IsSqlServer()
            ? "SELECT name FROM sys.tables WHERE schema_id = SCHEMA_ID(N'dbo') AND name = N'WorkerNodes'"
            : db.Database.IsNpgsql() ? "SELECT table_name FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = 'WorkerNodes'"
            : "SELECT name FROM sqlite_master WHERE type='table' AND name='WorkerNodes'";
        if (await command.ExecuteScalarAsync(ct) is null)
        {
            var provider = db.Database.IsSqlServer() ? "mssql" : db.Database.IsNpgsql() ? "postgres" : "sqlite";
            await using var stream = typeof(SchemaUpgrade).Assembly.GetManifestResourceStream($"Runner.Engine.Migrations.006-platform.{provider}.sql")
                ?? throw new InvalidOperationException("Upgrade resource missing");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(ct);
            foreach (var batch in sql.Split("\nGO", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            { command.CommandText = batch; await command.ExecuteNonQueryAsync(ct); }
        }
        await db.WorkerNodes.AsNoTracking().Take(1).ToListAsync(ct);
        await db.Jobs.AsNoTracking().OrderBy(x => x.Id).Take(1).ToListAsync(ct);
        await db.PluginPackages.AsNoTracking().Select(x => x.ManifestJson).Take(1).ToListAsync(ct);
        await db.PluginChannels.AsNoTracking().Select(x => x.MaxConcurrency).Take(1).ToListAsync(ct);
        await db.JobDependencies.AsNoTracking().Take(1).ToListAsync(ct);
        await db.Workflows.AsNoTracking().Take(1).ToListAsync(ct);
        await db.AuditRecords.AsNoTracking().Take(1).ToListAsync(ct);
        await db.Schedules.AsNoTracking().Take(1).ToListAsync(ct);
        await RecordVersionAsync(db, 6, ct); await tx.CommitAsync(ct);
    }

    private static Task<int> RecordVersionAsync(RunnerDb db, int version, CancellationToken ct) => db.Database.IsSqlServer()
        ? db.Database.ExecuteSqlInterpolatedAsync($"IF NOT EXISTS (SELECT 1 FROM [dbo].[__RunnerSchema] WITH (UPDLOCK, HOLDLOCK) WHERE [Version] = {version}) INSERT INTO [dbo].[__RunnerSchema] ([Version]) VALUES ({version})", ct)
        : db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"__RunnerSchema\" (\"Version\") VALUES ({version}) ON CONFLICT (\"Version\") DO NOTHING", ct);

}
