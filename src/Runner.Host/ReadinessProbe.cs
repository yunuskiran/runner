using Microsoft.EntityFrameworkCore;
using Runner.Engine;
namespace Runner.Host;

public sealed class ReadinessProbe(IDbContextFactory<RunnerDb> factory)
{
    private readonly SemaphoreSlim gate = new(1);
    private DateTime until;
    private bool ready;
    public async Task<bool> ReadyAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (until > DateTime.UtcNow) return ready;
            try
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                // Schema binding without scans or result data; one round trip, cached for 15 seconds.
                await db.Database.ExecuteSqlRawAsync("""
                    SELECT j."PriorityUtc", j."WorkflowId", j."ResultJson", j."Queue", j."Priority", j."StepKey",
                      p."ManifestJson", p."DefaultExecutionMode", c."MaxConcurrency", c."ExecutionMode", c."ExecutionToken", c."ExecutionLeaseUtc",
                      n."CapabilitiesJson", n."DrainRequested", d."DependsOnJobId", w."Name", a."StatusCode", l."Owner", f."SourceHash", t."Outcome", g."Level", z."DeliveryState", s."Cron", s."TimeZoneId", h."Revision", h."MaxConcurrency", h."PreviousMaxConcurrency", a."DetailsJson"
                    FROM "Jobs" j LEFT JOIN "PluginPackages" p ON 1=0 LEFT JOIN "PluginChannels" c ON 1=0
                    LEFT JOIN "WorkerNodes" n ON 1=0 LEFT JOIN "JobDependencies" d ON 1=0 LEFT JOIN "Workflows" w ON 1=0
                    LEFT JOIN "AuditRecords" a ON 1=0 LEFT JOIN "ClusterLeases" l ON 1=0 LEFT JOIN "Functions" f ON 1=0
                    LEFT JOIN "Attempts" t ON 1=0 LEFT JOIN "JobLogs" g ON 1=0 LEFT JOIN "Alerts" z ON 1=0
                    LEFT JOIN "Schedules" s ON 1=0 LEFT JOIN "PackageChanges" h ON 1=0 WHERE 1=0
                    """, ct);
                ready = true;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { ready = false; }
            until = DateTime.UtcNow.AddSeconds(ready ? 15 : 1); return ready;
        }
        finally { gate.Release(); }
    }
}
