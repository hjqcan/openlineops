namespace OpenLineOps.Traceability.Api.Models;

public sealed record ProductionUnitMaterialLifecycleResponse(
    Guid ProductionUnitId,
    string ProductModelId,
    string ProductionUnitIdentityInputKey,
    string ProductionUnitIdentityValue,
    string? LotId,
    string CurrentDisposition,
    string? DispositionReason,
    TraceMaterialLocationResponse? CurrentLocation,
    TraceMaterialLocationResponse? CurrentCarrierLocation,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset ObservedThroughUtc,
    IReadOnlyCollection<TraceMaterialGenealogyResponse> Genealogy,
    IReadOnlyCollection<TraceMaterialLocationTransitionResponse> MaterialLocationTransitions,
    IReadOnlyCollection<TraceSlotOccupancyTransitionResponse> SlotOccupancyTransitions,
    IReadOnlyCollection<TraceDispositionTransitionResponse> DispositionTransitions);
