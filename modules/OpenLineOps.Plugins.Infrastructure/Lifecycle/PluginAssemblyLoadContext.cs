using System.Reflection;
using System.Runtime.Loader;

namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

internal sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly IReadOnlyDictionary<string, Assembly> _sharedAssemblies;

    public PluginAssemblyLoadContext(
        string mainAssemblyPath,
        IReadOnlyDictionary<string, Assembly> sharedAssemblies)
        : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        _sharedAssemblies = sharedAssemblies;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null
            && _sharedAssemblies.TryGetValue(assemblyName.Name, out var sharedAssembly))
        {
            return sharedAssembly;
        }

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);

        return assemblyPath is null
            ? null
            : LoadFromAssemblyPath(assemblyPath);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);

        return libraryPath is null
            ? IntPtr.Zero
            : LoadUnmanagedDllFromPath(libraryPath);
    }
}
