using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Infrastructure.Execution;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Devices.Tests;

public sealed class DeviceRuntimeCommandExecutorTests
{
    [Fact]
    public async Task ExecuteAsyncMapsFrozenReleaseRouteToDeviceRequest()
    {
        var deviceExecutor = new CapturingDeviceCommandExecutor(
            DeviceCommandExecutionResult.Completed("scan-ok"));
        var executor = new DeviceRuntimeCommandExecutor(deviceExecutor);

        var result = await executor.ExecuteAsync(CreateContext(), SimulatorRoute());

        Assert.Equal(RuntimeCommandExecutionOutcome.Completed, result.Outcome);
        Assert.Equal("scan-ok", result.Payload);

        var request = Assert.IsType<DeviceCommandExecutionRequest>(deviceExecutor.Request);
        Assert.Equal(ProjectReleaseRuntimeProviderKinds.Simulator, request.ProviderKind);
        Assert.Equal("simulator://scanner-01", request.ProviderKey);
        Assert.Equal("scanner-01", request.DeviceInstanceId.Value);
        Assert.Equal("device.scanner:scan", request.CommandDefinitionId.Value);
        Assert.Equal("device.scanner", request.CapabilityId.Value);
        Assert.Equal("Scan", request.CommandName);
        Assert.Equal("{\"serial\":\"ABC\"}", request.InputPayload);
    }

    [Theory]
    [InlineData(DeviceCommandExecutionOutcome.Failed, RuntimeCommandExecutionOutcome.Failed)]
    [InlineData(DeviceCommandExecutionOutcome.Rejected, RuntimeCommandExecutionOutcome.Rejected)]
    [InlineData(DeviceCommandExecutionOutcome.TimedOut, RuntimeCommandExecutionOutcome.TimedOut)]
    public async Task ExecuteAsyncMapsDeviceTerminalOutcomesToRuntimeOutcomes(
        DeviceCommandExecutionOutcome deviceOutcome,
        RuntimeCommandExecutionOutcome expectedOutcome)
    {
        var executor = new DeviceRuntimeCommandExecutor(
            new CapturingDeviceCommandExecutor(new DeviceCommandExecutionResult(
                deviceOutcome,
                null,
                "terminal")));

        var result = await executor.ExecuteAsync(CreateContext(), SimulatorRoute());

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal("terminal", result.Reason);
    }

    private static ProjectReleaseDeviceCommandRoute SimulatorRoute()
    {
        return new ProjectReleaseDeviceCommandRoute(
            ProjectReleaseRuntimeProviderKinds.Simulator,
            "simulator://scanner-01",
            new DeviceInstanceId("scanner-01"),
            new DeviceCommandDefinitionId("device.scanner:scan"),
            new DeviceCapabilityId("device.scanner"));
    }

    private static RuntimeCommandExecutionContext CreateContext()
    {
        return new RuntimeCommandExecutionContext(
            new RuntimeSessionId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            new ProductionRunId(Guid.Parse("00000000-0000-0000-0000-000000000010")),
            OpenLineOps.Runtime.Domain.ProductionUnits.ProductionUnitId.New(),
            "line-main",
            "operation-scan",
            "operation-scan@0001",
            1,
            "station-scan",
            new ProductionUnitIdentity("model-main", "serialNumber", "ABC"),
            null,
            null,
            null,
            null,
            new ConfigurationSnapshotId("snapshot-001"),
            new RuntimeStepId(Guid.Parse("00000000-0000-0000-0000-000000000002")),
            new RuntimeCommandId(Guid.Parse("00000000-0000-0000-0000-000000000003")),
            new RuntimeNodeId("node-scan"),
            new RuntimeCapabilityId("device.scanner"),
            "Scan",
            "{\"serial\":\"ABC\"}",
            TimeSpan.FromSeconds(30),
            new RuntimeActionId("node-scan:action:1"),
            "System",
            "system.scanner",
            "project-main",
            "application-main",
            "project-snapshot-main",
            new Dictionary<string, ProductionContextValue>(),
            [new ResourceLeaseFenceEvidence(
                new ResourceRequirement(ResourceKind.Station, "station-scan"),
                1,
                new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero))]);
    }

    private sealed class CapturingDeviceCommandExecutor(
        DeviceCommandExecutionResult result) : IDeviceCommandExecutor
    {
        public DeviceCommandExecutionRequest? Request { get; private set; }

        public Task<DeviceCommandExecutionResult> ExecuteAsync(
            DeviceCommandExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(result);
        }
    }
}
