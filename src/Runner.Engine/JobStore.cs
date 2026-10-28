using Microsoft.EntityFrameworkCore;
using Runner.Contracts;
using Microsoft.Extensions.Options;
using Runner.Engine.Packaging;
namespace Runner.Engine;

public interface IJobStore
{
    Task<Job?> ClaimAsync(string worker, DateTime now, TimeSpan lease, CancellationToken ct);
    Task<bool> RenewAsync(Guid id, Guid token, DateTime now, TimeSpan lease, CancellationToken ct);
    Task<bool> CompleteAsync(Job job, DateTime now, string? errorCode, CancellationToken ct, string? resultJson = null);
}

/// <summary>All ownership changes use conditional SQL updates; no process-local locks are required.</summary>
public sealed class JobStore(IDbContextFactory<RunnerDb> factory, IOptions<PackageOptions>? packages = null) : IJobStore
{
    private readonly bool packagesEnabled = packages?.Value.Enabled ?? true;
    public async Task<Job?> ClaimAsync(string worker, DateTime now, TimeSpan lease, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await FinalizeExpiredAsync(db, now, false, ct);
        var ids = await db.Jobs.AsNoTracking().Where(x => !x.CancellationRequested && x.Attempts < x.MaxAttempts &&
            (x.PackageId == null || (packagesEnabled && db.PluginChannels.Any(p => p.PackageId == x.PackageId && p.Enabled &&
                (p.ExecutionMode == PluginExecutionMode.Parallel || p.ExecutionLeaseUtc == null || p.ExecutionLeaseUtc <= now)))) &&
            ((x.State == JobState.Pending && x.AvailableUtc <= now) ||
             (x.State == JobState.Running && x.LeaseUntilUtc <= now)))
            .OrderBy(x => x.AvailableUtc).ThenBy(x => x.CreatedUtc).ThenBy(x => x.Id).Select(x => new { x.Id, x.PackageId }).Take(64).ToListAsync(ct);
        var blockedPackages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in ids)
        {
            if (candidate.PackageId is { } blockedId && blockedPackages.Contains(blockedId)) continue;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var id = candidate.Id;
            PluginChannel? channel = null;
            if (candidate.PackageId is { } packageId)
            {
                if (await PackageConcurrency.LockAsync(db, packageId, ct) == 0) continue;
                channel = await db.PluginChannels.AsNoTracking().SingleAsync(x => x.PackageId == packageId, ct);
                if (!channel.Enabled || (channel.ExecutionMode == PluginExecutionMode.Sequential && channel.ExecutionLeaseUtc > now)) { blockedPackages.Add(packageId); continue; }
            }
            var token = Guid.NewGuid();
            var count = await db.Jobs.Where(x => x.Id == id && !x.CancellationRequested && x.Attempts < x.MaxAttempts &&
            (x.PackageId == null || (packagesEnabled && db.PluginChannels.Any(p => p.PackageId == x.PackageId && p.Enabled &&
                (p.ExecutionMode == PluginExecutionMode.Parallel || p.ExecutionLeaseUtc == null || p.ExecutionLeaseUtc <= now)))) &&
                ((x.State == JobState.Pending && x.AvailableUtc <= now) || (x.State == JobState.Running && x.LeaseUntilUtc <= now)))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, JobState.Running)
                    .SetProperty(x => x.LeaseToken, token).SetProperty(x => x.LeaseUntilUtc, now + lease)
                    .SetProperty(x => x.ResultJson, (string?)null).SetProperty(x => x.Worker, worker).SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);
            if (count == 1)
            {
                if (channel?.ExecutionMode == PluginExecutionMode.Sequential)
                    await db.PluginChannels.Where(x => x.PackageId == candidate.PackageId)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExecutionToken, token).SetProperty(x => x.ExecutionLeaseUtc, now + lease), ct);
                var job = await db.Jobs.AsNoTracking().SingleAsync(x => x.Id == id, ct);
                var abandoned = await db.Attempts.AsNoTracking().Where(x => x.JobId == id && x.Outcome == "Running").Select(x => x.Id).ToListAsync(ct);
                foreach (var old in abandoned) await ExecutionJournal.FinishAttemptAsync(db, old, now, "LeaseExpired", "lease_expired", ct);
                db.Attempts.Add(new ExecutionAttempt { Id = token, JobId = id, JobType = job.Type, Attempt = job.Attempts, Worker = worker, StartedUtc = now });
                ExecutionJournal.AddLog(db, job, "Execution started", now);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return job;
            }
            await transaction.RollbackAsync(ct);
        }
        return null;
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
            // Coalesce missed ticks into one job. The compare-and-swap and insert commit together.
            if (await db.Schedules.Where(x => x.Id == schedule.Id && x.Enabled && x.NextUtc == schedule.NextUtc &&
                (x.PackageId == null || (packagesEnabled && db.PluginChannels.Any(p => p.PackageId == x.PackageId && p.Enabled))))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextUtc, now.AddSeconds(schedule.IntervalSeconds)), ct) == 1)
            {
                db.Jobs.Add(new Job { Type = schedule.Type, Payload = schedule.Payload, PackageId = schedule.PackageId, PackageVersion = schedule.PackageVersion,
                    IdempotencyKey = $"schedule:{schedule.Id}:{schedule.NextUtc.Ticks}", AvailableUtc = now });
                await db.SaveChangesAsync(ct);
                count++;
            }
            await tx.CommitAsync(ct);
        }
        return count;
    }

    public async Task MaintainAsync(DateTime now, int retentionDays, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await FinalizeExpiredAsync(db, now, true, ct);
        var cutoff = now.AddDays(-retentionDays);
        await db.JobLogs.Where(x => x.TimestampUtc < cutoff).ExecuteDeleteAsync(ct);
        await db.Attempts.Where(x => x.FinishedUtc < cutoff).ExecuteDeleteAsync(ct);
        await db.Alerts.Where(x => x.CreatedUtc < cutoff && x.Acknowledged && (x.DeliveryState == "Delivered" || x.DeliveryState == "Parked")).ExecuteDeleteAsync(ct);
        await db.Jobs.Where(x => x.FinishedUtc < cutoff && (x.State == JobState.Succeeded || x.State == JobState.Cancelled))
            .ExecuteDeleteAsync(ct);
    }
    public async Task WriteLogAsync(Job job, string level, string message, CancellationToken ct)
    {
        if (level is not ("Information" or "Warning" or "Error")) throw new ArgumentException("Invalid log level");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (job.PackageId is not null)
        {
            await PackageConcurrency.LockAsync(db, job.PackageId, ct);
            if (!await PackageConcurrency.OwnsLeaseAsync(db, job.PackageId, job.LeaseToken!.Value, DateTime.UtcNow, ct))
                throw new OperationCanceledException("Plugin lease is no longer owned");
        }
        // A conditional no-op update serializes this write with ownership changes and fences stale writers.
        var owned = await db.Jobs.Where(x => x.Id == job.Id && x.State == JobState.Running && x.LeaseToken == job.LeaseToken &&
            x.LeaseUntilUtc > DateTime.UtcNow && !x.CancellationRequested)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Worker, x => x.Worker), ct);
        if (owned != 1) throw new OperationCanceledException("Job lease is no longer owned");
        // Bound business log volume per attempt. Lifecycle entries remain independent.
        if (await db.JobLogs.CountAsync(x => x.LeaseToken == job.LeaseToken, ct) >= 200) return;
        ExecutionJournal.AddLog(db, job, message, DateTime.UtcNow, level);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    }

    private static async Task FinalizeExpiredAsync(RunnerDb db, DateTime now, bool cancellation, CancellationToken ct)
    {
        var candidates = await db.Jobs.AsNoTracking().Where(x => cancellation
            ? x.CancellationRequested && (x.State == JobState.Pending || (x.State == JobState.Running && x.LeaseUntilUtc <= now))
            : !x.CancellationRequested && x.State == JobState.Running && x.LeaseUntilUtc <= now && x.Attempts >= x.MaxAttempts)
            .OrderBy(x => x.CreatedUtc).Take(100).ToListAsync(ct);
        foreach (var job in candidates)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (job.PackageId is not null) await PackageConcurrency.LockAsync(db, job.PackageId, ct);
            var state = cancellation ? JobState.Cancelled : JobState.DeadLetter;
            var error = cancellation ? "cancelled" : "lease_expired";
            var count = await db.Jobs.Where(x => x.Id == job.Id && x.State == job.State && x.LeaseToken == job.LeaseToken &&
                x.CancellationRequested == cancellation && (x.State == JobState.Pending || x.LeaseUntilUtc <= now))
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
