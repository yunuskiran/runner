using System.Text.Json;
using Runner.Contracts;

namespace Example.NuGetPlugin;

[RunnerPlugin(ExecutionMode = PluginExecutionMode.Sequential)]
public sealed class InvoicePlugin : IRunnerPlugin
{
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        using var payload = JsonDocument.Parse(context.Payload);
        var delay = payload.RootElement.TryGetProperty("delayMs", out var value) ? value.GetInt32() : 50;
        if (delay is < 0 or > 60000) throw new ArgumentOutOfRangeException(nameof(context));
        var startedUtc = DateTime.UtcNow;
        await Task.Delay(delay, cancellationToken);
        var finishedUtc = DateTime.UtcNow;
        context.ReportResult?.Invoke(JsonSerializer.Serialize(new { context.JobId, context.Attempt, processed = true, startedUtc, finishedUtc }));
    }
}
