namespace Runner.Contracts;

/// <summary>Handlers can execute more than once. Use JobId as an idempotency key for external side effects.</summary>
public interface IJobHandler
{
    /// <summary>Unique job type registered by a legacy handler.</summary>
    string Type { get; }
    /// <summary>Execute the business operation and honor cooperative cancellation.</summary>
    Task ExecuteAsync(JobContext context, CancellationToken cancellationToken);
}

/// <summary>Business input and host-managed execution services for one job attempt.</summary>
/// <param name="JobId">Stable ID shared by retries of the same job.</param>
/// <param name="Payload">JSON object supplied to this execution.</param>
/// <param name="Attempt">One-based attempt number.</param>
public sealed record JobContext(Guid JobId, string Payload, int Attempt)
{
    /// <summary>Optional centralized business log sink. Levels: Information, Warning, Error.</summary>
    public Func<string, string, CancellationToken, Task>? WriteLogAsync { get; init; }
    /// <summary>Optional JSON result sink, bounded to 64 KiB by the worker.</summary>
    public Action<string>? ReportResult { get; init; }
}
