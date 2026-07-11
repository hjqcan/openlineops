using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Agent.Contracts;

namespace OpenLineOps.Runtime.Application.Runs;

public static class StationDispatchMessageIdentity
{
    public static ResourceLeaseChanged CreateLeaseGranted(
        StationJobRequested request,
        StationResourceFence fence)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(fence);
        var idempotencyKey = $"{request.IdempotencyKey}/resource-lease/"
                             + $"{fence.ResourceKind}/{fence.ResourceId}/{fence.FencingToken}";
        var message = new ResourceLeaseChanged(
            DeterministicGuid(idempotencyKey),
            idempotencyKey,
            request.AgentId,
            request.StationId,
            request.JobId,
            request.ProductionRunId,
            request.OperationRunId,
            fence.ResourceKind,
            fence.ResourceId,
            fence.FencingToken,
            StationResourceLeaseStatuses.Granted,
            request.RequestedAtUtc,
            fence.ExpiresAtUtc);
        StationMessageContract.Validate(message);
        return message;
    }

    public static Guid CreateMaterialArrivalMessageId(
        Guid productionUnitId,
        string lineId,
        string stationSystemId,
        string actorId,
        DateTimeOffset arrivedAtUtc)
    {
        if (productionUnitId == Guid.Empty)
        {
            throw new ArgumentException("Production Unit id cannot be empty.", nameof(productionUnitId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(lineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stationSystemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (arrivedAtUtc == default || arrivedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Material arrival time must be a non-default UTC value.",
                nameof(arrivedAtUtc));
        }

        return DeterministicGuid(
            $"material-arrival/{productionUnitId:D}/{lineId}/{stationSystemId}/"
            + $"{actorId}/{arrivedAtUtc:O}");
    }

    public static string CreateMaterialArrivalIdempotencyKey(Guid messageId)
    {
        if (messageId == Guid.Empty)
        {
            throw new ArgumentException("Material arrival message id cannot be empty.", nameof(messageId));
        }

        return $"material-arrival/{messageId:D}";
    }

    private static Guid DeterministicGuid(string identity)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return new Guid(hash.AsSpan(0, 16));
    }
}
