using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Infrastructure.Execution;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Identifiers;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Sessions;
using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Infrastructure.Events;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Devices.Tests;

public sealed class DeviceRuntimeCommandExecutorTests
{
    private static readonly DateTimeOffset StartedAtUtc = new(2026, 6, 29, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExecuteAsyncMapsRuntimeContextToDeviceCommandRequest()
    {
        var routeResolver = new CapturingRouteResolver(new DeviceCommandRoute(
            new DeviceInstanceId("scanner-01"),
            new DeviceCommandDefinitionId("scan"),
            new DeviceCapabilityId("device.scanner")));
        var deviceExecutor = new CapturingDeviceCommandExecutor(
            DeviceCommandExecutionResult.Completed("scan-ok"));
        var executor = new DeviceRuntimeCommandExecutor(routeResolver, deviceExecutor);

        var result = await executor.ExecuteAsync(CreateContext("Scan", "{\"serial\":\"ABC\"}"));

        Assert.Equal(RuntimeCommandExecutionOutcome.Completed, result.Outcome);
        Assert.Equal("scan-ok", result.Payload);

        var routeRequest = Assert.IsType<DeviceCommandRouteRequest>(routeResolver.Request);
        Assert.Equal("station-a", routeRequest.StationId);
        Assert.Equal("snapshot-001", routeRequest.ConfigurationSnapshotId);
        Assert.Equal("device.scanner", routeRequest.CapabilityId.Value);
        Assert.Equal("Scan", routeRequest.CommandName);

        var deviceRequest = Assert.IsType<DeviceCommandExecutionRequest>(deviceExecutor.Request);
        Assert.Equal("scanner-01", deviceRequest.DeviceInstanceId.Value);
        Assert.Equal("scan", deviceRequest.CommandDefinitionId.Value);
        Assert.Equal("device.scanner", deviceRequest.CapabilityId.Value);
        Assert.Equal("Scan", deviceRequest.CommandName);
        Assert.Equal("{\"serial\":\"ABC\"}", deviceRequest.InputPayload);
    }

    [Fact]
    public async Task ExecuteAsyncRejectsCommandWhenNoDeviceRouteExists()
    {
        var routeResolver = new CapturingRouteResolver(route: null);
        var deviceExecutor = new CapturingDeviceCommandExecutor(
            DeviceCommandExecutionResult.Completed("should-not-run"));
        var executor = new DeviceRuntimeCommandExecutor(routeResolver, deviceExecutor);

        var result = await executor.ExecuteAsync(CreateContext("Scan", inputPayload: null));

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.NotNull(result.Reason);
        Assert.Contains("No device command route", result.Reason, StringComparison.Ordinal);
        Assert.Null(deviceExecutor.Request);
    }

    [Theory]
    [InlineData("Fail", RuntimeCommandExecutionOutcome.Failed)]
    [InlineData("Reject", RuntimeCommandExecutionOutcome.Rejected)]
    [InlineData("Timeout", RuntimeCommandExecutionOutcome.TimedOut)]
    public async Task ExecuteAsyncMapsDeviceTerminalOutcomesToRuntimeOutcomes(
        string commandName,
        RuntimeCommandExecutionOutcome expectedOutcome)
    {
        var executor = new DeviceRuntimeCommandExecutor(
            new StaticDeviceCommandRouteResolver(),
            new FakeDeviceCommandExecutor());

        var result = await executor.ExecuteAsync(CreateContext(commandName, inputPayload: null));

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public async Task RuntimeSessionRunnerCanCompleteAProcessThroughDeviceCommandBoundary()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var eventPublisher = new InMemoryRuntimeDomainEventPublisher();
        var commandExecutor = new DeviceRuntimeCommandExecutor(
            new StaticDeviceCommandRouteResolver(),
            new FakeDeviceCommandExecutor());
        var runner = new RuntimeSessionRunner(
            repository,
            eventPublisher,
            commandExecutor,
            new DeterministicRuntimeIdProvider(),
            new FixedClock(StartedAtUtc));

        var result = await runner.RunAsync(new StartRuntimeSessionRequest(
            new StationId("station-a"),
            new ConfigurationSnapshotId("snapshot-001"),
            new RecipeSnapshotId("recipe-001"),
            new ExecutableRuntimeProcess(
                new ProcessDefinitionId("process-device-smoke"),
                new ProcessVersionId("process-device-smoke@1.0.0"),
                [
                    new ExecutableRuntimeNode(
                        new RuntimeNodeId("node-scan"),
                        "Scan barcode",
                        new RuntimeCapabilityId("device.scanner"),
                        "Scan",
                        TimeSpan.FromSeconds(30),
                        "{\"serial\":\"ABC\"}")
                ])));

        Assert.True(result.IsSuccess);
        Assert.Equal(RuntimeSessionStatus.Completed, result.Value.Status);
        Assert.Equal(1, result.Value.CompletedSteps);
        Assert.Equal(1, result.Value.CommandCount);

        var persisted = await repository.GetByIdAsync(result.Value.SessionId);
        Assert.NotNull(persisted);
        var command = Assert.Single(persisted.Commands);
        Assert.Equal(RuntimeCommandStatus.Completed, command.Status);
        Assert.NotNull(command.ResultPayload);
        Assert.Contains(
            "\"deviceInstanceId\":\"station-a:device.scanner\"",
            command.ResultPayload,
            StringComparison.Ordinal);
    }

    private static RuntimeCommandExecutionContext CreateContext(string commandName, string? inputPayload)
    {
        return new RuntimeCommandExecutionContext(
            new RuntimeSessionId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            new StationId("station-a"),
            new ConfigurationSnapshotId("snapshot-001"),
            new RuntimeStepId(Guid.Parse("00000000-0000-0000-0000-000000000002")),
            new RuntimeCommandId(Guid.Parse("00000000-0000-0000-0000-000000000003")),
            new RuntimeNodeId("node-scan"),
            new RuntimeCapabilityId("device.scanner"),
            commandName,
            inputPayload,
            TimeSpan.FromSeconds(30));
    }

    private sealed class CapturingRouteResolver(DeviceCommandRoute? route) : IDeviceCommandRouteResolver
    {
        public DeviceCommandRouteRequest? Request { get; private set; }

        public ValueTask<DeviceCommandRoute?> ResolveAsync(
            DeviceCommandRouteRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;

            return ValueTask.FromResult(route);
        }
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

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class DeterministicRuntimeIdProvider : IRuntimeIdProvider
    {
        private int _value;

        public RuntimeSessionId NewSessionId()
        {
            return new RuntimeSessionId(NextGuid());
        }

        public RuntimeStepId NewStepId()
        {
            return new RuntimeStepId(NextGuid());
        }

        public RuntimeCommandId NewCommandId()
        {
            return new RuntimeCommandId(NextGuid());
        }

        private Guid NextGuid()
        {
            _value++;
            return Guid.Parse($"00000000-0000-0000-0000-{_value:000000000000}");
        }
    }
}
