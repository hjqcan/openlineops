using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Scripting;
using OpenLineOps.Runtime.Infrastructure.Commands;

namespace OpenLineOps.Runtime.Api.DependencyInjection;

internal sealed class ConfigurableRuntimeCommandExecutor : IRuntimeCommandExecutor
{
    private readonly IConfiguration _configuration;
    private readonly RuntimeCommandExecutorOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly SimulatedRuntimeCommandExecutor _simulatedExecutor;
    private readonly RuntimeFlowCommandExecutor _flowExecutor;
    private readonly IRuntimeScriptExecutor _scriptExecutor;

    public ConfigurableRuntimeCommandExecutor(
        IConfiguration configuration,
        RuntimeCommandExecutorOptions options,
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
        if (RuntimeFlowCommand.IsWait(context))
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

        var executor = _configuration[$"{RuntimeCommandExecutorOptions.SectionName}:CommandExecutor"]
            ?? _options.CommandExecutor;

        if (RuntimeCommandExecutors.IsSimulator(executor))
        {
            return await _simulatedExecutor.ExecuteAsync(context, cancellationToken)
                .ConfigureAwait(false);
        }

        if (RuntimeCommandExecutors.IsPlugin(executor))
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

        throw new InvalidOperationException(
            $"Unsupported runtime command executor '{executor}'.");
    }
}
