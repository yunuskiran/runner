using System.Diagnostics;
using System.Text.Json;
namespace Runner.Benchmarks;

public sealed class NodeProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task<string> errors;
    private NodeProcess(Process process) { this.process = process; errors = process.StandardError.ReadToEndAsync(); }
    public static async Task<NodeProcess> Start(NodeConfig config)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(NodeProcess).Assembly.Location);
        start.ArgumentList.Add("--node"); start.Environment.Remove("BENCH_MSSQL");
        var node = new NodeProcess(Process.Start(start)!);
        try
        {
            await node.process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(config)); await node.process.StandardInput.FlushAsync();
            var ready = await node.process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60));
            if (ready != "READY") throw new InvalidOperationException("Worker did not start: " + await node.errors);
            return node;
        }
        catch { await node.DisposeAsync(); throw; }
    }
    public double CpuMs { get { process.Refresh(); return process.TotalProcessorTime.TotalMilliseconds; } }
    public long WorkingSet { get { process.Refresh(); return process.WorkingSet64; } }
    public void Verify() { if (process.HasExited) throw new InvalidOperationException("Benchmark worker exited unexpectedly"); }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("stop"); await process.StandardInput.FlushAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException) { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        await process.WaitForExitAsync(); process.Dispose();
    }
    public static async Task Run()
    {
        var config = JsonSerializer.Deserialize<NodeConfig>((await Console.In.ReadLineAsync())!)!;
        await using var engine = new Engine(config); await engine.Initialize(true); Console.WriteLine("READY");
        // Parent loss also stops the node. Credentials only arrived through private stdin.
        _ = await Console.In.ReadLineAsync();
    }
}
