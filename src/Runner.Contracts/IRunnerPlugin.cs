namespace Runner.Contracts;

/// <summary>Entry point for trusted, dashboard-installed plugin packages. Keep constructors free of side effects.</summary>
public interface IRunnerPlugin
{
    /// <summary>Run one job attempt; report JSON through the context and honor cancellation.</summary>
    Task ExecuteAsync(JobContext context, CancellationToken cancellationToken);
}
