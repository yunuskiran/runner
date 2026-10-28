using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Runner.Engine;
using Runner.Contracts;
using Runner.Engine.Packaging;
using Microsoft.Extensions.Options;
using Xunit;
namespace Runner.Tests;

public sealed class StoreTests : IAsyncLifetime
{
    private TestFactory factory = null!;
    private JobStore store = null!;
    private readonly DateTime now = new(DateTime.UtcNow.Ticks / 10 * 10, DateTimeKind.Utc);
    public async Task InitializeAsync()
    {
        var pg = Environment.GetEnvironmentVariable("TEST_POSTGRES");
        var mssql = Environment.GetEnvironmentVariable("TEST_MSSQL");
        if (pg is not null && mssql is not null) throw new InvalidOperationException("Select TEST_POSTGRES or TEST_MSSQL, not both");
        var options = new DbContextOptionsBuilder<RunnerDb>();
        if (mssql is not null)
        {
            var cs = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(mssql) { InitialCatalog = $"runner_test_{Guid.NewGuid():N}" };
            RunnerStorage.Configure(options, "Mssql", cs.ConnectionString);
        }
        else if (pg is null) options.UseSqlite($"Data Source={Path.GetTempPath()}runner-{Guid.NewGuid():N}.db;Default Timeout=30");
        else { var cs = new Npgsql.NpgsqlConnectionStringBuilder(pg) { Database = $"runner_test_{Guid.NewGuid():N}" }; options.UseNpgsql(cs.ConnectionString); }
        factory = new TestFactory(options.Options); store = new JobStore(factory);
        await using var db = factory.CreateDbContext(); await db.Database.EnsureCreatedAsync();
    }
    public async Task DisposeAsync() { await using var db = factory.CreateDbContext(); await db.Database.EnsureDeletedAsync(); }
    private async Task<Job> Add(int attempts = 3)
    {
        await using var db = factory.CreateDbContext();
        var job = new Job { Type = "test", MaxAttempts = attempts, AvailableUtc = now.AddSeconds(-1) };
        db.Add(job); await db.SaveChangesAsync(); return job;
    }
    private Task<Job?> Claim(DateTime? at = null) => store.ClaimAsync("test-worker", at ?? now, TimeSpan.FromSeconds(30), default);
    [Fact] public async Task Concurrent_claim_has_one_owner()
    {
        await Add(); var claims = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Claim()));
        Assert.Single(claims, x => x is not null); Assert.Equal(1, claims.Single(x => x is not null)!.Attempts);
    }
    [Fact] public async Task Expired_owner_cannot_complete_or_renew()
    {
        await Add(); var first = (await Claim())!; var second = (await Claim(now.AddSeconds(31)))!;
        Assert.NotEqual(first.LeaseToken, second.LeaseToken); Assert.Equal(2, second.Attempts);
        Assert.False(await store.CompleteAsync(first, now.AddSeconds(32), null, default));
        Assert.False(await store.RenewAsync(first.Id, first.LeaseToken!.Value, now.AddSeconds(32), TimeSpan.FromSeconds(30), default));
        Assert.True(await store.CompleteAsync(second, now.AddSeconds(32), null, default));
    }
    [Fact] public async Task Expired_lease_cannot_complete_even_before_reclaim()
    {
        await Add(); var job = (await Claim())!;
        Assert.False(await store.CompleteAsync(job, now.AddSeconds(31), null, default));
        Assert.False(await store.RenewAsync(job.Id, job.LeaseToken!.Value, now.AddSeconds(31), TimeSpan.FromSeconds(30), default));
    }
    [Fact] public async Task Failure_retries_then_deadletters()
    {
        await Add(2); var first = (await Claim())!;
        Assert.True(await store.CompleteAsync(first, now, "failed", default));
        Assert.Null(await Claim());
        var second = (await Claim(now.AddSeconds(10)))!; Assert.Equal(2, second.Attempts);
        await store.CompleteAsync(second, now.AddSeconds(10), "failed", default);
        await using var db = factory.CreateDbContext(); Assert.Equal(JobState.DeadLetter, (await db.Jobs.SingleAsync()).State);
    }
    [Fact] public async Task Final_crashed_attempt_deadletters()
    {
        await Add(1); await Claim(); Assert.Null(await Claim(now.AddSeconds(31)));
        await using var db = factory.CreateDbContext(); var job = await db.Jobs.SingleAsync();
        Assert.Equal(JobState.DeadLetter, job.State); Assert.Equal("lease_expired", job.ErrorCode);
    }
    [Fact] public async Task Cancellation_prevents_completion_and_eventually_finalizes()
    {
        await Add(); var job = (await Claim())!;
        await using (var db = factory.CreateDbContext()) await db.Jobs.ExecuteUpdateAsync(s => s.SetProperty(x => x.CancellationRequested, true));
        Assert.False(await store.RenewAsync(job.Id, job.LeaseToken!.Value, now, TimeSpan.FromSeconds(30), default));
        Assert.False(await store.CompleteAsync(job, now, null, default));
        await store.MaintainAsync(now.AddSeconds(31), 14, default);
        await using var verify = factory.CreateDbContext(); Assert.Equal(JobState.Cancelled, (await verify.Jobs.SingleAsync()).State);
    }
    [Fact] public async Task Pending_cancellation_never_executes()
    {
        await Add(); await using (var db = factory.CreateDbContext()) await db.Jobs.ExecuteUpdateAsync(s => s.SetProperty(x => x.CancellationRequested, true));
        Assert.Null(await Claim()); await store.MaintainAsync(now, 14, default);
        await using var verify = factory.CreateDbContext(); Assert.Equal(JobState.Cancelled, (await verify.Jobs.SingleAsync()).State);
    }
    [Fact] public async Task Scheduler_is_atomic_and_coalesces_missed_ticks()
    {
        await using (var db = factory.CreateDbContext()) { db.Schedules.Add(new Schedule { Type = "test", IntervalSeconds = 60, NextUtc = now.AddHours(-1) }); await db.SaveChangesAsync(); }
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.DispatchSchedulesAsync(now, default)));
        await using var verify = factory.CreateDbContext(); Assert.Equal(1, await verify.Jobs.CountAsync());
        Assert.Equal(now.AddSeconds(60), (await verify.Schedules.SingleAsync()).NextUtc);
    }
    [Fact] public async Task Retention_keeps_deadletters()
    {
        await using (var db = factory.CreateDbContext()) { db.Jobs.AddRange(new Job { State = JobState.Succeeded, FinishedUtc = now.AddDays(-20) }, new Job { State = JobState.DeadLetter, FinishedUtc = now.AddDays(-20) }); await db.SaveChangesAsync(); }
        await store.MaintainAsync(now, 14, default);
        await using var verify = factory.CreateDbContext(); Assert.Equal(JobState.DeadLetter, (await verify.Jobs.SingleAsync()).State);
    }
    [Fact] public async Task Idempotency_key_is_unique()
    {
        await using var db = factory.CreateDbContext(); db.Jobs.Add(new Job { IdempotencyKey = "same" }); await db.SaveChangesAsync();
        db.Jobs.Add(new Job { IdempotencyKey = "same" }); await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
    [Fact] public async Task Functions_upgrade_preserves_jobs_and_is_repeatable()
    {
        var original = await Add();
        await using var db = factory.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"Functions\"; ALTER TABLE \"Jobs\" DROP COLUMN \"ResultJson\";");
        await SchemaUpgrade.ApplyFunctionsAsync(db);
        await SchemaUpgrade.ApplyFunctionsAsync(db);
        Assert.Equal(original.Id, (await db.Jobs.SingleAsync()).Id);
        db.Functions.Add(new FunctionDefinition { Name = "upgraded", Version = 1, Source = "return 1;", SourceHash = ScriptCompiler.Hash("return 1;") });
        await db.SaveChangesAsync(); Assert.Equal(1, await db.Functions.CountAsync());
    }
    [Fact] public async Task Script_handler_loads_pinned_function_and_returns_json()
    {
        using var compiler = new ScriptCompiler();
        var function = new FunctionDefinition { Name = "double", Version = 1,
            Source = "return new { value = Arguments.GetProperty(\"x\").GetInt32() * 2, attempt = Attempt };" };
        function.SourceHash = ScriptCompiler.Hash(function.Source);
        await using (var db = factory.CreateDbContext()) { db.Functions.Add(function); await db.SaveChangesAsync(); }
        var handler = new ScriptFunctionHandler(factory, compiler, Microsoft.Extensions.Options.Options.Create(new ScriptingOptions { Enabled = true }));
        string? output = null;
        var payload = System.Text.Json.JsonSerializer.Serialize(new { functionId = function.Id, arguments = new { x = 21 } });
        await handler.ExecuteAsync(new Runner.Contracts.JobContext(Guid.NewGuid(), payload, 2) { ReportResult = json => output = json }, default);
        using var result = System.Text.Json.JsonDocument.Parse(output!);
        Assert.Equal(42, result.RootElement.GetProperty("value").GetInt32());
        Assert.Equal(2, result.RootElement.GetProperty("attempt").GetInt32());
        await using var verify = factory.CreateDbContext();
        await verify.Functions.ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ExecuteAsync(new Runner.Contracts.JobContext(Guid.NewGuid(), payload, 1), default));
    }
    [Fact] public async Task Result_is_committed_only_by_current_owner()
    {
        await Add(); var first = (await Claim())!; var second = (await Claim(now.AddSeconds(31)))!;
        Assert.False(await store.CompleteAsync(first, now.AddSeconds(32), null, default, "1"));
        Assert.True(await store.CompleteAsync(second, now.AddSeconds(32), null, default, "42"));
        await using var db = factory.CreateDbContext(); Assert.Equal("42", (await db.Jobs.SingleAsync()).ResultJson);
    }
    [Fact] public async Task Function_versions_are_unique()
    {
        await using var db = factory.CreateDbContext();
        db.Functions.Add(new FunctionDefinition { Name = "same", Version = 1 }); await db.SaveChangesAsync();
        db.Functions.Add(new FunctionDefinition { Name = "same", Version = 1 });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
    [Fact] public async Task Central_journal_and_alert_commit_with_terminal_failure()
    {
        await Add(1); var job = (await Claim())!;
        await store.WriteLogAsync(job, "Information", "Business event", default);
        Assert.True(await store.CompleteAsync(job, now.AddSeconds(1), "expected_failure", default));
        Assert.False(await store.CompleteAsync(job, now.AddSeconds(1), "expected_failure", default));
        await using var db = factory.CreateDbContext();
        Assert.Equal("DeadLetter", (await db.Attempts.SingleAsync()).Outcome);
        Assert.Single(await db.Alerts.ToListAsync());
        Assert.Equal(3, await db.JobLogs.CountAsync());
    }
    [Fact] public async Task Expired_final_attempt_generates_alert_once()
    {
        await Add(1); await Claim(); await Claim(now.AddSeconds(31)); await Claim(now.AddSeconds(32));
        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.Alerts.CountAsync()); Assert.Equal("DeadLetter", (await db.Attempts.SingleAsync()).Outcome);
    }
    [Fact] public async Task Alert_delivery_is_fenced_and_retries_are_bounded()
    {
        await Add(1); var job = (await Claim())!; await store.CompleteAsync(job, now, "failed", default);
        var alerts = new AlertStore(factory);
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => alerts.ClaimAsync(now, default)));
        var first = Assert.Single(claims, x => x is not null)!;
        var second = (await alerts.ClaimAsync(now.AddSeconds(61), default))!;
        Assert.False(await alerts.FinishAsync(first, true, now.AddSeconds(62), default));
        var current = second; var at = now.AddSeconds(62);
        while (current.DeliveryAttempts < 8)
        {
            Assert.True(await alerts.FinishAsync(current, false, at, default));
            at = at.AddHours(2); current = (await alerts.ClaimAsync(at, default))!;
        }
        await alerts.FinishAsync(current, false, at, default);
        await using var db = factory.CreateDbContext(); Assert.Equal("Parked", (await db.Alerts.SingleAsync()).DeliveryState);
    }
    [Fact] public async Task Operations_upgrade_is_repeatable_and_preserves_jobs()
    {
        var job = await Add(); await using var db = factory.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"Attempts\"; DROP TABLE \"JobLogs\"; DROP TABLE \"Alerts\";");
        await SchemaUpgrade.ApplyOperationsAsync(db); await SchemaUpgrade.ApplyOperationsAsync(db);
        Assert.Equal(job.Id, (await db.Jobs.SingleAsync()).Id); Assert.Empty(await db.Attempts.ToListAsync());
    }
    private PackageManager PackageService() => new(factory, Options.Create(new PackageOptions { CacheDirectory = Path.Combine(Path.GetTempPath(), "runner-packages-tests") }));
    private static PackageFile[] FixtureFiles() => Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "package-fixture"))
        .Select(path => new PackageFile(Path.GetFileName(path), File.ReadAllBytes(path))).ToArray();
    [Fact] public async Task Package_install_is_immutable_and_repeatable()
    {
        var packages = PackageService(); var files = FixtureFiles();
        await Assert.ThrowsAsync<PackageException>(() => packages.InstallAsync("company.echo", "0.1.0", "Runner.SamplePlugin.dll", "Runner.SamplePlugin.EchoHandler", files, default));
        var first = await packages.InstallAsync("company.echo", "1.0.0", "Runner.SamplePlugin.dll", null, files, default);
        var again = await packages.InstallAsync("company.echo", "1.0.0", "Runner.SamplePlugin.dll", null, files.Reverse().ToArray(), default);
        Assert.Equal(first.Digest, again.Digest);
        Assert.Equal("Runner.SamplePlugin.EchoPlugin", first.EntryType);
        await Assert.ThrowsAsync<PackageConflictException>(() => packages.InstallAsync("company.echo", "1.0.0", "Runner.SamplePlugin.dll", null,
            files.Append(new PackageFile("settings.json", "{}"u8.ToArray())).ToArray(), default));
        await using var db = factory.CreateDbContext(); Assert.Equal(1, await db.PluginPackages.CountAsync());
    }
    [Fact] public async Task Package_activation_revision_prevents_lost_updates_and_records_rollback()
    {
        var packages = PackageService();
        foreach (var version in new[] { "1.0.0", "2.0.0" }) await packages.InstallAsync("company.echo", version, "Runner.SamplePlugin.dll", null, FixtureFiles(), default);
        Assert.True(await packages.SetChannelAsync("company.echo", "1.0.0", true, 0, default));
        Assert.False(await packages.SetChannelAsync("company.echo", "2.0.0", true, 0, default));
        Assert.True(await packages.SetChannelAsync("company.echo", "2.0.0", true, 1, default));
        Assert.True(await packages.SetChannelAsync("company.echo", "1.0.0", true, 2, default));
        Assert.True(await packages.SetChannelAsync("company.echo", null, false, 3, default));
        await using var db = factory.CreateDbContext(); var channel = await db.PluginChannels.SingleAsync();
        Assert.Equal("1.0.0", channel.ActiveVersion); Assert.False(channel.Enabled); Assert.Equal(4, await db.PackageChanges.CountAsync());
    }
    [Fact] public async Task Pinned_package_job_waits_while_disabled_and_preserves_version()
    {
        var packages = PackageService();
        foreach (var version in new[] { "1.0.0", "2.0.0" }) await packages.InstallAsync("company.echo", version, "Runner.SamplePlugin.dll", null, FixtureFiles(), default);
        await packages.SetChannelAsync("company.echo", "1.0.0", true, 0, default);
        using var input = System.Text.Json.JsonDocument.Parse("{\"packageId\":\"company.echo\",\"payload\":{\"name\":\"test\"}}");
        var pinned = await packages.PinAsync(input.RootElement, default); Assert.Equal("1.0.0", pinned.Version);
        await using (var db = factory.CreateDbContext())
        {
            db.Jobs.Add(new Job { Type = PackageJobHandler.JobType, PackageId = pinned.PackageId, PackageVersion = pinned.Version,
                Payload = System.Text.Json.JsonSerializer.Serialize(pinned, System.Text.Json.JsonSerializerOptions.Web), AvailableUtc = now.AddSeconds(-1) });
            await db.SaveChangesAsync();
        }
        await packages.SetChannelAsync("company.echo", "2.0.0", false, 1, default);
        Assert.Null(await Claim());
        await packages.SetChannelAsync("company.echo", "2.0.0", true, 2, default);
        var job = (await Claim())!; Assert.Equal("1.0.0", job.PackageVersion);
        using var provider = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();
        var handler = new PackageJobHandler(packages, provider); string? output = null;
        await handler.ExecuteAsync(new Runner.Contracts.JobContext(job.Id, job.Payload, job.Attempts) { ReportResult = json => output = json }, default);
        Assert.Contains("Hello, test!", output!, StringComparison.Ordinal);
    }
    [Fact] public async Task Disabled_package_schedules_do_not_dispatch_or_advance()
    {
        var packages = PackageService(); await packages.InstallAsync("company.echo", "1.0.0", "Runner.SamplePlugin.dll", null, FixtureFiles(), default);
        await using (var db = factory.CreateDbContext())
        {
            db.Schedules.Add(new Schedule { Type = PackageJobHandler.JobType, PackageId = "company.echo", PackageVersion = "1.0.0", IntervalSeconds = 60, NextUtc = now.AddHours(-1) });
            await db.SaveChangesAsync();
        }
        Assert.Equal(0, await store.DispatchSchedulesAsync(now, default));
        await packages.SetChannelAsync("company.echo", "1.0.0", true, 0, default);
        var disabledStore = new JobStore(factory, Options.Create(new PackageOptions { Enabled = false }));
        Assert.Equal(0, await disabledStore.DispatchSchedulesAsync(now, default));
        Assert.Equal(1, await store.DispatchSchedulesAsync(now, default));
        await using var verify = factory.CreateDbContext(); Assert.Equal("1.0.0", (await verify.Jobs.SingleAsync()).PackageVersion);
    }
    [Fact] public async Task Package_upgrade_preserves_existing_jobs_and_is_repeatable()
    {
        var job = await Add(); await using var db = factory.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"PluginPackages\"; DROP TABLE \"PluginChannels\"; DROP TABLE \"PackageChanges\"; ALTER TABLE \"Jobs\" DROP COLUMN \"PackageId\"; ALTER TABLE \"Jobs\" DROP COLUMN \"PackageVersion\"; ALTER TABLE \"Schedules\" DROP COLUMN \"PackageId\"; ALTER TABLE \"Schedules\" DROP COLUMN \"PackageVersion\";");
        await SchemaUpgrade.ApplyConcurrencyAsync(db); await SchemaUpgrade.ApplyConcurrencyAsync(db);
        Assert.Equal(job.Id, (await db.Jobs.SingleAsync()).Id); Assert.Empty(await db.PluginPackages.Select(x => x.PackageId).ToListAsync());
        var packages = PackageService();
        foreach (var version in new[] { "1.0.0-rc.1", "1.0.0-RC.1" })
            await packages.InstallAsync("company.echo", version, "Runner.SamplePlugin.dll", null, FixtureFiles(), default);
        Assert.Equal(2, await db.PluginPackages.CountAsync());
    }
    [Fact] public async Task Corrupt_package_archive_is_rejected_at_execution()
    {
        var packages = PackageService(); await packages.InstallAsync("company.echo", "1.0.0", "Runner.SamplePlugin.dll", null, FixtureFiles(), default);
        await using (var db = factory.CreateDbContext()) await db.PluginPackages.ExecuteUpdateAsync(s => s.SetProperty(x => x.Archive, new byte[] { 1, 2, 3 }));
        await Assert.ThrowsAsync<PackageException>(() => packages.MaterializeAsync("company.echo", "1.0.0", default));
    }
    [Fact] public async Task Idempotency_keys_preserve_case_and_allow_multiple_nulls()
    {
        await using var db = factory.CreateDbContext();
        db.Jobs.AddRange(new Job(), new Job(), new Job { IdempotencyKey = "Order-123" }, new Job { IdempotencyKey = "order-123" });
        await db.SaveChangesAsync();
        Assert.Equal(4, await db.Jobs.CountAsync());
        Assert.Equal(1, await db.Jobs.CountAsync(x => x.IdempotencyKey == "Order-123"));
    }
    private async Task SeedPluginJobs(PluginExecutionMode mode, int count, string packageId = "concurrency.test", string version = "1.0.0")
    {
        await using var db = factory.CreateDbContext();
        db.PluginChannels.Add(new PluginChannel { PackageId = packageId, Enabled = true, ActiveVersion = version, ExecutionMode = mode });
        for (int n = 0; n < count; n++) db.Jobs.Add(new Job { Type = PackageJobHandler.JobType, PackageId = packageId, PackageVersion = n % 2 == 0 ? version : "2.0.0", AvailableUtc = now.AddSeconds(-1) });
        await db.SaveChangesAsync();
    }
    [Fact] public async Task Sequential_claim_is_exclusive_across_workers_and_versions()
    {
        await SeedPluginJobs(PluginExecutionMode.Sequential, 8);
        var claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(n => store.ClaimAsync("node-" + n, now, TimeSpan.FromSeconds(30), default)));
        var owner = Assert.Single(claims, x => x is not null)!;
        Assert.True(await store.CompleteAsync(owner, now.AddSeconds(1), null, default));
        var next = (await Claim(now.AddSeconds(2)))!; Assert.NotEqual(owner.Id, next.Id);
        await using var db = factory.CreateDbContext(); Assert.Equal(2, await db.Attempts.CountAsync());
    }
    [Fact] public async Task Parallel_claims_allow_different_jobs_to_overlap()
    {
        await SeedPluginJobs(PluginExecutionMode.Parallel, 8);
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(n => store.ClaimAsync("node-" + n, now, TimeSpan.FromSeconds(30), default)));
        Assert.Equal(8, claims.Count(x => x is not null)); Assert.Equal(8, claims.Select(x => x!.Id).Distinct().Count());
    }
    [Fact] public async Task Sequential_renewal_failure_and_takeover_preserve_fencing()
    {
        await SeedPluginJobs(PluginExecutionMode.Sequential, 2);
        var first = (await Claim())!;
        Assert.True(await store.RenewAsync(first.Id, first.LeaseToken!.Value, now.AddSeconds(20), TimeSpan.FromSeconds(30), default));
        Assert.Null(await Claim(now.AddSeconds(31)));
        var next = (await Claim(now.AddSeconds(51)))!;
        Assert.False(await store.RenewAsync(first.Id, first.LeaseToken.Value, now.AddSeconds(52), TimeSpan.FromSeconds(30), default));
        Assert.False(await store.CompleteAsync(first, now.AddSeconds(52), null, default));
        Assert.True(await store.CompleteAsync(next, now.AddSeconds(52), null, default));
    }
    [Fact] public async Task Sequential_packages_are_independent_and_retry_releases_the_slot()
    {
        await SeedPluginJobs(PluginExecutionMode.Sequential, 2, "package.one");
        await SeedPluginJobs(PluginExecutionMode.Sequential, 1, "package.two");
        var first = (await Claim())!; var second = (await Claim())!; Assert.NotEqual(first.PackageId, second.PackageId);
        Assert.Null(await Claim());
        await store.CompleteAsync(first, now, "retry", default);
        var third = (await Claim())!; Assert.Equal(first.PackageId, third.PackageId); Assert.NotEqual(first.Id, third.Id);
    }
    [Fact] public async Task Mode_change_requires_valid_leases_to_drain()
    {
        await SeedPluginJobs(PluginExecutionMode.Parallel, 2);
        await using (var db = factory.CreateDbContext()) { db.PluginPackages.Add(new PluginPackage { PackageId = "concurrency.test", Version = "1.0.0" }); await db.SaveChangesAsync(); }
        var job = (await Claim())!;
        await Assert.ThrowsAsync<PackageConflictException>(() => PackageService().SetChannelAsync("concurrency.test", "1.0.0", true, 0, default, executionMode: PluginExecutionMode.Sequential));
        await store.CompleteAsync(job, now.AddSeconds(1), null, default);
        Assert.True(await PackageService().SetChannelAsync("concurrency.test", "1.0.0", true, 0, default, executionMode: PluginExecutionMode.Sequential));
    }
    [Fact] public async Task Sequential_final_crash_releases_slot_for_following_job()
    {
        await SeedPluginJobs(PluginExecutionMode.Sequential, 2);
        var job = (await Claim())!;
        await using (var db = factory.CreateDbContext()) await db.Jobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.MaxAttempts, 1));
        var next = (await Claim(now.AddSeconds(31)))!; Assert.NotEqual(job.Id, next.Id);
        await using var verify = factory.CreateDbContext(); Assert.Equal(next.LeaseToken, (await verify.PluginChannels.SingleAsync()).ExecutionToken);
    }
    [Fact] public async Task Concurrency_upgrade_preserves_versions_and_defaults_to_parallel()
    {
        var packages = PackageService(); await packages.InstallAsync("company.echo", "1.0.0", "Runner.SamplePlugin.dll", null, FixtureFiles(), default);
        await using var db = factory.CreateDbContext();
        // A freshly created EF schema has no column defaults; the upgrade adds defaults for existing rows.
        if (db.Database.IsSqlServer())
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE [dbo].[PluginPackages] DROP COLUMN [DefaultExecutionMode]; ALTER TABLE [dbo].[PluginChannels] DROP COLUMN [ExecutionMode], [ExecutionToken], [ExecutionLeaseUtc]; ALTER TABLE [dbo].[PackageChanges] DROP COLUMN [ExecutionMode], [PreviousExecutionMode];");
        }
        else await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"PluginPackages\" DROP COLUMN \"DefaultExecutionMode\"; ALTER TABLE \"PluginChannels\" DROP COLUMN \"ExecutionMode\"; ALTER TABLE \"PluginChannels\" DROP COLUMN \"ExecutionToken\"; ALTER TABLE \"PluginChannels\" DROP COLUMN \"ExecutionLeaseUtc\"; ALTER TABLE \"PackageChanges\" DROP COLUMN \"ExecutionMode\"; ALTER TABLE \"PackageChanges\" DROP COLUMN \"PreviousExecutionMode\";");
        await SchemaUpgrade.ApplyConcurrencyAsync(db); await SchemaUpgrade.ApplyConcurrencyAsync(db);
        Assert.Equal(PluginExecutionMode.Parallel, (await db.PluginChannels.SingleAsync()).ExecutionMode);
        Assert.Equal(1, await db.PluginPackages.CountAsync());
    }
    [Fact] public async Task Concurrent_first_installs_commit_one_channel_and_all_versions()
    {
        var packages = PackageService(); var files = FixtureFiles();
        await Task.WhenAll(Enumerable.Range(1, 4).Select(n => packages.InstallAsync("concurrent.install", $"1.0.{n}", "Runner.SamplePlugin.dll", null, files, default)));
        await using var db = factory.CreateDbContext();
        Assert.Equal(4, await db.PluginPackages.CountAsync()); Assert.Equal(1, await db.PluginChannels.CountAsync());
    }
    [Fact] public async Task Lost_sequential_slot_rejects_business_logs_from_the_previous_job()
    {
        await SeedPluginJobs(PluginExecutionMode.Sequential, 2);
        var first = (await Claim())!;
        await using (var db = factory.CreateDbContext())
            await db.Jobs.Where(x => x.Id == first.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AvailableUtc, now.AddHours(1)));
        var next = (await Claim(now.AddSeconds(31)))!; Assert.NotEqual(first.Id, next.Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteLogAsync(first, "Information", "stale owner", default));
    }
    private sealed class TestFactory(DbContextOptions<RunnerDb> options) : IDbContextFactory<RunnerDb>
    { public RunnerDb CreateDbContext() => new(options); }
}
