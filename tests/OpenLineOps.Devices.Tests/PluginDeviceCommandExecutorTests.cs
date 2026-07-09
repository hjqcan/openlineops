using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Infrastructure.Execution;
using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Commands;

namespace OpenLineOps.Devices.Tests;

public sealed class PluginDeviceCommandExecutorTests
{
    [Fact]
    public async Task ExecuteAsyncInvokesPluginDeviceCommand()
    {
        var invoker = new CapturingPluginDeviceCommandInvoker(
            PluginDeviceCommandInvocationResult.Completed("{\"barcode\":\"ABC-123\"}"));
        var executor = new PluginDeviceCommandExecutor(
            new InMemoryPluginDeviceCommandInventory(ScannerCommand),
            invoker);

        var result = await executor.ExecuteAsync(CreateRequest());

        Assert.Equal(DeviceCommandExecutionOutcome.Completed, result.Outcome);
        Assert.Equal("{\"barcode\":\"ABC-123\"}", result.ResultPayload);
        Assert.NotNull(invoker.Request);
        Assert.Equal("openlineops.scanner-driver", invoker.Request.PluginId);
        Assert.Equal("scanner-01", invoker.Request.DeviceInstanceId);
        Assert.Equal("device.scanner:scan", invoker.Request.CommandDefinitionId);
        Assert.Equal("device.scanner", invoker.Request.Capability);
        Assert.Equal("Scan", invoker.Request.CommandName);
        Assert.Equal("{\"serial\":\"ABC\"}", invoker.Request.InputPayload);
        Assert.Equal(30000, invoker.Request.TimeoutMilliseconds);
    }

    [Fact]
    public async Task ExecuteAsyncRejectsCommandWhenPluginManifestDoesNotDeclareIt()
    {
        var invoker = new CapturingPluginDeviceCommandInvoker(
            PluginDeviceCommandInvocationResult.Completed("should-not-run"));
        var executor = new PluginDeviceCommandExecutor(
            new InMemoryPluginDeviceCommandInventory(),
            invoker);

        var result = await executor.ExecuteAsync(CreateRequest());

        Assert.Equal(DeviceCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.Contains("No plugin device command", result.FailureReason, StringComparison.Ordinal);
        Assert.Null(invoker.Request);
    }

    [Fact]
    public async Task ExecuteAsyncRejectsCommandWhenDefinitionDoesNotMatchManifestCommand()
    {
        var invoker = new CapturingPluginDeviceCommandInvoker(
            PluginDeviceCommandInvocationResult.Completed("should-not-run"));
        var executor = new PluginDeviceCommandExecutor(
            new InMemoryPluginDeviceCommandInventory(ScannerCommand),
            invoker);

        var result = await executor.ExecuteAsync(CreateRequest(commandDefinitionId: "device.scanner:other"));

        Assert.Equal(DeviceCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.Contains("does not match requested definition", result.FailureReason, StringComparison.Ordinal);
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
        var executor = new PluginDeviceCommandExecutor(
            new InMemoryPluginDeviceCommandInventory(ScannerCommand),
            invoker);

        var result = await executor.ExecuteAsync(CreateRequest());

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal("plugin terminal outcome", result.FailureReason);
    }

    private static PluginDeviceCommandDescriptor ScannerCommand { get; } = new(
        "openlineops.scanner-driver",
        "Scanner Driver",
        PluginKind.DeviceDriver,
        "device.scanner:scan",
        "device.scanner",
        "Scan",
        "{\"type\":\"object\"}",
        "{\"type\":\"object\"}",
        30000,
        0);

    private static DeviceCommandExecutionRequest CreateRequest(
        string commandDefinitionId = "device.scanner:scan")
    {
        return new DeviceCommandExecutionRequest(
            new DeviceInstanceId("scanner-01"),
            new DeviceCommandDefinitionId(commandDefinitionId),
            new DeviceCapabilityId("device.scanner"),
            "Scan",
            "{\"serial\":\"ABC\"}",
            TimeSpan.FromSeconds(30));
    }

    private sealed class InMemoryPluginDeviceCommandInventory(
        params PluginDeviceCommandDescriptor[] commands) : IPluginDeviceCommandInventory
    {
        public ValueTask<IReadOnlyCollection<PluginDeviceCommandDescriptor>> ListDeviceCommandsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult<IReadOnlyCollection<PluginDeviceCommandDescriptor>>(commands);
        }
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
