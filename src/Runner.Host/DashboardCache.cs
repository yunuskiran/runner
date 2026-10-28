using Microsoft.Extensions.Caching.Memory;
namespace Runner.Host;

/// <summary>Bounded, coalesced aggregate reads; opening more dashboards does not multiply report scans.</summary>
public sealed class DashboardCache : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 32 });
    private readonly SemaphoreSlim gate = new(1);
    public async Task<T> ReadAsync<T>(string key, Func<Task<T>> read, CancellationToken ct)
    {
        if (cache.TryGetValue<T>(key, out var found)) return found!;
        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue<T>(key, out found)) return found!;
            var value = await read();
            cache.Set(key, value, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(15) });
            return value;
        }
        finally { gate.Release(); }
    }
    public void Dispose() { cache.Dispose(); gate.Dispose(); }
}
