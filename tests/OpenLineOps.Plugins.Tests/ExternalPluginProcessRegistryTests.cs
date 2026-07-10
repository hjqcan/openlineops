using OpenLineOps.Plugins.Application.Commands;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Infrastructure.Lifecycle;

namespace OpenLineOps.Plugins.Tests;

public sealed class ExternalPluginProcessRegistryTests
{
    [Fact]
    public void ExactPackageLookupDoesNotFallBackToSamePluginIdWithDifferentContent()
    {
        var registry = new ExternalPluginProcessRegistry();
        var process = new StubExternalPluginProcess();
        var registered = Identity('a');
        var differentContent = Identity('b');

        registry.Register(registered, process);

        Assert.True(registry.TryGet(registered, out var exact));
        Assert.Same(process, exact);
        Assert.False(registry.TryGet(differentContent, out _));
        Assert.True(registry.TryGet(registered.PluginId, out var developmentLookup));
        Assert.Same(process, developmentLookup);
    }

    [Fact]
    public void PluginIdLookupDoesNotTrimOrCaseFoldAliases()
    {
        var registry = new ExternalPluginProcessRegistry();
        var process = new StubExternalPluginProcess();
        registry.Register("plugin.exact", process);

        Assert.False(registry.TryGet(" plugin.exact ", out _));
        Assert.False(registry.TryGet("Plugin.Exact", out _));
        Assert.Throws<ArgumentException>(() => registry.Register(" plugin.exact ", process));

        registry.Unregister(" plugin.exact ", process);

        Assert.True(registry.TryGet("plugin.exact", out var exact));
        Assert.Same(process, exact);
    }

    private static PluginPackageRuntimeIdentity Identity(char contentHashCharacter)
    {
        return new PluginPackageRuntimeIdentity(
            "plugin.exact",
            "1.2.3",
            new string(contentHashCharacter, 64),
            new string('c', 64),
            new string('d', 64),
            "1.0.0",
            "win-x64",
            "openlineops.plugin-abi/1");
    }

    private sealed class StubExternalPluginProcess : IExternalPluginProcess
    {
        public bool HasExited => false;

        public ValueTask<PluginDeviceCommandInvocationResult> ExecuteDeviceCommandAsync(
            PluginDeviceCommandInvocationRequest request,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(PluginDeviceCommandInvocationResult.Completed());
        }

        public ValueTask<PluginProcessCommandInvocationResult> ExecuteProcessCommandAsync(
            PluginProcessCommandInvocationRequest request,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(PluginProcessCommandInvocationResult.Completed());
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
