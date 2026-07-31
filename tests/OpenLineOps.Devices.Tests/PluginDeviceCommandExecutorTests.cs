using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Infrastructure.Execution;
using OpenLineOps.Plugins.Application.Commands;
using OpenLineOps.Plugins.Application.Discovery;

namespace OpenLineOps.Devices.Tests;

public sealed class PluginDeviceCommandExecutorTests
{
    [Fact]
    public async Task ExecuteAsyncUsesFrozenReleasePackageIdentity()
    {
        var invoker = new CapturingPluginDeviceCommandInvoker(
            PluginDeviceCommandInvocationResult.Completed("{\"barcode\":\"ABC-123\"}"));
        var executor = new PluginDeviceCommandExecutor(invoker);

        var result = await executor.ExecuteAsync(CreateRequest(LockedPackage()));

        Assert.Equal(DeviceCommandExecutionOutcome.Completed, result.Outcome);
        Assert.Equal("{\"barcode\":\"ABC-123\"}", result.ResultPayload);
        var request = Assert.IsType<PluginDeviceCommandInvocationRequest>(invoker.Request);
        Assert.Equal("openlineops.scanner-driver", request.PluginId);
        Assert.Equal("scanner-01", request.DeviceInstanceId);
        Assert.Equal("device.scanner:scan", request.CommandDefinitionId);
        var identity = Assert.IsType<PluginPackageExecutionIdentity>(request.PackageIdentity);
        Assert.Equal("project.main", identity.ProjectId);
        Assert.Equal("application.main", identity.ApplicationId);
        Assert.Equal("4.5.6", identity.PackageIdentity.Version);
        Assert.Equal(new string('a', 64), identity.PackageIdentity.PackageContentSha256);
    }

    [Fact]
    public async Task ExecuteAsyncRejectsIncompleteFrozenPackageIdentity()
    {
        var invoker = new CapturingPluginDeviceCommandInvoker(
            PluginDeviceCommandInvocationResult.Completed("should-not-run"));
        var executor = new PluginDeviceCommandExecutor(invoker);
        var package = LockedPackage() with { PackageContentSha256 = string.Empty };

        var result = await executor.ExecuteAsync(CreateRequest(package));

        Assert.Equal(DeviceCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.Contains("incomplete", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(invoker.Request);
    }

    [Theory]
    [InlineData(PluginDeviceCommandInvocationOutcome.Failed, DeviceCommandExecutionOutcome.Failed)]
    [InlineData(PluginDeviceCommandInvocationOutcome.Rejected, DeviceCommandExecutionOutcome.Rejected)]
    [InlineData(PluginDeviceCommandInvocationOutcome.TimedOut, DeviceCommandExecutionOutcome.TimedOut)]
    public async Task ExecuteAsyncMapsPluginTerminalOutcomes(
        PluginDeviceCommandInvocationOutcome pluginOutcome,
        DeviceCommandExecutionOutcome expectedOutcome)
    {
        var invoker = new CapturingPluginDeviceCommandInvoker(new PluginDeviceCommandInvocationResult(
            pluginOutcome,
            null,
            "plugin terminal outcome"));
        var executor = new PluginDeviceCommandExecutor(invoker);

        var result = await executor.ExecuteAsync(CreateRequest(LockedPackage()));

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal("plugin terminal outcome", result.FailureReason);
    }

    private static DevicePluginPackageIdentity LockedPackage()
    {
        return new DevicePluginPackageIdentity(
            "openlineops.scanner-driver",
            "4.5.6",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "1.0.0",
            "win-x64",
            "openlineops.plugin-abi/1");
    }

    private static DeviceCommandExecutionRequest CreateRequest(DevicePluginPackageIdentity package)
    {
        return new DeviceCommandExecutionRequest(
            "project.main",
            "application.main",
            ProjectReleaseRuntimeProviderKinds.PluginCommand,
            "plugin://openlineops.scanner-driver/device.scanner:scan",
            new DeviceInstanceId("scanner-01"),
            new DeviceCommandDefinitionId("device.scanner:scan"),
            new DeviceCapabilityId("device.scanner"),
            "Scan",
            "{\"serial\":\"ABC\"}",
            TimeSpan.FromSeconds(30),
            package);
    }

    private sealed class CapturingPluginDeviceCommandInvoker(
        PluginDeviceCommandInvocationResult result) : IPluginDeviceCommandInvoker
    {
        public PluginDeviceCommandInvocationRequest? Request { get; private set; }

        public ValueTask<PluginDeviceCommandInvocationResult> ExecuteAsync(
            PluginDeviceCommandInvocationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return ValueTask.FromResult(result);
        }
    }
}
