using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Application.Validation;

namespace OpenLineOps.Plugins.Application.Lifecycle;

public sealed class PluginLifecycleManager : IPluginLifecycleManager
{
    private readonly IPluginPackageCatalog _packageCatalog;
    private readonly IPluginManifestValidator _manifestValidator;
    private readonly IPluginInstanceActivator _pluginActivator;
    private readonly Dictionary<string, IOpenLineOpsPlugin> _activePlugins = new(StringComparer.Ordinal);

    public PluginLifecycleManager(
        IPluginPackageCatalog packageCatalog,
        IPluginManifestValidator manifestValidator,
        IPluginInstanceActivator pluginActivator)
    {
        _packageCatalog = packageCatalog;
        _manifestValidator = manifestValidator;
        _pluginActivator = pluginActivator;
    }

    public async ValueTask<IReadOnlyCollection<PluginLifecycleRecord>> StartAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var packages = await _packageCatalog
            .DiscoverAsync(cancellationToken)
            .ConfigureAwait(false);
        List<PluginLifecycleRecord> records = [];

        foreach (var package in packages.OrderBy(candidate => candidate.Manifest.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var validationReport = _manifestValidator.Validate(package.Manifest);
            if (!validationReport.IsValid)
            {
                records.Add(PluginLifecycleRecord.Invalid(package.Manifest, validationReport.Issues));

                continue;
            }

            records.Add(await StartPluginAsync(package, services, cancellationToken).ConfigureAwait(false));
        }

        return records;
    }

    public async ValueTask<IReadOnlyCollection<PluginLifecycleRecord>> StopAsync(
        CancellationToken cancellationToken = default)
    {
        List<PluginLifecycleRecord> records = [];

        foreach (var plugin in _activePlugins.Values.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await plugin.DisposeAsync().ConfigureAwait(false);
                records.Add(PluginLifecycleRecord.Stopped(plugin.Manifest));
            }
            catch (Exception exception)
            {
                records.Add(PluginLifecycleRecord.Failed(
                    plugin.Manifest,
                    $"Plugin stop failed: {exception.Message}"));
            }
        }

        _activePlugins.Clear();

        return records;
    }

    private async ValueTask<PluginLifecycleRecord> StartPluginAsync(
        PluginPackageDescriptor package,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        PluginActivationResult activationResult;
        try
        {
            activationResult = await _pluginActivator
                .ActivateAsync(package, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return PluginLifecycleRecord.Failed(
                package.Manifest,
                $"Plugin activation failed: {exception.Message}");
        }

        if (!activationResult.Succeeded || activationResult.Plugin is null)
        {
            return PluginLifecycleRecord.Failed(
                package.Manifest,
                activationResult.FailureReason ?? "Plugin activation failed.");
        }

        var plugin = activationResult.Plugin;
        try
        {
            var status = await plugin
                .InitializeAsync(services, cancellationToken)
                .ConfigureAwait(false);

            return await HandleInitializationStatusAsync(plugin, status).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await plugin.DisposeAsync().ConfigureAwait(false);

            return PluginLifecycleRecord.Failed(
                package.Manifest,
                $"Plugin initialization failed: {exception.Message}");
        }
    }

    private async ValueTask<PluginLifecycleRecord> HandleInitializationStatusAsync(
        IOpenLineOpsPlugin plugin,
        PluginInitializationStatus status)
    {
        if (status == PluginInitializationStatus.Initialized)
        {
            _activePlugins[plugin.Manifest.Id] = plugin;

            return PluginLifecycleRecord.Initialized(plugin.Manifest);
        }

        if (status == PluginInitializationStatus.Degraded)
        {
            _activePlugins[plugin.Manifest.Id] = plugin;

            return PluginLifecycleRecord.Degraded(plugin.Manifest);
        }

        await plugin.DisposeAsync().ConfigureAwait(false);

        return PluginLifecycleRecord.Failed(
            plugin.Manifest,
            $"Plugin returned initialization status {status}.");
    }
}
