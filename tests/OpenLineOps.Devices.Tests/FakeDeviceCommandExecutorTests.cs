using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Infrastructure.Execution;

namespace OpenLineOps.Devices.Tests;

public sealed class FakeDeviceCommandExecutorTests
{
    [Fact]
    public async Task ExecuteAsyncCompletesNormalCommandsWithEchoPayload()
    {
        var executor = new FakeDeviceCommandExecutor();
        var request = CreateRequest("Inspect", "{\"serial\":\"ABC\"}");

        var result = await executor.ExecuteAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(DeviceCommandExecutionOutcome.Completed, result.Outcome);
        Assert.Contains("\"deviceInstanceId\":\"camera-01\"", result.ResultPayload, StringComparison.Ordinal);
        Assert.Contains("\"commandName\":\"Inspect\"", result.ResultPayload, StringComparison.Ordinal);
        Assert.Contains("\"inputPayload\":\"{\\\"serial\\\":\\\"ABC\\\"}\"", result.ResultPayload, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Fail", DeviceCommandExecutionOutcome.Failed)]
    [InlineData("Reject", DeviceCommandExecutionOutcome.Rejected)]
    [InlineData("Timeout", DeviceCommandExecutionOutcome.TimedOut)]
    public async Task ExecuteAsyncCanSimulateTerminalFailures(
        string commandName,
        DeviceCommandExecutionOutcome expectedOutcome)
    {
        var executor = new FakeDeviceCommandExecutor();

        var result = await executor.ExecuteAsync(CreateRequest(commandName, inputPayload: null));

        Assert.False(result.Succeeded);
        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.NotNull(result.FailureReason);
    }

    private static DeviceCommandExecutionRequest CreateRequest(string commandName, string? inputPayload)
    {
        return new DeviceCommandExecutionRequest(
            new DeviceInstanceId("camera-01"),
            new DeviceCommandDefinitionId("inspect"),
            new DeviceCapabilityId("vision-camera"),
            commandName,
            inputPayload,
            TimeSpan.FromSeconds(30));
    }
}
