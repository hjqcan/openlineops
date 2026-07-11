using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Devices.Application.Execution.ExternalPrograms;
using OpenLineOps.Devices.Infrastructure.Execution;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Scripting;
using OpenLineOps.Runtime.Infrastructure.Commands;

namespace OpenLineOps.Devices.Api.DependencyInjection;

internal sealed class ProjectReleaseRuntimeCommandExecutor : IRuntimeCommandExecutor
{
    private readonly IProjectReleaseRuntimeCommandRouteResolver _routeResolver;
    private readonly DeviceRuntimeCommandExecutor _deviceExecutor;
    private readonly PluginRuntimeCommandExecutor _processPluginExecutor;
    private readonly RuntimeFlowCommandExecutor _flowExecutor;
    private readonly IRuntimeScriptExecutor _scriptExecutor;
    private readonly IExternalProgramHost _externalProgramHost;

    public ProjectReleaseRuntimeCommandExecutor(
        IProjectReleaseRuntimeCommandRouteResolver routeResolver,
        DeviceRuntimeCommandExecutor deviceExecutor,
        PluginRuntimeCommandExecutor processPluginExecutor,
        RuntimeFlowCommandExecutor flowExecutor,
        IRuntimeScriptExecutor scriptExecutor,
        IExternalProgramHost externalProgramHost)
    {
        _routeResolver = routeResolver;
        _deviceExecutor = deviceExecutor;
        _processPluginExecutor = processPluginExecutor;
        _flowExecutor = flowExecutor;
        _scriptExecutor = scriptExecutor;
        _externalProgramHost = externalProgramHost;
    }

    public async ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
        RuntimeCommandExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (RuntimeFlowCommand.IsInternal(context))
        {
            return await _flowExecutor.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        }

        if (RuntimeScriptCommand.IsPythonScript(context))
        {
            if (!RuntimeScriptExecutionRequest.TryCreate(context, out var request, out var error))
            {
                return RuntimeCommandExecutionResult.Rejected(
                    error ?? "Python script command payload is invalid.");
            }

            return await _scriptExecutor.ExecuteAsync(request!, cancellationToken).ConfigureAwait(false);
        }

        var route = await _routeResolver.ResolveAsync(
                new DeviceCommandRouteRequest(
                    context.SessionId.ToString(),
                    context.ProductionRunId.ToString(),
                    context.ProductionLineDefinitionId,
                    context.OperationId,
                    context.OperationAttempt,
                    context.ProductionUnitIdentity.ModelId,
                    context.ProductionUnitIdentity.InputKey,
                    context.ProductionUnitIdentity.Value,
                    context.LotId,
                    context.CarrierId,
                    context.FixtureId,
                    context.DeviceId,
                    context.StepId.ToString(),
                    context.CommandId.ToString(),
                    context.NodeId.Value,
                    context.StationSystemId,
                    context.ConfigurationSnapshotId.Value,
                    new OpenLineOps.Devices.Domain.Identifiers.DeviceCapabilityId(
                        context.TargetCapability.Value),
                    context.CommandName,
                    context.ProjectId,
                    context.ApplicationId,
                    context.ProjectSnapshotId,
                    context.TargetKind,
                    context.TargetId,
                    context.InputPayload,
                    context.Timeout),
                cancellationToken)
            .ConfigureAwait(false);

        return route switch
        {
            ProjectReleaseExternalTestProgramCommandRoute externalTestRoute =>
                await ProjectReleaseExternalTestProgramCommandExecutor.ExecuteAsync(
                        context,
                        externalTestRoute,
                        ExecuteExternalTestProviderAsync,
                        _externalProgramHost,
                        cancellationToken)
                    .ConfigureAwait(false),
            ProjectReleaseProcessCommandRoute =>
                await _processPluginExecutor.ExecuteAsync(context, cancellationToken).ConfigureAwait(false),
            ProjectReleaseDeviceCommandRoute deviceRoute =>
                await _deviceExecutor.ExecuteAsync(context, deviceRoute, cancellationToken).ConfigureAwait(false),
            null => RuntimeCommandExecutionResult.Rejected(
                $"Immutable release does not contain exactly one executable provider binding for capability '{context.TargetCapability.Value}' and target '{context.TargetKind}/{context.TargetId}'."),
            _ => RuntimeCommandExecutionResult.Rejected(
                $"Immutable release provider kind '{route.ProviderKind}' is not executable.")
        };
    }

    private async ValueTask<RuntimeCommandExecutionResult> ExecuteExternalTestProviderAsync(
        RuntimeCommandExecutionContext context,
        ProjectReleaseRuntimeCommandRoute route,
        CancellationToken cancellationToken)
    {
        return route switch
        {
            ProjectReleaseProcessCommandRoute =>
                await _processPluginExecutor.ExecuteAsync(context, cancellationToken).ConfigureAwait(false),
            ProjectReleaseDeviceCommandRoute deviceRoute =>
                await _deviceExecutor.ExecuteAsync(context, deviceRoute, cancellationToken).ConfigureAwait(false),
            _ => RuntimeCommandExecutionResult.Rejected(
                $"Frozen external test provider kind '{route.ProviderKind}' is not executable.")
        };
    }
}
