using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Devices.Infrastructure.Execution;

namespace OpenLineOps.Devices.Api.DependencyInjection;

internal sealed class ConfigurableDeviceCommandExecutor : IDeviceCommandExecutor
{
    private readonly IConfiguration _configuration;
    private readonly DeviceCommandExecutionOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly FakeDeviceCommandExecutor _fakeExecutor;
    private readonly ConfiguredSimulatorDeviceCommandExecutor _configuredSimulatorExecutor;

    public ConfigurableDeviceCommandExecutor(
        IConfiguration configuration,
        DeviceCommandExecutionOptions options,
        IServiceProvider serviceProvider,
        FakeDeviceCommandExecutor fakeExecutor,
        ConfiguredSimulatorDeviceCommandExecutor configuredSimulatorExecutor)
    {
        _configuration = configuration;
        _options = options;
        _serviceProvider = serviceProvider;
        _fakeExecutor = fakeExecutor;
        _configuredSimulatorExecutor = configuredSimulatorExecutor;
    }

    public Task<DeviceCommandExecutionResult> ExecuteAsync(
        DeviceCommandExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.PluginPackage is not null)
        {
            return ExecutePluginAsync(request, cancellationToken);
        }

        var provider = _configuration[$"{DeviceRuntimeBridgeOptions.DevicesExecutionSectionName}:Provider"]
            ?? _options.Provider;

        if (DeviceCommandExecutorProviders.IsFake(provider))
        {
            return _fakeExecutor.ExecuteAsync(request, cancellationToken);
        }

        if (DeviceCommandExecutorProviders.IsConfiguredSimulator(provider))
        {
            return _configuredSimulatorExecutor.ExecuteAsync(request, cancellationToken);
        }

        if (DeviceCommandExecutorProviders.IsPlugin(provider))
        {
            return ExecutePluginAsync(request, cancellationToken);
        }

        throw new InvalidOperationException(
            $"Unsupported device command executor provider '{provider}'.");
    }

    private Task<DeviceCommandExecutionResult> ExecutePluginAsync(
        DeviceCommandExecutionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var pluginExecutor = _serviceProvider.GetService<PluginDeviceCommandExecutor>()
                ?? ActivatorUtilities.CreateInstance<PluginDeviceCommandExecutor>(_serviceProvider);

            return pluginExecutor.ExecuteAsync(request, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                "Plugin device command execution requires IPluginDeviceCommandInventory and IPluginDeviceCommandInvoker registrations.",
                exception);
        }
    }
}
