using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Infrastructure.Execution;

namespace OpenLineOps.Devices.Tests;

public sealed class ConfiguredSimulatorDeviceCommandExecutorTests
{
    [Fact]
    public async Task ExecuteAsyncCompletesConfiguredCommandDefinitionWithPayload()
    {
        var executor = new ConfiguredSimulatorDeviceCommandExecutor(new ConfiguredSimulatorDeviceCommandOptions
        {
            Commands =
            {
                new ConfiguredSimulatorDeviceCommandRule
                {
                    CommandDefinitionId = "device.scanner:scan",
                    Outcome = nameof(DeviceCommandExecutionOutcome.Completed),
                    ResultPayload = "{\"barcode\":\"ABC-123\"}"
                }
            }
        });

        var result = await executor.ExecuteAsync(CreateRequest("device.scanner:scan", "device.scanner", "Scan"));

        Assert.Equal(DeviceCommandExecutionOutcome.Completed, result.Outcome);
        Assert.Equal("{\"barcode\":\"ABC-123\"}", result.ResultPayload);
    }

    [Fact]
    public async Task ExecuteAsyncMatchesCapabilityAndCommandNameWhenDefinitionIdIsNotConfigured()
    {
        var executor = new ConfiguredSimulatorDeviceCommandExecutor(new ConfiguredSimulatorDeviceCommandOptions
        {
            Commands =
            {
                new ConfiguredSimulatorDeviceCommandRule
                {
                    CapabilityId = "device.camera",
                    CommandName = "Capture",
                    Outcome = nameof(DeviceCommandExecutionOutcome.Completed)
                }
            }
        });

        var result = await executor.ExecuteAsync(CreateRequest("device.camera:capture", "device.camera", "capture"));

        Assert.True(result.Succeeded);
        Assert.Contains("\"simulator\":\"configured\"", result.ResultPayload, StringComparison.Ordinal);
        Assert.Contains("\"capabilityId\":\"device.camera\"", result.ResultPayload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsyncRejectsUnmatchedCommandByDefault()
    {
        var executor = new ConfiguredSimulatorDeviceCommandExecutor(new ConfiguredSimulatorDeviceCommandOptions());

        var result = await executor.ExecuteAsync(CreateRequest("device.scanner:scan", "device.scanner", "Scan"));

        Assert.Equal(DeviceCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.Equal("Configured simulator command rejected.", result.FailureReason);
    }

    [Theory]
    [InlineData(nameof(DeviceCommandExecutionOutcome.Failed), DeviceCommandExecutionOutcome.Failed)]
    [InlineData(nameof(DeviceCommandExecutionOutcome.Rejected), DeviceCommandExecutionOutcome.Rejected)]
    [InlineData(nameof(DeviceCommandExecutionOutcome.TimedOut), DeviceCommandExecutionOutcome.TimedOut)]
    public async Task ExecuteAsyncMapsConfiguredTerminalOutcomes(
        string configuredOutcome,
        DeviceCommandExecutionOutcome expectedOutcome)
    {
        var executor = new ConfiguredSimulatorDeviceCommandExecutor(new ConfiguredSimulatorDeviceCommandOptions
        {
            Commands =
            {
                new ConfiguredSimulatorDeviceCommandRule
                {
                    CommandName = "Inspect",
                    Outcome = configuredOutcome,
                    FailureReason = "configured terminal outcome"
                }
            }
        });

        var result = await executor.ExecuteAsync(CreateRequest("device.vision:inspect", "device.vision", "Inspect"));

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal("configured terminal outcome", result.FailureReason);
    }

    [Fact]
    public async Task ExecuteAsyncTimesOutWhenConfiguredDelayExceedsRequestTimeout()
    {
        var executor = new ConfiguredSimulatorDeviceCommandExecutor(new ConfiguredSimulatorDeviceCommandOptions
        {
            Commands =
            {
                new ConfiguredSimulatorDeviceCommandRule
                {
                    CommandName = "SlowScan",
                    Outcome = nameof(DeviceCommandExecutionOutcome.Completed),
                    DelayMilliseconds = 2000
                }
            }
        });

        var result = await executor.ExecuteAsync(CreateRequest(
            "device.scanner:slow-scan",
            "device.scanner",
            "SlowScan",
            timeout: TimeSpan.FromMilliseconds(10)));

        Assert.Equal(DeviceCommandExecutionOutcome.TimedOut, result.Outcome);
        Assert.Contains("exceeded timeout", result.FailureReason, StringComparison.Ordinal);
    }

    private static DeviceCommandExecutionRequest CreateRequest(
        string commandDefinitionId,
        string capabilityId,
        string commandName,
        TimeSpan? timeout = null)
    {
        return new DeviceCommandExecutionRequest(
            new DeviceInstanceId("device-01"),
            new DeviceCommandDefinitionId(commandDefinitionId),
            new DeviceCapabilityId(capabilityId),
            commandName,
            "{\"serial\":\"ABC\"}",
            timeout ?? TimeSpan.FromSeconds(30));
    }
}
