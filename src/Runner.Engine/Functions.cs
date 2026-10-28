using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Runner.Contracts;
namespace Runner.Engine;

public sealed class FunctionDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public int Version { get; set; }
    public string Source { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ScriptingOptions
{
    public bool Enabled { get; set; }
}

/// <summary>Trusted scripts execute with the host's process privileges. This is not a sandbox.</summary>
public sealed class ScriptGlobals(Guid jobId, int attempt, JsonElement arguments, CancellationToken cancellationToken)
{
    public DatabaseCommands Db { get; init; } = null!;
    public FunctionApplications Apps { get; init; } = null!;
    public FunctionLog Log { get; init; } = null!;
    public Guid JobId { get; } = jobId;
    public int Attempt { get; } = attempt;
    public JsonElement Arguments { get; } = arguments;
    public CancellationToken CancellationToken { get; } = cancellationToken;
}

public sealed record ScriptDiagnostic(string Id, int Line, int Column, string Message);
public sealed class ScriptValidationException(IReadOnlyList<ScriptDiagnostic> diagnostics) : Exception("Script compilation failed")
{
    public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; } = diagnostics;
}

public sealed class ScriptCompiler : IDisposable
{
    public const int MaxSourceBytes = 32768;
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 32 });
    private readonly object gate = new();
    public static string Hash(string source) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    public ScriptRunner<object?> Compile(string source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source) || Encoding.UTF8.GetByteCount(source) > MaxSourceBytes)
            throw new ScriptValidationException([new("SOURCE_SIZE", 1, 1, "Source must contain 1–32768 UTF-8 bytes")]);
        var directives = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(kind: SourceCodeKind.Script), cancellationToken: ct)
            .GetRoot(ct).DescendantTrivia(descendIntoTrivia: true)
            .Where(x => x.IsKind(SyntaxKind.ReferenceDirectiveTrivia) || x.IsKind(SyntaxKind.LoadDirectiveTrivia)).ToList();
        if (directives.Count != 0)
            throw new ScriptValidationException(directives.Select(x =>
            {
                var position = x.GetLocation().GetLineSpan().StartLinePosition;
                return new ScriptDiagnostic("DIRECTIVE_NOT_ALLOWED", position.Line + 1, position.Character + 1, "#r and #load are not supported");
            }).ToList());
        var key = Hash(source);
        lock (gate)
        {
            ct.ThrowIfCancellationRequested();
            if (cache.TryGetValue<ScriptRunner<object?>>(key, out var existing)) return existing!;
            var options = ScriptOptions.Default
                .WithReferences(typeof(object).Assembly, typeof(JsonElement).Assembly, typeof(ScriptGlobals).Assembly,
                    typeof(Task).Assembly, typeof(Enumerable).Assembly)
                .WithImports("System", "System.Linq", "System.Text.Json", "System.Threading", "System.Threading.Tasks")
                .WithFilePath("function.csx");
            var script = CSharpScript.Create<object?>(source, options, typeof(ScriptGlobals));
            var errors = script.Compile(ct).Where(x => x.Severity == DiagnosticSeverity.Error).Select(x =>
            {
                var position = x.Location.GetLineSpan().StartLinePosition;
                return new ScriptDiagnostic(x.Id, position.Line + 1, position.Character + 1, x.GetMessage());
            }).ToList();
            if (errors.Count != 0) throw new ScriptValidationException(errors);
            var runner = script.CreateDelegate(ct);
            cache.Set(key, runner, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(30) });
            return runner;
        }
    }
    public void Dispose() => cache.Dispose();
}

public sealed class ScriptFunctionHandler(IDbContextFactory<RunnerDb> factory, ScriptCompiler compiler,
    IOptions<ScriptingOptions> options, DatabaseCommands? database = null, ApplicationCommands? applications = null, IsolatedExecutor? executor = null) : IJobHandler
{
    public const string JobType = "runner.script";
    public string Type => JobType;
    public async Task ExecuteAsync(JobContext context, CancellationToken ct)
    {
        if (!options.Value.Enabled) throw new InvalidOperationException("Scripting is disabled on this worker");
        using var payload = JsonDocument.Parse(context.Payload);
        var id = payload.RootElement.GetProperty("functionId").GetGuid();
        var arguments = payload.RootElement.GetProperty("arguments");
        if (arguments.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Arguments must be a JSON object");
        await using var db = await factory.CreateDbContextAsync(ct);
        var definition = await db.Functions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new InvalidOperationException("Function version not found");
        if (!definition.Enabled) throw new InvalidOperationException("Function version is disabled");
        if (ScriptCompiler.Hash(definition.Source) != definition.SourceHash) throw new InvalidOperationException("Function source hash mismatch");
        if (executor?.Enabled == true)
        {
            await executor.ExecuteAsync(context, null, null, definition.Source, ct);
            return;
        }
        var runner = compiler.Compile(definition.Source, ct);
        var result = await runner(new ScriptGlobals(context.JobId, context.Attempt, arguments, ct) { Db = database!, Apps = new FunctionApplications(applications!, context), Log = new FunctionLog(context) }, ct);
        ct.ThrowIfCancellationRequested();
        context.ReportResult?.Invoke(JsonSerializer.Serialize(result));
    }
}
