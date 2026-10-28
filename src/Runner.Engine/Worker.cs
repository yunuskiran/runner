using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Runner.Contracts;
namespace Runner.Engine;

public sealed class Worker(IServiceScopeFactory scopes, JobStore store, HandlerCatalog catalog,
    IOptions<RunnerOptions> options, ILogger<Worker> log) : BackgroundService
{
    public static readonly ActivitySource Activities = new("Runner");
    private static readonly Meter Meter = new("Runner");
    private static readonly Counter<long> Finished = Meter.CreateCounter<long>("runner.jobs.finished");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("runner.jobs.duration", "s");
    private readonly RunnerOptions config = options.Value;
    private readonly string workerId = $"{options.Value.InstanceName ?? Environment.MachineName}:{Guid.NewGuid():N}";

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => !config.WorkerEnabled
        ? Task.CompletedTask : Task.WhenAll(Enumerable.Range(0, config.Concurrency).Select(_ => Loop(stoppingToken)).Append(Scheduler(stoppingToken)));

    private async Task Scheduler(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await store.DispatchSchedulesAsync(DateTime.UtcNow, ct);
                await store.MaintainAsync(DateTime.UtcNow, config.RetentionDays, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogError(e, "Schedule/maintenance cycle failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var job = await store.ClaimAsync(workerId, DateTime.UtcNow, TimeSpan.FromSeconds(config.LeaseSeconds), ct);
                if (job is not null) { await Run(job, ct); continue; }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogError(e, "Worker cycle failed"); }
            try { await Task.Delay(config.PollMilliseconds, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private async Task Run(Job job, CancellationToken stopping)
    {
        using var activity = Activities.StartActivity("job.execute");
        activity?.SetTag("job.id", job.Id).SetTag("job.type", job.Type).SetTag("job.attempt", job.Attempts);
        using var scopeLog = log.BeginScope(new Dictionary<string, object> { ["JobId"] = job.Id, ["JobType"] = job.Type, ["Attempt"] = job.Attempts });
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stopping);
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
            await heartbeatStop.CancelAsync();
            try { await heartbeat; } catch (OperationCanceledException) { }
        }
        // On shutdown leave ownership to expire; another process can recover the job.
        if (stopping.IsCancellationRequested) return;
        using var finalization = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var completed = await store.CompleteAsync(job, DateTime.UtcNow, error, finalization.Token, resultJson);
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
                if (!await store.RenewAsync(job.Id, job.LeaseToken!.Value, DateTime.UtcNow, TimeSpan.FromSeconds(config.LeaseSeconds), ct))
                { await execution.CancelAsync(); return; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { log.LogError(e, "Heartbeat failed; cancelling execution"); await execution.CancelAsync(); }
    }
}
