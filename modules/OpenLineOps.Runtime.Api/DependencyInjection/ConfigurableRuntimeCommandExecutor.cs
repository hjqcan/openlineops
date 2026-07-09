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
    private readonly IRuntimeScriptExecutor _scriptExecutor;
    private readonly RuntimeAutomationPlanDispatcher _automationPlanDispatcher;

    public ConfigurableRuntimeCommandExecutor(
        IConfiguration configuration,
        RuntimeCommandExecutorOptions options,
        IServiceProvider serviceProvider,
        SimulatedRuntimeCommandExecutor simulatedExecutor,
        IRuntimeScriptExecutor scriptExecutor,
        RuntimeAutomationPlanDispatcher automationPlanDispatcher)
    {
        _configuration = configuration;
        _options = options;
        _serviceProvider = serviceProvider;
        _simulatedExecutor = simulatedExecutor;
        _scriptExecutor = scriptExecutor;
        _automationPlanDispatcher = automationPlanDispatcher;
    }

    public async ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
        RuntimeCommandExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (RuntimeScriptCommand.IsPythonScript(context))
        {
            if (!RuntimeScriptExecutionRequest.TryCreate(context, out var request, out var error))
            {
                return RuntimeCommandExecutionResult.Rejected(
                    error ?? "Python script command payload is invalid.");
            }

            var scriptResult = await _scriptExecutor.ExecuteAsync(request!, cancellationToken)
                .ConfigureAwait(false);

            return await _automationPlanDispatcher.DispatchAsync(
                request!,
                scriptResult,
                ExecuteAsync,
                cancellationToken).ConfigureAwait(false);
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
