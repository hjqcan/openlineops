using System.Text.Json.Serialization;

namespace OpenLineOps.Runtime.Api.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReportStationControllerHandshakeApiRequest(
    string OwnerInstanceId,
    long AgentFencingToken,
    string ControllerSessionId,
    long HeartbeatSequence,
    long CommandSequence,
    long AcknowledgedCommandSequence,
    bool Busy,
    bool Completed,
    bool Error,
    string? ErrorCode,
    bool RecipeConfirmed,
    string? ConfirmedRecipeId,
    string? ConfirmedRecipeVersion,
    bool SafetyPermitGranted,
    DateTimeOffset SourceTimestampUtc,
    string? CommandId,
    long CommandFencingToken,
    string ObservedMode,
    string ObservedState,
    long StateSequence,
    string Reason);

public sealed record StationControllerHandshakeReportApiResponse(
    string OwnerAgentId,
    string OwnerAgentInstanceId,
    long AgentFencingToken,
    string ControllerSessionId,
    long HeartbeatSequence,
    long CommandSequence,
    long AcknowledgedCommandSequence,
    string? CommandId,
    long CommandFencingToken,
    string ObservedMode,
    string ObservedState,
    long StateSequence,
    bool Busy,
    bool Completed,
    bool Error,
    string? ErrorCode,
    bool RecipeConfirmed,
    string? ConfirmedRecipeId,
    string? ConfirmedRecipeVersion,
    bool SafetyPermitGranted,
    DateTimeOffset SourceTimestampUtc,
    DateTimeOffset ReceivedAtUtc);

public sealed record StationControllerHandshakeApiResponse(
    string StationId,
    long Revision,
    bool RecoveryRequired,
    long RecoveryEpoch,
    long OperationalEpoch,
    string OperationalStateSha256,
    bool ExecutionAllowed,
    string ExecutionReadinessCode,
    string ExecutionReadinessReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastChangedAtUtc,
    StationControllerHandshakeReportApiResponse LatestReport);

public sealed record StationControllerHandshakeFactApiResponse(
    long Sequence,
    string Kind,
    bool RecoveryRequired,
    string ActorId,
    string Reason,
    DateTimeOffset OccurredAtUtc,
    StationControllerHandshakeReportApiResponse Report);

public sealed record StationControllerHandshakeFactsApiResponse(
    string StationId,
    IReadOnlyList<StationControllerHandshakeFactApiResponse> Facts);
