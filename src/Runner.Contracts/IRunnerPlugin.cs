namespace Runner.Contracts;

/// <summary>Entry point for trusted, dashboard-installed plugin packages. Keep constructors free of side effects.</summary>
public interface IRunnerPlugin
{
    Task ExecuteAsync(JobContext context, CancellationToken cancellationToken);
}
