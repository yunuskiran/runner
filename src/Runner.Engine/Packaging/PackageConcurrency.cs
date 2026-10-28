using Microsoft.EntityFrameworkCore;
using Runner.Contracts;
namespace Runner.Engine.Packaging;

/// <summary>Short database row locks coordinate claims, policy changes and lease updates in one lock order.</summary>
public static class PackageConcurrency
{
    public static Task<int> LockAsync(RunnerDb db, string id, CancellationToken ct, int? revision = null) =>
        db.PluginChannels.Where(x => x.PackageId == id && (revision == null || x.Revision == revision))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Revision, x => x.Revision), ct);

    public static async Task<bool> OwnsLeaseAsync(RunnerDb db, string? id, Guid token, DateTime now, CancellationToken ct)
    {
        if (id is null) return true;
        return await db.PluginChannels.AnyAsync(x => x.PackageId == id && (x.ExecutionMode == PluginExecutionMode.Parallel ||
            (x.ExecutionToken == token && x.ExecutionLeaseUtc > now)), ct);
    }
    public static Task<int> ReleaseAsync(RunnerDb db, string? id, Guid? token, CancellationToken ct) =>
        db.PluginChannels.Where(x => x.PackageId == id && x.ExecutionToken == token)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExecutionToken, (Guid?)null).SetProperty(x => x.ExecutionLeaseUtc, (DateTime?)null), ct);
}
