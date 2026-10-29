using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Runner.Contracts;
namespace Runner.Engine;

public sealed class Worker(IServiceScopeFactory scopes, JobStore store, HandlerCatalog catalog,
    IOptions<RunnerOptions> options, ILogger<Worker> log, NodeRegistry nodes, QueueSignals signals, WorkflowService workflows) : BackgroundService
{
    public static readonly ActivitySource Activities = new("Runner");
    private static readonly Meter Meter = new("Runner");
    private static readonly Counter<long> Finished = Meter.CreateCounter<long>("runner.jobs.finished");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("runner.jobs.duration", "s");
    private readonly RunnerOptions config = options.Value;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> cancellations = new();
    private int active;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.WorkerEnabled) return;
        await nodes.HeartbeatAsync(0, nodes.UtcNow, stoppingToken);
        await Task.WhenAll(Control(stoppingToken), Dispatch(stoppingToken), Scheduler(stoppingToken));
    }

    private async Task Control(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var wasDraining = nodes.Draining; var versions = nodes.CompatiblePackages;
                await nodes.HeartbeatAsync(Volatile.Read(ref active), nodes.UtcNow, ct);
                if (wasDraining != nodes.Draining || !ReferenceEquals(versions, nodes.CompatiblePackages)) signals.Pulse();
                if (nodes.Draining && nodes.DrainDeadlineUtc <= nodes.UtcNow)
                    foreach (var cancellation in cancellations.Values)
                        try { await cancellation.CancelAsync(); } catch (ObjectDisposedException) { }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogError(e, "Node heartbeat failed; new claims paused"); nodes.Draining = true; }
            try { await Task.Delay(TimeSpan.FromSeconds(config.NodeHeartbeatSeconds), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private async Task Scheduler(CancellationToken ct)
    {
        var maintenance = DateTime.MinValue;
        var leadershipCheck = DateTime.MinValue;
        bool leader = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = nodes.UtcNow;
                if (now >= leadershipCheck)
                {
                    leader = await nodes.LeadAsync(now, ct);
                    leadershipCheck = now.AddSeconds(config.NodeHeartbeatSeconds);
                }
                if (leader)
                {
                    var work = await store.BackgroundWorkAsync(now, ct);
                    var dispatched = work.Schedules ? await store.DispatchSchedulesAsync(now, ct) : 0;
                    if (work.Workflows) dispatched += await workflows.AdvanceAsync(now, ct);
                    if (work.Recovery) await store.RecoverAsync(now, ct);
                    if (dispatched > 0) signals.Pulse();
                    if (now >= maintenance)
                    {
                        var backlog = await store.MaintainAsync(now, config.RetentionDays, ct);
                        maintenance = now.AddSeconds(backlog ? config.SchedulerSeconds : config.MaintenanceSeconds);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogError(e, "Schedule/maintenance cycle failed"); leader = false; leadershipCheck = DateTime.MinValue; }
            try { await Task.Delay(TimeSpan.FromSeconds(config.SchedulerSeconds), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private async Task Dispatch(CancellationToken ct)
    {
        var tasks = new List<Task>();
        var backoff = new IdleBackoff(config.PollMilliseconds, config.MaxIdlePollSeconds);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                tasks.RemoveAll(x => x.IsCompleted);
                if (nodes.Draining || tasks.Count >= config.Concurrency)
                { await signals.WaitAsync(TimeSpan.FromSeconds(1), ct); continue; }
                try
                {
                    var claimed = false;
                    await foreach (var job in store.ClaimAvailableForNodeAsync(nodes, config.Concurrency - tasks.Count, TimeSpan.FromSeconds(config.LeaseSeconds), ct))
                    {
                        claimed = true;
                        tasks.Add(RunTracked(job, ct));
                    }
                    if (claimed) { backoff.Reset(); continue; }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception e) { log.LogError(e, "Worker cycle failed"); }
                if (await signals.WaitAsync(backoff.Next(), ct)) backoff.Reset();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { await Task.WhenAll(tasks); }
    }

    private async Task RunTracked(Job job, CancellationToken ct)
    {
        Interlocked.Increment(ref active);
        try { await Run(job, ct); }
        catch (Exception e) { log.LogError(e, "Job finalization failed; lease recovery will retry"); }
        finally { Interlocked.Decrement(ref active); signals.Pulse(); }
    }

    private async Task Run(Job job, CancellationToken stopping)
    {
        using var activity = Activities.StartActivity("job.execute");
        activity?.SetTag("job.id", job.Id).SetTag("job.type", job.Type).SetTag("job.attempt", job.Attempts);
        using var scopeLog = log.BeginScope(new Dictionary<string, object> { ["JobId"] = job.Id, ["JobType"] = job.Type, ["Attempt"] = job.Attempts });
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        cancellations[job.Id] = execution;
        execution.CancelAfter(TimeSpan.FromSeconds(config.JobTimeoutSeconds));
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var heartbeat = Heartbeat(job, execution, heartbeatStop.Token);
        var timer = Stopwatch.StartNew();
        string? error = null;
        string? resultJson = null;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (!catalog.Types.TryGetValue(job.Type, out var type)) throw new InvalidOperationException("Unknown handler");
            var handler = (IJobHandler)scope.ServiceProvider.GetRequiredService(type);
            await handler.ExecuteAsync(new JobContext(job.Id, job.Payload, job.Attempts) { WriteLogAsync = (level, message, ct) => store.WriteLogAsync(job, level, message, ct), ReportResult = json =>
            {
                if (System.Text.Encoding.UTF8.GetByteCount(json) > 65536) throw new InvalidOperationException("Job result exceeds 64 KiB");
                using var parsed = System.Text.Json.JsonDocument.Parse(json);
                resultJson = json;
            } }, execution.Token);
            execution.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { error = stopping.IsCancellationRequested ? "shutdown" : "cancelled_or_timed_out"; }
        catch (JobExecutionException e) { error = e.Code; log.LogError("Job operation failed: {ErrorCode}", error); }
        catch (Exception e) { error = "handler_failed"; log.LogError("Job handler failed: {ErrorType}", e.GetType().Name); }
        finally
        {
            cancellations.TryRemove(job.Id, out _);
            await heartbeatStop.CancelAsync();
            try { await heartbeat; } catch (OperationCanceledException) { }
        }
        // On shutdown leave ownership to expire; another process can recover the job.
        if (stopping.IsCancellationRequested) return;
        using var finalization = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var completed = await store.CompleteAsync(job, nodes.UtcNow, error, finalization.Token, resultJson);
        if (!completed) log.LogWarning("Completion rejected: lease lost or cancellation requested");
        Finished.Add(1, new KeyValuePair<string, object?>("outcome", completed ? error ?? "succeeded" : "lease_lost"));
        Duration.Record(timer.Elapsed.TotalSeconds);
    }

    private async Task Heartbeat(Job job, CancellationTokenSource execution, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(config.HeartbeatSeconds), ct);
                if (!await store.RenewAsync(job.Id, job.LeaseToken!.Value, nodes.UtcNow, TimeSpan.FromSeconds(config.LeaseSeconds), ct))
                { await execution.CancelAsync(); return; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { log.LogError(e, "Heartbeat failed; cancelling execution"); await execution.CancelAsync(); }
    }
}
