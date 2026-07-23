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
        if (request.RequestedAtUtc == default || request.RequestedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Station Safe Stop request timestamp must be non-default UTC.",
                nameof(request));
        }

        var stations = request.Run.Operations
            .Where(operation => operation.StartedAtUtc is { } startedAtUtc
                && startedAtUtc <= request.RequestedAtUtc
                && (operation.CompletedAtUtc is null
                    || operation.CompletedAtUtc >= request.RequestedAtUtc))
            .GroupBy(
                operation => operation.Definition.StationSystemId,
                StringComparer.Ordinal)
            .ToArray();
        var acknowledgedAtUtc = request.RequestedAtUtc;
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
                .ThenBy(static operation => operation.OperationRunId, StringComparer.Ordinal)
                .Select(static operation => operation.OperationRunId)
                .FirstOrDefault();
            var idempotencyKey = $"{request.Run.RunId.Value:D}/safe-stop/{station.Key}/{operationRunId}";
            var message = new StationSafeStopRequested(
                StationJobIdentity.CreateSafetyMessageId(idempotencyKey),
                idempotencyKey,
                route.AgentId,
                route.StationId,
                station.Key,
                request.Run.RunId.Value,
                operationRunId,
                request.ActorId,
                request.Reason,
                request.RequestedAtUtc);
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

            if (acknowledgement.AcknowledgedAtUtc > acknowledgedAtUtc)
            {
                acknowledgedAtUtc = acknowledgement.AcknowledgedAtUtc;
            }
        }

        return StationSafetyResult.Success(acknowledgedAtUtc);
    }
}
