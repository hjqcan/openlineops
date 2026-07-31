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
    IReadOnlyList<StationTransitionAuditApiResponse> TransitionAudit);

public sealed record StationTransitionAuditApiResponse(
    long Sequence,
    string FromState,
    string ToState,
    string Trigger,
    string Mode,
    string ActorId,
    string Reason,
    StationReadinessApiModel Readiness,
    DateTimeOffset OccurredAtUtc);
