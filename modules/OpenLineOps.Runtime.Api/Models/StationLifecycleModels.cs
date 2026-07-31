using System.Text.Json.Serialization;

namespace OpenLineOps.Runtime.Api.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateStationLifecycleApiRequest(
    string Mode,
    StationReadinessApiModel Readiness,
    string Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChangeStationModeApiRequest(
    string Mode,
    string Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateStationReadinessApiRequest(
    StationReadinessApiModel Readiness,
    string Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StationLifecycleCommandApiRequest(string Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AcknowledgeStationLifecycleCommandApiRequest(
    string OwnerInstanceId,
    long FencingToken,
    string? CommandId,
    string? ControllerSessionId,
    long? CommandSequence,
    string? ObservedMode,
    string? ObservedState,
    long? StateSequence,
    string Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StationReadinessApiModel(
    bool InterlocksSatisfied,
    bool Homed,
    bool CriticalDevicesHealthy,
    bool RecipeVerified,
    bool CalibrationValid,
    bool SafetyPermitGranted);

public sealed record StationLifecycleApiResponse(
    string StationId,
    long Revision,
    string Mode,
    string State,
    StationReadinessApiModel Readiness,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastChangedAtUtc,
    StationControllerCommandApiResponse? PendingControllerCommand,
    StationControllerCommandDeliveryClaimApiResponse?
        PendingControllerCommandDelivery,
    StationControllerRecoveryApiResponse? PendingControllerRecovery,
    IReadOnlyList<StationTransitionAuditApiResponse> TransitionAudit);

public sealed record StationControllerCommandApiResponse(
    string CommandId,
    string ControllerSessionId,
    long ExpectedCommandSequence,
    string OwnerAgentId,
    string OwnerAgentInstanceId,
    long FencingToken,
    string Trigger,
    string ExpectedMode,
    string ExpectedCompletionState,
    string Idempotency,
    string SafetyClass,
    string? ConfirmedRecipeId,
    string? ConfirmedRecipeVersion,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset DeadlineUtc,
    long IssuedOperationalEpoch,
    Guid? RecipeAssignmentId,
    Guid? RecipeDeploymentId,
    string? RecipeConfigurationSha256);

public sealed record StationControllerCommandDeliveryClaimApiResponse(
    string CommandId,
    string OwnerAgentId,
    string OwnerAgentInstanceId,
    long FencingToken,
    DateTimeOffset FirstClaimedAtUtc,
    DateTimeOffset LastClaimedAtUtc,
    int DeliveryCount);

public sealed record StationControllerRecoveryApiResponse(
    string IntentId,
    string CommandId,
    string Idempotency,
    string Reason,
    DateTimeOffset RequiredAtUtc);

public sealed record PendingStationControllerCommandApiResponse(
    string StationId,
    long LifecycleRevision,
    StationControllerCommandApiResponse Command);

public sealed record StationLifecycleFactApiResponse(
    string StationId,
    long Sequence,
    long LifecycleRevision,
    string Kind,
    DateTimeOffset OccurredAtUtc,
    string PayloadSha256,
    string PreviousFactSha256,
    string FactSha256);

public sealed record StationTransitionAuditApiResponse(
    long Sequence,
    string FromState,
    string ToState,
    string Trigger,
    string Mode,
    string ActorId,
    string Reason,
    StationReadinessApiModel Readiness,
    DateTimeOffset OccurredAtUtc,
    StationControllerCommandApiResponse? ControllerCommand);
