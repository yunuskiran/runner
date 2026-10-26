using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Runner.Engine;
namespace Runner.Host;

public static partial class FunctionEndpoints
{
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9_.-]{0,119}$")]
    private static partial Regex ValidName();

    public static void MapFunctions(this RouteGroupBuilder api)
    {
        api.MapGet("/scripting", (IOptions<ScriptingOptions> options) => new
        { enabled = options.Value.Enabled, maxSourceBytes = ScriptCompiler.MaxSourceBytes });
        var functions = api.MapGroup("/functions");
        functions.AddEndpointFilter(async (context, next) => context.HttpContext.RequestServices
            .GetRequiredService<IOptions<ScriptingOptions>>().Value.Enabled ? await next(context) : Results.NotFound());
        functions.MapGet("/", async (int? page, IDbContextFactory<RunnerDb> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return await db.Functions.AsNoTracking().OrderBy(x => x.Name).ThenByDescending(x => x.Version)
                .Skip(Math.Clamp(page ?? 0, 0, 10000) * 50).Take(50)
                .Select(x => new { x.Id, x.Name, x.Version, x.Enabled, x.SourceHash, x.CreatedUtc }).ToListAsync(ct);
        });
        functions.MapGet("/{id:guid}", async (Guid id, IDbContextFactory<RunnerDb> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var function = await db.Functions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            return function is null ? Results.NotFound() : Results.Ok(function);
        });
        functions.MapPost("/", async (RegisterFunction input, ScriptCompiler compiler, IDbContextFactory<RunnerDb> factory,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            if (input.Name is null || !ValidName().IsMatch(input.Name) || input.Version is < 1 or > 1000000 || input.Source is null)
                return Results.BadRequest(new { error = "Name must use letters, digits, '.', '_' or '-'; version must be 1–1000000" });
            try { compiler.Compile(input.Source, ct); }
            catch (ScriptValidationException e) { return Results.BadRequest(new { error = e.Message, diagnostics = e.Diagnostics }); }
            await using var db = await factory.CreateDbContextAsync(ct);
            if (await db.Functions.AnyAsync(x => x.Name == input.Name && x.Version == input.Version, ct))
                return Results.Conflict(new { error = "This immutable function version already exists. Register a new version." });
            var function = new FunctionDefinition { Name = input.Name, Version = input.Version, Source = input.Source,
                SourceHash = ScriptCompiler.Hash(input.Source) };
            db.Functions.Add(function);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                if (await db.Functions.AnyAsync(x => x.Name == input.Name && x.Version == input.Version, ct)) return Results.Conflict();
                throw;
            }
            logs.CreateLogger("Runner.Functions").LogInformation("Registered function {FunctionId} {FunctionName} v{Version} hash {SourceHash}",
                function.Id, function.Name, function.Version, function.SourceHash);
            return Results.Created($"/api/functions/{function.Id}", function);
        }).RequireRateLimiting("script-registration");
        functions.MapPatch("/{id:guid}", async (Guid id, FunctionStatus input, IDbContextFactory<RunnerDb> factory,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var changed = await db.Functions.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, input.Enabled), ct);
            if (changed == 0) return Results.NotFound();
            logs.CreateLogger("Runner.Functions").LogInformation("Function {FunctionId} enabled={Enabled}", id, input.Enabled);
            return Results.NoContent();
        });
    }

    public static async Task<bool> ValidPayloadAsync(string type, JsonElement payload, RunnerDb db, CancellationToken ct)
    {
        if (type != ScriptFunctionHandler.JobType) return true;
        return payload.TryGetProperty("functionId", out var id) && id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var guid) &&
            payload.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Object &&
            await db.Functions.AnyAsync(x => x.Id == guid && x.Enabled, ct);
    }
}
public sealed record RegisterFunction(string Name, int Version, string Source);
public sealed record FunctionStatus(bool Enabled);
