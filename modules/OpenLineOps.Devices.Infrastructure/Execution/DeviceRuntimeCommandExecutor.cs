using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Runtime.Application.Commands;

namespace OpenLineOps.Devices.Infrastructure.Execution;

public sealed class DeviceRuntimeCommandExecutor : IRuntimeCommandExecutor
{
    private readonly IDeviceCommandRouteResolver _routeResolver;
    private readonly IDeviceCommandExecutor _deviceCommandExecutor;

    public DeviceRuntimeCommandExecutor(
        IDeviceCommandRouteResolver routeResolver,
        IDeviceCommandExecutor deviceCommandExecutor)
    {
        _routeResolver = routeResolver;
        _deviceCommandExecutor = deviceCommandExecutor;
    }

    public async ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
        RuntimeCommandExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var routeRequest = new DeviceCommandRouteRequest(
            context.SessionId.ToString(),
            context.StepId.ToString(),
            context.CommandId.ToString(),
            context.NodeId.Value,
            context.StationId.Value,
            context.ConfigurationSnapshotId.Value,
            new DeviceCapabilityId(context.TargetCapability.Value),
            context.CommandName);

        var route = await _routeResolver
            .ResolveAsync(routeRequest, cancellationToken)
            .ConfigureAwait(false);

        if (route is null)
        {
            return RuntimeCommandExecutionResult.Rejected(
                $"No device command route found for capability '{context.TargetCapability.Value}' and command '{context.CommandName}'.");
        }

        var deviceRequest = new DeviceCommandExecutionRequest(
            route.DeviceInstanceId,
            route.CommandDefinitionId,
            route.CapabilityId,
            context.CommandName,
            context.InputPayload,
            context.Timeout);

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
