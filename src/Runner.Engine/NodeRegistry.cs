using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Runner.Engine;

public sealed class NodeRegistry(IDbContextFactory<RunnerDb> factory, IOptions<RunnerOptions> options,
    IOptions<OperationOptions> operations, HandlerCatalog catalog, ClusterClock? clock = null)
{
    public DateTime UtcNow => clock?.UtcNow ?? DateTime.UtcNow;
    public string Id { get; } = $"{(options.Value.InstanceName ?? Environment.MachineName)[..Math.Min(80, (options.Value.InstanceName ?? Environment.MachineName).Length)]}:{Guid.NewGuid():N}";
    public NodeCapabilities Capabilities { get; } = NodeCapabilities.Create(operations.Value, options.Value, catalog);
    public volatile bool Draining;
    public DateTime? DrainDeadlineUtc { get; private set; }
    private string[] compatiblePackages = [];
    public string[] CompatiblePackages => Volatile.Read(ref compatiblePackages);
    private string? refreshedStamp;
    private bool packagesLoaded;

    public async Task HeartbeatAsync(int active, DateTime now, CancellationToken ct)
    {
        if (clock is not null) { await clock.SynchronizeAsync(ct); now = clock.UtcNow; }
        await using var db = await factory.CreateDbContextAsync(ct);
        // Read drain state and the small package-registry marker in one request per heartbeat.
        var snapshot = await (from marker in db.ClusterLeases.AsNoTracking()
            where marker.Name == "packages"
            join worker in db.WorkerNodes on Id equals worker.Id into workers
            from nodeRow in workers.DefaultIfEmpty()
            select new { marker.Owner, Node = nodeRow }).SingleAsync(ct);
        var node = snapshot.Node;
        if (node is null)
        {
            db.WorkerNodes.Add(new WorkerNode { Id = Id, Name = options.Value.InstanceName ?? Environment.MachineName,
                StartedUtc = now, HeartbeatUtc = now, ActiveJobs = active, Concurrency = options.Value.Concurrency,
                CapabilitiesJson = JsonSerializer.Serialize(Capabilities, JsonSerializerOptions.Web) });
            await db.SaveChangesAsync(ct);
        }
        else
        {
            Draining = node.DrainRequested;
            DrainDeadlineUtc = node.DrainDeadlineUtc;
            await db.WorkerNodes.Where(x => x.Id == Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.HeartbeatUtc, now)
                .SetProperty(x => x.ActiveJobs, active).SetProperty(x => x.DrainAcknowledgedUtc, Draining ? now : (DateTime?)null), ct);
        }
        await RefreshPackagesAsync(db, snapshot.Owner, ct);
    }

    public async Task RefreshPackagesAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var stamp = await db.ClusterLeases.Where(x => x.Name == "packages").Select(x => x.Owner).SingleAsync(ct);
        await RefreshPackagesAsync(db, stamp, ct);
    }

    private async Task RefreshPackagesAsync(RunnerDb db, string? stamp, CancellationToken ct)
    {
        if (packagesLoaded && stamp == refreshedStamp) return;
        var versions = await db.PluginPackages.AsNoTracking().OrderBy(x => x.PackageId).ThenBy(x => x.Version)
            .Select(x => new { x.PackageId, x.Version, x.ManifestJson }).Take(10001).ToListAsync(ct);
        if (versions.Count > 10000) throw new InvalidOperationException("Package registry exceeds the supported 10000-version placement limit");
        Volatile.Write(ref compatiblePackages, versions.Where(x => PluginManifest.Parse(x.ManifestJson).Incompatibilities(Capabilities).Length == 0)
            .Select(x => x.PackageId + "@" + x.Version).ToArray());
        refreshedStamp = stamp; packagesLoaded = true;
    }

    public async Task<bool> LeadAsync(DateTime now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var until = now.AddSeconds(Math.Max(30, options.Value.NodeHeartbeatSeconds * 3));
        if (await db.ClusterLeases.Where(x => x.Name == "scheduler" && (x.Owner == Id || x.UntilUtc <= now))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Owner, Id).SetProperty(x => x.UntilUtc, until), ct) == 1) return true;
        if (await db.ClusterLeases.AnyAsync(x => x.Name == "scheduler", ct)) return false;
        db.ClusterLeases.Add(new ClusterLease { Name = "scheduler", Owner = Id, UntilUtc = until });
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.ClusterLeases.AnyAsync(x => x.Name == "scheduler", ct)) return false;
            throw;
        }
    }
}
