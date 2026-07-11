using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Runs;

namespace OpenLineOps.Runtime.Infrastructure.Commands;

public sealed class AgentStationSafetyController(
    IStationSafetyGateway gateway,
    IStationDeploymentResolver deploymentResolver) : IStationSafetyController
{
    public async ValueTask<StationSafetyResult> RequestSafeStopAsync(
        StationSafetyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stations = request.Run.Operations
            .Where(operation => !IsTerminal(operation.ExecutionStatus))
            .GroupBy(
                operation => operation.Definition.StationSystemId,
                StringComparer.Ordinal)
            .ToArray();
        foreach (var station in stations)
        {
            var route = await deploymentResolver.ResolveAsync(
                new StationDeploymentRequest(
                    request.Run.ProjectId,
                    request.Run.ApplicationId,
                    request.Run.ProjectSnapshotId,
                    station.Key),
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    route.ProductionLineDefinitionId,
                    request.Run.ProductionLineDefinitionId,
                    StringComparison.Ordinal))
            {
                return StationSafetyResult.Failure(
                    "Runtime.SafeStopDeploymentMismatch",
                    "Station deployment does not match the Production Run's exact frozen Production Line.");
            }

            var operationRunId = station
                .OrderByDescending(static operation => operation.Attempt)
                .Select(static operation => operation.OperationRunId)
                .FirstOrDefault();
            var idempotencyKey = $"{request.Run.RunId.Value:D}/safe-stop/{station.Key}";
            var message = new StationSafeStopRequested(
                Guid.NewGuid(),
                idempotencyKey,
                route.AgentId,
                route.StationId,
                station.Key,
                request.Run.RunId.Value,
                operationRunId,
                request.ActorId,
                request.Reason,
                DateTimeOffset.UtcNow);
            var acknowledgement = await gateway.RequestSafeStopAsync(message, cancellationToken)
                .ConfigureAwait(false);
            if (acknowledgement.RequestMessageId != message.MessageId
                || !string.Equals(
                    acknowledgement.IdempotencyKey,
                    idempotencyKey,
                    StringComparison.Ordinal)
                || !string.Equals(
                    acknowledgement.AgentId,
                    route.AgentId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    acknowledgement.StationId,
                    route.StationId,
                    StringComparison.Ordinal))
            {
                return StationSafetyResult.Failure(
                    "Runtime.SafeStopAcknowledgementMismatch",
                    "Station Agent acknowledged a different Safe Stop request.");
            }

            if (!acknowledgement.Accepted)
            {
                return StationSafetyResult.Failure(
                    acknowledgement.FailureCode ?? "Runtime.SafeStopRejected",
                    acknowledgement.FailureReason ?? "Station Agent rejected Safe Stop.");
            }
        }

        return StationSafetyResult.Success();
    }

    private static bool IsTerminal(OpenLineOps.Runtime.Contracts.ExecutionStatus status) => status is
        OpenLineOps.Runtime.Contracts.ExecutionStatus.Completed
        or OpenLineOps.Runtime.Contracts.ExecutionStatus.Failed
        or OpenLineOps.Runtime.Contracts.ExecutionStatus.TimedOut
        or OpenLineOps.Runtime.Contracts.ExecutionStatus.Canceled
        or OpenLineOps.Runtime.Contracts.ExecutionStatus.Rejected;
}
