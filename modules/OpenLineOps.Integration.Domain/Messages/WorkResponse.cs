using OpenLineOps.Integration.Domain.Identifiers;
using OpenLineOps.Integration.Domain.Serialization;

namespace OpenLineOps.Integration.Domain.Messages;

public enum WorkResponseStatus
{
    Accepted = 0,
    Rejected = 1,
    Completed = 2,
    Failed = 3
}

public sealed record WorkResponse
{
    public WorkResponse(
        WorkResponseId id,
        WorkRequestId requestId,
        WorkOrderId workOrderId,
        WorkResponseStatus status,
        string targetSystem,
        DateTimeOffset occurredAtUtc,
        string payloadJson)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        RequestId = requestId ?? throw new ArgumentNullException(nameof(requestId));
        WorkOrderId = workOrderId ?? throw new ArgumentNullException(nameof(workOrderId));
        Status = IntegrationGuard.Defined(status, nameof(status));
        TargetSystem = IntegrationGuard.RequiredCanonical(targetSystem, nameof(targetSystem));
        OccurredAtUtc = IntegrationGuard.Utc(occurredAtUtc, nameof(occurredAtUtc));
        PayloadJson = IntegrationCanonicalJson.Normalize(payloadJson);
    }

    public WorkResponseId Id { get; }

    public WorkRequestId RequestId { get; }

    public WorkOrderId WorkOrderId { get; }

    public WorkResponseStatus Status { get; }

    public string TargetSystem { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public string PayloadJson { get; }
}
