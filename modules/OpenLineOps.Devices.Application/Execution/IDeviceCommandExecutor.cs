namespace OpenLineOps.Devices.Application.Execution;

public interface IDeviceCommandExecutor
{
    Task<DeviceCommandExecutionResult> ExecuteAsync(
        DeviceCommandExecutionRequest request,
        CancellationToken cancellationToken = default);
}
