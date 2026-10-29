using Microsoft.EntityFrameworkCore;
using Runner.Engine;
using Xunit;

namespace Runner.Tests;

public sealed partial class StoreTests
{
    private async Task<List<Job>> Window(NodeRegistry node, int capacity)
    {
        var jobs = new List<Job>();
        await foreach (var job in store.ClaimAvailableForNodeAsync(node, capacity, TimeSpan.FromSeconds(30), default)) jobs.Add(job);
        return jobs;
    }

    [Fact] public async Task Dispatch_window_claims_only_free_slots_and_commits_each_journal()
    {
        for (var i = 0; i < 9; i++) await Add();
        var node = Node(); await node.HeartbeatAsync(0, now, default);
        var jobs = await Window(node, 3);
        Assert.Equal(3, jobs.Count); Assert.Equal(3, jobs.Select(x => x.LeaseToken).Distinct().Count());
        await using var db = factory.CreateDbContext();
        Assert.Equal(3, await db.Jobs.CountAsync(x => x.State == JobState.Running && x.Attempts == 1));
        Assert.Equal(6, await db.Jobs.CountAsync(x => x.State == JobState.Pending && x.Attempts == 0));
        Assert.Equal(3, await db.Attempts.CountAsync(x => x.Outcome == "Running"));
        Assert.Equal(3, await db.JobLogs.CountAsync(x => x.Message == "Execution started"));
        foreach (var job in jobs)
        {
            var saved = await db.Jobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
            Assert.Equal(saved.LeaseToken, job.LeaseToken);
            Assert.InRange(Math.Abs((saved.LeaseUntilUtc!.Value - job.LeaseUntilUtc!.Value).Ticks), 0, 10); // PostgreSQL stores microseconds.
            Assert.Equal(saved.Attempts, job.Attempts); Assert.Equal(saved.Worker, job.Worker);
        }
    }

    [Fact] public async Task Competing_dispatch_windows_never_duplicate_ownership()
    {
        for (var i = 0; i < 16; i++) await Add();
        var a = Node("a"); var b = Node("b");
        await a.HeartbeatAsync(0, now, default); await b.HeartbeatAsync(0, now, default);
        var windows = await Task.WhenAll(Window(a, 4), Window(b, 4));
        Assert.All(windows, x => Assert.Equal(4, x.Count));
        Assert.Equal(8, windows.SelectMany(x => x).Select(x => x.Id).Distinct().Count());
        await using var db = factory.CreateDbContext();
        Assert.Equal(8, await db.Attempts.CountAsync()); Assert.Equal(8, await db.Jobs.CountAsync(x => x.Attempts == 1));
    }

    [Fact] public async Task Dispatch_window_rechecks_drain_between_yielded_jobs()
    {
        for (var i = 0; i < 4; i++) await Add();
        var node = Node(); await node.HeartbeatAsync(0, now, default);
        await using var window = store.ClaimAvailableForNodeAsync(node, 4, TimeSpan.FromSeconds(30), default).GetAsyncEnumerator();
        Assert.True(await window.MoveNextAsync());
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(1, await db.Attempts.CountAsync()); // Visible before enumeration continues.
            await db.WorkerNodes.Where(x => x.Id == node.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DrainRequested, true));
        }
        Assert.False(node.Draining); // The in-memory heartbeat has not observed the change.
        Assert.False(await window.MoveNextAsync());
        await using var verify = factory.CreateDbContext(); Assert.Equal(1, await verify.Attempts.CountAsync());
    }

    [Fact] public async Task Dispatch_window_rechecks_cancellation_and_attempt_snapshot()
    {
        for (var i = 0; i < 3; i++) await Add();
        var node = Node(); await node.HeartbeatAsync(0, now, default);
        await using var window = store.ClaimAvailableForNodeAsync(node, 3, TimeSpan.FromSeconds(30), default).GetAsyncEnumerator();
        Assert.True(await window.MoveNextAsync());
        var peer = (await store.ClaimAsync("peer", now, TimeSpan.FromSeconds(30), default))!;
        Assert.True(await store.CompleteAsync(peer, now, "retry", default));
        await using (var db = factory.CreateDbContext())
        {
            await db.Jobs.Where(x => x.Id == peer.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AvailableUtc, now));
            await db.Jobs.Where(x => x.State == JobState.Pending && x.Id != peer.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.CancellationRequested, true));
        }
        Assert.False(await window.MoveNextAsync()); // The peer's retry cannot use a stale attempt number.
        var retry = Assert.Single(await Window(node, 1));
        Assert.Equal(peer.Id, retry.Id); Assert.Equal(2, retry.Attempts); Assert.NotEqual(peer.LeaseToken, retry.LeaseToken);
    }

    [Fact] public async Task Dispatch_window_stops_after_local_heartbeat_failure_pauses_claims()
    {
        for (var i = 0; i < 3; i++) await Add();
        var node = Node(); await node.HeartbeatAsync(0, now, default);
        await using var window = store.ClaimAvailableForNodeAsync(node, 3, TimeSpan.FromSeconds(30), default).GetAsyncEnumerator();
        Assert.True(await window.MoveNextAsync());
        node.Draining = true;
        Assert.False(await window.MoveNextAsync());
        await using var db = factory.CreateDbContext();
        Assert.False((await db.WorkerNodes.SingleAsync()).DrainRequested);
        Assert.Equal(1, await db.Attempts.CountAsync());
    }

    [Fact] public async Task Dispatch_window_rechecks_package_limit_after_each_claim()
    {
        await SeedPluginJobs(Runner.Contracts.PluginExecutionMode.Parallel, 8);
        await using (var db = factory.CreateDbContext())
        {
            db.PluginPackages.Add(new Runner.Engine.Packaging.PluginPackage { PackageId = "concurrency.test", Version = "1.0.0" });
            await db.SaveChangesAsync();
            await db.PluginChannels.ExecuteUpdateAsync(s => s.SetProperty(x => x.MaxConcurrency, 2));
        }
        var node = Node(); await node.HeartbeatAsync(0, now, default);
        Assert.Equal(2, (await Window(node, 8)).Count);
        await using var verify = factory.CreateDbContext(); Assert.Equal(2, await verify.Attempts.CountAsync());
    }

    [Theory] [InlineData(40)] [InlineData(-1)]
    public async Task Journal_finish_is_one_conditional_write_with_bounded_nonnegative_duration(int days)
    {
        var token = Guid.NewGuid();
        await using var db = factory.CreateDbContext();
        db.Attempts.Add(new ExecutionAttempt { Id = token, JobId = Guid.NewGuid(), StartedUtc = now.AddDays(-days) });
        await db.SaveChangesAsync();
        await ExecutionJournal.FinishAttemptAsync(db, token, now, "Succeeded", null, default);
        await ExecutionJournal.FinishAttemptAsync(db, token, now.AddDays(1), "DeadLetter", "stale", default);
        await ExecutionJournal.FinishAttemptAsync(db, Guid.NewGuid(), now, "Succeeded", null, default);
        var saved = await db.Attempts.AsNoTracking().SingleAsync();
        Assert.Equal("Succeeded", saved.Outcome); Assert.Null(saved.ErrorCode); Assert.Equal(now, saved.FinishedUtc);
        Assert.InRange(saved.DurationMilliseconds!.Value, Math.Max(0, TimeSpan.FromDays(days).TotalMilliseconds) - 1,
            Math.Max(0, TimeSpan.FromDays(days).TotalMilliseconds) + 1);
    }

    [Fact] public async Task Background_probe_detects_due_schedules_blocked_steps_and_recovery()
    {
        var empty = await store.BackgroundWorkAsync(now, default);
        Assert.Equal(new JobStore.BackgroundWork(false, false, false), empty);
        await using (var db = factory.CreateDbContext())
        {
            db.Schedules.Add(new Schedule { Type = "test", IntervalSeconds = 60, NextUtc = now.AddMinutes(1) });
            db.Jobs.Add(new Job { Type = "test", AvailableUtc = now.AddHours(1) }); await db.SaveChangesAsync();
        }
        Assert.Equal(empty, await store.BackgroundWorkAsync(now, default));
        await using (var db = factory.CreateDbContext())
        {
            db.Jobs.AddRange(new Job { Type = "test", State = JobState.Blocked },
                new Job { Type = "test", State = JobState.Running, Attempts = 5, LeaseUntilUtc = now.AddSeconds(-1) });
            await db.SaveChangesAsync();
        }
        Assert.Equal(new JobStore.BackgroundWork(true, true, true), await store.BackgroundWorkAsync(now.AddMinutes(2), default));
    }

    [Fact] public async Task Background_probe_leaves_retryable_expiry_to_claims_but_detects_cancellation()
    {
        await using var db = factory.CreateDbContext();
        db.Jobs.Add(new Job { Type = "test", State = JobState.Running, Attempts = 1, LeaseUntilUtc = now.AddSeconds(-1) });
        await db.SaveChangesAsync();
        Assert.False((await store.BackgroundWorkAsync(now, default)).Recovery);
        await db.Jobs.ExecuteUpdateAsync(s => s.SetProperty(x => x.CancellationRequested, true));
        Assert.True((await store.BackgroundWorkAsync(now, default)).Recovery);
        await store.RecoverAsync(now, default);
        Assert.Equal(JobState.Cancelled, (await db.Jobs.AsNoTracking().SingleAsync()).State);
        await db.Jobs.ExecuteUpdateAsync(s => s.SetProperty(x => x.State, JobState.Pending));
        Assert.True((await store.BackgroundWorkAsync(now, default)).Recovery);
    }
}
