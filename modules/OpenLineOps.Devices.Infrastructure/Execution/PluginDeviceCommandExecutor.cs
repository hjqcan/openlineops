using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Plugins.Application.Commands;
using OpenLineOps.Plugins.Application.Discovery;

namespace OpenLineOps.Devices.Infrastructure.Execution;

public sealed class PluginDeviceCommandExecutor : IDeviceCommandExecutor
{
    private readonly IPluginDeviceCommandInvoker _commandInvoker;

    public PluginDeviceCommandExecutor(IPluginDeviceCommandInvoker commandInvoker)
    {
        _commandInvoker = commandInvoker;
    }

    public async Task<DeviceCommandExecutionResult> ExecuteAsync(
        DeviceCommandExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(
                request.ProviderKind,
                ProjectReleaseRuntimeProviderKinds.PluginCommand,
                StringComparison.Ordinal)
            || request.PluginPackage is null)
        {
            return DeviceCommandExecutionResult.Rejected(
                "Plugin device execution requires a PluginCommand release route with a frozen package identity.");
        }

        var runtimeIdentity = new PluginPackageRuntimeIdentity(
            request.PluginPackage.PluginId,
            request.PluginPackage.Version,
            request.PluginPackage.PackageContentSha256,
            request.PluginPackage.ManifestSha256,
            request.PluginPackage.EntryAssemblySha256,
            request.PluginPackage.ContractVersion,
            request.PluginPackage.RuntimeIdentifier,
            request.PluginPackage.AbiVersion);
        if (!runtimeIdentity.IsComplete)
        {
            return DeviceCommandExecutionResult.Rejected(
                "Plugin device execution was rejected because the immutable frozen package identity is incomplete.");
        }

        var packageIdentity = new PluginPackageExecutionIdentity(
            request.ProjectId,
            request.ApplicationId,
            runtimeIdentity);

        var invocationResult = await _commandInvoker
            .ExecuteAsync(
                new PluginDeviceCommandInvocationRequest(
                    request.PluginPackage.PluginId,
                    request.DeviceInstanceId.Value,
                    request.CommandDefinitionId.Value,
                    request.CapabilityId.Value,
                    request.CommandName,
                    request.InputPayload,
                    ToTimeoutMilliseconds(request.Timeout),
                    packageIdentity),
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
