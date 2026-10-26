namespace Runner.Contracts;

/// <summary>Handlers can execute more than once. Use JobId as an idempotency key for external side effects.</summary>
public interface IJobHandler
{
    string Type { get; }
    Task ExecuteAsync(JobContext context, CancellationToken cancellationToken);
}

public sealed record JobContext(Guid JobId, string Payload, int Attempt);
