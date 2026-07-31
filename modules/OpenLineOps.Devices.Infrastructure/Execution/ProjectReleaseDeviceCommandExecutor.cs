using OpenLineOps.Devices.Application.Execution;

namespace OpenLineOps.Devices.Infrastructure.Execution;

public sealed class ProjectReleaseDeviceCommandExecutor : IDeviceCommandExecutor
{
    private readonly ProjectReleaseSimulatorDeviceCommandExecutor _simulatorExecutor;
    private readonly PluginDeviceCommandExecutor _pluginExecutor;

    public ProjectReleaseDeviceCommandExecutor(
        ProjectReleaseSimulatorDeviceCommandExecutor simulatorExecutor,
        PluginDeviceCommandExecutor pluginExecutor)
    {
        _simulatorExecutor = simulatorExecutor;
        _pluginExecutor = pluginExecutor;
    }

    public Task<DeviceCommandExecutionResult> ExecuteAsync(
        DeviceCommandExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.ProviderKind switch
        {
            ProjectReleaseRuntimeProviderKinds.Simulator =>
                _simulatorExecutor.ExecuteAsync(request, cancellationToken),
            ProjectReleaseRuntimeProviderKinds.PluginCommand =>
                _pluginExecutor.ExecuteAsync(request, cancellationToken),
            _ => Task.FromResult(DeviceCommandExecutionResult.Rejected(
                $"Release provider kind '{request.ProviderKind}' is not executable."))
        };
    }
}
