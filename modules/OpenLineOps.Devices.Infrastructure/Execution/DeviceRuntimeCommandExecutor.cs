using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Runtime.Application.Commands;

namespace OpenLineOps.Devices.Infrastructure.Execution;

public sealed class DeviceRuntimeCommandExecutor
{
    private readonly IDeviceCommandExecutor _deviceCommandExecutor;

    public DeviceRuntimeCommandExecutor(IDeviceCommandExecutor deviceCommandExecutor)
    {
        _deviceCommandExecutor = deviceCommandExecutor;
    }

    public async ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
        RuntimeCommandExecutionContext context,
        ProjectReleaseDeviceCommandRoute route,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(context, route, context.InputPayload, cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
        RuntimeCommandExecutionContext context,
        ProjectReleaseDeviceCommandRoute route,
        string? inputPayload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(route);

        var deviceRequest = new DeviceCommandExecutionRequest(
            context.ProjectId,
            context.ApplicationId,
            route.ProviderKind,
            route.ProviderKey,
            route.DeviceInstanceId,
            route.CommandDefinitionId,
            route.CapabilityId,
            context.CommandName,
            inputPayload,
            context.Timeout,
            route.PluginPackage);

        var deviceResult = await _deviceCommandExecutor
            .ExecuteAsync(deviceRequest, cancellationToken)
            .ConfigureAwait(false);

        return deviceResult.Outcome switch
        {
            DeviceCommandExecutionOutcome.Completed => RuntimeCommandExecutionResult.Completed(deviceResult.ResultPayload),
            DeviceCommandExecutionOutcome.Failed => RuntimeCommandExecutionResult.Failed(
                deviceResult.FailureReason ?? "Device command failed."),
            DeviceCommandExecutionOutcome.Rejected => RuntimeCommandExecutionResult.Rejected(
                deviceResult.FailureReason ?? "Device command rejected."),
            DeviceCommandExecutionOutcome.TimedOut => RuntimeCommandExecutionResult.TimedOut(
                deviceResult.FailureReason ?? "Device command timed out."),
            _ => RuntimeCommandExecutionResult.Failed(
                $"Unsupported device command outcome '{deviceResult.Outcome}'.")
        };
    }
}
