using System.Text.Json;
using OpenLineOps.Devices.Application.Execution;

namespace OpenLineOps.Devices.Infrastructure.Execution;

public sealed class ProjectReleaseSimulatorDeviceCommandExecutor : IDeviceCommandExecutor
{
    public Task<DeviceCommandExecutionResult> ExecuteAsync(
        DeviceCommandExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(
                request.ProviderKind,
                ProjectReleaseRuntimeProviderKinds.Simulator,
                StringComparison.Ordinal))
        {
            return Task.FromResult(DeviceCommandExecutionResult.Rejected(
                $"Release provider '{request.ProviderKind}' is not a Simulator binding."));
        }

        var payload = JsonSerializer.Serialize(new
        {
            providerKind = request.ProviderKind,
            providerKey = request.ProviderKey,
            deviceInstanceId = request.DeviceInstanceId.Value,
            commandDefinitionId = request.CommandDefinitionId.Value,
            capabilityId = request.CapabilityId.Value,
            commandName = request.CommandName,
            inputPayload = request.InputPayload
        });

        return Task.FromResult(DeviceCommandExecutionResult.Completed(payload));
    }
}
