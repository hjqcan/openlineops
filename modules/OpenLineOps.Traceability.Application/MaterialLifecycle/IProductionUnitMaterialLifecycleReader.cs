using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Traceability.Application.Records;

namespace OpenLineOps.Traceability.Application.MaterialLifecycle;

public interface IProductionUnitMaterialLifecycleReader
{
    Task<Result<ProductionUnitMaterialLifecycleDetails>> GetAsync(
        Guid productionUnitId,
        CancellationToken cancellationToken = default);
}

public sealed record ProductionUnitMaterialLifecycleDetails(
    Guid ProductionUnitId,
    string ProductModelId,
    string ProductionUnitIdentityInputKey,
    string ProductionUnitIdentityValue,
    string? LotId,
    string CurrentDisposition,
    string? DispositionReason,
    TraceMaterialLocationDetails? CurrentLocation,
    TraceMaterialLocationDetails? CurrentCarrierLocation,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset ObservedThroughUtc,
    IReadOnlyCollection<TraceMaterialGenealogyDetails> Genealogy,
    IReadOnlyCollection<TraceMaterialLocationTransitionDetails> MaterialLocationTransitions,
    IReadOnlyCollection<TraceSlotOccupancyTransitionDetails> SlotOccupancyTransitions,
    IReadOnlyCollection<TraceDispositionTransitionDetails> DispositionTransitions);
