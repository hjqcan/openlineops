using System.Reflection;
using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Infrastructure.Lifecycle;

namespace OpenLineOps.Plugins.Tests;

public sealed class AssemblyLoadContextPluginInstanceActivatorTests
{
    [Fact]
    public async Task ActivateAsyncLoadsPluginFromEntryAssembly()
    {
        var manifest = CreateManifest(
            typeof(LoadableAssemblyPlugin),
            LoadableAssemblyPlugin.ManifestId);
        var activator = new AssemblyLoadContextPluginInstanceActivator();

        var result = await activator.ActivateAsync(Package(manifest));

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.NotNull(result.Plugin);
        Assert.Equal(LoadableAssemblyPlugin.ManifestId, result.Plugin.Manifest.Id);
        Assert.Equal(PluginInitializationStatus.Initialized, await result.Plugin.InitializeAsync(new EmptyServiceProvider()));

        await result.Plugin.DisposeAsync();
    }

    [Fact]
    public async Task ActivateAsyncRejectsEntryTypeThatDoesNotImplementPluginContract()
    {
        var manifest = CreateManifest(
            typeof(NotAPlugin),
            "openlineops.not-a-plugin");
        var activator = new AssemblyLoadContextPluginInstanceActivator();

        var result = await activator.ActivateAsync(Package(manifest));

        Assert.False(result.Succeeded);
        Assert.Contains("must implement", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActivateAsyncRejectsEntryAssemblyOutsidePackageDirectory()
    {
        var manifest = CreateManifest(
            typeof(LoadableAssemblyPlugin),
            LoadableAssemblyPlugin.ManifestId,
            entryAssembly: Path.Combine("..", Path.GetFileName(TestAssemblyPath)));
        var activator = new AssemblyLoadContextPluginInstanceActivator();

        var result = await activator.ActivateAsync(Package(manifest));

        Assert.False(result.Succeeded);
        Assert.Contains("outside package directory", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActivateAsyncRejectsPluginManifestIdMismatch()
    {
        var manifest = CreateManifest(
            typeof(LoadableAssemblyPlugin),
            "openlineops.mismatched-plugin");
        var activator = new AssemblyLoadContextPluginInstanceActivator();

        var result = await activator.ActivateAsync(Package(manifest));

        Assert.False(result.Succeeded);
        Assert.Contains("does not match package manifest id", result.FailureReason, StringComparison.Ordinal);
    }

    private static string TestAssemblyPath => typeof(AssemblyLoadContextPluginInstanceActivatorTests).Assembly.Location;

    private static PluginPackageDescriptor Package(PluginManifest manifest)
    {
        var packagePath = Path.GetDirectoryName(TestAssemblyPath)
            ?? throw new InvalidOperationException("Test assembly path has no directory.");

        return new PluginPackageDescriptor(
            manifest,
            packagePath,
            Path.Combine(packagePath, "manifest.json"));
    }

    private static PluginManifest CreateManifest(
        Type entryType,
        string manifestId,
        string? entryAssembly = null)
    {
        return new PluginManifest(
            manifestId,
            manifestId,
            "1.0.0",
            PluginKind.DeviceDriver,
            entryAssembly ?? Path.GetFileName(TestAssemblyPath),
            entryType.FullName ?? throw new InvalidOperationException("Entry type has no full name."),
            ["device.test"]);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            return null;
        }
    }
}

public sealed class LoadableAssemblyPlugin : IOpenLineOpsPlugin
{
    public const string ManifestId = "openlineops.loadable-test-plugin";

    public PluginManifest Manifest { get; } = new(
        ManifestId,
        "Loadable Test Plugin",
        "1.0.0",
        PluginKind.DeviceDriver,
        "OpenLineOps.Plugins.Tests.dll",
        typeof(LoadableAssemblyPlugin).FullName!,
        ["device.test"]);

    public ValueTask<PluginInitializationStatus> InitializeAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(PluginInitializationStatus.Initialized);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

public sealed class NotAPlugin;
