namespace OpenLineOps.Traceability.Api.Models;

public sealed record StationSafetyTraceSearchResponse(
    IReadOnlyCollection<StationEmergencyStopTraceResponse> Items);

public sealed record StationEmergencyStopTraceResponse(
    Guid MessageId,
    string IdempotencyKey,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string StationSystemId,
    string AgentId,
    string StationId,
    IReadOnlyCollection<Guid> RelatedProductionRunIds,
    string ActorId,
    string Reason,
    DateTimeOffset RequestedAtUtc,
    string Status,
    Guid? AcknowledgementMessageId,
    DateTimeOffset? AcknowledgedAtUtc,
    string? FailureCode,
    string? FailureReason,
    int DispatchAttemptCount,
    string? LastDispatchFailure,
    DateTimeOffset LastUpdatedAtUtc,
    IReadOnlyCollection<StationSafetyTraceEvidenceResponse> Evidence);

public sealed record StationSafetyTraceEvidenceResponse(
    long Sequence,
    string Kind,
    Guid MessageId,
    DateTimeOffset OccurredAtUtc,
    string? FailureCode,
    string? FailureReason);
