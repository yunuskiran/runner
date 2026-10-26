using Microsoft.EntityFrameworkCore;
namespace Runner.Engine;

public interface IJobStore
{
    Task<Job?> ClaimAsync(string worker, DateTime now, TimeSpan lease, CancellationToken ct);
    Task<bool> RenewAsync(Guid id, Guid token, DateTime now, TimeSpan lease, CancellationToken ct);
    Task<bool> CompleteAsync(Job job, DateTime now, string? errorCode, CancellationToken ct, string? resultJson = null);
}

/// <summary>All ownership changes use conditional SQL updates; no process-local locks are required.</summary>
public sealed class JobStore(IDbContextFactory<RunnerDb> factory) : IJobStore
{
    public async Task<Job?> ClaimAsync(string worker, DateTime now, TimeSpan lease, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // A crashed final attempt must become dead-letter rather than remain running forever.
        await db.Jobs.Where(x => x.State == JobState.Running && x.LeaseUntilUtc <= now && x.Attempts >= x.MaxAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, JobState.DeadLetter)
                .SetProperty(x => x.ErrorCode, "lease_expired").SetProperty(x => x.FinishedUtc, now)
                .SetProperty(x => x.LeaseToken, (Guid?)null).SetProperty(x => x.LeaseUntilUtc, (DateTime?)null), ct);
        var ids = await db.Jobs.AsNoTracking().Where(x => !x.CancellationRequested && x.Attempts < x.MaxAttempts &&
            ((x.State == JobState.Pending && x.AvailableUtc <= now) ||
             (x.State == JobState.Running && x.LeaseUntilUtc <= now)))
            .OrderBy(x => x.AvailableUtc).Select(x => x.Id).Take(16).ToListAsync(ct);
        foreach (var id in ids)
        {
            var token = Guid.NewGuid();
            var count = await db.Jobs.Where(x => x.Id == id && !x.CancellationRequested && x.Attempts < x.MaxAttempts &&
                ((x.State == JobState.Pending && x.AvailableUtc <= now) || (x.State == JobState.Running && x.LeaseUntilUtc <= now)))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, JobState.Running)
                    .SetProperty(x => x.LeaseToken, token).SetProperty(x => x.LeaseUntilUtc, now + lease)
                    .SetProperty(x => x.ResultJson, (string?)null).SetProperty(x => x.Worker, worker).SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);
            if (count == 1) return await db.Jobs.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        }
        return null;
    }

    public async Task<bool> RenewAsync(Guid id, Guid token, DateTime now, TimeSpan lease, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Jobs.Where(x => x.Id == id && x.State == JobState.Running && x.LeaseToken == token &&
                x.LeaseUntilUtc > now && !x.CancellationRequested)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntilUtc, now + lease), ct) == 1;
    }

    public async Task<bool> CompleteAsync(Job job, DateTime now, string? errorCode, CancellationToken ct, string? resultJson = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var state = errorCode is null ? JobState.Succeeded : job.Attempts >= job.MaxAttempts ? JobState.DeadLetter : JobState.Pending;
        var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(job.Attempts, 8))) + Random.Shared.NextDouble() * 3);
        return await db.Jobs.Where(x => x.Id == job.Id && x.State == JobState.Running &&
                x.LeaseToken == job.LeaseToken && x.LeaseUntilUtc > now && !x.CancellationRequested)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, state).SetProperty(x => x.ErrorCode, errorCode)
                .SetProperty(x => x.ResultJson, errorCode == null ? resultJson : null).SetProperty(x => x.AvailableUtc, now + delay)
                .SetProperty(x => x.FinishedUtc, state == JobState.Pending ? (DateTime?)null : now)
                .SetProperty(x => x.LeaseToken, (Guid?)null).SetProperty(x => x.LeaseUntilUtc, (DateTime?)null), ct) == 1;
    }

    public async Task<int> DispatchSchedulesAsync(DateTime now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var due = await db.Schedules.AsNoTracking().Where(x => x.Enabled && x.NextUtc <= now).OrderBy(x => x.NextUtc).Take(50).ToListAsync(ct);
        int count = 0;
        foreach (var schedule in due)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Coalesce missed ticks into one job. The compare-and-swap and insert commit together.
            if (await db.Schedules.Where(x => x.Id == schedule.Id && x.Enabled && x.NextUtc == schedule.NextUtc)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextUtc, now.AddSeconds(schedule.IntervalSeconds)), ct) == 1)
            {
                db.Jobs.Add(new Job { Type = schedule.Type, Payload = schedule.Payload,
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
        await db.Jobs.Where(x => x.CancellationRequested && (x.State == JobState.Pending ||
            (x.State == JobState.Running && x.LeaseUntilUtc <= now)))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, JobState.Cancelled).SetProperty(x => x.FinishedUtc, now)
                .SetProperty(x => x.LeaseToken, (Guid?)null).SetProperty(x => x.LeaseUntilUtc, (DateTime?)null), ct);
        var cutoff = now.AddDays(-retentionDays);
        await db.Jobs.Where(x => x.FinishedUtc < cutoff && (x.State == JobState.Succeeded || x.State == JobState.Cancelled))
            .ExecuteDeleteAsync(ct);
    }
}
