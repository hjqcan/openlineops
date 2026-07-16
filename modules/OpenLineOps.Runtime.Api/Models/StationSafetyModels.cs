using System.Text.Json.Serialization;

namespace OpenLineOps.Runtime.Api.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RequestStationEmergencyStopApiRequest(
    string MessageId,
    string IdempotencyKey,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string Reason,
    string RequestedAtUtc);

public sealed record StationEmergencyStopApiResponse(
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
    string RequestedAtUtc,
    string Status,
    Guid? AcknowledgementMessageId,
    string? AcknowledgedAtUtc,
    string? FailureCode,
    string? FailureReason,
    int DispatchAttemptCount,
    string? LastDispatchFailure,
    string LastUpdatedAtUtc,
    bool Replayed,
    IReadOnlyCollection<StationSafetyEvidenceApiResponse> Evidence);

public sealed record StationSafetyEvidenceApiResponse(
    long Sequence,
    string Kind,
    Guid MessageId,
    string OccurredAtUtc,
    string? FailureCode,
    string? FailureReason);

public sealed record StationSafetyEventsApiResponse(
    IReadOnlyCollection<StationEmergencyStopApiResponse> Events);
