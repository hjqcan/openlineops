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
        string materialKind,
        string materialId,
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string packageContentSha256,
        string stationId,
        string lineId,
        string stationSystemId,
        string actorId,
        DateTimeOffset arrivedAtUtc)
    {
        if (!StationMaterialKinds.IsDefined(materialKind))
        {
            throw new ArgumentException(
                "Material kind must be exactly ProductionUnit or Carrier.",
                nameof(materialKind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(materialId);
        if (materialKind == StationMaterialKinds.ProductionUnit
            && (!Guid.TryParseExact(materialId, "D", out var productionUnitId)
                || !string.Equals(
                    materialId,
                    productionUnitId.ToString("D"),
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Production Unit material id must be canonical lowercase D-format UUID text.",
                nameof(materialId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectSnapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageContentSha256);
        if (packageContentSha256.Length != 64
            || packageContentSha256.Any(static character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "Package content SHA-256 must be lowercase hexadecimal.",
                nameof(packageContentSha256));
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
            $"material-arrival/{projectId}/{applicationId}/{projectSnapshotId}/"
            + $"{packageContentSha256}/{stationId}/{materialKind}/{materialId}/{lineId}/"
            + $"{stationSystemId}/{actorId}/{arrivedAtUtc:O}");
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
