using System.Reflection;
using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Application.Lifecycle;

namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public sealed class AssemblyLoadContextPluginInstanceActivator : IPluginInstanceActivator
{
    private static readonly IReadOnlyDictionary<string, Assembly> SharedAssemblies =
        new Dictionary<string, Assembly>(StringComparer.Ordinal)
        {
            [typeof(IOpenLineOpsPlugin).Assembly.GetName().Name!] = typeof(IOpenLineOpsPlugin).Assembly
        };

    public async ValueTask<PluginActivationResult> ActivateAsync(
        PluginPackageDescriptor package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();

        var assemblyPath = ResolveEntryAssemblyPath(package);
        if (assemblyPath is null)
        {
            return PluginActivationResult.Failure(
                $"Plugin entry assembly '{package.Manifest.EntryAssembly}' is outside package directory '{package.PackagePath}'.");
        }

        if (!File.Exists(assemblyPath))
        {
            return PluginActivationResult.Failure(
                $"Plugin entry assembly '{assemblyPath}' was not found.");
        }

        var loadContext = new PluginAssemblyLoadContext(assemblyPath, SharedAssemblies);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var pluginType = assembly.GetType(package.Manifest.EntryType, throwOnError: false, ignoreCase: false);
            if (pluginType is null)
            {
                loadContext.Unload();

                return PluginActivationResult.Failure(
                    $"Plugin entry type '{package.Manifest.EntryType}' was not found in '{assemblyPath}'.");
            }

            if (!typeof(IOpenLineOpsPlugin).IsAssignableFrom(pluginType))
            {
                loadContext.Unload();

                return PluginActivationResult.Failure(
                    $"Plugin entry type '{package.Manifest.EntryType}' must implement {nameof(IOpenLineOpsPlugin)}.");
            }

            if (Activator.CreateInstance(pluginType) is not IOpenLineOpsPlugin plugin)
            {
                loadContext.Unload();

                return PluginActivationResult.Failure(
                    $"Plugin entry type '{package.Manifest.EntryType}' could not be constructed.");
            }

            if (!string.Equals(plugin.Manifest.Id, package.Manifest.Id, StringComparison.Ordinal))
            {
                return await DisposePluginAndFailAsync(
                    plugin,
                    loadContext,
                    $"Plugin manifest id '{plugin.Manifest.Id}' does not match package manifest id '{package.Manifest.Id}'.")
                    .ConfigureAwait(false);
            }

            return PluginActivationResult.Success(new AssemblyLoadedOpenLineOpsPlugin(plugin, loadContext));
        }
        catch (Exception exception)
        {
            loadContext.Unload();

            return PluginActivationResult.Failure($"Plugin assembly activation failed: {exception.Message}");
        }
    }

    private static async ValueTask<PluginActivationResult> DisposePluginAndFailAsync(
        IOpenLineOpsPlugin plugin,
        PluginAssemblyLoadContext loadContext,
        string failureReason)
    {
        try
        {
            await plugin.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            loadContext.Unload();
        }

        return PluginActivationResult.Failure(failureReason);
    }

    private static string? ResolveEntryAssemblyPath(PluginPackageDescriptor package)
    {
        var packagePath = Path.GetFullPath(package.PackagePath);
        var assemblyPath = Path.GetFullPath(Path.Combine(packagePath, package.Manifest.EntryAssembly));

        return IsPathInsideDirectory(assemblyPath, packagePath)
            ? assemblyPath
            : null;
    }

    private static bool IsPathInsideDirectory(string candidatePath, string directoryPath)
    {
        var relativePath = Path.GetRelativePath(directoryPath, candidatePath);

        return !relativePath.StartsWith("..", StringComparison.Ordinal)
            && !Path.IsPathRooted(relativePath);
    }

    private sealed class AssemblyLoadedOpenLineOpsPlugin(
        IOpenLineOpsPlugin inner,
        PluginAssemblyLoadContext loadContext) : IOpenLineOpsPlugin
    {
        public PluginManifest Manifest => inner.Manifest;

        public ValueTask<PluginInitializationStatus> InitializeAsync(
            IServiceProvider services,
            CancellationToken cancellationToken = default)
        {
            return inner.InitializeAsync(services, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                loadContext.Unload();
            }
        }
    }
}
