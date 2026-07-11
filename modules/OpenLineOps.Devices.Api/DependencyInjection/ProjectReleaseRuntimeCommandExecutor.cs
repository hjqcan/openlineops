using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Devices.Application.Execution.ExternalPrograms;
using OpenLineOps.Devices.Infrastructure.Execution;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Scripting;
using OpenLineOps.Runtime.Infrastructure.Commands;
using System.Text.Json;

namespace OpenLineOps.Devices.Api.DependencyInjection;

internal sealed class ProjectReleaseRuntimeCommandExecutor : IRuntimeCommandExecutor
{
    private static readonly JsonSerializerOptions LineControllerJsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IProjectReleaseRuntimeCommandRouteResolver _routeResolver;
    private readonly DeviceRuntimeCommandExecutor _deviceExecutor;
    private readonly PluginRuntimeCommandExecutor _processPluginExecutor;
    private readonly RuntimeFlowCommandExecutor _flowExecutor;
    private readonly IRuntimeScriptExecutor _scriptExecutor;
    private readonly IExternalProgramHost _externalProgramHost;
    private readonly IRuntimeCommandResourceFenceValidator _resourceFenceValidator;

    public ProjectReleaseRuntimeCommandExecutor(
        IProjectReleaseRuntimeCommandRouteResolver routeResolver,
        DeviceRuntimeCommandExecutor deviceExecutor,
        PluginRuntimeCommandExecutor processPluginExecutor,
        RuntimeFlowCommandExecutor flowExecutor,
        IRuntimeScriptExecutor scriptExecutor,
        IExternalProgramHost externalProgramHost,
        IRuntimeCommandResourceFenceValidator resourceFenceValidator)
    {
        _routeResolver = routeResolver;
        _deviceExecutor = deviceExecutor;
        _processPluginExecutor = processPluginExecutor;
        _flowExecutor = flowExecutor;
        _scriptExecutor = scriptExecutor;
        _externalProgramHost = externalProgramHost;
        _resourceFenceValidator = resourceFenceValidator;
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
            var fenceRejection = await ValidateResourceFencesAsync(context, cancellationToken)
                .ConfigureAwait(false);
            if (fenceRejection is not null)
            {
                return fenceRejection;
            }

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
                    context.OperationRunId,
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
                    context.ActionId.Value,
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
                    context.Timeout,
                    context.ResourceLeaseFences.Select(static fence =>
                        new DeviceCommandResourceFenceEvidence(
                            fence.Resource.Kind.ToString(),
                            fence.Resource.ResourceId,
                            fence.FencingToken,
                            fence.ExpiresAtUtc))),
                cancellationToken)
            .ConfigureAwait(false);

        var routeFenceRejection = await ValidateResourceFencesAsync(context, cancellationToken)
            .ConfigureAwait(false);
        if (routeFenceRejection is not null)
        {
            return routeFenceRejection;
        }

        return route switch
        {
            ProjectReleaseLineControllerCommandRoute lineControllerRoute =>
                await ExecuteLineControllerAsync(
                        context,
                        lineControllerRoute,
                        cancellationToken)
                    .ConfigureAwait(false),
            ProjectReleaseExternalProgramCommandRoute externalProgramRoute =>
                await ProjectReleaseExternalProgramCommandExecutor.ExecuteAsync(
                        context,
                        externalProgramRoute,
                        ExecuteExternalProgramProviderAsync,
                        ValidateResourceFencesAsync,
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

    private async ValueTask<RuntimeCommandExecutionResult> ExecuteLineControllerAsync(
        RuntimeCommandExecutionContext context,
        ProjectReleaseLineControllerCommandRoute route,
        CancellationToken cancellationToken)
    {
        var inputPayload = JsonSerializer.Serialize(new LineControllerCommandEnvelope(
            route.AuthorizationId,
            route.TargetStationSystemId,
            route.TargetSystemId,
            route.TargetBindingId,
            route.TargetCapabilityId,
            route.TargetAction,
            context.InputPayload), LineControllerJsonOptions);
        return await _deviceExecutor.ExecuteAsync(
                context,
                route.ControllerRoute,
                inputPayload,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<RuntimeCommandExecutionResult> ExecuteExternalProgramProviderAsync(
        RuntimeCommandExecutionContext context,
        ProjectReleaseRuntimeCommandRoute route,
        CancellationToken cancellationToken)
    {
        var fenceRejection = await ValidateResourceFencesAsync(context, cancellationToken)
            .ConfigureAwait(false);
        if (fenceRejection is not null)
        {
            return fenceRejection;
        }

        return route switch
        {
            ProjectReleaseProcessCommandRoute =>
                await _processPluginExecutor.ExecuteAsync(context, cancellationToken).ConfigureAwait(false),
            ProjectReleaseDeviceCommandRoute deviceRoute =>
                await _deviceExecutor.ExecuteAsync(context, deviceRoute, cancellationToken).ConfigureAwait(false),
            _ => RuntimeCommandExecutionResult.Rejected(
                $"Frozen external program provider kind '{route.ProviderKind}' is not executable.")
        };
    }

    private async ValueTask<RuntimeCommandExecutionResult?> ValidateResourceFencesAsync(
        RuntimeCommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        var validation = await _resourceFenceValidator.ValidateAsync(
                context.ProductionRunId,
                context.OperationRunId,
                context.ResourceLeaseFences,
                cancellationToken)
            .ConfigureAwait(false);
        return validation.Accepted
            ? null
            : RuntimeCommandExecutionResult.Rejected(
                validation.RejectionReason
                ?? "Runtime command resource lease fences are no longer current.");
    }

}
