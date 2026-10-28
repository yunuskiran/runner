# Runner Plugin SDK

Build .NET 10 plugins for the Runner dashboard package manager. This package supplies `Runner.Contracts.dll`, XML API documentation, and an MSBuild target that keeps the shared contract out of plugin output. The host supplies that assembly at runtime.

Reference the SDK in your plugin project:

```xml
<PackageReference Include="Runner.PluginSdk" Version="0.1.0" PrivateAssets="all" />
```

```csharp
using System.Text.Json;
using Runner.Contracts;

[RunnerPlugin(ExecutionMode = PluginExecutionMode.Sequential)]
public sealed class InvoicePlugin : IRunnerPlugin
{
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Perform idempotent business work using context.JobId or a business operation key.
        context.ReportResult?.Invoke(JsonSerializer.Serialize(new { context.JobId, processed = true }));
        return Task.CompletedTask;
    }
}
```

Use `Parallel` to allow overlapping jobs, or `Sequential` for one valid lease per package ID across all versions and hosts sharing a database. The attribute sets the initial package policy; an operator can override it in the dashboard. It is not a workflow dependency or exactly-once guarantee. Lease expiry permits recovery, so a disconnected/uncooperative old process may still perform external side effects. Honor cancellation and make those effects idempotent.

Publish the class library and upload its output folder/DLLs through Runner's dashboard. Include dependency DLLs and `.deps.json`; do not include `Runner.Contracts.dll`. Plugins run as trusted code with host privileges. Use Runner hosts supporting schema version 5 before enabling sequential mode.

The package ID can be overridden at pack time. Availability/ownership on nuget.org is not established by creating a local package. No feed credentials are included; publishing is a separate release action.
