using System.Text.Json;
using Runner.Contracts;
using Microsoft.Extensions.Logging;
namespace Runner.SamplePlugin;

/// <summary>Dashboard-installable entry point; the existing EchoHandler remains a legacy configured handler.</summary>
public sealed class EchoPlugin(ILogger<EchoPlugin>? logger = null) : IRunnerPlugin
{
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        using var payload = JsonDocument.Parse(context.Payload);
        var name = payload.RootElement.TryGetProperty("name", out var value) ? value.GetString() : "world";
        if (context.WriteLogAsync is { } log) await log("Information", "Dashboard-installed plugin executed", cancellationToken);
        logger?.LogInformation("Echo plugin business log");
        cancellationToken.ThrowIfCancellationRequested();
        context.ReportResult?.Invoke(JsonSerializer.Serialize(new { greeting = $"Hello, {name}!", jobId = context.JobId }));
    }
}
