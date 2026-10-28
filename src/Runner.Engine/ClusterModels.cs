using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Runner.Engine.Packaging;

namespace Runner.Engine;

public sealed class WorkerNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime HeartbeatUtc { get; set; }
    public bool DrainRequested { get; set; }
    public DateTime? DrainDeadlineUtc { get; set; }
    public DateTime? DrainAcknowledgedUtc { get; set; }
    public int ActiveJobs { get; set; }
    public int Concurrency { get; set; }
    public string CapabilitiesJson { get; set; } = "{}";
}

public sealed class ClusterLease
{
    public string Name { get; set; } = "";
    public string? Owner { get; set; }
    public DateTime UntilUtc { get; set; }
}

public sealed class WorkflowRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class JobDependency
{
    public Guid JobId { get; set; }
    public Guid DependsOnJobId { get; set; }
}

public sealed class AuditRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Actor { get; set; } = "";
    public string AuthenticationType { get; set; } = "";
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string TraceId { get; set; } = "";
    public string DetailsJson { get; set; } = "{}";
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }
    public int? StatusCode { get; set; }
}

public sealed record NodeCapabilities(string Os, string Architecture, string HostVersion, string SdkVersion,
    string[] Resources, string[] Tags, string[] Handlers, string[] Queues)
{
    public static NodeCapabilities Create(OperationOptions operations, RunnerOptions runner, HandlerCatalog catalog) => new(
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "osx",
        RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), "1.0.0", "0.1.0",
        operations.Connections.Keys.Select(x => "connection:" + x).Concat(operations.Applications.Keys.Select(x => "application:" + x)).ToArray(),
        runner.Tags, catalog.Types.Keys.ToArray(), runner.Queues);
}

/// <summary>Portable runner.manifest.json, pinned as part of the immutable archive.</summary>
public sealed record PluginManifest(int SchemaVersion = 1, string MinimumHostVersion = "1.0.0", string MinimumSdkVersion = "0.1.0",
    string[]? OperatingSystems = null, string[]? Architectures = null, string[]? RequiredResources = null, string[]? RequiredTags = null)
{
    public static PluginManifest Parse(string json)
    {
        PluginManifest result;
        try { result = JsonSerializer.Deserialize<PluginManifest>(json, JsonSerializerOptions.Web) ?? throw new JsonException(); }
        catch (JsonException) { throw new PackageException("Invalid runner.manifest.json"); }
        if (json.Length > 16384 || result.SchemaVersion != 1 || !Version.TryParse(result.MinimumHostVersion, out _) ||
            !Version.TryParse(result.MinimumSdkVersion, out _) || result.MinimumHostVersion.Length > 32 || result.MinimumSdkVersion.Length > 32 ||
            (result.OperatingSystems ?? []).Any(x => x is not ("windows" or "linux" or "osx")) ||
            (result.Architectures ?? []).Any(x => x is not ("x64" or "arm64" or "x86" or "arm")) ||
            (result.RequiredResources ?? []).Concat(result.RequiredTags ?? []).Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 120) ||
            (result.RequiredResources?.Length ?? 0) + (result.RequiredTags?.Length ?? 0) > 64)
            throw new PackageException("Unsupported manifest schema, version or capabilities");
        return result;
    }

    public string[] Incompatibilities(NodeCapabilities node)
    {
        var reasons = new List<string>();
        if (Version.Parse(node.HostVersion) < Version.Parse(MinimumHostVersion)) reasons.Add("Host version " + MinimumHostVersion + " required");
        if (Version.Parse(node.SdkVersion) < Version.Parse(MinimumSdkVersion)) reasons.Add("SDK version " + MinimumSdkVersion + " required");
        if (OperatingSystems is { Length: > 0 } && !OperatingSystems.Contains(node.Os)) reasons.Add("Unsupported OS: " + node.Os);
        if (Architectures is { Length: > 0 } && !Architectures.Contains(node.Architecture)) reasons.Add("Unsupported architecture: " + node.Architecture);
        reasons.AddRange((RequiredResources ?? []).Except(node.Resources, StringComparer.Ordinal).Select(x => "Missing resource: " + x));
        reasons.AddRange((RequiredTags ?? []).Except(node.Tags, StringComparer.Ordinal).Select(x => "Missing tag: " + x));
        return reasons.ToArray();
    }
}
