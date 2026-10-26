using System.Text.Json;
using Runner.Contracts;
namespace Runner.SamplePlugin;

public sealed class EchoHandler : IJobHandler
{
    public string Type => "sample.echo";
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        using var payload = JsonDocument.Parse(context.Payload);
        int delay = payload.RootElement.TryGetProperty("delayMs", out var value) ? value.GetInt32() : 100;
        if (delay is < 0 or > 60000) throw new ArgumentOutOfRangeException(nameof(context));
        await Task.Delay(delay, cancellationToken);
        if (payload.RootElement.TryGetProperty("fail", out var fail) && fail.GetBoolean())
            throw new InvalidOperationException("Requested sample failure");
    }
}
