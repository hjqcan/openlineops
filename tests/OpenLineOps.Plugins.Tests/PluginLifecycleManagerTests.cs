using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Application.Lifecycle;
using OpenLineOps.Plugins.Application.Validation;

namespace OpenLineOps.Plugins.Tests;

public sealed class PluginLifecycleManagerTests
{
    [Fact]
    public async Task StartAsyncInitializesValidPluginsAndKeepsFailureIsolated()
    {
        var initializedManifest = CreateManifest("plugin.initialized", "device.scanner");
        var failedManifest = CreateManifest("plugin.failed", "device.camera");
        var catalog = new InMemoryPluginPackageCatalog(
            Package(initializedManifest),
            Package(failedManifest));
        var initializedPlugin = new FakePlugin(initializedManifest, PluginInitializationStatus.Initialized);
        var failedPlugin = new FakePlugin(failedManifest, PluginInitializationStatus.Failed);
        var activator = new ScriptedPluginActivator(initializedPlugin, failedPlugin);
        var manager = new PluginLifecycleManager(
            catalog,
            new PluginManifestValidator(),
            activator);

        var records = await manager.StartAsync(new EmptyServiceProvider());

        Assert.Equal(2, records.Count);
        Assert.Contains(records, record =>
            record.Manifest.Id == initializedManifest.Id
            && record.State == PluginLifecycleState.Initialized);
        Assert.Contains(records, record =>
            record.Manifest.Id == failedManifest.Id
            && record.State == PluginLifecycleState.Failed);
        Assert.False(initializedPlugin.Disposed);
        Assert.True(failedPlugin.Disposed);
    }

    [Fact]
    public async Task StartAsyncDoesNotActivateInvalidManifest()
    {
        var invalidManifest = new PluginManifest(
            "",
            "Invalid",
            "1.0.0",
            PluginKind.DeviceDriver,
            "Invalid.dll",
            "Invalid.Plugin",
            []);
        var activator = new ScriptedPluginActivator();
        var manager = new PluginLifecycleManager(
            new InMemoryPluginPackageCatalog(Package(invalidManifest)),
            new PluginManifestValidator(),
            activator);

        var records = await manager.StartAsync(new EmptyServiceProvider());

        var record = Assert.Single(records);
        Assert.Equal(PluginLifecycleState.Invalid, record.State);
        Assert.NotEmpty(record.ValidationIssues);
        Assert.Equal(0, activator.ActivationCount);
    }

    [Fact]
    public async Task StopAsyncDisposesInitializedAndDegradedPlugins()
    {
        var initializedManifest = CreateManifest("plugin.initialized", "device.scanner");
        var degradedManifest = CreateManifest("plugin.degraded", "device.camera");
        var initializedPlugin = new FakePlugin(initializedManifest, PluginInitializationStatus.Initialized);
        var degradedPlugin = new FakePlugin(degradedManifest, PluginInitializationStatus.Degraded);
        var manager = new PluginLifecycleManager(
            new InMemoryPluginPackageCatalog(Package(initializedManifest), Package(degradedManifest)),
            new PluginManifestValidator(),
            new ScriptedPluginActivator(initializedPlugin, degradedPlugin));

        var startRecords = await manager.StartAsync(new EmptyServiceProvider());
        var stopRecords = await manager.StopAsync();

        Assert.Contains(startRecords, record => record.State == PluginLifecycleState.Initialized);
        Assert.Contains(startRecords, record => record.State == PluginLifecycleState.Degraded);
        Assert.Equal(2, stopRecords.Count);
        Assert.All(stopRecords, record => Assert.Equal(PluginLifecycleState.Stopped, record.State));
        Assert.True(initializedPlugin.Disposed);
        Assert.True(degradedPlugin.Disposed);
    }

    private static PluginPackageDescriptor Package(PluginManifest manifest)
    {
        return new PluginPackageDescriptor(manifest, "plugins/test", "plugins/test/openlineops-plugin.json");
    }

    private static PluginManifest CreateManifest(string id, string capability)
    {
        return new PluginManifest(
            id,
            id,
            "1.0.0",
            PluginKind.DeviceDriver,
            $"{id}.dll",
            $"{id}.Plugin",
            [capability]);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            return null;
        }
    }

    private sealed class InMemoryPluginPackageCatalog(
        params PluginPackageDescriptor[] packages) : IPluginPackageCatalog
    {
        public ValueTask<IReadOnlyCollection<PluginPackageDescriptor>> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<IReadOnlyCollection<PluginPackageDescriptor>>(packages);
        }
    }

    private sealed class ScriptedPluginActivator(params IOpenLineOpsPlugin[] plugins) : IPluginInstanceActivator
    {
        private readonly Queue<IOpenLineOpsPlugin> _plugins = new(plugins);

        public int ActivationCount { get; private set; }

        public ValueTask<PluginActivationResult> ActivateAsync(
            PluginPackageDescriptor package,
            CancellationToken cancellationToken = default)
        {
            ActivationCount++;

            return ValueTask.FromResult(
                _plugins.Count == 0
                    ? PluginActivationResult.Failure("No scripted plugin available.")
                    : PluginActivationResult.Success(_plugins.Dequeue()));
        }
    }

    private sealed class FakePlugin(
        PluginManifest manifest,
        PluginInitializationStatus initializationStatus) : IOpenLineOpsPlugin
    {
        public PluginManifest Manifest { get; } = manifest;

        public bool Disposed { get; private set; }

        public ValueTask<PluginInitializationStatus> InitializeAsync(
            IServiceProvider services,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(initializationStatus);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;

            return ValueTask.CompletedTask;
        }
    }
}
