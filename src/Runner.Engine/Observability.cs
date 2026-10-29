using Microsoft.EntityFrameworkCore;
using Runner.Contracts;
namespace Runner.Engine;

public sealed class ExecutionAttempt
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public string JobType { get; set; } = "";
    public int Attempt { get; set; }
    public string Worker { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public string Outcome { get; set; } = "Running";
    public double? DurationMilliseconds { get; set; }
    public string? ErrorCode { get; set; }
}
public sealed class JobLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public Guid LeaseToken { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Level { get; set; } = "Information";
    public string Message { get; set; } = "";
}
public sealed class JobAlert
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public string JobType { get; set; } = "";
    public string ErrorCode { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public bool Acknowledged { get; set; }
    public string DeliveryState { get; set; } = "Pending";
    public int DeliveryAttempts { get; set; }
    public DateTime NextDeliveryUtc { get; set; }
    public Guid? DeliveryToken { get; set; }
    public DateTime? DeliveryLeaseUtc { get; set; }
}

public static class ExecutionJournal
{
    public static void AddLog(RunnerDb db, Job job, string message, DateTime now, string level = "Information") =>
        db.JobLogs.Add(new JobLog { JobId = job.Id, LeaseToken = job.LeaseToken ?? Guid.Empty,
            TimestampUtc = now, Level = level, Message = message.Length <= 4000 ? message : message[..4000] + " [truncated]" });
    public static void AddAlert(RunnerDb db, Job job, DateTime now, string error) =>
        db.Alerts.Add(new JobAlert { Id = job.LeaseToken ?? job.Id, JobId = job.Id, JobType = job.Type,
            CreatedUtc = now, ErrorCode = error, NextDeliveryUtc = now });
    public static Task FinishAttemptAsync(RunnerDb db, Guid token, DateTime now, string outcome, string? error, CancellationToken ct)
    {
        // Calculate against the durable start time in the same conditional write: no read round trip,
        // no local elapsed-time guess, and no overflow for attempts recovered after a long outage.
        if (db.Database.IsSqlServer()) return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE [dbo].[Attempts] SET [Outcome]={outcome}, [ErrorCode]={error}, [FinishedUtc]={now},
              [DurationMilliseconds]=CASE WHEN [StartedUtc]>{now} THEN 0 ELSE DATEDIFF_BIG(microsecond,[StartedUtc],{now})/1000.0 END
            WHERE [Id]={token} AND [Outcome]='Running'
            """, ct);
        if (db.Database.IsNpgsql()) return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Attempts" SET "Outcome"={outcome}, "ErrorCode"={error}, "FinishedUtc"={now},
              "DurationMilliseconds"=GREATEST(0,EXTRACT(EPOCH FROM ({now}-"StartedUtc"))*1000)
            WHERE "Id"={token} AND "Outcome"='Running'
            """, ct);
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Attempts" SET "Outcome"={outcome}, "ErrorCode"={error}, "FinishedUtc"={now},
              "DurationMilliseconds"=MAX(0,(julianday({now})-julianday("StartedUtc"))*86400000)
            WHERE "Id"={token} AND "Outcome"='Running'
            """, ct);
    }
}

public sealed class FunctionLog(JobContext context)
{
    public Task InfoAsync(string message, CancellationToken ct = default) => context.WriteLogAsync?.Invoke("Information", message, ct) ?? Task.CompletedTask;
    public Task WarningAsync(string message, CancellationToken ct = default) => context.WriteLogAsync?.Invoke("Warning", message, ct) ?? Task.CompletedTask;
}
