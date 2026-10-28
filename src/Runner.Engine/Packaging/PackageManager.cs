using System.Text.Json;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Runner.Contracts;
namespace Runner.Engine.Packaging;

public sealed class PackageManager(IDbContextFactory<RunnerDb> factory, IOptions<PackageOptions> options, ClusterClock? clock = null)
{
    public bool Enabled => options.Value.Enabled;
    public async Task<PackageInfo> InstallAsync(string id, string version, string entryAssembly, string? entryType,
        IReadOnlyCollection<PackageFile> files, CancellationToken ct)
    {
        if (!Enabled) throw new PackageException("Package management is disabled");
        if (!PackageArchive.ValidIdentity(id, version)) throw new PackageException("Use a lowercase package ID and a semantic version such as 1.2.3 or 1.2.3-rc.1");
        entryAssembly = PackageArchive.SafePath(entryAssembly);
        var manifestFile = files.SingleOrDefault(x => x.Path.Replace('\\', '/') == "runner.manifest.json");
        var manifestJson = manifestFile is null ? "{}" : System.Text.Encoding.UTF8.GetString(manifestFile.Content);
        var manifest = PluginManifest.Parse(manifestJson);
        manifestJson = JsonSerializer.Serialize(manifest, JsonSerializerOptions.Web);
        var archive = PackageArchive.Build(files, entryAssembly);
        var digest = PackageArchive.Hash(archive);
        var temporary = Path.Combine(Path.GetTempPath(), "runner-package-inspect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var defaultMode = PluginExecutionMode.Parallel;
        try
        {
            PackageArchive.Extract(archive, temporary);
            var inspected = PackageArchive.Inspect(temporary, entryAssembly, entryType);
            try
            {
                entryType = inspected.EntryType.FullName!;
                defaultMode = inspected.EntryType.GetCustomAttribute<RunnerPluginAttribute>()?.ExecutionMode ?? PluginExecutionMode.Parallel;
                if (!Enum.IsDefined(defaultMode)) throw new PackageException("Unknown plugin execution mode");
            }
            finally { inspected.Context.Unload(); }
        }
        finally { TryDelete(temporary); }
        // Commit the immutable version and first channel policy together. A losing concurrent
        // first install retries only after observing the channel/version committed by its peer.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var existing = await VersionInfo(db, id, version, ct);
            if (existing is not null) return MatchExisting(existing, digest, entryAssembly, entryType!);
            var newChannel = !await db.PluginChannels.AnyAsync(x => x.PackageId == id, ct);
            if (newChannel) db.PluginChannels.Add(new PluginChannel { PackageId = id, ExecutionMode = defaultMode });
            var package = new PluginPackage { PackageId = id, Version = version, EntryAssembly = entryAssembly,
                EntryType = entryType!, Digest = digest, Archive = archive, DefaultExecutionMode = defaultMode, ManifestJson = manifestJson };
            db.PluginPackages.Add(package);
            try
            {
                await db.SaveChangesAsync(ct);
                await db.ClusterLeases.Where(x => x.Name == "packages").ExecuteUpdateAsync(s => s.SetProperty(x => x.Owner, Guid.NewGuid().ToString("N")), ct);
                await transaction.CommitAsync(ct);
                return new(id, version, entryAssembly, entryType!, digest, package.InstalledUtc, defaultMode);
            }
            catch (DbUpdateException)
            {
                await transaction.RollbackAsync(ct);
                await using var verification = await factory.CreateDbContextAsync(ct);
                var winner = await VersionInfo(verification, id, version, ct);
                if (winner is not null) return MatchExisting(winner, digest, entryAssembly, entryType!);
                if (newChannel && attempt < 2 && await verification.PluginChannels.AnyAsync(x => x.PackageId == id, ct)) continue;
                throw;
            }
        }
        throw new PackageConflictException("Concurrent package installation did not settle; refresh and retry");
    }
    private static Task<PackageInfo?> VersionInfo(RunnerDb db, string id, string version, CancellationToken ct) =>
        db.PluginPackages.AsNoTracking().Where(x => x.PackageId == id && x.Version == version)
            .Select(x => new PackageInfo(x.PackageId, x.Version, x.EntryAssembly, x.EntryType, x.Digest, x.InstalledUtc, x.DefaultExecutionMode)).SingleOrDefaultAsync(ct);
    private static PackageInfo MatchExisting(PackageInfo existing, string digest, string entryAssembly, string entryType)
    {
        if (existing.Digest == digest && existing.EntryAssembly == entryAssembly && existing.EntryType == entryType) return existing;
        throw new PackageConflictException("This package version is immutable. Upload changed content under a new version.");
    }

    public async Task<bool> SetChannelAsync(string id, string? version, bool enabled, int revision, CancellationToken ct, string actor = "operator", PluginExecutionMode? executionMode = null, int? maxConcurrency = null, bool setLimit = false)
    {
        if (!Enabled) throw new PackageException("Package management is disabled");
        await using var db = await factory.CreateDbContextAsync(ct);
        if (enabled && (version is null || !await db.PluginPackages.AnyAsync(x => x.PackageId == id && x.Version == version, ct)))
            throw new PackageException("Select an installed package version to activate");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (executionMode is { } requestedMode && !Enum.IsDefined(requestedMode)) throw new PackageException("Invalid execution mode");
        if (await PackageConcurrency.LockAsync(db, id, ct, revision) == 0) return false;
        var previous = await db.PluginChannels.AsNoTracking().SingleOrDefaultAsync(x => x.PackageId == id, ct);
        if (previous is null || previous.Revision != revision) return false;
        var targetVersion = enabled ? version : previous.ActiveVersion;
        var targetMode = executionMode ?? previous.ExecutionMode;
        var targetLimit = setLimit ? maxConcurrency : previous.MaxConcurrency;
        if (targetLimit is < 1 or > 1024) throw new PackageException("Cluster concurrency must be 1–1024 or unlimited");
        if ((targetMode != previous.ExecutionMode || targetLimit != previous.MaxConcurrency) && await db.Jobs.AnyAsync(x => x.PackageId == id && x.State == JobState.Running && x.LeaseUntilUtc > (clock == null ? DateTime.UtcNow : clock.UtcNow), ct))
            throw new PackageConflictException("Drain running jobs before changing the package execution mode");
        var changed = await db.PluginChannels.Where(x => x.PackageId == id && x.Revision == revision)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ActiveVersion, targetVersion).SetProperty(x => x.Enabled, enabled)
                .SetProperty(x => x.MaxConcurrency, targetLimit).SetProperty(x => x.ExecutionMode, targetMode).SetProperty(x => x.Revision, x => x.Revision + 1).SetProperty(x => x.ChangedUtc, DateTime.UtcNow), ct) == 1;
        if (!changed) return false;
        db.PackageChanges.Add(new PackageChange { PackageId = id, PreviousVersion = previous.ActiveVersion, Version = targetVersion,
            Enabled = enabled, MaxConcurrency = targetLimit, PreviousMaxConcurrency = previous.MaxConcurrency, ExecutionMode = targetMode, PreviousExecutionMode = previous.ExecutionMode, Revision = revision + 1, Actor = actor.Length > 120 ? actor[..120] : actor });
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return true;
    }
    public async Task<PluginRequest> PinAsync(JsonElement payload, CancellationToken ct)
    {
        if (!Enabled) throw new PackageException("Package execution is disabled");
        PluginRequest request;
        try { request = payload.Deserialize<PluginRequest>(JsonSerializerOptions.Web) ?? throw new PackageException("Invalid package request"); }
        catch (JsonException) { throw new PackageException("Invalid package request"); }
        if (string.IsNullOrWhiteSpace(request.PackageId) || request.Payload.ValueKind != JsonValueKind.Object)
            throw new PackageException("Package ID and JSON object payload are required");
        await using var db = await factory.CreateDbContextAsync(ct);
        var channel = await db.PluginChannels.AsNoTracking().SingleOrDefaultAsync(x => x.PackageId == request.PackageId, ct);
        if (channel is null || !channel.Enabled) throw new PackageException("Package is not enabled");
        var version = request.Version ?? channel.ActiveVersion;
        if (version is null || !await db.PluginPackages.AnyAsync(x => x.PackageId == request.PackageId && x.Version == version, ct))
            throw new PackageException("Package version not installed");
        return request with { Version = version };
    }
    public async Task<(string Directory, PackageInfo Info)> MaterializeAsync(string id, string version, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var info = await db.PluginPackages.AsNoTracking().Where(x => x.PackageId == id && x.Version == version)
            .Select(x => new PackageInfo(x.PackageId, x.Version, x.EntryAssembly, x.EntryType, x.Digest, x.InstalledUtc, x.DefaultExecutionMode)).SingleOrDefaultAsync(ct)
            ?? throw new PackageException("Pinned plugin package is unavailable");
        var root = Path.GetFullPath(options.Value.CacheDirectory);
        var destination = Path.Combine(root, info.Digest);
        // Immutable, digest-addressed local ZIP cache avoids downloading the same DB blob for every job.
        if (info.Digest.Length != 64 || !info.Digest.All(char.IsAsciiHexDigit)) throw new PackageException("Invalid package digest");
        RejectLinks(root); Directory.CreateDirectory(root);
        var archiveDirectory = Path.Combine(root, "archives"); RejectLinks(archiveDirectory); Directory.CreateDirectory(archiveDirectory);
        var cached = Path.Combine(archiveDirectory, info.Digest + ".zip");
        byte[]? archive = null;
        if (File.Exists(cached))
        {
            var file = new FileInfo(cached);
            if (file.LinkTarget is not null) throw new PackageException("Archive cache must not contain symlinks");
            if (file.Length <= PackageArchive.MaxBytes)
            {
                var bytes = await File.ReadAllBytesAsync(cached, ct);
                if (PackageArchive.Hash(bytes) == info.Digest) archive = bytes;
            }
            if (archive is null) File.Delete(cached);
        }
        if (archive is null)
        {
            archive = await db.PluginPackages.Where(x => x.PackageId == id && x.Version == version).Select(x => x.Archive).SingleAsync(ct);
            if (archive.Length > PackageArchive.MaxBytes || PackageArchive.Hash(archive) != info.Digest) throw new PackageException("Package integrity check failed");
            var staging = cached + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(staging, archive, ct);
                try { File.Move(staging, cached); } catch (IOException) when (File.Exists(cached)) { }
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }
        RejectLinks(root);
        Directory.CreateDirectory(root);
        destination += "-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(destination);
        try { PackageArchive.Extract(archive, destination); return (destination, info); }
        catch { TryDelete(destination); throw; }
    }
    private static void RejectLinks(string root)
    {
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if (directory.LinkTarget is not null) throw new PackageException("Package cache must not use symlink/reparse-point directories");
    }
    public static void TryDelete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { /* Windows may retain DLL mappings until the collectible context is collected. */ }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class PackageJobHandler(PackageManager packages, IServiceProvider services, IsolatedExecutor? executor = null) : IJobHandler
{
    public const string JobType = "runner.plugin";
    public string Type => JobType;
    public async Task ExecuteAsync(JobContext context, CancellationToken ct)
    {
        if (!packages.Enabled) throw new JobExecutionException("package_execution_disabled");
        var request = JsonSerializer.Deserialize<PluginRequest>(context.Payload, JsonSerializerOptions.Web)
            ?? throw new JobExecutionException("invalid_package_job");
        if (request.Version is null) throw new JobExecutionException("package_version_not_pinned");
        var materialized = await packages.MaterializeAsync(request.PackageId, request.Version, ct);
        PluginLoadContext? loadContext = null;
        try
        {
            if (executor?.Enabled == true)
            {
                await executor.ExecuteAsync(context with { Payload = request.Payload.GetRawText() }, materialized.Directory, materialized.Info, null, ct);
                return;
            }
            var entry = PackageArchive.Inspect(materialized.Directory, materialized.Info.EntryAssembly, materialized.Info.EntryType);
            loadContext = entry.Context;
            var plugin = (IRunnerPlugin)ActivatorUtilities.CreateInstance(services, entry.EntryType);
            try { await plugin.ExecuteAsync(context with { Payload = request.Payload.GetRawText() }, ct); }
            finally
            {
                if (plugin is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
                else if (plugin is IDisposable disposable) disposable.Dispose();
            }
        }
        finally { loadContext?.Unload(); PackageManager.TryDelete(materialized.Directory); }
    }
}
