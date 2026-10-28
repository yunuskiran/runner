using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Runner.Engine;
using Runner.Engine.Packaging;

namespace Runner.Host;

public static class PlatformEndpoints
{
    public static void MapPlatform(this RouteGroupBuilder api)
    {
        api.MapGet("/session", (HttpContext context) => new { name = context.User.Identity?.Name,
            roles = context.User.FindAll(System.Security.Claims.ClaimTypes.Role).Select(x => x.Value) });
        api.MapGet("/diagnostics/database", (DatabaseMetrics metrics) => metrics.Snapshot());
        api.MapGet("/nodes", async (IDbContextFactory<RunnerDb> factory, IOptions<RunnerOptions> options, ClusterClock clock, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var cutoff = clock.UtcNow.AddSeconds(-options.Value.NodeHeartbeatSeconds * 3 - 5);
            return await db.WorkerNodes.AsNoTracking().OrderByDescending(x => x.HeartbeatUtc).Take(200)
                .Select(x => new { x.Id, x.Name, x.StartedUtc, x.HeartbeatUtc, x.DrainRequested, x.DrainDeadlineUtc, x.DrainAcknowledgedUtc,
                    x.ActiveJobs, x.Concurrency, x.CapabilitiesJson, online = x.HeartbeatUtc >= cutoff,
                    state = x.HeartbeatUtc < cutoff ? "Offline" : x.DrainRequested ? x.DrainAcknowledgedUtc == null ? "DrainRequested" : x.ActiveJobs == 0 ? "Drained" : "Draining" : "Online" }).ToListAsync(ct);
        });
        api.MapPost("/nodes/{id}/drain", async (string id, DrainNode input, HttpContext context, IDbContextFactory<RunnerDb> factory, ClusterClock clock, CancellationToken ct) =>
        {
            if (input.DeadlineSeconds is < 1 or > 86400) return Results.BadRequest();
            context.Items["RunnerAudit"] = new { input.Enabled, input.DeadlineSeconds };
            await using var db = await factory.CreateDbContextAsync(ct);
            return await db.WorkerNodes.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DrainRequested, input.Enabled)
                .SetProperty(x => x.DrainAcknowledgedUtc, (DateTime?)null)
                .SetProperty(x => x.DrainDeadlineUtc, input.Enabled ? clock.UtcNow.AddSeconds(input.DeadlineSeconds) : (DateTime?)null), ct) == 1
                ? Results.Accepted() : Results.NotFound();
        });
        api.MapGet("/packages/{id}/versions/{version}/placement", async (string id, string version, IDbContextFactory<RunnerDb> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var json = await db.PluginPackages.Where(x => x.PackageId == id && x.Version == version).Select(x => x.ManifestJson).SingleOrDefaultAsync(ct);
            if (json is null) return Results.NotFound();
            var manifest = PluginManifest.Parse(json);
            var nodes = await db.WorkerNodes.AsNoTracking().OrderByDescending(x => x.HeartbeatUtc).Take(200).ToListAsync(ct);
            return Results.Ok(new { manifest, nodes = nodes.Select(x => new { x.Id, x.Name, x.HeartbeatUtc, x.DrainRequested,
                reasons = manifest.Incompatibilities(JsonSerializer.Deserialize<NodeCapabilities>(x.CapabilitiesJson, JsonSerializerOptions.Web)!) }) });
        });
        api.MapGet("/audit", async (int? page, IDbContextFactory<RunnerDb> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return await db.AuditRecords.AsNoTracking().OrderByDescending(x => x.StartedUtc).ThenByDescending(x => x.Id)
                .Skip(Math.Clamp(page ?? 0, 0, 10000) * 100).Take(100).ToListAsync(ct);
        });
        api.MapGet("/workflows", async (IDbContextFactory<RunnerDb> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return await db.Workflows.AsNoTracking().OrderByDescending(x => x.CreatedUtc).Take(100).ToListAsync(ct);
        });
        api.MapGet("/workflows/{id:guid}", async (Guid id, IDbContextFactory<RunnerDb> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var workflow = await db.Workflows.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (workflow is null) return Results.NotFound();
            var jobs = await db.Jobs.AsNoTracking().Where(x => x.WorkflowId == id).OrderBy(x => x.CreatedUtc)
                .Select(x => new { x.Id, x.StepKey, x.Type, x.State, x.Attempts, x.ErrorCode, x.ResultJson }).ToListAsync(ct);
            var ids = jobs.Select(x => x.Id).ToArray();
            var dependencies = await db.JobDependencies.AsNoTracking().Where(x => ids.Contains(x.JobId)).ToListAsync(ct);
            return Results.Ok(new { workflow, jobs, dependencies });
        });
        api.MapPost("/workflows", async (SubmitWorkflow input, HandlerCatalog catalog, PackageManager packages,
            DatabaseCommands database, ApplicationCommands applications, IDbContextFactory<RunnerDb> factory, IOptions<RunnerOptions> options, ClusterClock clock, CancellationToken ct) =>
        {
            if (input.Steps is null || input.Steps.Any(x => x is null || string.IsNullOrWhiteSpace(x.Key)) || string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 120) return Results.BadRequest();
            try { WorkflowService.Validate(input.Steps.ToDictionary(x => x.Key, x => x.DependsOn ?? [], StringComparer.Ordinal)); }
            catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
            await using var db = await factory.CreateDbContextAsync(ct);
            var workflow = new WorkflowRun { Name = input.Name, CreatedUtc = clock.UtcNow };
            var jobs = new Dictionary<string, Job>(StringComparer.Ordinal);
            foreach (var step in input.Steps)
            {
                var job = step.Job;
                if (job is null || job.Type is null || !catalog.Types.ContainsKey(job.Type) || job.Payload.ValueKind != JsonValueKind.Object ||
                    System.Text.Encoding.UTF8.GetByteCount(job.Payload.GetRawText()) > 65536 || job.MaxAttempts is < 1 or > 20 ||
                    job.Priority is < -10 or > 10 || !Admission.ValidQueue(job.Queue) || job.DelaySeconds is < 0 or > 31536000 || job.IdempotencyKey is not null ||
                    !OperationEndpoints.ValidPayload(job.Type, job.Payload, database, applications) || !await FunctionEndpoints.ValidPayloadAsync(job.Type, job.Payload, db, ct)) return Results.BadRequest();
                PluginRequest? plugin = null;
                try { if (job.Type == PackageJobHandler.JobType) plugin = await packages.PinAsync(job.Payload, ct); }
                catch (PackageException e) { return Results.BadRequest(new { error = e.Message }); }
                var entity = new Job { CreatedUtc = clock.UtcNow, WorkflowId = workflow.Id, StepKey = step.Key, Type = job.Type,
                    Payload = plugin is null ? job.Payload.GetRawText() : JsonSerializer.Serialize(plugin, JsonSerializerOptions.Web),
                    PackageId = plugin?.PackageId, PackageVersion = plugin?.Version, Queue = job.Queue, Priority = job.Priority,
                    MaxAttempts = job.MaxAttempts, AvailableUtc = clock.UtcNow.AddSeconds(job.DelaySeconds),
                    State = (step.DependsOn?.Length ?? 0) == 0 ? JobState.Pending : JobState.Blocked };
                Admission.SetOrder(entity); jobs.Add(step.Key, entity);
            }
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await Admission.LockAsync(db, ct);
            if (!await Admission.HasCapacityAsync(db, jobs.Count, options.Value.MaxPendingJobs, ct)) return Results.Conflict(new { error = "Outstanding job capacity reached" });
            db.Workflows.Add(workflow); db.Jobs.AddRange(jobs.Values);
            foreach (var step in input.Steps)
                foreach (var dependency in step.DependsOn ?? []) db.JobDependencies.Add(new JobDependency { JobId = jobs[step.Key].Id, DependsOnJobId = jobs[dependency].Id });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return Results.Created("/api/workflows/" + workflow.Id, new { workflow.Id, jobs = jobs.ToDictionary(x => x.Key, x => x.Value.Id) });
        }).RequireRateLimiting("submission");
    }
}
public sealed record DrainNode(bool Enabled = true, int DeadlineSeconds = 300);
public sealed record WorkflowStep(string Key, SubmitJob Job, string[]? DependsOn = null);
public sealed record SubmitWorkflow(string Name, WorkflowStep[] Steps);
