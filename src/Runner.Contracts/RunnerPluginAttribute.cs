namespace Runner.Contracts;

/// <summary>Controls cluster-wide overlap between jobs of the same plugin package.</summary>
public enum PluginExecutionMode
{
    /// <summary>Different jobs may execute at the same time on available workers.</summary>
    Parallel = 0,
    /// <summary>One valid execution lease at a time across package versions and worker hosts.</summary>
    Sequential = 1
}

/// <summary>Declares the package's initial execution policy without instantiating its entry point.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RunnerPluginAttribute : Attribute
{
    /// <summary>Default mode used when this package ID is first installed.</summary>
    public PluginExecutionMode ExecutionMode { get; set; } = PluginExecutionMode.Parallel;
}
