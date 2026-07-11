using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Runs;

namespace OpenLineOps.Runtime.Infrastructure.Commands;

public sealed class AgentStationOperationCanceler(
    IStationJobCancellationGateway gateway,
    IStationDeploymentResolver deploymentResolver) : IStationOperationCanceler
{
    public async ValueTask<StationOperationCancellationResult> CancelAsync(
        StationOperationCancellationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var route = await deploymentResolver.ResolveAsync(
                new StationDeploymentRequest(
                    request.Run.ProjectId,
                    request.Run.ApplicationId,
                    request.Run.ProjectSnapshotId,
                    request.Operation.Definition.StationSystemId),
                cancellationToken)
            .ConfigureAwait(false);
        var message = new StationJobCancelRequested(
            Guid.NewGuid(),
            StationJobIdentity.CreateCancellationIdempotencyKey(request.JobIdempotencyKey),
            StationJobIdentity.CreateJobId(request.JobIdempotencyKey),
            request.JobIdempotencyKey,
            route.AgentId,
            route.StationId,
            request.Operation.Definition.StationSystemId,
            request.Run.RunId.Value,
            request.Operation.OperationRunId,
            request.ActorId,
            request.Reason,
            request.RequestedAtUtc);
        var acknowledgement = await gateway.RequestCancelAsync(message, cancellationToken)
            .ConfigureAwait(false);
        if (acknowledgement.RequestMessageId != message.MessageId
            || acknowledgement.JobId != message.JobId
            || !string.Equals(acknowledgement.IdempotencyKey, message.IdempotencyKey, StringComparison.Ordinal)
            || !string.Equals(acknowledgement.AgentId, message.AgentId, StringComparison.Ordinal)
            || !string.Equals(acknowledgement.StationId, message.StationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Station Agent returned a cancellation acknowledgement for a different command.");
        }

        return acknowledgement.Accepted
            ? StationOperationCancellationResult.Success()
            : StationOperationCancellationResult.Failure(
                acknowledgement.FailureCode ?? "Runtime.StationJobCancelRejected",
                acknowledgement.FailureReason
                ?? "Station Agent rejected the operation cancellation request.");
    }
}
