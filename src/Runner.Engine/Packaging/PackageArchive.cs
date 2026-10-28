using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Runner.Contracts;
namespace Runner.Engine.Packaging;

public static partial class PackageArchive
{
    public const int MaxBytes = 50 * 1024 * 1024;
    public const int MaxFiles = 512;
    [GeneratedRegex("^[a-z][a-z0-9.-]{0,47}$")]
    private static partial Regex PackageName();
    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$")]
    private static partial Regex ReleaseVersion();
    public static bool ValidIdentity(string? id, string? version) => id is not null && version is not null &&
        PackageName().IsMatch(id) && version.Length <= 64 && ReleaseVersion().IsMatch(version) &&
        (!version.Contains('-') || version[(version.IndexOf('-') + 1)..].Split('.').All(x => !x.All(char.IsDigit) || x.Length == 1 || x[0] != '0'));

    public static string SafePath(string path)
    {
        path = path.Replace('\\', '/');
        var parts = path.Split('/');
        if (path.Length is 0 or > 240 || parts.Length > 16 || parts.Any(p => p.Length is 0 or > 100 || p is "." or ".." ||
            p.EndsWith('.') || p.EndsWith(' ') || p.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c))))
            throw new PackageException("Package paths must be relative, portable paths without traversal or special characters");
        foreach (var part in parts)
        {
            var name = part.Split('.')[0].ToUpperInvariant();
            if (name is "CON" or "PRN" or "AUX" or "NUL" || Regex.IsMatch(name, "^(COM|LPT)[1-9]$"))
                throw new PackageException("Reserved Windows file name in package");
        }
        return path;
    }
    public static byte[] Build(IReadOnlyCollection<PackageFile> files, string entryAssembly)
    {
        if (files.Count is 0 or > MaxFiles || files.Sum(x => (long)x.Content.Length) > MaxBytes)
            throw new PackageException("Select 1–512 files, up to 50 MiB total");
        var normalized = files.Select(x => new PackageFile(SafePath(x.Path), x.Content)).ToList();
        if (normalized.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Count)
            throw new PackageException("Duplicate or case-colliding package paths");
        entryAssembly = SafePath(entryAssembly);
        if (!entryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !normalized.Any(x => x.Path == entryAssembly))
            throw new PackageException("Select an entry DLL included in the upload");
        if (normalized.Any(x => Path.GetFileName(x.Path).Equals("Runner.Contracts.dll", StringComparison.OrdinalIgnoreCase)))
            throw new PackageException("Runner.Contracts.dll is supplied by the host; exclude its copy from the package");
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in normalized.OrderBy(x => x.Path, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(file.Path, CompressionLevel.Fastest);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open(); stream.Write(file.Content);
            }
        if (buffer.Length > MaxBytes) throw new PackageException("Compressed package exceeds 50 MiB");
        return buffer.ToArray();
    }
    public static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
    public static void Extract(byte[] content, string destination)
    {
        using var archive = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
        if (archive.Entries.Count is 0 or > MaxFiles || archive.Entries.Sum(x => x.Length) > MaxBytes)
            throw new PackageException("Invalid package file count or size");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = SafePath(entry.FullName);
            if (!names.Add(name) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new PackageException("Duplicate files and symbolic links are not allowed");
            var target = Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open(); using var output = new FileStream(target, FileMode.CreateNew);
            input.CopyTo(output);
        }
    }
    public static (PluginLoadContext Context, Type EntryType) Inspect(string directory, string entryAssembly, string? entryType)
    {
        var context = new PluginLoadContext(Path.Combine(directory, SafePath(entryAssembly)));
        try
        {
            var assembly = context.LoadFromAssemblyPath(Path.Combine(directory, entryAssembly));
            var candidates = assembly.GetExportedTypes().Where(t => t.IsClass && !t.IsAbstract && !t.ContainsGenericParameters &&
                typeof(IRunnerPlugin).IsAssignableFrom(t)).ToList();
            if (!string.IsNullOrWhiteSpace(entryType)) candidates = candidates.Where(x => x.FullName == entryType).ToList();
            if (candidates.Count != 1) throw new PackageException("Entry DLL must expose one IRunnerPlugin implementation, or specify its full entry type name");
            // Resolve declared managed dependencies before accepting the package. Constructors are not invoked here.
            foreach (var dependency in assembly.GetReferencedAssemblies()) context.LoadFromAssemblyName(dependency);
            if (!candidates[0].GetConstructors().Any()) throw new PackageException("Plugin entry point needs a public constructor");
            return (context, candidates[0]);
        }
        catch (Exception e) when (e is BadImageFormatException or FileNotFoundException or FileLoadException or TypeLoadException or ReflectionTypeLoadException)
        { context.Unload(); throw new PackageException("Entry DLL or its dependencies could not load. Include compatible .NET 10 dependencies and .deps.json."); }
        catch { context.Unload(); throw; }
    }
}

public sealed class PluginLoadContext(string assemblyPath) : AssemblyLoadContext(isCollectible: true)
{
    private readonly AssemblyDependencyResolver resolver = new(assemblyPath);
    private readonly string directory = Path.GetDirectoryName(assemblyPath)!;
    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == typeof(IRunnerPlugin).Assembly.GetName().Name || name.Name == typeof(PluginLoadContext).Assembly.GetName().Name ||
            name.Name?.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) == true) return null;
        var path = resolver.ResolveAssemblyToPath(name);
        if (path is null && name.Name is not null)
        {
            var adjacent = Path.Combine(directory, name.Name + ".dll");
            if (File.Exists(adjacent)) path = adjacent;
        }
        return path is null ? null : LoadFromAssemblyPath(path);
    }
    protected override nint LoadUnmanagedDll(string name)
    {
        var path = resolver.ResolveUnmanagedDllToPath(name);
        return path is null ? 0 : LoadUnmanagedDllFromPath(path);
    }
}
