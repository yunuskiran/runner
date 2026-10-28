using Microsoft.Extensions.Options;
using Runner.Engine;
namespace Runner.Host;

public sealed class ApiClockMaintenance(ClusterClock clock, IOptions<RunnerOptions> options, ILogger<ApiClockMaintenance> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (options.Value.WorkerEnabled) return;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(options.Value.NodeHeartbeatSeconds), ct); await clock.SynchronizeAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogError(e, "API clock synchronization failed"); }
        }
    }
}
