using Microsoft.EntityFrameworkCore;
using Runner.Contracts;
using Microsoft.Extensions.Options;
using Runner.Engine.Packaging;
using System.Runtime.CompilerServices;
namespace Runner.Engine;

public interface IJobStore
{
    Task<Job?> ClaimAsync(string worker, DateTime now, TimeSpan lease, CancellationToken ct);
    Task<bool> RenewAsync(Guid id, Guid token, DateTime now, TimeSpan lease, CancellationToken ct);
    Task<bool> CompleteAsync(Job job, DateTime now, string? errorCode, CancellationToken ct, string? resultJson = null);
}

/// <summary>All ownership changes use conditional SQL updates; no process-local locks are required.</summary>
public sealed class JobStore(IDbContextFactory<RunnerDb> factory, IOptions<PackageOptions>? packages = null, IOptions<RunnerOptions>? runner = null, AlertSignals? alerts = null, ClusterClock? clock = null) : IJobStore
{
    private DateTime UtcNow => clock?.UtcNow ?? DateTime.UtcNow;
    private readonly bool packagesEnabled = packages?.Value.Enabled ?? true;
    public Task<Job?> ClaimAsync(string worker, DateTime now, TimeSpan lease, CancellationToken ct) =>
        ClaimCoreAsync(worker, now, lease, null, [], true, ct);

    public Task<Job?> ClaimForNodeAsync(NodeRegistry node, DateTime now, TimeSpan lease, CancellationToken ct) =>
        ClaimCoreAsync(node.Id, now, lease, node.Capabilities, node.CompatiblePackages, false, ct);

    /// <summary>Shares a bounded candidate read across free slots; each lease commits independently before yielding.</summary>
    public IAsyncEnumerable<Job> ClaimAvailableForNodeAsync(NodeRegistry node, int capacity, TimeSpan lease, CancellationToken ct) =>
        ClaimWindowAsync(node.Id, () => node.UtcNow, lease, node.Capabilities, node.CompatiblePackages, false, capacity, ct, () => !node.Draining);

    public sealed record BackgroundWork(bool Schedules, bool Workflows, bool Recovery);

    /// <summary>One indexed existence probe replaces four empty background scans per scheduler cycle.</summary>
    public async Task<BackgroundWork> BackgroundWorkAsync(DateTime now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ClusterLeases.Where(x => x.Name == "scheduler").Select(_ => new BackgroundWork(
            db.Schedules.Any(x => x.Enabled && x.NextUtc <= now),
            db.Jobs.Any(x => x.State == JobState.Blocked && !x.CancellationRequested),
            db.Jobs.Any(x => x.State == JobState.Running && x.LeaseUntilUtc <= now &&
                (x.CancellationRequested || x.Attempts >= x.MaxAttempts)) ||
            db.Jobs.Any(x => x.CancellationRequested && (x.State == JobState.Pending || x.State == JobState.Blocked))))
            .SingleAsync(ct);
    }

    private IQueryable<Job> Eligible(RunnerDb db, DateTime now, NodeCapabilities? capabilities, string[] compatible, string worker) =>
        db.Jobs.AsNoTracking().Where(x => !x.CancellationRequested && x.Attempts < x.MaxAttempts &&
            (capabilities == null || (db.WorkerNodes.Any(n => n.Id == worker && !n.DrainRequested) && capabilities.Handlers.Contains(x.Type) && capabilities.Queues.Contains(x.Queue) &&
                (x.PackageId == null || compatible.Contains(x.PackageId + "@" + x.PackageVersion)))) &&
            (x.PackageId == null || (packagesEnabled && db.PluginChannels.Any(p => p.PackageId == x.PackageId && p.Enabled &&
                (p.ExecutionMode == PluginExecutionMode.Parallel || p.ExecutionLeaseUtc == null || p.ExecutionLeaseUtc <= now) &&
                (p.MaxConcurrency == null || db.Jobs.Count(j => j.PackageId == p.PackageId && j.State == JobState.Running && j.LeaseUntilUtc > now) < p.MaxConcurrency)))) &&
            ((x.State == JobState.Pending && x.AvailableUtc <= now) || (x.State == JobState.Running && x.LeaseUntilUtc <= now)));

    private async Task<Job?> ClaimCoreAsync(string worker, DateTime now, TimeSpan lease, NodeCapabilities? capabilities,
        string[] compatible, bool finalize, CancellationToken ct)
    {
        await foreach (var job in ClaimWindowAsync(worker, () => now, lease, capabilities, compatible, finalize, 1, ct)) return job;
        return null;
    }

    private async IAsyncEnumerable<Job> ClaimWindowAsync(string worker, Func<DateTime> clockNow, TimeSpan lease, NodeCapabilities? capabilities,
        string[] compatible, bool finalize, int capacity, [EnumeratorCancellation] CancellationToken ct, Func<bool>? mayClaim = null)
    {
        if (capacity is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(capacity));
        var now = clockNow();
        await using var db = await factory.CreateDbContextAsync(ct);
        if (finalize) await FinalizeExpiredAsync(db, now, false, ct);
        var ids = await Eligible(db, now, capabilities, compatible, worker).OrderBy(x => x.PriorityUtc).ThenBy(x => x.CreatedUtc).ThenBy(x => x.Id)
            .Select(x => x.Id).Take(64).ToListAsync(ct);
        if (ids.Count == 0) yield break;
        // Reuse one pooled connection only while filling this bounded window, never during idle waits.
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var bodies = new Dictionary<Guid, Job>();
            var blockedPackages = new HashSet<string>(StringComparer.Ordinal);
            var claimed = 0;
            for (var index = 0; index < ids.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                if (mayClaim?.Invoke() == false) yield break;
                if (bodies.Count == 0)
                {
                    // Fetch bodies only for currently free slots (at most eight per read), not all
                    // 64 contention candidates. Large payloads must not scale with the search window.
                    var batch = ids.Skip(index).Take(Math.Min(8, capacity - claimed)).ToArray();
                    bodies = await db.Jobs.AsNoTracking().Where(x => batch.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
                }
                if (!bodies.Remove(ids[index], out var candidate)) continue;
                now = clockNow();
                if (candidate.PackageId is { } blockedId && blockedPackages.Contains(blockedId)) continue;
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var id = candidate.Id;
                PluginChannel? channel = null;
                if (candidate.PackageId is { } packageId)
                {
                    if (await PackageConcurrency.LockAsync(db, packageId, ct) == 0) continue;
                    channel = await db.PluginChannels.AsNoTracking().SingleAsync(x => x.PackageId == packageId, ct);
                    if (!channel.Enabled || (channel.ExecutionMode == PluginExecutionMode.Sequential && channel.ExecutionLeaseUtc > now) ||
                        (channel.MaxConcurrency is { } limit && await db.Jobs.CountAsync(x => x.PackageId == packageId && x.State == JobState.Running && x.LeaseUntilUtc > now, ct) >= limit))
                    { blockedPackages.Add(packageId); continue; }
                }
                var token = Guid.NewGuid();
                if (mayClaim?.Invoke() == false) yield break;
                now = clockNow(); // A contended channel lock must not consume the new job's lease budget.
                // The snapshot is only a hint. Recheck all placement/policy/ownership predicates in SQL,
                // including its attempt/state/token, before using the immutable job body from that read.
                var count = await Eligible(db, now, capabilities, compatible, worker).Where(x => x.Id == id &&
                        x.Attempts == candidate.Attempts && x.State == candidate.State && x.LeaseToken == candidate.LeaseToken)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, JobState.Running)
                        .SetProperty(x => x.LeaseToken, token).SetProperty(x => x.LeaseUntilUtc, now + lease)
                        .SetProperty(x => x.ResultJson, (string?)null).SetProperty(x => x.Worker, worker).SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);
                if (count == 1)
                {
                    if (channel?.ExecutionMode == PluginExecutionMode.Sequential)
                        await db.PluginChannels.Where(x => x.PackageId == candidate.PackageId)
                            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExecutionToken, token).SetProperty(x => x.ExecutionLeaseUtc, now + lease), ct);
                    var job = candidate;
                    if (job.State == JobState.Running && job.LeaseToken is { } abandoned)
                        await ExecutionJournal.FinishAttemptAsync(db, abandoned, now, "LeaseExpired", "lease_expired", ct);
                    job.State = JobState.Running; job.LeaseToken = token; job.LeaseUntilUtc = now + lease;
                    job.ResultJson = null; job.Worker = worker; job.Attempts++;
                    db.Attempts.Add(new ExecutionAttempt { Id = token, JobId = id, JobType = job.Type, Attempt = job.Attempts, Worker = worker, StartedUtc = now });
                    ExecutionJournal.AddLog(db, job, "Execution started", now);
                    await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
                    db.ChangeTracker.Clear();
                    yield return job;
                    if (++claimed >= capacity) yield break;
                    continue;
                }
                await transaction.RollbackAsync(ct);
            }
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    public async Task<bool> RenewAsync(Guid id, Guid token, DateTime now, TimeSpan lease, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var packageId = await db.Jobs.Where(x => x.Id == id).Select(x => x.PackageId).SingleOrDefaultAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (packageId is not null)
        {
            await PackageConcurrency.LockAsync(db, packageId, ct);
            if (!await PackageConcurrency.OwnsLeaseAsync(db, packageId, token, now, ct)) return false;
        }
        var renewed = await db.Jobs.Where(x => x.Id == id && x.State == JobState.Running && x.LeaseToken == token &&
                x.LeaseUntilUtc > now && !x.CancellationRequested)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntilUtc, now + lease), ct) == 1;
        if (!renewed) return false;
        if (packageId is not null)
            await db.PluginChannels.Where(x => x.PackageId == packageId && x.ExecutionToken == token)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExecutionLeaseUtc, now + lease), ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> CompleteAsync(Job job, DateTime now, string? errorCode, CancellationToken ct, string? resultJson = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (job.PackageId is not null)
        {
            await PackageConcurrency.LockAsync(db, job.PackageId, ct);
            if (!await PackageConcurrency.OwnsLeaseAsync(db, job.PackageId, job.LeaseToken!.Value, now, ct)) return false;
        }
        var state = errorCode is null ? JobState.Succeeded : job.Attempts >= job.MaxAttempts ? JobState.DeadLetter : JobState.Pending;
        var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(job.Attempts, 8))) + Random.Shared.NextDouble() * 3);
        var completed = await db.Jobs.Where(x => x.Id == job.Id && x.State == JobState.Running &&
                x.LeaseToken == job.LeaseToken && x.LeaseUntilUtc > now && !x.CancellationRequested)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, state).SetProperty(x => x.ErrorCode, errorCode)
                .SetProperty(x => x.ResultJson, errorCode == null ? resultJson : null).SetProperty(x => x.AvailableUtc, now + delay)
                .SetProperty(x => x.FinishedUtc, state == JobState.Pending ? (DateTime?)null : now)
                .SetProperty(x => x.LeaseToken, (Guid?)null).SetProperty(x => x.LeaseUntilUtc, (DateTime?)null), ct) == 1;
        if (!completed) return false;
        if (job.PackageId is not null) await PackageConcurrency.ReleaseAsync(db, job.PackageId, job.LeaseToken, ct);
        var outcome = state == JobState.Pending ? "RetryScheduled" : state.ToString();
        await ExecutionJournal.FinishAttemptAsync(db, job.LeaseToken!.Value, now, outcome, errorCode, ct);
        ExecutionJournal.AddLog(db, job, errorCode is null ? "Execution succeeded" : $"Execution {outcome}: {errorCode}", now, errorCode is null ? "Information" : "Error");
        if (state == JobState.DeadLetter) ExecutionJournal.AddAlert(db, job, now, errorCode ?? "handler_failed");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (state == JobState.DeadLetter) alerts?.Pulse();
        return true;
    }

    public async Task<int> DispatchSchedulesAsync(DateTime now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var due = await db.Schedules.AsNoTracking().Where(x => x.Enabled && x.NextUtc <= now && (x.PackageId == null || (packagesEnabled && db.PluginChannels.Any(p => p.PackageId == x.PackageId && p.Enabled)))).OrderBy(x => x.NextUtc).Take(50).ToListAsync(ct);
        int count = 0;
        foreach (var schedule in due)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var occurrence = schedule.MisfirePolicy == "CatchUp" ? schedule.NextUtc : now;
            var next = CalendarSchedule.Next(schedule, occurrence);
            var emit = schedule.MisfirePolicy != "Skip" || now - schedule.NextUtc <= TimeSpan.FromSeconds(60);
            await Admission.LockAsync(db, ct);
            if (emit && !await Admission.HasCapacityAsync(db, 1, runner?.Value.MaxPendingJobs ?? 100000, ct)) continue;
            if (await db.Schedules.Where(x => x.Id == schedule.Id && x.Enabled && x.NextUtc == schedule.NextUtc &&
                (x.PackageId == null || (packagesEnabled && db.PluginChannels.Any(p => p.PackageId == x.PackageId && p.Enabled))))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextUtc, next), ct) == 1 && emit)
            {
                db.Jobs.Add(new Job { CreatedUtc = now, Type = schedule.Type, Payload = schedule.Payload, PackageId = schedule.PackageId, PackageVersion = schedule.PackageVersion,
                    IdempotencyKey = $"schedule:{schedule.Id}:{schedule.NextUtc.Ticks}", AvailableUtc = now, PriorityUtc = now });
                await db.SaveChangesAsync(ct); count++;
            }
            await tx.CommitAsync(ct);
        }
        return count;
    }

    public async Task<bool> MaintainAsync(DateTime now, int retentionDays, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await FinalizeExpiredAsync(db, now, true, ct);
        await FinalizeExpiredAsync(db, now, false, ct);
        var cutoff = now.AddDays(-retentionDays);
        var logs = await db.JobLogs.Where(x => x.TimestampUtc < cutoff).OrderBy(x => x.TimestampUtc).Select(x => x.Id).Take(1000).ToArrayAsync(ct);
        await db.JobLogs.Where(x => logs.Contains(x.Id)).ExecuteDeleteAsync(ct);
        var attempts = await db.Attempts.Where(x => x.FinishedUtc < cutoff).OrderBy(x => x.FinishedUtc).Select(x => x.Id).Take(1000).ToArrayAsync(ct);
        await db.Attempts.Where(x => attempts.Contains(x.Id)).ExecuteDeleteAsync(ct);
        var alerts = await db.Alerts.Where(x => x.CreatedUtc < cutoff && x.Acknowledged && (x.DeliveryState == "Delivered" || x.DeliveryState == "Parked")).OrderBy(x => x.CreatedUtc).Select(x => x.Id).Take(1000).ToArrayAsync(ct);
        await db.Alerts.Where(x => alerts.Contains(x.Id)).ExecuteDeleteAsync(ct);
        var jobs = await db.Jobs.Where(x => x.WorkflowId == null && x.FinishedUtc < cutoff && (x.State == JobState.Succeeded || x.State == JobState.Cancelled))
            .OrderBy(x => x.FinishedUtc).Select(x => x.Id).Take(1000).ToArrayAsync(ct);
        await db.Jobs.Where(x => jobs.Contains(x.Id)).ExecuteDeleteAsync(ct);
        return logs.Length == 1000 || attempts.Length == 1000 || alerts.Length == 1000 || jobs.Length == 1000;
    }
    public async Task WriteLogAsync(Job job, string level, string message, CancellationToken ct)
    {
        if (level is not ("Information" or "Warning" or "Error")) throw new ArgumentException("Invalid log level");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (job.PackageId is not null)
        {
            await PackageConcurrency.LockAsync(db, job.PackageId, ct);
            if (!await PackageConcurrency.OwnsLeaseAsync(db, job.PackageId, job.LeaseToken!.Value, UtcNow, ct))
                throw new OperationCanceledException("Plugin lease is no longer owned");
        }
        // A conditional no-op update serializes this write with ownership changes and fences stale writers.
        var owned = await db.Jobs.Where(x => x.Id == job.Id && x.State == JobState.Running && x.LeaseToken == job.LeaseToken &&
            x.LeaseUntilUtc > UtcNow && !x.CancellationRequested)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Worker, x => x.Worker), ct);
        if (owned != 1) throw new OperationCanceledException("Job lease is no longer owned");
        // Bound business log volume per attempt. Lifecycle entries remain independent.
        if (await db.JobLogs.CountAsync(x => x.LeaseToken == job.LeaseToken, ct) >= 200) return;
        ExecutionJournal.AddLog(db, job, message, UtcNow, level);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    }

    public async Task RecoverAsync(DateTime now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await FinalizeExpiredAsync(db, now, false, ct);
        await FinalizeExpiredAsync(db, now, true, ct);
    }

    private static async Task FinalizeExpiredAsync(RunnerDb db, DateTime now, bool cancellation, CancellationToken ct)
    {
        var candidates = await db.Jobs.AsNoTracking().Where(x => cancellation
            ? x.CancellationRequested && ((x.State == JobState.Pending || x.State == JobState.Blocked) || (x.State == JobState.Running && x.LeaseUntilUtc <= now))
            : !x.CancellationRequested && x.State == JobState.Running && x.LeaseUntilUtc <= now && x.Attempts >= x.MaxAttempts)
            .OrderBy(x => x.CreatedUtc).Take(100).ToListAsync(ct);
        foreach (var job in candidates)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (job.PackageId is not null) await PackageConcurrency.LockAsync(db, job.PackageId, ct);
            var state = cancellation ? JobState.Cancelled : JobState.DeadLetter;
            var error = cancellation ? "cancelled" : "lease_expired";
            var count = await db.Jobs.Where(x => x.Id == job.Id && x.State == job.State && x.LeaseToken == job.LeaseToken &&
                x.CancellationRequested == cancellation && (x.State == JobState.Pending || x.State == JobState.Blocked || x.LeaseUntilUtc <= now))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, state).SetProperty(x => x.FinishedUtc, now)
                    .SetProperty(x => x.ErrorCode, error).SetProperty(x => x.LeaseToken, (Guid?)null).SetProperty(x => x.LeaseUntilUtc, (DateTime?)null), ct);
            if (count == 1)
            {
                if (job.PackageId is not null) await PackageConcurrency.ReleaseAsync(db, job.PackageId, job.LeaseToken, ct);
                if (job.LeaseToken is { } token) await ExecutionJournal.FinishAttemptAsync(db, token, now, state.ToString(), error, ct);
                ExecutionJournal.AddLog(db, job, $"Execution {state}: {error}", now, cancellation ? "Warning" : "Error");
                if (!cancellation) ExecutionJournal.AddAlert(db, job, now, error);
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
    }

}
