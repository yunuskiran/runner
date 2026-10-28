using System.Threading.Channels;

namespace Runner.Engine;

/// <summary>A local latency hint. Durable DB polling remains the cross-node recovery path.</summary>
public sealed class QueueSignals
{
    private readonly Channel<bool> channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite });
    public void Pulse() => channel.Writer.TryWrite(true);
    public async Task<bool> WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(delay);
        try { await channel.Reader.ReadAsync(deadline.Token); return true; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }
}

public sealed class IdleBackoff(int minimumMilliseconds, int maximumSeconds)
{
    private int misses;
    public void Reset() => misses = 0;
    public TimeSpan Next() => TimeSpan.FromMilliseconds(Math.Min(maximumSeconds * 1000,
        minimumMilliseconds * Math.Pow(2, Math.Min(misses++, 16))) * (0.8 + Random.Shared.NextDouble() * 0.2));
}

public sealed class AlertSignals
{
    private readonly QueueSignals signals = new();
    public void Pulse() => signals.Pulse();
    public Task WaitAsync(TimeSpan delay, CancellationToken ct) => signals.WaitAsync(delay, ct);
}
