using OpenLineOps.Devices.Application.Execution;

namespace OpenLineOps.Devices.Infrastructure.Execution;

public sealed class FakeDeviceCommandExecutor : IDeviceCommandExecutor
{
    public Task<DeviceCommandExecutionResult> ExecuteAsync(
        DeviceCommandExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var result = request.CommandName switch
        {
            "Fail" => DeviceCommandExecutionResult.Failed("Fake device command failed."),
            "Reject" => DeviceCommandExecutionResult.Rejected("Fake device command rejected."),
            "Timeout" => DeviceCommandExecutionResult.TimedOut("Fake device command timed out."),
            _ => DeviceCommandExecutionResult.Completed(BuildResultPayload(request))
        };

        return Task.FromResult(result);
    }

    private static string BuildResultPayload(DeviceCommandExecutionRequest request)
    {
        return $$"""
            {"deviceInstanceId":"{{request.DeviceInstanceId.Value}}","commandName":"{{request.CommandName}}","inputPayload":{{FormatNullableJsonString(request.InputPayload)}}}
            """;
    }

    private static string FormatNullableJsonString(string? value)
    {
        return value is null
            ? "null"
            : $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
}
