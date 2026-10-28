using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Runner.Contracts;
using Runner.Engine;
using Runner.Engine.Packaging;
using Xunit;

namespace Runner.Tests;

public sealed partial class StoreTests
{
    private async Task RemovePlatformSchemaAsync(RunnerDb db)
    {
        foreach (var (table, index) in new[] { ("Jobs", "IX_Jobs_FinishedUtc"), ("Attempts", "IX_Attempts_FinishedUtc"), ("JobLogs", "IX_JobLogs_TimestampUtc"), ("Jobs", "IX_Jobs_PackageId_State_LeaseUntilUtc"), ("Jobs", "IX_Jobs_State_PriorityUtc"), ("Jobs", "IX_Jobs_WorkflowId_StepKey") })
        {
            var sql = db.Database.IsSqlServer() ? $"DROP INDEX [{index}] ON [dbo].[{table}]" : $"DROP INDEX \"{index}\"";
            await db.Database.ExecuteSqlRawAsync(sql);
        }
        foreach (var table in new[] { "WorkerNodes", "ClusterLeases", "Workflows", "JobDependencies", "AuditRecords" })
            { var drop = $"DROP TABLE \"{table}\""; await db.Database.ExecuteSqlRawAsync(drop); }
        foreach (var (table, column) in new[] { ("PackageChanges", "MaxConcurrency"), ("PackageChanges", "PreviousMaxConcurrency"), ("Jobs", "WorkflowId"), ("Jobs", "StepKey"), ("Jobs", "Queue"), ("Jobs", "Priority"), ("Jobs", "PriorityUtc"),
            ("Schedules", "Cron"), ("Schedules", "TimeZoneId"), ("Schedules", "MisfirePolicy"), ("PluginChannels", "MaxConcurrency"), ("PluginPackages", "ManifestJson") })
            { var drop = $"ALTER TABLE \"{table}\" DROP COLUMN \"{column}\""; await db.Database.ExecuteSqlRawAsync(drop); }
    }

    [Fact] public async Task Platform_upgrade_preserves_jobs_packages_schedules_and_is_repeatable()
    {
        var original = await Add(); var packages = PackageService();
        await packages.InstallAsync("company.echo", "1.0.0", "Runner.SamplePlugin.dll", null, FixtureFiles(), default);
        await using var db = factory.CreateDbContext();
        db.Schedules.Add(new Schedule { Type = "test", IntervalSeconds = 60, NextUtc = now.AddMinutes(1) }); await db.SaveChangesAsync();
        await RemovePlatformSchemaAsync(db);
        await SchemaUpgrade.ApplyPlatformAsync(db); await SchemaUpgrade.ApplyPlatformAsync(db);
        Assert.Equal(original.Id, (await db.Jobs.SingleAsync()).Id);
        Assert.Equal("default", (await db.Jobs.SingleAsync()).Queue);
        Assert.Equal("{}", (await db.PluginPackages.SingleAsync()).ManifestJson);
        Assert.Null((await db.PluginChannels.SingleAsync()).MaxConcurrency);
        Assert.Equal("UTC", (await db.Schedules.SingleAsync()).TimeZoneId);
        Assert.Equal(3, await db.ClusterLeases.CountAsync());
    }

    [Fact] public async Task Cluster_limit_is_atomic_across_competing_workers_and_releases_on_completion()
    {
        await SeedPluginJobs(PluginExecutionMode.Parallel, 12);
        await using (var db = factory.CreateDbContext()) await db.PluginChannels.ExecuteUpdateAsync(s => s.SetProperty(x => x.MaxConcurrency, 3));
        var claims = await Task.WhenAll(Enumerable.Range(0, 16).Select(n => store.ClaimAsync("node-" + n, now, TimeSpan.FromSeconds(30), default)));
        Assert.Equal(3, claims.Count(x => x is not null));
        await store.CompleteAsync(claims.First(x => x is not null)!, now.AddSeconds(1), null, default);
        Assert.NotNull(await Claim(now.AddSeconds(2))); Assert.Null(await Claim(now.AddSeconds(2)));
    }

    [Fact] public async Task Busy_package_does_not_hide_unrelated_work_behind_a_large_backlog()
    {
        await SeedPluginJobs(PluginExecutionMode.Parallel, 150);
        await using (var db = factory.CreateDbContext()) await db.PluginChannels.ExecuteUpdateAsync(s => s.SetProperty(x => x.MaxConcurrency, 1));
        await Claim(); var independent = await Add();
        Assert.Equal(independent.Id, (await Claim())!.Id);
    }

    [Fact] public async Task Limit_changes_require_drain_and_do_not_override_sequential_policy()
    {
        await SeedPluginJobs(PluginExecutionMode.Sequential, 3);
        await using (var db = factory.CreateDbContext()) { db.PluginPackages.Add(new PluginPackage { PackageId = "concurrency.test", Version = "1.0.0" }); await db.SaveChangesAsync(); }
        var job = (await Claim())!;
        await Assert.ThrowsAsync<PackageConflictException>(() => PackageService().SetChannelAsync("concurrency.test", "1.0.0", true, 0, default, maxConcurrency: 3, setLimit: true));
        await store.CompleteAsync(job, now, null, default);
        Assert.True(await PackageService().SetChannelAsync("concurrency.test", "1.0.0", true, 0, default, maxConcurrency: 3, setLimit: true));
        Assert.NotNull(await Claim()); Assert.Null(await Claim());
    }

    private NodeRegistry Node(string name = "test-node", string[]? queues = null) => new(factory,
        Options.Create(new RunnerOptions { InstanceName = name, Queues = queues ?? ["default"] }),
        Options.Create(new OperationOptions()), new HandlerCatalog([new PlacementHandler("test"), new PlacementHandler(PackageJobHandler.JobType)]));
    private sealed class PlacementHandler(string type) : IJobHandler
    { public string Type => type; public Task ExecuteAsync(JobContext context, CancellationToken ct) => Task.CompletedTask; }

    [Fact] public async Task Placement_filters_incompatible_versions_before_claim_without_consuming_attempts()
    {
        var files = FixtureFiles().Append(new PackageFile("runner.manifest.json", System.Text.Encoding.UTF8.GetBytes("{\"requiredTags\":[\"dedicated-gpu\"]}"))).ToArray();
        var packages = PackageService(); await packages.InstallAsync("company.echo", "1.0.0", "Runner.SamplePlugin.dll", null, files, default);
        await packages.SetChannelAsync("company.echo", "1.0.0", true, 0, default);
        await using (var db = factory.CreateDbContext()) { db.Jobs.Add(new Job { Type = PackageJobHandler.JobType, PackageId = "company.echo", PackageVersion = "1.0.0", AvailableUtc = now }); await db.SaveChangesAsync(); }
        var independent = await Add(); var node = Node(); await node.HeartbeatAsync(0, now, default);
        Assert.Equal(independent.Id, (await store.ClaimForNodeAsync(node, now, TimeSpan.FromSeconds(30), default))!.Id);
        Assert.Null(await store.ClaimForNodeAsync(node, now, TimeSpan.FromSeconds(30), default));
        await using var verify = factory.CreateDbContext(); Assert.Equal(0, (await verify.Jobs.SingleAsync(x => x.PackageId != null)).Attempts);
    }

    [Fact] public async Task Node_registration_acknowledges_drain_and_reports_active_jobs()
    {
        var node = Node(); await node.HeartbeatAsync(0, now, default);
        await using (var db = factory.CreateDbContext()) await db.WorkerNodes.Where(x => x.Id == node.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DrainRequested, true).SetProperty(x => x.DrainDeadlineUtc, now.AddMinutes(1)));
        await node.HeartbeatAsync(2, now.AddSeconds(10), default);
        Assert.True(node.Draining); Assert.Equal(now.AddMinutes(1), node.DrainDeadlineUtc);
        await using var verify = factory.CreateDbContext(); var row = await verify.WorkerNodes.SingleAsync();
        Assert.Equal(2, row.ActiveJobs); Assert.NotNull(row.DrainAcknowledgedUtc);
    }

    [Fact] public async Task Scheduler_leadership_has_one_valid_owner_and_recovers_after_expiry()
    {
        var a = Node("a"); var b = Node("b");
        var claims = await Task.WhenAll(a.LeadAsync(now, default), b.LeadAsync(now, default)); Assert.Single(claims, x => x);
        var other = claims[0] ? b : a;
        Assert.False(await other.LeadAsync(now.AddSeconds(20), default)); Assert.True(await other.LeadAsync(now.AddSeconds(31), default));
    }

    [Fact] public async Task Worker_queues_are_filtered_in_sql()
    {
        await using (var db = factory.CreateDbContext()) { db.Jobs.Add(new Job { Type = "test", Queue = "finance", AvailableUtc = now }); await db.SaveChangesAsync(); }
        var a = Node(); var b = Node(queues: ["finance"]);
        await a.HeartbeatAsync(0, now, default); await b.HeartbeatAsync(0, now, default);
        Assert.Null(await store.ClaimForNodeAsync(a, now, TimeSpan.FromSeconds(30), default));
        Assert.NotNull(await store.ClaimForNodeAsync(b, now, TimeSpan.FromSeconds(30), default));
    }

    [Fact] public async Task Aging_prevents_continuous_high_priority_arrivals_from_starving_old_jobs()
    {
        var old = new Job { Type = "test", Priority = -10, CreatedUtc = now.AddMinutes(-21), AvailableUtc = now.AddMinutes(-21) }; Admission.SetOrder(old);
        var recent = new Job { Type = "test", Priority = 10, CreatedUtc = now, AvailableUtc = now }; Admission.SetOrder(recent);
        await using (var db = factory.CreateDbContext()) { db.Jobs.AddRange(old, recent); await db.SaveChangesAsync(); }
        Assert.Equal(old.Id, (await Claim())!.Id);
    }

    [Fact] public async Task Admission_limit_is_shared_and_atomic_for_concurrent_submissions()
    {
        async Task<bool> Submit()
        {
            await using var db = factory.CreateDbContext(); await using var tx = await db.Database.BeginTransactionAsync();
            await Admission.LockAsync(db, default);
            if (!await Admission.HasCapacityAsync(db, 1, 3, default)) return false;
            db.Jobs.Add(new Job()); await db.SaveChangesAsync(); await tx.CommitAsync(); return true;
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Submit())); Assert.Equal(3, results.Count(x => x));
    }

    [Fact] public async Task Workflow_waits_for_every_parent_and_preserves_delayed_start()
    {
        var a = new Job { Type = "test", AvailableUtc = now }; var b = new Job { Type = "test", AvailableUtc = now };
        var c = new Job { Type = "test", State = JobState.Blocked, AvailableUtc = now.AddHours(1) };
        await using (var db = factory.CreateDbContext()) { db.Jobs.AddRange(a, b, c); db.JobDependencies.AddRange(new JobDependency { JobId = c.Id, DependsOnJobId = a.Id }, new JobDependency { JobId = c.Id, DependsOnJobId = b.Id }); await db.SaveChangesAsync(); }
        var workflow = new WorkflowService(factory); var first = (await Claim())!;
        await store.CompleteAsync(first, now, null, default); Assert.Equal(0, await workflow.AdvanceAsync(now, default));
        var second = (await Claim())!; await store.CompleteAsync(second, now, null, default);
        Assert.Equal(1, await workflow.AdvanceAsync(now, default)); Assert.Null(await Claim());
        Assert.Equal(c.Id, (await Claim(now.AddHours(1)))!.Id);
    }

    [Fact] public async Task Workflow_failure_cancels_descendants_but_a_retrying_parent_does_not()
    {
        var parent = new Job { Type = "test", AvailableUtc = now, MaxAttempts = 2 };
        var child = new Job { Type = "test", State = JobState.Blocked };
        var grandchild = new Job { Type = "test", State = JobState.Blocked };
        await using (var db = factory.CreateDbContext()) { db.Jobs.AddRange(parent, child, grandchild); db.JobDependencies.AddRange(new JobDependency { JobId = child.Id, DependsOnJobId = parent.Id }, new JobDependency { JobId = grandchild.Id, DependsOnJobId = child.Id }); await db.SaveChangesAsync(); }
        var workflows = new WorkflowService(factory); var first = (await Claim())!;
        await store.CompleteAsync(first, now, "failed", default); Assert.Equal(0, await workflows.AdvanceAsync(now, default));
        var second = (await Claim(now.AddSeconds(10)))!; await store.CompleteAsync(second, now.AddSeconds(10), "failed", default);
        Assert.Equal(1, await workflows.AdvanceAsync(now.AddSeconds(10), default)); Assert.Equal(1, await workflows.AdvanceAsync(now.AddSeconds(11), default));
        await using var verify = factory.CreateDbContext(); Assert.Equal(2, await verify.Jobs.CountAsync(x => x.ErrorCode == "dependency_failed"));
    }

    [Fact] public void Workflow_rejects_cycles_unknown_parents_and_duplicate_edges()
    {
        Assert.Throws<ArgumentException>(() => WorkflowService.Validate(new Dictionary<string, string[]> { ["a"] = ["b"], ["b"] = ["a"] }));
        Assert.Throws<ArgumentException>(() => WorkflowService.Validate(new Dictionary<string, string[]> { ["a"] = ["missing"] }));
        Assert.Throws<ArgumentException>(() => WorkflowService.Validate(new Dictionary<string, string[]> { ["a"] = [], ["b"] = ["a", "a"] }));
    }

    [Theory] [InlineData("Coalesce", 1)] [InlineData("Skip", 0)] [InlineData("CatchUp", 1)]
    public async Task Calendar_misfire_policies_are_atomic_and_bounded(string policy, int expected)
    {
        await using (var db = factory.CreateDbContext()) { db.Schedules.Add(new Schedule { Type = "test", Cron = "* * * * *", TimeZoneId = "UTC", MisfirePolicy = policy, NextUtc = now.AddHours(-1) }); await db.SaveChangesAsync(); }
        await store.DispatchSchedulesAsync(now, default);
        await using var verify = factory.CreateDbContext(); Assert.Equal(expected, await verify.Jobs.CountAsync());
        Assert.Equal(policy == "CatchUp", (await verify.Schedules.SingleAsync()).NextUtc <= now);
    }

    [Fact] public void Calendar_handles_time_zones_and_spring_dst_gap()
    {
        var schedule = new Schedule { Cron = "30 2 * * *", TimeZoneId = "America/New_York" };
        var next = CalendarSchedule.Next(schedule, new DateTime(2026, 3, 8, 5, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 3, 8, 7, 0, 0, DateTimeKind.Utc), next);
        schedule.TimeZoneId = "Australia/Sydney";
        next = CalendarSchedule.Next(schedule, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 6, 1, 16, 30, 0, DateTimeKind.Utc), next);
    }

    [Fact] public void Manifest_reports_each_missing_capability_and_rejects_future_schema()
    {
        var manifest = PluginManifest.Parse("{\"minimumHostVersion\":\"2.0.0\",\"operatingSystems\":[\"windows\"],\"requiredResources\":[\"connection:warehouse\"],\"requiredTags\":[\"gpu\"]}");
        Assert.Equal(4, manifest.Incompatibilities(new("linux", "x64", "1.0.0", "0.1.0", [], [], [], [])).Length);
        Assert.Throws<PackageException>(() => PluginManifest.Parse("{\"schemaVersion\":2}"));
    }

    [Fact] public void Idle_backoff_is_bounded_and_resets_after_work()
    {
        var backoff = new IdleBackoff(1000, 30);
        Assert.InRange(backoff.Next().TotalSeconds, .8, 1);
        for (int i = 0; i < 20; i++) Assert.InRange(backoff.Next().TotalSeconds, 0, 30);
        backoff.Reset(); Assert.InRange(backoff.Next().TotalSeconds, .8, 1);
    }

    [Fact] public async Task Lease_clock_uses_database_time_and_ignores_local_wall_clock_jumps()
    {
        var time = new JumpingTimeProvider(); var clock = new ClusterClock(factory, time);
        await clock.SynchronizeAsync(default); var baseline = clock.UtcNow;
        time.Wall = time.Wall.AddYears(10); time.Timestamp += TimeSpan.FromSeconds(10).Ticks;
        Assert.Equal(baseline.AddSeconds(10), clock.UtcNow);
        time.Wall = time.Wall.AddYears(-20); time.Timestamp += TimeSpan.FromSeconds(1).Ticks;
        Assert.Equal(baseline.AddSeconds(11), clock.UtcNow);
    }
    private sealed class JumpingTimeProvider : TimeProvider
    {
        public long Timestamp;
        public DateTimeOffset Wall = DateTimeOffset.UtcNow;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => Wall;
    }

    [Fact] public void Calendar_fixed_time_does_not_repeat_during_fall_dst_overlap()
    {
        var schedule = new Schedule { Cron = "30 1 * * *", TimeZoneId = "America/New_York" };
        var first = CalendarSchedule.Next(schedule, new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc), first);
        Assert.Equal(new DateTime(2026, 11, 2, 6, 30, 0, DateTimeKind.Utc), CalendarSchedule.Next(schedule, first));
    }

    [Fact] public async Task Drain_request_blocks_new_claims_even_before_the_next_node_heartbeat()
    {
        var node = Node(); await node.HeartbeatAsync(0, now, default); await Add();
        await using (var db = factory.CreateDbContext()) await db.WorkerNodes.Where(x => x.Id == node.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DrainRequested, true));
        Assert.False(node.Draining);
        Assert.Null(await store.ClaimForNodeAsync(node, now, TimeSpan.FromSeconds(30), default));
    }

    [Fact] public async Task Warm_archive_cache_avoids_reloading_the_immutable_database_blob()
    {
        var packages = PackageService();
        await packages.InstallAsync("cache.test", "1.0.0", "Runner.SamplePlugin.dll", null, FixtureFiles(), default);
        var first = await packages.MaterializeAsync("cache.test", "1.0.0", default); PackageManager.TryDelete(first.Directory);
        // A warm, verified cache remains usable even if the stored blob becomes unreadable/corrupt.
        await using (var db = factory.CreateDbContext()) await db.PluginPackages.ExecuteUpdateAsync(s => s.SetProperty(x => x.Archive, new byte[] { 1 }));
        var second = await packages.MaterializeAsync("cache.test", "1.0.0", default);
        Assert.True(File.Exists(Path.Combine(second.Directory, "Runner.SamplePlugin.dll"))); PackageManager.TryDelete(second.Directory);
    }

    [Fact] public async Task Corrupt_local_archive_cache_is_repaired_from_the_database()
    {
        var packages = PackageService();
        var info = await packages.InstallAsync("cache.test", "1.0.0", "Runner.SamplePlugin.dll", null, FixtureFiles(), default);
        var first = await packages.MaterializeAsync("cache.test", "1.0.0", default); PackageManager.TryDelete(first.Directory);
        var cached = Path.Combine(packageCache, "archives", info.Digest + ".zip");
        await File.WriteAllBytesAsync(cached, [1, 2, 3]);
        var second = await packages.MaterializeAsync("cache.test", "1.0.0", default);
        Assert.Equal(info.Digest, PackageArchive.Hash(await File.ReadAllBytesAsync(cached))); PackageManager.TryDelete(second.Directory);
    }

    [Fact] public async Task Retention_deletes_bounded_batches_and_reports_backlog_for_catch_up()
    {
        await using (var db = factory.CreateDbContext())
        {
            db.JobLogs.AddRange(Enumerable.Range(0, 1001).Select(_ => new JobLog { TimestampUtc = now.AddDays(-20), Message = "old" }));
            await db.SaveChangesAsync();
        }
        Assert.True(await store.MaintainAsync(now, 14, default));
        await using (var verify = factory.CreateDbContext()) Assert.Equal(1, await verify.JobLogs.CountAsync());
        Assert.False(await store.MaintainAsync(now, 14, default));
        await using var final = factory.CreateDbContext(); Assert.Empty(await final.JobLogs.ToListAsync());
    }
}
