using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Devices.Infrastructure.Execution;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Scripting;
using OpenLineOps.Runtime.Infrastructure.Commands;

namespace OpenLineOps.Devices.Api.DependencyInjection;

internal sealed class ConfigurableRuntimeCommandExecutor : IRuntimeCommandExecutor
{
    private readonly IConfiguration _configuration;
    private readonly DeviceRuntimeBridgeOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly SimulatedRuntimeCommandExecutor _simulatedExecutor;
    private readonly RuntimeFlowCommandExecutor _flowExecutor;
    private readonly IRuntimeScriptExecutor _scriptExecutor;

    public ConfigurableRuntimeCommandExecutor(
        IConfiguration configuration,
        DeviceRuntimeBridgeOptions options,
        IServiceProvider serviceProvider,
        SimulatedRuntimeCommandExecutor simulatedExecutor,
        RuntimeFlowCommandExecutor flowExecutor,
        IRuntimeScriptExecutor scriptExecutor)
    {
        _configuration = configuration;
        _options = options;
        _serviceProvider = serviceProvider;
        _simulatedExecutor = simulatedExecutor;
        _flowExecutor = flowExecutor;
        _scriptExecutor = scriptExecutor;
    }

    public async ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
        RuntimeCommandExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (RuntimeFlowCommand.IsInternal(context))
        {
            return await _flowExecutor.ExecuteAsync(context, cancellationToken)
                .ConfigureAwait(false);
        }

        if (RuntimeScriptCommand.IsPythonScript(context))
        {
            if (!RuntimeScriptExecutionRequest.TryCreate(context, out var request, out var error))
            {
                return RuntimeCommandExecutionResult.Rejected(
                    error ?? "Python script command payload is invalid.");
            }

            return await _scriptExecutor.ExecuteAsync(request!, cancellationToken)
                .ConfigureAwait(false);
        }

        var executor = _configuration[$"{DeviceRuntimeBridgeOptions.RuntimeSectionName}:CommandExecutor"]
            ?? _options.CommandExecutor;

        if (DeviceRuntimeCommandExecutors.IsDeviceBacked(executor))
        {
            var deviceExecutor = _serviceProvider.GetRequiredService<DeviceRuntimeCommandExecutor>();

            return await deviceExecutor.ExecuteAsync(context, cancellationToken)
                .ConfigureAwait(false);
        }

        if (DeviceRuntimeCommandExecutors.IsPlugin(executor))
        {
            try
            {
                var pluginExecutor = _serviceProvider.GetService<PluginRuntimeCommandExecutor>()
                    ?? ActivatorUtilities.CreateInstance<PluginRuntimeCommandExecutor>(_serviceProvider);

                return await pluginExecutor.ExecuteAsync(context, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException(
                    "Plugin runtime command execution requires IPluginProcessCommandInventory and IPluginProcessCommandInvoker registrations.",
                    exception);
            }
        }

        return await _simulatedExecutor.ExecuteAsync(context, cancellationToken)
            .ConfigureAwait(false);
    }
}
