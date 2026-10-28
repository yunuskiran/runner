using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Runner.Contracts;
using Runner.Engine.Packaging;

namespace Runner.Engine;

public sealed class ExecutionOptions
{
    public string Isolation { get; set; } = "Process";
    public string ExecutorAssembly { get; set; } = "";
}

public sealed record ChildRequest(Guid JobId, int Attempt, string Payload, string? Directory, string? EntryAssembly,
    string? EntryType, string? Source, OperationOptions Operations, string ProtocolToken);
public sealed record ChildMessage(string Kind, string? Data, string? Level = null);

/// <summary>Crash/timeout isolation for trusted code. It does not restrict OS permissions or network access.</summary>
public sealed class IsolatedExecutor(IOptions<ExecutionOptions> options, IOptions<OperationOptions> operations)
{
    public bool Enabled => options.Value.Isolation == "Process";

    public async Task ExecuteAsync(JobContext context, string? directory, PackageInfo? package, string? source, CancellationToken ct)
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        var request = new ChildRequest(context.JobId, context.Attempt, context.Payload, directory,
            package?.EntryAssembly, package?.EntryType, source, operations.Value, token);
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable"))
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(options.Value.ExecutorAssembly);
        start.ArgumentList.Add("--execute-child");
        var inherited = new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "TMPDIR", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64" }
            .Select(x => (Key: x, Value: Environment.GetEnvironmentVariable(x))).ToArray();
        start.Environment.Clear();
        foreach (var item in inherited) if (item.Value is not null) start.Environment[item.Key] = item.Value;
        var temporary = Path.Combine(Path.GetTempPath(), "runner-executor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary); start.WorkingDirectory = temporary;
        using var process = new Process { StartInfo = start };
        string? failure = null;
        bool finished = false;
        try
        {
            ct.ThrowIfCancellationRequested(); process.Start();
            using var io = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var output = ReadOutput();
            var errors = DrainErrors();
            async Task ReadOutput()
            {
                int records = 0;
                await foreach (var line in Lines(process.StandardOutput, io.Token))
                {
                    if (!line.StartsWith(token + ":", StringComparison.Ordinal)) continue;
                    if (++records > 205) throw new JobExecutionException("executor_output_limit");
                    var message = JsonSerializer.Deserialize<ChildMessage>(line[(token.Length + 1)..])
                        ?? throw new JobExecutionException("executor_protocol_invalid");
                    if (message.Kind == "log" && context.WriteLogAsync is not null)
                        await context.WriteLogAsync(message.Level ?? "Information", message.Data ?? "", io.Token);
                    else if (message.Kind == "result") context.ReportResult?.Invoke(message.Data ?? "null");
                    else if (message.Kind == "error") failure = message.Data;
                    else if (message.Kind == "finished") finished = true;
                }
            }
            async Task DrainErrors()
            {
                // Never persist exception text or arbitrary stderr, which may contain secrets.
                await foreach (var _ in Lines(process.StandardError, io.Token)) { }
            }
            var payload = JsonSerializer.Serialize(request);
            if (Encoding.UTF8.GetByteCount(payload) > 1048576) throw new JobExecutionException("executor_input_limit");
            await process.StandardInput.WriteLineAsync(payload.AsMemory(), ct); await process.StandardInput.FlushAsync(ct);
            var exit = process.WaitForExitAsync(ct);
            // A failed pipe/log sink must also terminate the child, rather than waiting for a hung process.
            var pending = new List<Task> { exit, output, errors };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                await completed;
                if (completed == exit) io.CancelAfter(TimeSpan.FromSeconds(5));
                pending.Remove(completed);
            }
            ct.ThrowIfCancellationRequested();
            if (failure is not null) throw new JobExecutionException(failure);
            if (process.ExitCode != 0 || !finished) throw new JobExecutionException("executor_process_failed");
        }
        finally
        {
            try
            {
                if (!process.HasExited && ct.IsCancellationRequested)
                {
                    try
                    {
                        await process.StandardInput.WriteLineAsync("cancel").WaitAsync(TimeSpan.FromSeconds(1));
                        await process.StandardInput.FlushAsync().WaitAsync(TimeSpan.FromSeconds(1));
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                    }
                    catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException) { }
                }
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
            PackageManager.TryDelete(temporary);
        }
    }

    private static async IAsyncEnumerable<string> Lines(StreamReader reader, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new char[4096]; var line = new StringBuilder(); long total = 0;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) { if (line.Length > 0) yield return line.ToString(); yield break; }
            total += count;
            if (total > 2 * 1024 * 1024) throw new JobExecutionException("executor_output_limit");
            for (int i = 0; i < count; i++)
            {
                if (buffer[i] == '\n') { yield return line.ToString().TrimEnd('\r'); line.Clear(); }
                else { line.Append(buffer[i]); if (line.Length > 262144) throw new JobExecutionException("executor_output_limit"); }
            }
        }
    }
}

public static class ChildExecution
{
    public static async Task<int> RunAsync()
    {
        // Start before the web host: this process never constructs RunnerDb or authenticates an operator.
        var input = await Console.In.ReadLineAsync();
        if (input is null || input.Length > 1048576) return 2;
        var request = JsonSerializer.Deserialize<ChildRequest>(input) ?? throw new InvalidOperationException("Missing child request");
        var inputPipe = Console.In;
        using var cancellation = new CancellationTokenSource();
        // EOF also terminates a child whose parent crashed, without a DB heartbeat in every executor.
        var watchdog = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    var command = inputPipe.ReadLine();
                    if (command is null) Environment.Exit(137);
                    if (command == "cancel") cancellation.Cancel();
                }
            }
            catch { Environment.Exit(137); }
        }) { IsBackground = true, Name = "Runner parent watchdog" };
        watchdog.Start();
        var protocol = Console.Out;
        Console.SetOut(TextWriter.Null); Console.SetError(TextWriter.Null);
        var gate = new SemaphoreSlim(1);
        int logs = 0;
        async Task Send(ChildMessage message)
        {
            await gate.WaitAsync();
            try { await protocol.WriteLineAsync(request.ProtocolToken + ":" + JsonSerializer.Serialize(message)); await protocol.FlushAsync(); }
            finally { gate.Release(); }
        }
        var context = new JobContext(request.JobId, request.Payload, request.Attempt)
        {
            WriteLogAsync = async (level, message, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref logs) <= 200)
                    await Send(new("log", message.Length > 4000 ? message[..4000] : message, level));
            },
            ReportResult = json =>
            {
                if (Encoding.UTF8.GetByteCount(json) > 65536) throw new JobExecutionException("executor_result_limit");
                using var parsed = JsonDocument.Parse(json);
                Send(new("result", json)).GetAwaiter().GetResult();
            }
        };
        var services = new ServiceCollection().AddLogging(x => { x.ClearProviders(); x.AddProvider(new ChildLogProvider(context)); });
        services.AddSingleton<IOptions<OperationOptions>>(Options.Create(request.Operations));
        services.AddSingleton<DatabaseCommands>(); services.AddSingleton<ApplicationCommands>();
        await using var provider = services.BuildServiceProvider();
        try
        {
            if (request.Source is { } source)
            {
                using var compiler = new ScriptCompiler(); using var payload = JsonDocument.Parse(request.Payload);
                var runner = compiler.Compile(source, cancellation.Token);
                var result = await runner(new ScriptGlobals(request.JobId, request.Attempt, payload.RootElement.GetProperty("arguments"), cancellation.Token)
                { Db = provider.GetRequiredService<DatabaseCommands>(), Apps = new FunctionApplications(provider.GetRequiredService<ApplicationCommands>(), context), Log = new FunctionLog(context) }, cancellation.Token);
                context.ReportResult(JsonSerializer.Serialize(result));
            }
            else
            {
                var entry = PackageArchive.Inspect(request.Directory!, request.EntryAssembly!, request.EntryType);
                try
                {
                    var plugin = (IRunnerPlugin)ActivatorUtilities.CreateInstance(provider, entry.EntryType);
                    try { await plugin.ExecuteAsync(context, cancellation.Token); }
                    finally { if (plugin is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync(); else if (plugin is IDisposable disposable) disposable.Dispose(); }
                }
                finally { entry.Context.Unload(); }
            }
            await Send(new("finished", null)); return 0;
        }
        catch (Exception e)
        {
            await Send(new("error", e is JobExecutionException business ? business.Code : "handler_failed")); return 1;
        }
    }
}
