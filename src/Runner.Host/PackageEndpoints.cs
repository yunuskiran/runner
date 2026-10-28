using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Runner.Engine;
using Runner.Contracts;
using Runner.Engine.Packaging;
namespace Runner.Host;

public static class PackageEndpoints
{
    public static void MapPackages(this RouteGroupBuilder api)
    {
        api.MapGet("/packages/settings", (PackageManager packages) => new { enabled = packages.Enabled, maxBytes = PackageArchive.MaxBytes, maxFiles = PackageArchive.MaxFiles });
        var routes = api.MapGroup("/packages");
        routes.AddEndpointFilter(async (context, next) =>
        {
            if (!context.HttpContext.RequestServices.GetRequiredService<PackageManager>().Enabled) return Results.NotFound();
            try { return await next(context); }
            catch (PackageException e) { return Results.BadRequest(new { error = e.Message }); }
            catch (PackageConflictException e) { return Results.Conflict(new { error = e.Message }); }
        });
        routes.MapGet("/", async (int? page, IDbContextFactory<RunnerDb> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return await db.PluginChannels.AsNoTracking().OrderBy(x => x.PackageId).Skip(Math.Clamp(page ?? 0, 0, 10000) * 50).Take(50).Select(x => new PackageChannelInfo(x.PackageId, x.ActiveVersion, x.Enabled, x.Revision, x.ChangedUtc, x.ExecutionMode, x.MaxConcurrency)).ToListAsync(ct);
        });
        routes.MapGet("/{id}/channel", async (string id, IDbContextFactory<RunnerDb> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var channel = await db.PluginChannels.AsNoTracking().Where(x => x.PackageId == id).Select(x => new PackageChannelInfo(x.PackageId, x.ActiveVersion, x.Enabled, x.Revision, x.ChangedUtc, x.ExecutionMode, x.MaxConcurrency)).SingleOrDefaultAsync(ct);
            return channel is null ? Results.NotFound() : Results.Ok(channel);
        });
        routes.MapGet("/{id}/versions", async (string id, int? page, IDbContextFactory<RunnerDb> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return await db.PluginPackages.AsNoTracking().Where(x => x.PackageId == id).OrderByDescending(x => x.InstalledUtc)
                .Skip(Math.Clamp(page ?? 0, 0, 10000) * 50).Take(50)
                .Select(x => new PackageInfo(x.PackageId, x.Version, x.EntryAssembly, x.EntryType, x.Digest, x.InstalledUtc, x.DefaultExecutionMode)).ToListAsync(ct);
        });
        routes.MapGet("/{id}/history", async (string id, int? page, IDbContextFactory<RunnerDb> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return await db.PackageChanges.AsNoTracking().Where(x => x.PackageId == id).OrderByDescending(x => x.Revision)
                .Skip(Math.Clamp(page ?? 0, 0, 10000) * 50).Take(50).ToListAsync(ct);
        });
        routes.MapPost("/install", async (HttpRequest request, PackageManager packages, NodeRegistry nodes, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "Upload DLL files or a folder using multipart/form-data" });
            var limit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = 64 * 1024 * 1024;
            var form = await request.ReadFormAsync(ct);
            if (form.Files.Count is 0 or > PackageArchive.MaxFiles || form.Files.Sum(f => f.Length) > PackageArchive.MaxBytes)
                return Results.BadRequest(new { error = "Select 1–512 files, at most 50 MiB total" });
            var files = new List<PackageFile>();
            foreach (var file in form.Files)
            {
                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, ct);
                files.Add(new PackageFile(file.FileName, buffer.ToArray()));
            }
            var info = await packages.InstallAsync(form["packageId"].ToString(), form["version"].ToString(),
                form["entryAssembly"].ToString(), form["entryType"].ToString(), files, ct);
            request.HttpContext.Items["RunnerAudit"] = new { info.PackageId, info.Version, info.Digest };
            await nodes.RefreshPackagesAsync(ct);
            return Results.Created($"/api/packages/{info.PackageId}/versions", info);
        }).WithMetadata(new RequestSizeLimitAttribute(64 * 1024 * 1024)).RequireRateLimiting("package-install");
        routes.MapPost("/{id}/activation", async (string id, PackageActivation input, HttpContext context, PackageManager packages, CancellationToken ct) =>
        {
            context.Items["RunnerAudit"] = input;
            return await packages.SetChannelAsync(id, input.Version, input.Enabled, input.Revision, ct, context.User.Identity?.Name ?? "operator", input.ExecutionMode, input.MaxConcurrency, input.SetLimit)
                ? Results.NoContent() : Results.Conflict(new { error = "Package state changed. Refresh and retry using the current revision." });
        });
    }
}
public sealed record PackageActivation(string? Version, bool Enabled, int Revision, PluginExecutionMode? ExecutionMode = null, int? MaxConcurrency = null, bool SetLimit = false);
public sealed record PackageChannelInfo(string PackageId, string? ActiveVersion, bool Enabled, int Revision, DateTime ChangedUtc, PluginExecutionMode ExecutionMode, int? MaxConcurrency);
