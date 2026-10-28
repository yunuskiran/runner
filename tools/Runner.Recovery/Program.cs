using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Npgsql;
using Runner.Engine;
using Runner.Engine.Packaging;

// Creates only disposable databases with generated names; no existing database is restored over.
var provider = Environment.GetEnvironmentVariable("RECOVERY_PROVIDER") ?? throw new ArgumentException("Set RECOVERY_PROVIDER=Postgres or Mssql");
var admin = Environment.GetEnvironmentVariable("RECOVERY_CONNECTION") ?? throw new ArgumentException("Set RECOVERY_CONNECTION securely");
if (provider is not ("Postgres" or "Mssql")) throw new ArgumentException("Select Postgres or Mssql");
var suffix = Guid.NewGuid().ToString("N");
var original = "runner_drill_" + suffix;
var restored = "runner_restore_" + suffix;
string Connection(string name) => provider == "Postgres" ? new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString
    : new SqlConnectionStringBuilder(admin) { InitialCatalog = name }.ConnectionString;
RunnerDb Database(string name) { var options = new DbContextOptionsBuilder<RunnerDb>(); RunnerStorage.Configure(options, provider, Connection(name)); return new(options.Options); }
var container = Environment.GetEnvironmentVariable("RECOVERY_CONTAINER") ?? (provider == "Postgres" ? "runner-postgres-test" : "runner-mssql-test");
var backup = provider == "Postgres" ? "/tmp/" + original + ".dump" : "/var/opt/mssql/data/" + original + ".bak";
var fixtureId = Guid.NewGuid();
var archive = System.Security.Cryptography.RandomNumberGenerator.GetBytes(262144);
try
{
    await using (var db = Database(original))
    {
        await db.Database.EnsureCreatedAsync();
        db.Jobs.Add(new Job { Id = fixtureId, State = JobState.Succeeded, ResultJson = "{\"restored\":true}", FinishedUtc = DateTime.UtcNow });
        db.PluginPackages.Add(new PluginPackage { PackageId = "recovery.fixture", Version = "1.0.0", Archive = archive, Digest = PackageArchive.Hash(archive) });
        db.Functions.Add(new FunctionDefinition { Name = "recovery", Version = 1, Source = "return 42;", SourceHash = ScriptCompiler.Hash("return 42;") });
        db.Schedules.Add(new Schedule { Type = "test", Cron = "0 9 * * *", TimeZoneId = "Australia/Sydney", NextUtc = DateTime.UtcNow.AddDays(1) });
        await db.SaveChangesAsync();
    }
    var stopwatch = Stopwatch.StartNew();
    if (provider == "Postgres")
    {
        await Docker("exec", container, "pg_dump", "-U", "postgres", "-Fc", original, "-f", backup);
        await using (var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(admin) { Database = "postgres" }.ConnectionString))
        { await connection.OpenAsync(); await using var command = new NpgsqlCommand($"CREATE DATABASE \"{restored}\"", connection); await command.ExecuteNonQueryAsync(); }
        await Docker("exec", container, "pg_restore", "-U", "postgres", "--no-owner", "--no-acl", "-d", restored, backup);
    }
    else
    {
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(admin) { InitialCatalog = "master" }.ConnectionString);
        await connection.OpenAsync();
        await using (var backupCommand = new SqlCommand($"BACKUP DATABASE [{original}] TO DISK=@path WITH COPY_ONLY, INIT, CHECKSUM", connection) { CommandTimeout = 120 })
        { backupCommand.Parameters.AddWithValue("@path", backup); await backupCommand.ExecuteNonQueryAsync(); }
        await using (var verify = new SqlCommand("RESTORE VERIFYONLY FROM DISK=@path WITH CHECKSUM", connection) { CommandTimeout = 120 })
        { verify.Parameters.AddWithValue("@path", backup); await verify.ExecuteNonQueryAsync(); }
        var files = new List<(string Logical, string Type)>();
        await using (var list = new SqlCommand("RESTORE FILELISTONLY FROM DISK=@path", connection))
        { list.Parameters.AddWithValue("@path", backup); await using var reader = await list.ExecuteReaderAsync(); while (await reader.ReadAsync()) files.Add((reader.GetString(0), reader.GetString(2))); }
        var moves = files.Select((file, index) => "MOVE '" + file.Logical.Replace("'", "''") + "' TO '/var/opt/mssql/data/" + restored + "_" + index + (file.Type == "L" ? ".ldf" : ".mdf") + "'");
        await using var restore = new SqlCommand($"RESTORE DATABASE [{restored}] FROM DISK=@path WITH " + string.Join(", ", moves) + ", CHECKSUM", connection) { CommandTimeout = 120 };
        restore.Parameters.AddWithValue("@path", backup); await restore.ExecuteNonQueryAsync();
    }
    await using (var db = Database(restored))
    {
        if ((await db.Jobs.SingleAsync()).Id != fixtureId || (await db.Jobs.SingleAsync()).ResultJson != "{\"restored\":true}") throw new InvalidOperationException("Job restore mismatch");
        var package = await db.PluginPackages.SingleAsync();
        if (!package.Archive.SequenceEqual(archive) || PackageArchive.Hash(package.Archive) != package.Digest) throw new InvalidOperationException("Package restore mismatch");
        if ((await db.Functions.SingleAsync()).Source != "return 42;" || (await db.Schedules.SingleAsync()).TimeZoneId != "Australia/Sydney") throw new InvalidOperationException("Definition restore mismatch");
        await SchemaUpgrade.ApplyPlatformAsync(db); await SchemaUpgrade.ApplyPlatformAsync(db);
    }
    var report = new { provider, restoreSeconds = stopwatch.Elapsed.TotalSeconds, jobsVerified = 1, archiveBytesVerified = archive.Length, sourceAndCalendarVerified = true, repeatableUpgradeVerified = true,
        note = "Disposable local databases, not production RTO/RPO certification." };
    Directory.CreateDirectory("artifacts"); await File.WriteAllTextAsync("artifacts/recovery-" + provider.ToLowerInvariant() + ".json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    Console.WriteLine("PASS: " + provider + " native backup/restore into a separate database; job, package digest, function, calendar and repeatable schema upgrade verified.");
}
finally
{
    await using (var db = Database(restored)) await db.Database.EnsureDeletedAsync();
    await using (var db = Database(original)) await db.Database.EnsureDeletedAsync();
    await Docker("exec", container, "rm", "-f", "--", backup);
}

static async Task Docker(params string[] arguments)
{
    var start = new ProcessStartInfo("docker") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var variable in new[] { "DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_TLS", "DOCKER_TLS_VERIFY", "DOCKER_CERT_PATH" }) start.Environment.Remove(variable);
    start.ArgumentList.Add("--host=unix:///var/run/docker.sock"); foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker failed to start");
    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120)); await Task.WhenAll(stdout, stderr);
    if (process.ExitCode != 0) throw new InvalidOperationException("Recovery container operation failed: " + (await stderr)[..Math.Min(2000, (await stderr).Length)]);
}
