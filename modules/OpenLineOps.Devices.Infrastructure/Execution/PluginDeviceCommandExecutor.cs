using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Plugins.Application.Commands;

namespace OpenLineOps.Devices.Infrastructure.Execution;

public sealed class PluginDeviceCommandExecutor : IDeviceCommandExecutor
{
    private readonly IPluginDeviceCommandInventory _commandInventory;
    private readonly IPluginDeviceCommandInvoker _commandInvoker;

    public PluginDeviceCommandExecutor(
        IPluginDeviceCommandInventory commandInventory,
        IPluginDeviceCommandInvoker commandInvoker)
    {
        _commandInventory = commandInventory;
        _commandInvoker = commandInvoker;
    }

    public async Task<DeviceCommandExecutionResult> ExecuteAsync(
        DeviceCommandExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var descriptor = await _commandInventory
            .FindDeviceCommandAsync(request.CapabilityId.Value, request.CommandName, cancellationToken)
            .ConfigureAwait(false);
        if (descriptor is null)
        {
            return DeviceCommandExecutionResult.Rejected(
                $"No plugin device command found for capability '{request.CapabilityId.Value}' and command '{request.CommandName}'.");
        }

        if (!string.Equals(
                descriptor.CommandDefinitionId,
                request.CommandDefinitionId.Value,
                StringComparison.Ordinal))
        {
            return DeviceCommandExecutionResult.Rejected(
                $"Plugin device command definition '{descriptor.CommandDefinitionId}' does not match requested definition '{request.CommandDefinitionId.Value}'.");
        }

        var invocationResult = await _commandInvoker
            .ExecuteAsync(
                new PluginDeviceCommandInvocationRequest(
                    descriptor.PluginId,
                    request.DeviceInstanceId.Value,
                    request.CommandDefinitionId.Value,
                    request.CapabilityId.Value,
                    request.CommandName,
                    request.InputPayload,
                    ToTimeoutMilliseconds(request.Timeout)),
                cancellationToken)
            .ConfigureAwait(false);

        return invocationResult.Outcome switch
        {
            PluginDeviceCommandInvocationOutcome.Completed => DeviceCommandExecutionResult.Completed(
                invocationResult.ResultPayload),
            PluginDeviceCommandInvocationOutcome.Failed => DeviceCommandExecutionResult.Failed(
                invocationResult.FailureReason ?? "Plugin device command failed."),
            PluginDeviceCommandInvocationOutcome.Rejected => DeviceCommandExecutionResult.Rejected(
                invocationResult.FailureReason ?? "Plugin device command rejected."),
            PluginDeviceCommandInvocationOutcome.TimedOut => DeviceCommandExecutionResult.TimedOut(
                invocationResult.FailureReason ?? "Plugin device command timed out."),
            _ => DeviceCommandExecutionResult.Failed(
                $"Unsupported plugin device command outcome '{invocationResult.Outcome}'.")
        };
    }

    private static int ToTimeoutMilliseconds(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return 0;
        }

        if (timeout.TotalMilliseconds >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return Math.Max(1, (int)Math.Ceiling(timeout.TotalMilliseconds));
    }
}
