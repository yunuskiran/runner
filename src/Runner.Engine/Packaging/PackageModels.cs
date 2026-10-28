namespace Runner.Engine.Packaging;

public sealed class PluginPackage
{
    public string PackageId { get; set; } = "";
    public string Version { get; set; } = "";
    public string EntryAssembly { get; set; } = "";
    public string EntryType { get; set; } = "";
    public string Digest { get; set; } = "";
    public byte[] Archive { get; set; } = [];
    public DateTime InstalledUtc { get; set; } = DateTime.UtcNow;
}
public sealed class PluginChannel
{
    public string PackageId { get; set; } = "";
    public string? ActiveVersion { get; set; }
    public bool Enabled { get; set; }
    public int Revision { get; set; }
    public DateTime ChangedUtc { get; set; } = DateTime.UtcNow;
}
public sealed class PackageOptions
{
    public bool Enabled { get; set; } = true;
    public string CacheDirectory { get; set; } = "data/package-cache";
}
public sealed record PackageFile(string Path, byte[] Content);
public sealed record PackageInfo(string PackageId, string Version, string EntryAssembly, string EntryType, string Digest, DateTime InstalledUtc);
public sealed record PluginRequest(string PackageId, string? Version, System.Text.Json.JsonElement Payload);
public sealed class PackageException(string message) : Exception(message);
public sealed class PackageConflictException(string message) : Exception(message);

public sealed class PackageChange
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PackageId { get; set; } = "";
    public string? PreviousVersion { get; set; }
    public string? Version { get; set; }
    public bool Enabled { get; set; }
    public int Revision { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Actor { get; set; } = "operator";
}
