using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Traceability.Application.MaterialLifecycle;
using OpenLineOps.Traceability.Application.Records;

namespace OpenLineOps.Traceability.Api.RuntimeIntegration;

public sealed class ProductionUnitMaterialLifecycleReader(
    IProductionMaterialRepository materialRepository)
    : IProductionUnitMaterialLifecycleReader
{
    private readonly IProductionMaterialRepository _materialRepository = materialRepository
        ?? throw new ArgumentNullException(nameof(materialRepository));

    public async Task<Result<ProductionUnitMaterialLifecycleDetails>> GetAsync(
        Guid productionUnitId,
        CancellationToken cancellationToken = default)
    {
        if (productionUnitId == Guid.Empty)
        {
            return Result.Failure<ProductionUnitMaterialLifecycleDetails>(
                ApplicationError.Validation(
                    "Traceability.ProductionUnitIdInvalid",
                    "Production Unit id cannot be empty."));
        }

        var unitId = new ProductionUnitId(productionUnitId);
        var storedUnit = await _materialRepository
            .GetProductionUnitAsync(unitId, cancellationToken)
            .ConfigureAwait(false);
        if (storedUnit is null)
        {
            return Result.Failure<ProductionUnitMaterialLifecycleDetails>(
                ApplicationError.NotFound(
                    "Traceability.ProductionUnitMaterialLifecycle",
                    $"Production Unit {productionUnitId:D} does not exist."));
        }

        try
        {
            var directTimeline = await _materialRepository
                .ListTimelineAsync(
                    ProductionMaterialTimelineQuery.UnionScope(productionUnitId: unitId),
                    cancellationToken)
                .ConfigureAwait(false);
            var orderedDirectTimeline = OrderAndRequireUnique(directTimeline);
            var carrierMemberships = BuildCarrierMemberships(
                unitId,
                storedUnit.Aggregate.Location,
                orderedDirectTimeline);
            var carrierTimeline = await LoadCarrierTimelineAsync(
                    carrierMemberships,
                    cancellationToken)
                .ConfigureAwait(false);
            var lifecycleTimeline = OrderAndRequireUnique(
                orderedDirectTimeline.Concat(carrierTimeline));

            var unit = storedUnit.Aggregate;
            var currentCarrierLocation = await GetCurrentCarrierLocationAsync(
                    unit.Location,
                    cancellationToken)
                .ConfigureAwait(false);
            var observedThroughUtc = lifecycleTimeline
                .Select(static entry => entry.OccurredAtUtc)
                .Append(unit.RegisteredAtUtc)
                .Append(unit.LastTransitionAtUtc)
                .Append(currentCarrierLocation?.LastTransitionAtUtc ?? unit.RegisteredAtUtc)
                .Max();

            return Result.Success(new ProductionUnitMaterialLifecycleDetails(
                unit.Id.Value,
                unit.ProductModelId,
                unit.IdentityKey,
                unit.IdentityValue,
                unit.LotId?.Value,
                unit.Disposition.ToString(),
                unit.DispositionReason,
                unit.Location is null ? null : ToDetails(unit.Location),
                currentCarrierLocation?.Location is null
                    ? null
                    : ToDetails(currentCarrierLocation.Location),
                unit.RegisteredAtUtc,
                observedThroughUtc,
                lifecycleTimeline
                    .Where(static entry => entry.Kind == ProductionMaterialEvidenceKind.Genealogy)
                    .Select(ToGenealogyDetails)
                    .ToArray(),
                lifecycleTimeline
                    .Where(static entry => entry.Kind == ProductionMaterialEvidenceKind.LocationTransition)
                    .Select(ToLocationTransitionDetails)
                    .ToArray(),
                lifecycleTimeline
                    .Where(static entry => entry.Kind == ProductionMaterialEvidenceKind.SlotOccupancyTransition)
                    .Select(ToSlotOccupancyTransitionDetails)
                    .ToArray(),
                lifecycleTimeline
                    .Where(static entry => entry.Kind == ProductionMaterialEvidenceKind.DispositionTransition)
                    .Select(ToDispositionTransitionDetails)
                    .ToArray()));
        }
        catch (InvalidDataException exception)
        {
            return Result.Failure<ProductionUnitMaterialLifecycleDetails>(
                ApplicationError.Conflict(
                    "Traceability.ProductionUnitMaterialLifecycleInvalid",
                    exception.Message));
        }
    }

    private async Task<IReadOnlyCollection<ProductionMaterialTimelineEntry>> LoadCarrierTimelineAsync(
        IReadOnlyCollection<CarrierMembership> memberships,
        CancellationToken cancellationToken)
    {
        var entries = new List<ProductionMaterialTimelineEntry>();
        foreach (var carrierId in memberships
                     .Select(static membership => membership.CarrierId)
                     .Distinct()
                     .OrderBy(static id => id.Value, StringComparer.Ordinal))
        {
            var storedCarrier = await _materialRepository
                .GetCarrierAsync(carrierId, cancellationToken)
                .ConfigureAwait(false);
            if (storedCarrier is null)
            {
                throw new InvalidDataException(
                    $"Production Unit lifecycle references missing Carrier {carrierId.Value}.");
            }

            var carrierEntries = await _materialRepository
                .ListTimelineAsync(
                    ProductionMaterialTimelineQuery.UnionScope(carrierId: carrierId),
                    cancellationToken)
                .ConfigureAwait(false);
            var carrierMemberships = memberships
                .Where(membership => membership.CarrierId == carrierId)
                .ToArray();
            entries.AddRange(carrierEntries.Where(entry =>
                (entry.Kind is ProductionMaterialEvidenceKind.LocationTransition
                    or ProductionMaterialEvidenceKind.SlotOccupancyTransition)
                && carrierMemberships.Any(membership => membership.Contains(entry.OccurredAtUtc))));
        }

        return entries;
    }

    private async Task<CurrentCarrierState?> GetCurrentCarrierLocationAsync(
        MaterialLocation? unitLocation,
        CancellationToken cancellationToken)
    {
        if (unitLocation is not
            {
                Kind: MaterialLocationKind.CarrierPosition,
                CarrierId: { } carrierId
            })
        {
            return null;
        }

        var carrier = await _materialRepository
            .GetCarrierAsync(carrierId, cancellationToken)
            .ConfigureAwait(false);
        if (carrier is null)
        {
            throw new InvalidDataException(
                $"Production Unit current location references missing Carrier {carrierId.Value}.");
        }

        return new CurrentCarrierState(
            carrier.Aggregate.Location,
            carrier.Aggregate.LastTransitionAtUtc);
    }

    private static List<CarrierMembership> BuildCarrierMemberships(
        ProductionUnitId productionUnitId,
        MaterialLocation? currentLocation,
        IReadOnlyCollection<ProductionMaterialTimelineEntry> timeline)
    {
        var transitions = timeline
            .Where(entry =>
                entry.Kind == ProductionMaterialEvidenceKind.LocationTransition
                && entry.ProductionUnitId == productionUnitId)
            .OrderBy(static entry => entry.OccurredAtUtc)
            .ThenBy(static entry => entry.EvidenceId)
            .ToArray();
        var memberships = new List<CarrierMembership>();
        MaterialLocation? expectedSource = null;
        CarrierId? activeCarrierId = null;
        DateTimeOffset activeFromUtc = default;
        foreach (var transition in transitions)
        {
            if (!Equals(transition.SourceLocation, expectedSource))
            {
                throw new InvalidDataException(
                    $"Production Unit {productionUnitId} location evidence {transition.EvidenceId:D} "
                    + "does not continue from its preceding destination.");
            }

            var destination = transition.DestinationLocation
                ?? throw new InvalidDataException(
                    $"Production Unit location evidence {transition.EvidenceId:D} has no destination.");
            var destinationCarrierId = destination.Kind == MaterialLocationKind.CarrierPosition
                ? destination.CarrierId
                    ?? throw new InvalidDataException(
                        $"Carrier position evidence {transition.EvidenceId:D} has no Carrier identity.")
                : null;

            if (activeCarrierId is not null && activeCarrierId != destinationCarrierId)
            {
                memberships.Add(new CarrierMembership(
                    activeCarrierId,
                    activeFromUtc,
                    transition.OccurredAtUtc));
                activeCarrierId = null;
            }

            if (destinationCarrierId is not null && activeCarrierId is null)
            {
                activeCarrierId = destinationCarrierId;
                activeFromUtc = transition.OccurredAtUtc;
            }

            expectedSource = destination;
        }

        if (activeCarrierId is not null)
        {
            memberships.Add(new CarrierMembership(activeCarrierId, activeFromUtc, null));
        }

        if (!Equals(expectedSource, currentLocation))
        {
            throw new InvalidDataException(
                $"Production Unit {productionUnitId} current location does not match its append-only timeline.");
        }

        return memberships;
    }

    private static ProductionMaterialTimelineEntry[] OrderAndRequireUnique(
        IEnumerable<ProductionMaterialTimelineEntry> timeline)
    {
        var entries = timeline
            .OrderBy(static entry => entry.OccurredAtUtc)
            .ThenBy(static entry => entry.EvidenceId)
            .ToArray();
        var duplicate = entries
            .GroupBy(static entry => entry.EvidenceId)
            .FirstOrDefault(static group => group.Count() != 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"Production Material evidence {duplicate.Key:D} occurs more than once.");
        }

        return entries;
    }

    private static TraceMaterialGenealogyDetails ToGenealogyDetails(
        ProductionMaterialTimelineEntry entry)
    {
        var link = entry.Genealogy
            ?? throw new InvalidDataException(
                $"Genealogy evidence {entry.EvidenceId:D} has no link.");
        return new TraceMaterialGenealogyDetails(
            link.Id.Value,
            link.ParentUnitId.Value,
            link.ChildUnitId.Value,
            link.Relationship,
            link.OperationId,
            link.LinkedBy,
            link.LinkedAtUtc);
    }

    private static TraceMaterialLocationTransitionDetails ToLocationTransitionDetails(
        ProductionMaterialTimelineEntry entry)
    {
        var material = entry.Material
            ?? throw new InvalidDataException(
                $"Location evidence {entry.EvidenceId:D} has no material.");
        var destination = entry.DestinationLocation
            ?? throw new InvalidDataException(
                $"Location evidence {entry.EvidenceId:D} has no destination.");
        return new TraceMaterialLocationTransitionDetails(
            entry.EvidenceId,
            entry.ProductionRunId?.Value,
            material.Kind.ToString(),
            material.Value,
            entry.SourceLocation is null ? null : ToDetails(entry.SourceLocation),
            ToDetails(destination),
            entry.ActorId,
            entry.OccurredAtUtc);
    }

    private static TraceSlotOccupancyTransitionDetails ToSlotOccupancyTransitionDetails(
        ProductionMaterialTimelineEntry entry)
    {
        var slot = entry.Slot
            ?? throw new InvalidDataException(
                $"Slot occupancy evidence {entry.EvidenceId:D} has no Slot.");
        var previous = entry.PreviousSlotStatus
            ?? throw new InvalidDataException(
                $"Slot occupancy evidence {entry.EvidenceId:D} has no previous status.");
        var current = entry.CurrentSlotStatus
            ?? throw new InvalidDataException(
                $"Slot occupancy evidence {entry.EvidenceId:D} has no current status.");
        return new TraceSlotOccupancyTransitionDetails(
            entry.EvidenceId,
            entry.ProductionRunId?.Value,
            slot.LineId,
            slot.StationSystemId,
            slot.SlotId,
            entry.Material?.Kind.ToString(),
            entry.Material?.Value,
            previous.ToString(),
            current.ToString(),
            entry.ActorId,
            entry.OccurredAtUtc);
    }

    private static TraceDispositionTransitionDetails ToDispositionTransitionDetails(
        ProductionMaterialTimelineEntry entry)
    {
        var productionUnitId = entry.ProductionUnitId
            ?? throw new InvalidDataException(
                $"Disposition evidence {entry.EvidenceId:D} has no Production Unit.");
        var previous = entry.PreviousDisposition
            ?? throw new InvalidDataException(
                $"Disposition evidence {entry.EvidenceId:D} has no previous disposition.");
        var current = entry.CurrentDisposition
            ?? throw new InvalidDataException(
                $"Disposition evidence {entry.EvidenceId:D} has no current disposition.");
        return new TraceDispositionTransitionDetails(
            entry.EvidenceId,
            productionUnitId.Value,
            entry.ProductionRunId?.Value,
            previous.ToString(),
            current.ToString(),
            entry.Reason,
            entry.ActorId,
            entry.OccurredAtUtc);
    }

    private static TraceMaterialLocationDetails ToDetails(MaterialLocation location) => new(
        location.Kind.ToString(),
        location.LineId,
        location.StationSystemId,
        location.SlotId,
        location.CarrierId?.Value,
        location.CarrierPositionId);

    private sealed record CarrierMembership(
        CarrierId CarrierId,
        DateTimeOffset FromUtc,
        DateTimeOffset? UntilUtc)
    {
        public bool Contains(DateTimeOffset occurredAtUtc) =>
            occurredAtUtc >= FromUtc && (UntilUtc is null || occurredAtUtc < UntilUtc);
    }

    private sealed record CurrentCarrierState(
        MaterialLocation? Location,
        DateTimeOffset LastTransitionAtUtc);
}
