using OpenLineOps.Plugins.Application.Commands;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Runtime.Application.Commands;

namespace OpenLineOps.Runtime.Infrastructure.Commands;

public sealed class PluginRuntimeCommandExecutor : IRuntimeCommandExecutor
{
    private readonly IPluginProcessCommandInventory _commandInventory;
    private readonly IPluginProcessCommandInvoker _commandInvoker;
    private readonly IProjectReleasePluginCommandResolver? _releaseCommandResolver;

    public PluginRuntimeCommandExecutor(
        IPluginProcessCommandInventory commandInventory,
        IPluginProcessCommandInvoker commandInvoker,
        IProjectReleasePluginCommandResolver? releaseCommandResolver = null)
    {
        _commandInventory = commandInventory;
        _commandInvoker = commandInvoker;
        _releaseCommandResolver = releaseCommandResolver;
    }

    public async ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
        RuntimeCommandExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        string pluginId;
        string commandDefinitionId;
        PluginPackageRuntimeIdentity? packageIdentity;
        if (HasAnyProjectReleaseIdentity(context) && !HasProjectReleaseIdentity(context))
        {
            return RuntimeCommandExecutionResult.Rejected(
                "Project release identity is incomplete; project id, application id, and snapshot id are all required.");
        }

        if (HasProjectReleaseIdentity(context))
        {
            if (_releaseCommandResolver is null)
            {
                return RuntimeCommandExecutionResult.Rejected(
                    "Project Snapshot plugin execution requires a verified release package resolver.");
            }

            var releaseCommand = await _releaseCommandResolver.ResolveAsync(
                    context.ProjectId!,
                    context.ApplicationId!,
                    context.ProjectSnapshotId!,
                    context.TargetCapability.Value,
                    context.CommandName,
                    cancellationToken)
                .ConfigureAwait(false);
            if (releaseCommand is null)
            {
                return RuntimeCommandExecutionResult.Rejected(
                    $"Immutable release does not contain exactly one locked plugin command for capability '{context.TargetCapability.Value}' and command '{context.CommandName}'.");
            }

            pluginId = releaseCommand.PluginId;
            commandDefinitionId = releaseCommand.CommandDefinitionId;
            packageIdentity = new PluginPackageRuntimeIdentity(
                releaseCommand.PluginId,
                releaseCommand.PackageVersion,
                releaseCommand.PackageContentSha256,
                releaseCommand.ManifestSha256,
                releaseCommand.EntryAssemblySha256,
                releaseCommand.ContractVersion,
                releaseCommand.RuntimeIdentifier,
                releaseCommand.AbiVersion);
        }
        else
        {
            var descriptor = await _commandInventory
                .FindProcessCommandAsync(context.TargetCapability.Value, context.CommandName, cancellationToken)
                .ConfigureAwait(false);
            if (descriptor is null)
            {
                return RuntimeCommandExecutionResult.Rejected(
                    $"No plugin process command found for capability '{context.TargetCapability.Value}' and command '{context.CommandName}'.");
            }

            pluginId = descriptor.PluginId;
            commandDefinitionId = descriptor.CommandDefinitionId;
            packageIdentity = descriptor.PackageIdentity is { IsComplete: true }
                ? descriptor.PackageIdentity
                : null;
        }

        if (packageIdentity is not null && !packageIdentity.IsComplete)
        {
            return RuntimeCommandExecutionResult.Rejected(
                $"Plugin package identity for '{pluginId}' is incomplete.");
        }

        var invocationResult = await _commandInvoker
            .ExecuteAsync(
                new PluginProcessCommandInvocationRequest(
                    pluginId,
                    context.SessionId.ToString(),
                    context.StationId.Value,
                    context.ConfigurationSnapshotId.Value,
                    context.StepId.ToString(),
                    context.CommandId.ToString(),
                    context.NodeId.Value,
                    commandDefinitionId,
                    context.TargetCapability.Value,
                    context.CommandName,
                    context.InputPayload,
                    ToTimeoutMilliseconds(context.Timeout),
                    packageIdentity),
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

    private static bool HasProjectReleaseIdentity(RuntimeCommandExecutionContext context)
    {
        return !string.IsNullOrWhiteSpace(context.ProjectId)
            && !string.IsNullOrWhiteSpace(context.ApplicationId)
            && !string.IsNullOrWhiteSpace(context.ProjectSnapshotId);
    }

    private static bool HasAnyProjectReleaseIdentity(RuntimeCommandExecutionContext context)
    {
        return !string.IsNullOrWhiteSpace(context.ProjectId)
            || !string.IsNullOrWhiteSpace(context.ApplicationId)
            || !string.IsNullOrWhiteSpace(context.ProjectSnapshotId);
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
