using OpenLineOps.Runtime.Application.Runs;

namespace OpenLineOps.Runtime.Api.Models;

public sealed record ActiveProductionRunsResponse(
    IReadOnlyCollection<ProductionRunReadModel> Runs);

public sealed record ProductionLineRuntimeStateResponse(
    string ProductionLineDefinitionId,
    DateTimeOffset GeneratedAtUtc,
    int ActiveRunCount,
    IReadOnlyCollection<ProductionRunReadModel> ActiveRuns,
    IReadOnlyCollection<ProductionLineProductionUnitStateResponse> ProductionUnits,
    IReadOnlyCollection<ProductionLineStationStateResponse> Stations,
    IReadOnlyCollection<ProductionLineSlotStateResponse> Slots,
    IReadOnlyCollection<ProductionLineCarrierStateResponse> Carriers);

public sealed record ProductionLineProductionUnitStateResponse(
    Guid ProductionUnitId,
    string ProductModelId,
    string IdentityKey,
    string IdentityValue,
    string Disposition,
    string Judgement,
    Guid? ProductionRunId,
    MaterialLocationApiResponse? Location,
    DateTimeOffset LastTransitionAtUtc,
    IReadOnlyCollection<string> ActiveOperationRunIds);

public sealed record ProductionLineStationStateResponse(
    string StationSystemId,
    string Status,
    string? AgentId,
    string? StationId,
    Guid? AgentPresenceSessionId,
    long? AgentPresenceSequence,
    string? AgentPresenceState,
    string AgentPresenceHealth,
    DateTimeOffset? AgentPresenceLastSeenAtUtc,
    double? AgentPresenceAgeSeconds,
    IReadOnlyCollection<ProductionLineQueuedMaterialResponse> Queue,
    IReadOnlyCollection<ProductionLineStationOperationStateResponse> ActiveOperations);

public sealed record ProductionLineQueuedMaterialResponse(
    string MaterialKind,
    string MaterialId,
    DateTimeOffset QueuedAtUtc);

public sealed record ProductionLineStationOperationStateResponse(
    Guid ProductionRunId,
    Guid? ProductionUnitId,
    RuntimeProductionUnitIdentityResponse ProductionUnitIdentity,
    string OperationRunId,
    string OperationId,
    string ExecutionStatus,
    string Judgement,
    DateTimeOffset? StartedAtUtc,
    IReadOnlyCollection<ProductionLineResourceStateResponse> Resources);

public sealed record ProductionLineResourceStateResponse(
    string Kind,
    string ResourceId,
    string Status,
    long? FencingToken,
    DateTimeOffset? AcquiredAtUtc,
    DateTimeOffset? ExpiresAtUtc);

public sealed record ProductionLineSlotStateResponse(
    string StationSystemId,
    string SlotId,
    string Status,
    string? MaterialKind,
    string? MaterialId,
    DateTimeOffset LastTransitionAtUtc);

public sealed record ProductionLineCarrierStateResponse(
    string CarrierId,
    string CarrierTypeId,
    int Capacity,
    MaterialLocationApiResponse? Location,
    DateTimeOffset LastTransitionAtUtc,
    IReadOnlyCollection<ProductionLineCarrierPositionStateResponse> ProductionUnits);

public sealed record ProductionLineCarrierPositionStateResponse(
    string CarrierPositionId,
    Guid ProductionUnitId,
    string Disposition,
    string Judgement);
