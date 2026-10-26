using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using Runner.Contracts;
namespace Runner.Engine;

public sealed class HandlerCatalog(IEnumerable<IJobHandler> handlers)
{
    public IReadOnlyDictionary<string, Type> Types { get; } = handlers.ToDictionary(x => x.Type, x => x.GetType(), StringComparer.Ordinal);
}

public static class PluginLoader
{
    public static void AddPlugins(IServiceCollection services, IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var full = Path.GetFullPath(path);
            var assembly = new PluginContext(full).LoadFromAssemblyPath(full);
            foreach (var type in assembly.GetTypes().Where(t => !t.IsAbstract && !t.IsInterface && typeof(IJobHandler).IsAssignableFrom(t)))
            {
                services.AddScoped(type);
                services.AddScoped(typeof(IJobHandler), sp => (IJobHandler)sp.GetRequiredService(type));
            }
        }
    }
    private sealed class PluginContext(string path) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver resolver = new(path);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IJobHandler).Assembly.GetName().Name || name.Name?.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) == true)
                return null;
            var dependency = resolver.ResolveAssemblyToPath(name);
            return dependency is null ? null : LoadFromAssemblyPath(dependency);
        }
        protected override nint LoadUnmanagedDll(string name)
        {
            var dependency = resolver.ResolveUnmanagedDllToPath(name);
            return dependency is null ? 0 : LoadUnmanagedDllFromPath(dependency);
        }
    }
}
