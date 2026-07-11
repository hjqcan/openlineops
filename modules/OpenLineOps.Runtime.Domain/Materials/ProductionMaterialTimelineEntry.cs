using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.ProductionUnits;

namespace OpenLineOps.Runtime.Domain.Materials;

public enum ProductionMaterialEvidenceKind
{
    LocationTransition,
    SlotOccupancyTransition,
    DispositionTransition,
    Genealogy
}

public sealed record ProductionMaterialTimelineEntry
{
    private ProductionMaterialTimelineEntry(
        Guid evidenceId,
        ProductionMaterialEvidenceKind kind,
        ProductionRunId? productionRunId,
        string? operationRunId,
        long? slotFencingToken,
        ProductionUnitId? productionUnitId,
        CarrierId? carrierId,
        MaterialReference? material,
        MaterialLocation? sourceLocation,
        MaterialLocation? destinationLocation,
        SlotAddress? slot,
        SlotOccupancyStatus? previousSlotStatus,
        SlotOccupancyStatus? currentSlotStatus,
        ProductDisposition? previousDisposition,
        ProductDisposition? currentDisposition,
        MaterialGenealogyLink? genealogy,
        string? reason,
        string actorId,
        DateTimeOffset occurredAtUtc)
    {
        if (evidenceId == Guid.Empty)
        {
            throw new ArgumentException("Production Material evidence id cannot be empty.", nameof(evidenceId));
        }

        EvidenceId = evidenceId;
        Kind = Enum.IsDefined(kind) ? kind : throw new ArgumentOutOfRangeException(nameof(kind));
        ProductionRunId = productionRunId;
        OperationRunId = ProductionMaterialGuard.OptionalCanonical(
            operationRunId,
            nameof(operationRunId));
        SlotFencingToken = slotFencingToken;
        ProductionUnitId = productionUnitId;
        CarrierId = carrierId;
        Material = material;
        SourceLocation = sourceLocation;
        DestinationLocation = destinationLocation;
        Slot = slot;
        PreviousSlotStatus = previousSlotStatus;
        CurrentSlotStatus = currentSlotStatus;
        PreviousDisposition = previousDisposition;
        CurrentDisposition = currentDisposition;
        Genealogy = genealogy;
        Reason = ProductionMaterialGuard.OptionalCanonical(reason, nameof(reason));
        ActorId = ProductionMaterialGuard.Canonical(actorId, nameof(actorId));
        OccurredAtUtc = ProductionMaterialGuard.Utc(occurredAtUtc, nameof(occurredAtUtc));
        ValidateShape();
    }

    public Guid EvidenceId { get; }

    public ProductionMaterialEvidenceKind Kind { get; }

    public ProductionRunId? ProductionRunId { get; }

    public string? OperationRunId { get; }

    public long? SlotFencingToken { get; }

    public ProductionUnitId? ProductionUnitId { get; }

    public CarrierId? CarrierId { get; }

    public MaterialReference? Material { get; }

    public MaterialLocation? SourceLocation { get; }

    public MaterialLocation? DestinationLocation { get; }

    public SlotAddress? Slot { get; }

    public SlotOccupancyStatus? PreviousSlotStatus { get; }

    public SlotOccupancyStatus? CurrentSlotStatus { get; }

    public ProductDisposition? PreviousDisposition { get; }

    public ProductDisposition? CurrentDisposition { get; }

    public MaterialGenealogyLink? Genealogy { get; }

    public string? Reason { get; }

    public string ActorId { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public static ProductionMaterialTimelineEntry Location(
        Guid evidenceId,
        MaterialReference material,
        ProductionRunId? productionRunId,
        MaterialLocation? source,
        MaterialLocation destination,
        string actorId,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(destination);
        return new ProductionMaterialTimelineEntry(
            evidenceId,
            ProductionMaterialEvidenceKind.LocationTransition,
            productionRunId,
            null,
            null,
            material.Kind == MaterialKind.ProductionUnit
                ? material.RequireProductionUnitId()
                : null,
            material.Kind == MaterialKind.Carrier ? material.RequireCarrierId() : null,
            material,
            source,
            destination,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            actorId,
            occurredAtUtc);
    }

    public static ProductionMaterialTimelineEntry SlotOccupancy(
        Guid evidenceId,
        SlotAddress slot,
        MaterialReference? material,
        ProductionRunId? productionRunId,
        string? operationRunId,
        long? slotFencingToken,
        SlotOccupancyStatus previousStatus,
        SlotOccupancyStatus currentStatus,
        string actorId,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return new ProductionMaterialTimelineEntry(
            evidenceId,
            ProductionMaterialEvidenceKind.SlotOccupancyTransition,
            productionRunId,
            operationRunId,
            slotFencingToken,
            material?.Kind == MaterialKind.ProductionUnit
                ? material.RequireProductionUnitId()
                : null,
            material?.Kind == MaterialKind.Carrier ? material.RequireCarrierId() : null,
            material,
            null,
            null,
            slot,
            previousStatus,
            currentStatus,
            null,
            null,
            null,
            null,
            actorId,
            occurredAtUtc);
    }

    public static ProductionMaterialTimelineEntry Disposition(
        Guid evidenceId,
        ProductionUnitId productionUnitId,
        ProductionRunId? productionRunId,
        ProductDisposition previousDisposition,
        ProductDisposition currentDisposition,
        string? reason,
        string actorId,
        DateTimeOffset occurredAtUtc)
    {
        return new ProductionMaterialTimelineEntry(
            evidenceId,
            ProductionMaterialEvidenceKind.DispositionTransition,
            productionRunId,
            null,
            null,
            productionUnitId,
            null,
            MaterialReference.ForProductionUnit(productionUnitId),
            null,
            null,
            null,
            null,
            null,
            previousDisposition,
            currentDisposition,
            null,
            reason,
            actorId,
            occurredAtUtc);
    }

    public static ProductionMaterialTimelineEntry FromGenealogy(
        MaterialGenealogyLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return new ProductionMaterialTimelineEntry(
            link.Id.Value,
            ProductionMaterialEvidenceKind.Genealogy,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            link,
            null,
            link.LinkedBy,
            link.LinkedAtUtc);
    }

    private void ValidateShape()
    {
        var hasOperationRunId = OperationRunId is not null;
        var hasSlotFencingToken = SlotFencingToken is not null;
        if (hasOperationRunId != hasSlotFencingToken)
        {
            throw new ArgumentException(
                "Slot completion evidence must contain both Operation Run id and Slot fencing token.");
        }

        if (SlotFencingToken is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SlotFencingToken),
                "Slot completion evidence fencing token must be positive.");
        }

        var hasExactSlotCompletionIdentity = hasOperationRunId && hasSlotFencingToken;
        var valid = Kind switch
        {
            ProductionMaterialEvidenceKind.LocationTransition =>
                Material is not null
                && DestinationLocation is not null
                && Slot is null
                && PreviousSlotStatus is null
                && CurrentSlotStatus is null
                && PreviousDisposition is null
                && CurrentDisposition is null
                && Genealogy is null
                && Reason is null
                && !hasExactSlotCompletionIdentity,
            ProductionMaterialEvidenceKind.SlotOccupancyTransition =>
                Slot is not null
                && PreviousSlotStatus is not null
                && CurrentSlotStatus is not null
                && PreviousSlotStatus != CurrentSlotStatus
                && (PreviousSlotStatus is SlotOccupancyStatus.Reserved
                    or SlotOccupancyStatus.Occupied
                    or SlotOccupancyStatus.Running
                    || CurrentSlotStatus is SlotOccupancyStatus.Reserved
                        or SlotOccupancyStatus.Occupied
                        or SlotOccupancyStatus.Running) == (Material is not null)
                && SourceLocation is null
                && DestinationLocation is null
                && PreviousDisposition is null
                && CurrentDisposition is null
                && Genealogy is null
                && Reason is null
                && hasExactSlotCompletionIdentity == (
                    ProductionRunId is not null
                    && PreviousSlotStatus == SlotOccupancyStatus.Running
                    && CurrentSlotStatus == SlotOccupancyStatus.Occupied),
            ProductionMaterialEvidenceKind.DispositionTransition =>
                ProductionUnitId is not null
                && Material?.Kind == MaterialKind.ProductionUnit
                && PreviousDisposition is not null
                && CurrentDisposition is not null
                && PreviousDisposition != CurrentDisposition
                && SourceLocation is null
                && DestinationLocation is null
                && Slot is null
                && PreviousSlotStatus is null
                && CurrentSlotStatus is null
                && Genealogy is null
                && !hasExactSlotCompletionIdentity,
            ProductionMaterialEvidenceKind.Genealogy =>
                Genealogy is not null
                && Genealogy.Id.Value == EvidenceId
                && ProductionRunId is null
                && ProductionUnitId is null
                && CarrierId is null
                && Material is null
                && SourceLocation is null
                && DestinationLocation is null
                && Slot is null
                && PreviousSlotStatus is null
                && CurrentSlotStatus is null
                && PreviousDisposition is null
                && CurrentDisposition is null
                && Reason is null
                && !hasExactSlotCompletionIdentity,
            _ => false
        };
        if (!valid)
        {
            throw new ArgumentException($"Production Material evidence {Kind} has an invalid shape.");
        }

        if (Material is { Kind: MaterialKind.ProductionUnit }
            && ProductionUnitId != Material.RequireProductionUnitId()
            || Material is { Kind: MaterialKind.Carrier }
            && CarrierId != Material.RequireCarrierId())
        {
            throw new ArgumentException("Production Material evidence identity differs from its material.");
        }
    }
}
