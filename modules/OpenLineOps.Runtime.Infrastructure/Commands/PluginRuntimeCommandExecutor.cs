using OpenLineOps.Plugins.Application.Commands;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Runtime.Application.Commands;

namespace OpenLineOps.Runtime.Infrastructure.Commands;

public sealed class PluginRuntimeCommandExecutor : IRuntimeCommandExecutor
{
    private readonly IProjectReleasePluginCommandResolver _releaseCommandResolver;
    private readonly IPluginProcessCommandInvoker _commandInvoker;

    public PluginRuntimeCommandExecutor(
        IProjectReleasePluginCommandResolver releaseCommandResolver,
        IPluginProcessCommandInvoker commandInvoker)
    {
        _releaseCommandResolver = releaseCommandResolver;
        _commandInvoker = commandInvoker;
    }

    public async ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
        RuntimeCommandExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var releaseCommand = await _releaseCommandResolver.ResolveAsync(
                context.ProjectId,
                context.ApplicationId,
                context.ProjectSnapshotId,
                context.TargetCapability.Value,
                context.CommandName,
                context.TargetKind,
                context.TargetId,
                cancellationToken)
            .ConfigureAwait(false);
        if (releaseCommand is null)
        {
            return RuntimeCommandExecutionResult.Rejected(
                $"Immutable release does not contain exactly one locked plugin command for capability '{context.TargetCapability.Value}' and command '{context.CommandName}'.");
        }

        var packageIdentity = new PluginPackageRuntimeIdentity(
            releaseCommand.PluginId,
            releaseCommand.PackageVersion,
            releaseCommand.PackageContentSha256,
            releaseCommand.ManifestSha256,
            releaseCommand.EntryAssemblySha256,
            releaseCommand.ContractVersion,
            releaseCommand.RuntimeIdentifier,
            releaseCommand.AbiVersion);
        if (!packageIdentity.IsComplete)
        {
            return RuntimeCommandExecutionResult.Rejected(
                $"Immutable release plugin package identity for '{releaseCommand.PluginId}' is incomplete.");
        }

        var invocationResult = await _commandInvoker
            .ExecuteAsync(
                new PluginProcessCommandInvocationRequest(
                    releaseCommand.PluginId,
                    context.SessionId.ToString(),
                    context.StationId.Value,
                    context.ConfigurationSnapshotId.Value,
                    context.StepId.ToString(),
                    context.CommandId.ToString(),
                    context.NodeId.Value,
                    releaseCommand.CommandDefinitionId,
                    context.TargetCapability.Value,
                    context.CommandName,
                    context.InputPayload,
                    ToTimeoutMilliseconds(context.Timeout),
                    packageIdentity,
                    context.TargetKind,
                    context.TargetId),
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
