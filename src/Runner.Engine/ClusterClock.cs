using Microsoft.EntityFrameworkCore;
namespace Runner.Engine;

/// <summary>One DB clock sample per node heartbeat; lease time advances using a monotonic local timestamp.</summary>
public sealed class ClusterClock(IDbContextFactory<RunnerDb> factory, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly object gate = new();
    private DateTime anchorUtc;
    private long anchorTimestamp;
    public DateTime UtcNow
    {
        get { lock (gate) return anchorUtc == default ? time.GetUtcNow().UtcDateTime : anchorUtc + time.GetElapsedTime(anchorTimestamp); }
    }
    public async Task SynchronizeAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var started = time.GetTimestamp();
        var sql = db.Database.IsSqlServer() ? "SELECT SYSUTCDATETIME() AS [Value]" : db.Database.IsNpgsql()
            ? "SELECT clock_timestamp() AS \"Value\"" : "SELECT strftime('%Y-%m-%d %H:%M:%f', 'now') AS \"Value\"";
        var sample = await db.Database.SqlQueryRaw<DateTime>(sql).SingleAsync(ct);
        var elapsed = time.GetElapsedTime(started);
        if (elapsed > TimeSpan.FromSeconds(2)) throw new InvalidOperationException("Database clock sample exceeded the two-second uncertainty budget");
        lock (gate)
        {
            anchorUtc = DateTime.SpecifyKind(sample, DateTimeKind.Utc) + elapsed / 2;
            anchorTimestamp = time.GetTimestamp();
        }
    }
}
