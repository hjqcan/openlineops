using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Execution;

public sealed class ProductionOperationReadinessEvaluator(
    IProductionMaterialRepository materials) : IProductionOperationReadiness
{
    public async ValueTask<ProductionOperationReadiness> EvaluateAsync(
        ProductionRunSnapshot run,
        OperationRunSnapshot operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(operation);
        var unitEntry = await materials.GetProductionUnitAsync(
                run.ProductionUnitId,
                cancellationToken)
            .ConfigureAwait(false);
        if (unitEntry is null)
        {
            return Recovery($"Production Unit {run.ProductionUnitId} is missing.");
        }

        var unit = unitEntry.Aggregate;
        var evidence = new List<string>
        {
            $"unit:{unit.Id}:{unitEntry.Revision}"
        };
        if (unit.ActiveProductionRunId != run.RunId)
        {
            return Recovery(
                $"Production Unit {unit.Id} active run {unit.ActiveProductionRunId?.ToString() ?? "none"} "
                + $"does not match Production Run {run.RunId}.");
        }

        if (!string.Equals(
                unit.ProductModelId,
                run.ProductionUnitIdentity.ModelId,
                StringComparison.Ordinal)
            || !string.Equals(
                unit.IdentityKey,
                run.ProductionUnitIdentity.InputKey,
                StringComparison.Ordinal)
            || !string.Equals(
                unit.IdentityValue,
                run.ProductionUnitIdentity.Value,
                StringComparison.Ordinal)
            || unit.Disposition != run.Disposition)
        {
            return Recovery(
                $"Production Unit {unit.Id} evidence differs from frozen Production Run {run.RunId}.");
        }

        var location = unit.Location;
        MaterialReference slotMaterial = MaterialReference.ForProductionUnit(unit.Id);
        if (location is { Kind: MaterialLocationKind.CarrierPosition, CarrierId: { } carrierId })
        {
            var carrier = await materials.GetCarrierAsync(carrierId, cancellationToken)
                .ConfigureAwait(false);
            if (carrier is null)
            {
                return Recovery(
                    $"Production Unit {unit.Id} references missing Carrier {carrierId}.");
            }

            evidence.Add($"carrier:{carrierId.Value}:{carrier.Revision}");

            var carrierUnits = (await materials.ListProductionUnitsAsync(cancellationToken)
                    .ConfigureAwait(false))
                .Select(static entry => entry.Aggregate)
                .Where(candidate => candidate.Location is
                {
                    Kind: MaterialLocationKind.CarrierPosition,
                    CarrierId: { } candidateCarrierId
                } && candidateCarrierId == carrierId)
                .ToArray();
            if (carrierUnits.Length > carrier.Aggregate.Capacity
                || carrierUnits.GroupBy(
                        candidate => candidate.Location!.CarrierPositionId,
                        StringComparer.Ordinal)
                    .Any(group => group.Count() != 1))
            {
                return Recovery(
                    $"Carrier {carrierId} Production Unit positions differ from its capacity or contain duplicates.");
            }

            location = carrier.Aggregate.Location;
            slotMaterial = MaterialReference.ForCarrier(carrierId);
        }

        if (location is null)
        {
            return Waiting($"Production Unit {unit.Id} has not arrived at a Station.");
        }

        if (!string.Equals(
                location.LineId,
                run.ProductionLineDefinitionId,
                StringComparison.Ordinal)
            || !string.Equals(
                location.StationSystemId,
                operation.Definition.StationSystemId,
                StringComparison.Ordinal))
        {
            return Waiting(
                $"Production Unit {unit.Id} is at {location.LineId}/{location.StationSystemId}, "
                + $"waiting for {run.ProductionLineDefinitionId}/{operation.Definition.StationSystemId}.");
        }

        var slotRequirements = operation.Definition.ResourceRequirements
            .Where(requirement => requirement.Kind == ResourceKind.Slot)
            .ToArray();
        if (slotRequirements.Length > 1)
        {
            return Recovery(
                $"Operation {operation.Definition.OperationId} declares {slotRequirements.Length} Slot resources; "
                + "material readiness permits at most one explicit Slot resource.");
        }

        if (location.SlotAddress is not { } slotAddress)
        {
            return slotRequirements.Length == 0
                && operation.Definition.MaterialSlotRequirement is null
                ? Ready(evidence, [])
                : Waiting(
                    $"Production Unit {unit.Id} is not loaded in the Slot required by Operation "
                    + $"{operation.Definition.OperationId}.");
        }

        if (slotRequirements.Length == 1
            && !string.Equals(
                slotAddress.ToString(),
                slotRequirements[0].ResourceId,
                StringComparison.Ordinal))
        {
            return Waiting(
                $"Production Unit {unit.Id} is in Slot {slotAddress}, not required Slot "
                + $"{slotRequirements[0].ResourceId}.");
        }


        var materialSlotRequirement = operation.Definition.MaterialSlotRequirement;
        if (materialSlotRequirement is not null)
        {
            if (materialSlotRequirement.Resolution == MaterialSlotResolution.CurrentMaterialSlot
                && !string.Equals(
                    materialSlotRequirement.TopologyTargetId,
                    operation.Definition.StationSystemId,
                    StringComparison.Ordinal))
            {
                return Recovery(
                    $"Operation {operation.Definition.OperationId} CurrentMaterialSlot policy is not anchored to Station {operation.Definition.StationSystemId}.");
            }

            if (materialSlotRequirement.Resolution == MaterialSlotResolution.AvailableSlotInGroup
                && !materialSlotRequirement.EligibleSlotIds.Contains(
                    slotAddress.SlotId,
                    StringComparer.Ordinal))
            {
                return Waiting(
                    $"Production Unit {unit.Id} is in Slot {slotAddress.SlotId}, which is outside frozen Slot Group {materialSlotRequirement.TopologyTargetId}.");
            }
        }

        var slot = await materials.GetSlotAsync(slotAddress, cancellationToken)
            .ConfigureAwait(false);
        if (slot is null)
        {
            return Recovery($"Required Slot {slotAddress} is not registered.");
        }

        evidence.Add($"slot:{slotAddress}:{slot.Revision}");

        if (slot.Aggregate.Material != slotMaterial)
        {
            return Recovery(
                $"Slot {slotAddress} material {slot.Aggregate.Material} conflicts with physical location "
                + $"of {slotMaterial}.");
        }

        return slot.Aggregate.Status == SlotOccupancyStatus.Running
            ? new ProductionOperationReadiness(
                ProductionOperationReadinessKind.Ready,
                "Production material is running in its bound Slot.",
                [new ResourceRequirement(ResourceKind.Slot, slotAddress.ToString())],
                string.Join("|", evidence))
            : Waiting(
                $"Slot {slotAddress} is bound to {slotMaterial} but must be Running; "
                + $"current status is {slot.Aggregate.Status}.");
    }

    private static ProductionOperationReadiness Waiting(string reason) => new(
        ProductionOperationReadinessKind.Waiting,
        reason,
        [],
        null);

    private static ProductionOperationReadiness Recovery(string reason) => new(
        ProductionOperationReadinessKind.RecoveryRequired,
        reason,
        [],
        null);

    private static ProductionOperationReadiness Ready(
        IReadOnlyCollection<string> evidence,
        IReadOnlyCollection<ResourceRequirement> resources) => new(
        ProductionOperationReadinessKind.Ready,
        "Production material satisfies the Station execution preconditions.",
        resources,
        string.Join("|", evidence));
}
