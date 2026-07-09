using OpenLineOps.Plugins.Application.Commands;
using OpenLineOps.Runtime.Application.Commands;

namespace OpenLineOps.Runtime.Infrastructure.Commands;

public sealed class PluginRuntimeCommandExecutor : IRuntimeCommandExecutor
{
    private readonly IPluginProcessCommandInventory _commandInventory;
    private readonly IPluginProcessCommandInvoker _commandInvoker;

    public PluginRuntimeCommandExecutor(
        IPluginProcessCommandInventory commandInventory,
        IPluginProcessCommandInvoker commandInvoker)
    {
        _commandInventory = commandInventory;
        _commandInvoker = commandInvoker;
    }

    public async ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
        RuntimeCommandExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var descriptor = await _commandInventory
            .FindProcessCommandAsync(context.TargetCapability.Value, context.CommandName, cancellationToken)
            .ConfigureAwait(false);
        if (descriptor is null)
        {
            return RuntimeCommandExecutionResult.Rejected(
                $"No plugin process command found for capability '{context.TargetCapability.Value}' and command '{context.CommandName}'.");
        }

        var invocationResult = await _commandInvoker
            .ExecuteAsync(
                new PluginProcessCommandInvocationRequest(
                    descriptor.PluginId,
                    context.SessionId.ToString(),
                    context.StationId.Value,
                    context.ConfigurationSnapshotId.Value,
                    context.StepId.ToString(),
                    context.CommandId.ToString(),
                    context.NodeId.Value,
                    descriptor.CommandDefinitionId,
                    context.TargetCapability.Value,
                    context.CommandName,
                    context.InputPayload,
                    ToTimeoutMilliseconds(context.Timeout)),
                cancellationToken)
            .ConfigureAwait(false);

        return invocationResult.Outcome switch
        {
            PluginProcessCommandInvocationOutcome.Completed => RuntimeCommandExecutionResult.Completed(
                invocationResult.ResultPayload),
            PluginProcessCommandInvocationOutcome.Failed => RuntimeCommandExecutionResult.Failed(
                invocationResult.FailureReason ?? "Plugin process command failed."),
            PluginProcessCommandInvocationOutcome.Rejected => RuntimeCommandExecutionResult.Rejected(
                invocationResult.FailureReason ?? "Plugin process command rejected."),
            PluginProcessCommandInvocationOutcome.TimedOut => RuntimeCommandExecutionResult.TimedOut(
                invocationResult.FailureReason ?? "Plugin process command timed out."),
            PluginProcessCommandInvocationOutcome.Canceled => RuntimeCommandExecutionResult.Canceled(
                invocationResult.FailureReason ?? "Plugin process command canceled."),
            _ => RuntimeCommandExecutionResult.Failed(
                $"Unsupported plugin process command outcome '{invocationResult.Outcome}'.")
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
