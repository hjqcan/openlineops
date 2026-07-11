using System.Text.Json;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Infrastructure.Commands;

public sealed class AgentStationOperationDispatcher(
    IStationJobGateway gateway,
    IStationDeploymentResolver deploymentResolver) : IStationOperationDispatcher
{
    public async ValueTask<StationOperationDispatchResult> DispatchAsync(
        StationOperationDispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var route = await deploymentResolver.ResolveAsync(
            new StationDeploymentRequest(
                request.Run.ProjectId,
                request.Run.ApplicationId,
                request.Run.ProjectSnapshotId,
                request.Operation.Definition.StationSystemId),
            cancellationToken).ConfigureAwait(false);
        RequireExactProductionLine(route, request.Run.ProductionLineDefinitionId);
        using var inputs = JsonDocument.Parse("{}");
        var message = new StationJobRequested(
            Guid.NewGuid(),
            StationJobIdentity.CreateJobId(request.IdempotencyKey),
            request.IdempotencyKey,
            route.AgentId,
            route.StationId,
            request.Operation.Definition.StationSystemId,
            request.Run.RunId.Value,
            request.Run.ProductionUnitId.Value,
            request.RuntimeSessionId.Value,
            request.Operation.OperationRunId,
            request.Operation.Attempt,
            request.Run.ProductionUnitIdentity.ModelId,
            request.Run.ProductionUnitIdentity.InputKey,
            request.Run.ProductionUnitIdentity.Value,
            request.Run.LotId,
            request.Run.CarrierId,
            request.Run.ProjectId,
            request.Run.ApplicationId,
            request.Run.ProjectSnapshotId,
            request.Run.ProductionLineDefinitionId,
            request.Run.TopologyId,
            request.Run.ActorId,
            route.PackageContentSha256,
            request.Operation.Definition.OperationId,
            request.Operation.Definition.ProcessDefinitionId.Value,
            request.Operation.Definition.ProcessVersionId.Value,
            request.Operation.Definition.ConfigurationSnapshotId.Value,
            request.Operation.Definition.RecipeSnapshotId.Value,
            request.ResourceLeases.Select(lease => new StationResourceFence(
                lease.Resource.Kind.ToString(),
                lease.Resource.ResourceId,
                lease.FencingToken,
                lease.ExpiresAtUtc)).ToArray(),
            inputs.RootElement.Clone(),
            DateTimeOffset.UtcNow);
        var completion = await gateway.DispatchAsync(message, cancellationToken)
            .ConfigureAwait(false);
        if (completion.JobId != message.JobId
            || completion.RuntimeSessionId != message.RuntimeSessionId
            || !string.Equals(
                completion.IdempotencyKey,
                message.IdempotencyKey,
                StringComparison.Ordinal)
            || !string.Equals(completion.AgentId, message.AgentId, StringComparison.Ordinal)
            || !string.Equals(completion.StationId, message.StationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Station Agent returned a completion for a different idempotent job.");
        }

        if (completion.CompletedStepCount != completion.Steps.Count(step =>
                string.Equals(step.Status, "Completed", StringComparison.Ordinal))
            || completion.CommandCount != completion.Commands.Count
            || completion.IncidentCount != completion.Incidents.Count)
        {
            throw new InvalidDataException(
                "Station Agent completion evidence counts do not match its detailed evidence.");
        }

        return new StationOperationDispatchResult(
            completion.ExecutionStatus,
            completion.Judgement,
            ProductionContextOutputReader.Read(completion.Outputs),
            completion.CompletedStepCount,
            completion.CommandCount,
            completion.IncidentCount,
            completion.CompletedAtUtc,
            completion.FailureCode,
            completion.FailureReason);
    }

    private static void RequireExactProductionLine(
        StationDeploymentRoute route,
        string productionLineDefinitionId)
    {
        if (!string.Equals(
                route.ProductionLineDefinitionId,
                productionLineDefinitionId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Station deployment does not match the Production Run's exact frozen Production Line.");
        }
    }

}
