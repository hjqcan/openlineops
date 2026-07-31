using OpenLineOps.Integration.Domain.Identifiers;
using OpenLineOps.Integration.Domain.Serialization;

namespace OpenLineOps.Integration.Domain.Messages;

public enum WorkRequestKind
{
    CreateOrUpdate = 0,
    Release = 1,
    Hold = 2,
    Resume = 3,
    Complete = 4,
    Cancel = 5
}

public enum WorkRequestStatus
{
    Received = 0,
    Processing = 1,
    Completed = 2,
    Rejected = 3
}

public sealed record WorkRequest
{
    public WorkRequest(
        WorkRequestId id,
        WorkOrderId workOrderId,
        WorkRequestKind kind,
        WorkRequestStatus status,
        string sourceSystem,
        DateTimeOffset occurredAtUtc,
        string payloadJson)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        WorkOrderId = workOrderId ?? throw new ArgumentNullException(nameof(workOrderId));
        Kind = IntegrationGuard.Defined(kind, nameof(kind));
        Status = IntegrationGuard.Defined(status, nameof(status));
        SourceSystem = IntegrationGuard.RequiredCanonical(sourceSystem, nameof(sourceSystem));
        OccurredAtUtc = IntegrationGuard.Utc(occurredAtUtc, nameof(occurredAtUtc));
        PayloadJson = IntegrationCanonicalJson.Normalize(payloadJson);
    }

    public WorkRequestId Id { get; }

    public WorkOrderId WorkOrderId { get; }

    public WorkRequestKind Kind { get; }

    public WorkRequestStatus Status { get; }

    public string SourceSystem { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public string PayloadJson { get; }
}
