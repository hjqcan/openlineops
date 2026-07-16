using OpenLineOps.Plugins.Application.Commands;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Infrastructure.Lifecycle;

namespace OpenLineOps.Plugins.Tests;

public sealed class ExternalPluginProcessRegistryTests
{
    [Fact]
    public void ExactScopeAndPackageHashAreRequiredForLookupAndRemoval()
    {
        var registry = new ExternalPluginProcessRegistry();
        var process = new StubExternalPluginProcess();
        var registered = Identity("project-a", "application-a", 'a');

        registry.Register(registered, process);

        Assert.True(registry.TryGet(registered, out var exact));
        Assert.Same(process, exact);
        Assert.False(registry.TryGet(Identity("project-b", "application-a", 'a'), out _));
        Assert.False(registry.TryGet(Identity("project-a", "application-b", 'a'), out _));
        Assert.False(registry.TryGet(Identity("project-a", "application-a", 'b'), out _));
        registry.Unregister(Identity("project-b", "application-a", 'a'), process);
        Assert.True(registry.TryGet(registered, out _));
        registry.Unregister(registered, process);
        Assert.False(registry.TryGet(registered, out _));
    }

    private static PluginPackageExecutionIdentity Identity(
        string projectId,
        string applicationId,
        char contentHashCharacter) => new(
        projectId,
        applicationId,
        new PluginPackageRuntimeIdentity(
            "plugin.exact",
            "1.2.3",
            new string(contentHashCharacter, 64),
            new string('c', 64),
            new string('d', 64),
            "1.0.0",
            "win-x64",
            "openlineops.plugin-abi/1"));

    private sealed class StubExternalPluginProcess : IExternalPluginProcess
    {
        public bool HasExited => false;

        public ValueTask<PluginDeviceCommandInvocationResult> ExecuteDeviceCommandAsync(
            PluginDeviceCommandInvocationRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PluginDeviceCommandInvocationResult.Completed());

        public ValueTask<PluginProcessCommandInvocationResult> ExecuteProcessCommandAsync(
            PluginProcessCommandInvocationRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PluginProcessCommandInvocationResult.Completed());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
