namespace Runner.Contracts;

/// <summary>Handlers can execute more than once. Use JobId as an idempotency key for external side effects.</summary>
public interface IJobHandler
{
    string Type { get; }
    Task ExecuteAsync(JobContext context, CancellationToken cancellationToken);
}

public sealed record JobContext(Guid JobId, string Payload, int Attempt)
{
    /// <summary>Optional JSON result sink, bounded to 64 KiB by the worker.</summary>
    public Func<string, string, CancellationToken, Task>? WriteLogAsync { get; init; }
    public Action<string>? ReportResult { get; init; }
}
