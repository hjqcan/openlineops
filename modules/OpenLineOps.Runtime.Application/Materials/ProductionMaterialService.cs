using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.Operations;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Materials;

public sealed class ProductionMaterialService
{
    private readonly IProductionMaterialRepository _repository;
    private readonly IProductionRunRepository _productionRuns;

    public ProductionMaterialService(
        IProductionMaterialRepository repository,
        IProductionRunRepository productionRuns)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _productionRuns = productionRuns ?? throw new ArgumentNullException(nameof(productionRuns));
    }

    public async ValueTask<RuntimeOperationResult> RegisterLotAsync(
        RegisterProductionLotCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateEnvelope(command.ActorId, command.OccurredAtUtc);
        var lot = ProductionLot.Register(
            command.LotId,
            command.ProductModelId,
            command.DeclaredQuantity,
            command.ActorId,
            command.OccurredAtUtc);
        return await _repository.TryAddAsync(lot, cancellationToken).ConfigureAwait(false)
            ? RuntimeOperationResult.Accepted("Production Lot registered.")
            : Duplicate("Production Lot", command.LotId.Value);
    }

    public async ValueTask<RuntimeOperationResult> RegisterUnitAsync(
        RegisterProductionUnitCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateEnvelope(command.ActorId, command.OccurredAtUtc);
        if (command.LotId is not null)
        {
            var lotEntry = await _repository
                .GetProductionLotAsync(command.LotId, cancellationToken)
                .ConfigureAwait(false);
            if (lotEntry is null)
            {
                return NotFound("Production Lot", command.LotId.Value);
            }

            if (!string.Equals(
                    lotEntry.Aggregate.ProductModelId,
                    command.ProductModelId,
                    StringComparison.Ordinal))
            {
                return RuntimeOperationResult.Rejected(
                    "Runtime.ProductionLotModelMismatch",
                    $"Production Lot {command.LotId} belongs to model "
                    + $"{lotEntry.Aggregate.ProductModelId}, not {command.ProductModelId}.");
            }
        }

        var unit = ProductionUnit.Register(
            command.ProductionUnitId,
            command.ProductModelId,
            command.IdentityKey,
            command.IdentityValue,
            command.LotId,
            command.ActorId,
            command.OccurredAtUtc);
        return await _repository.TryAddAsync(unit, cancellationToken).ConfigureAwait(false)
            ? RuntimeOperationResult.Accepted("Production Unit registered.")
            : Duplicate("Production Unit or product identity", command.ProductionUnitId.ToString());
    }

    public async ValueTask<RuntimeOperationResult> RegisterCarrierAsync(
        RegisterCarrierCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateEnvelope(command.ActorId, command.OccurredAtUtc);
        var carrier = Carrier.Register(
            command.CarrierId,
            command.CarrierTypeId,
            command.Capacity,
            command.ActorId,
            command.OccurredAtUtc);
        return await _repository.TryAddAsync(carrier, cancellationToken).ConfigureAwait(false)
            ? RuntimeOperationResult.Accepted("Carrier registered.")
            : Duplicate("Carrier", command.CarrierId.Value);
    }

    public async ValueTask<RuntimeOperationResult> RegisterSlotAsync(
        RegisterSlotCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateEnvelope(command.ActorId, command.OccurredAtUtc);
        var occupancy = SlotOccupancy.Register(command.Slot, command.OccurredAtUtc);
        return await _repository.TryAddAsync(occupancy, cancellationToken).ConfigureAwait(false)
            ? RuntimeOperationResult.Accepted("Slot registered for occupancy tracking.")
            : Duplicate("Slot", command.Slot.ToString());
    }

    public async ValueTask<RuntimeOperationResult> ArriveAsync(
        ArriveMaterialCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.EvidenceId == Guid.Empty)
        {
            throw new ArgumentException(
                "Material arrival evidence id cannot be empty.",
                nameof(command));
        }

        ValidateEnvelope(command.ActorId, command.OccurredAtUtc);
        return command.Material.Kind switch
        {
            MaterialKind.ProductionUnit => await ArriveProductionUnitAsync(command, cancellationToken)
                .ConfigureAwait(false),
            MaterialKind.Carrier => await ArriveCarrierAsync(command, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                $"Unsupported material kind {command.Material.Kind}.")
        };
    }

    public async ValueTask<RuntimeOperationResult> ReserveSlotAsync(
        ReserveSlotCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateEnvelope(command.ActorId, command.OccurredAtUtc);
        var slot = await _repository.GetSlotAsync(command.Slot, cancellationToken)
            .ConfigureAwait(false);
        if (slot is null)
        {
            return NotFound("Slot", command.Slot.ToString());
        }

        var material = await GetMaterialStateAsync(command.Material, cancellationToken)
            .ConfigureAwait(false);
        if (!material.Found)
        {
            return NotFound(command.Material.Kind.ToString(), command.Material.Value);
        }

        var expectedQueue = MaterialLocation.AtStation(
            command.Slot.LineId,
            command.Slot.StationSystemId);
        if (!Equals(material.Location, expectedQueue))
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.MaterialNotAtStation",
                $"Material {command.Material} is not in the target Station queue.");
        }

        if (!material.CanEnterProduction)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.MaterialDispositionRejected",
                $"Material {command.Material} cannot enter Slot processing in its current disposition.");
        }

        var result = slot.Aggregate.Reserve(command.Material, command.OccurredAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        await CommitSlotWithMaterialFenceAsync(
                slot,
                material,
                command.Material,
                SlotOccupancyStatus.Available,
                command.ActorId,
                command.OccurredAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    public async ValueTask<RuntimeOperationResult> ReleaseSlotReservationAsync(
        ReleaseSlotReservationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateEnvelope(command.ActorId, command.OccurredAtUtc);
        var slot = await _repository.GetSlotAsync(command.Slot, cancellationToken)
            .ConfigureAwait(false);
        if (slot is null)
        {
            return NotFound("Slot", command.Slot.ToString());
        }

        var material = await GetMaterialStateAsync(command.Material, cancellationToken)
            .ConfigureAwait(false);
        if (!material.Found)
        {
            return NotFound(command.Material.Kind.ToString(), command.Material.Value);
        }

        var result = slot.Aggregate.ReleaseReservation(command.Material, command.OccurredAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        await CommitSlotWithMaterialFenceAsync(
                slot,
                material,
                command.Material,
                SlotOccupancyStatus.Reserved,
                command.ActorId,
                command.OccurredAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    public async ValueTask<RuntimeOperationResult> LoadSlotAsync(
        LoadSlotCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateEnvelope(command.ActorId, command.OccurredAtUtc);
        var movement = await RequireMaterialMovementAllowedAsync(
                command.Material,
                cancellationToken)
            .ConfigureAwait(false);
        if (!movement.Succeeded)
        {
            return movement;
        }

        var slot = await _repository.GetSlotAsync(command.Slot, cancellationToken)
            .ConfigureAwait(false);
        if (slot is null)
        {
            return NotFound("Slot", command.Slot.ToString());
        }

        var result = slot.Aggregate.Load(command.Material, command.OccurredAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        var source = MaterialLocation.AtStation(command.Slot.LineId, command.Slot.StationSystemId);
        var destination = MaterialLocation.InSlot(command.Slot);
        return await TransferWithSlotAsync(
                command.Material,
                source,
                destination,
                command.OccurredAtUtc,
                slot,
                result,
                null,
                SlotOccupancyStatus.Reserved,
                command.ActorId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<RuntimeOperationResult> StartSlotAsync(
        StartSlotCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateEnvelope(command.ActorId, command.OccurredAtUtc);
        var material = await GetMaterialStateAsync(command.Material, cancellationToken)
            .ConfigureAwait(false);
        if (!material.Found)
        {
            return NotFound(command.Material.Kind.ToString(), command.Material.Value);
        }

        if (!Equals(material.Location, MaterialLocation.InSlot(command.Slot)))
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.SlotMaterialLocationMismatch",
                $"Material {command.Material} is not physically located in Slot {command.Slot}.");
        }

        if (!material.CanEnterProduction)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.MaterialDispositionRejected",
                $"Material {command.Material} cannot start Slot processing in its current disposition.");
        }

        var slot = await _repository.GetSlotAsync(command.Slot, cancellationToken)
            .ConfigureAwait(false);
        if (slot is null)
        {
            return NotFound("Slot", command.Slot.ToString());
        }

        var result = slot.Aggregate.Start(command.Material, command.OccurredAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        await CommitSlotWithMaterialFenceAsync(
                slot,
                material,
                command.Material,
                SlotOccupancyStatus.Occupied,
                command.ActorId,
                command.OccurredAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    public async ValueTask<RuntimeOperationResult> CompleteSlotAsync(
        CompleteSlotCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateEnvelope(command.ActorId, command.OccurredAtUtc);
        var ownership = await RequireMaterialMovementAllowedAsync(
                command.Material,
                cancellationToken,
                allowActiveRunWithoutRunningOperation: false)
            .ConfigureAwait(false);
        if (!ownership.Succeeded)
        {
            return ownership;
        }

        var slot = await _repository.GetSlotAsync(command.Slot, cancellationToken)
            .ConfigureAwait(false);
        if (slot is null)
        {
            return NotFound("Slot", command.Slot.ToString());
        }

        var material = await GetMaterialStateAsync(command.Material, cancellationToken)
            .ConfigureAwait(false);
        if (!material.Found)
        {
            return NotFound(command.Material.Kind.ToString(), command.Material.Value);
        }

        if (!Equals(material.Location, MaterialLocation.InSlot(command.Slot)))
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.SlotMaterialLocationMismatch",
                $"Material {command.Material} is not physically located in Slot {command.Slot}.");
        }

        var result = slot.Aggregate.Complete(command.Material, command.OccurredAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        await _repository.CommitAsync(
            new ProductionMaterialCommit(
                slots: [new SlotOccupancyUpdate(slot.Aggregate, slot.Revision)],
                timeline:
                [
                    ProductionMaterialTimelineEntry.SlotOccupancy(
                        Guid.NewGuid(),
                        command.Slot,
                        command.Material,
                        MaterialProductionRunId(material),
                        null,
                        null,
                        SlotOccupancyStatus.Running,
                        SlotOccupancyStatus.Occupied,
                        command.ActorId,
                        command.OccurredAtUtc)
                ]),
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    public ValueTask<RuntimeOperationResult> BlockSlotAsync(
        BlockSlotCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return TransitionUnboundSlotAsync(
            command.Slot,
            command.ActorId,
            command.OccurredAtUtc,
            occupancy => occupancy.Block(command.Reason, command.OccurredAtUtc),
            SlotOccupancyStatus.Available,
            SlotOccupancyStatus.Blocked,
            cancellationToken);
    }

    public ValueTask<RuntimeOperationResult> UnblockSlotAsync(
        UnblockSlotCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return TransitionUnboundSlotAsync(
            command.Slot,
            command.ActorId,
            command.OccurredAtUtc,
            occupancy => occupancy.Unblock(command.OccurredAtUtc),
            SlotOccupancyStatus.Blocked,
            SlotOccupancyStatus.Available,
            cancellationToken);
    }

    public ValueTask<RuntimeOperationResult> SetSlotOfflineAsync(
        SetSlotOfflineCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return TransitionUnboundSlotAsync(
            command.Slot,
            command.ActorId,
            command.OccurredAtUtc,
            occupancy => occupancy.SetOffline(command.OccurredAtUtc),
            null,
            SlotOccupancyStatus.Offline,
            cancellationToken);
    }

    public ValueTask<RuntimeOperationResult> BringSlotOnlineAsync(
        BringSlotOnlineCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return TransitionUnboundSlotAsync(
            command.Slot,
            command.ActorId,
            command.OccurredAtUtc,
            occupancy => occupancy.BringOnline(command.OccurredAtUtc),
            SlotOccupancyStatus.Offline,
            SlotOccupancyStatus.Available,
            cancellationToken);
    }

    public async ValueTask<RuntimeOperationResult> UnloadSlotAsync(
        UnloadSlotCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateEnvelope(command.ActorId, command.OccurredAtUtc);
        var movement = await RequireMaterialMovementAllowedAsync(
                command.Material,
                cancellationToken)
            .ConfigureAwait(false);
        if (!movement.Succeeded)
        {
            return movement;
        }

        var slot = await _repository.GetSlotAsync(command.Slot, cancellationToken)
            .ConfigureAwait(false);
        if (slot is null)
        {
            return NotFound("Slot", command.Slot.ToString());
        }

        var destination = await ValidateDestinationAsync(
                command.Material,
                command.Destination,
                cancellationToken)
            .ConfigureAwait(false);
        if (destination.Rejection is not null)
        {
            return destination.Rejection;
        }

        var result = slot.Aggregate.Unload(command.Material, command.OccurredAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        return await TransferWithSlotAsync(
                command.Material,
                MaterialLocation.InSlot(command.Slot),
                command.Destination,
                command.OccurredAtUtc,
                slot,
                result,
                destination.CarrierFence,
                SlotOccupancyStatus.Occupied,
                command.ActorId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<RuntimeOperationResult> TransferAsync(
        TransferMaterialCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateEnvelope(command.ActorId, command.OccurredAtUtc);
        var movement = await RequireMaterialMovementAllowedAsync(
                command.Material,
                cancellationToken)
            .ConfigureAwait(false);
        if (!movement.Succeeded)
        {
            return movement;
        }

        if (command.ExpectedLocation.Kind == MaterialLocationKind.Slot
            || command.Destination.Kind == MaterialLocationKind.Slot)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.SlotTransferRequiresOccupancyCommand",
                "Transfers into or out of a Slot must use LoadSlot or UnloadSlot so material "
                + "location and Slot occupancy commit atomically.");
        }

        var destination = await ValidateDestinationAsync(
                command.Material,
                command.Destination,
                cancellationToken)
            .ConfigureAwait(false);
        if (destination.Rejection is not null)
        {
            return destination.Rejection;
        }

        return command.Material.Kind switch
        {
            MaterialKind.ProductionUnit => await TransferProductionUnitAsync(
                    command.Material.RequireProductionUnitId(),
                    command.ExpectedLocation,
                    command.Destination,
                    command.OccurredAtUtc,
                    null,
                    destination.CarrierFence,
                    null,
                    command.ActorId,
                    cancellationToken)
                .ConfigureAwait(false),
            MaterialKind.Carrier => await TransferCarrierAsync(
                    command.Material.RequireCarrierId(),
                    command.ExpectedLocation,
                    command.Destination,
                    command.OccurredAtUtc,
                    null,
                    null,
                    null,
                    command.ActorId,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                $"Unsupported material kind {command.Material.Kind}.")
        };
    }

    public async ValueTask<RuntimeOperationResult> HoldAsync(
        HoldProductionUnitCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateEnvelope(command.ActorId, command.OccurredAtUtc);
        var unit = await _repository.GetProductionUnitAsync(command.ProductionUnitId, cancellationToken)
            .ConfigureAwait(false);
        if (unit is null)
        {
            return NotFound("Production Unit", command.ProductionUnitId.ToString());
        }

        var running = await IsRunningInSlotAsync(unit.Aggregate.Location, cancellationToken)
            .ConfigureAwait(false);
        if (running)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionUnitRunning",
                $"Production Unit {command.ProductionUnitId} must stop before it can be held.");
        }

        var previousDisposition = unit.Aggregate.Disposition;
        var result = unit.Aggregate.Hold(command.Reason, command.OccurredAtUtc);
        return await CommitDispositionAsync(
                unit,
                previousDisposition,
                result,
                command.ActorId,
                command.OccurredAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<RuntimeOperationResult> ReleaseAsync(
        ReleaseProductionUnitCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateEnvelope(command.ActorId, command.OccurredAtUtc);
        var unit = await _repository.GetProductionUnitAsync(command.ProductionUnitId, cancellationToken)
            .ConfigureAwait(false);
        if (unit is null)
        {
            return NotFound("Production Unit", command.ProductionUnitId.ToString());
        }

        var previousDisposition = unit.Aggregate.Disposition;
        var result = unit.Aggregate.Release(command.OccurredAtUtc);
        return await CommitDispositionAsync(
                unit,
                previousDisposition,
                result,
                command.ActorId,
                command.OccurredAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<RuntimeOperationResult> ScrapAsync(
        ScrapProductionUnitCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateEnvelope(command.ActorId, command.OccurredAtUtc);
        var unit = await _repository.GetProductionUnitAsync(command.ProductionUnitId, cancellationToken)
            .ConfigureAwait(false);
        if (unit is null)
        {
            return NotFound("Production Unit", command.ProductionUnitId.ToString());
        }

        var running = await IsRunningInSlotAsync(unit.Aggregate.Location, cancellationToken)
            .ConfigureAwait(false);
        if (running)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionUnitRunning",
                $"Production Unit {command.ProductionUnitId} must stop before it can be scrapped.");
        }

        var previousDisposition = unit.Aggregate.Disposition;
        var result = unit.Aggregate.Scrap(command.Reason, command.OccurredAtUtc);
        return await CommitDispositionAsync(
                unit,
                previousDisposition,
                result,
                command.ActorId,
                command.OccurredAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<RuntimeOperationResult> LinkGenealogyAsync(
        LinkMaterialGenealogyCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateEnvelope(command.ActorId, command.OccurredAtUtc);
        var parent = await _repository.GetProductionUnitAsync(command.ParentUnitId, cancellationToken)
            .ConfigureAwait(false);
        var child = await _repository.GetProductionUnitAsync(command.ChildUnitId, cancellationToken)
            .ConfigureAwait(false);
        if (parent is null)
        {
            return NotFound("Production Unit", command.ParentUnitId.ToString());
        }

        if (child is null)
        {
            return NotFound("Production Unit", command.ChildUnitId.ToString());
        }

        var links = await _repository.ListGenealogyLinksAsync(cancellationToken)
            .ConfigureAwait(false);
        if (links.Any(link => link.ParentUnitId == command.ParentUnitId
            && link.ChildUnitId == command.ChildUnitId
            && string.Equals(link.Relationship, command.Relationship, StringComparison.Ordinal)))
        {
            return Duplicate(
                "Material genealogy relationship",
                $"{command.ParentUnitId}->{command.ChildUnitId}:{command.Relationship}");
        }

        if (WouldCreateGenealogyCycle(command.ParentUnitId, command.ChildUnitId, links))
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.MaterialGenealogyCycle",
                "The genealogy relationship would create a Production Unit cycle.");
        }

        var link = new MaterialGenealogyLink(
            command.LinkId,
            command.ParentUnitId,
            command.ChildUnitId,
            command.Relationship,
            command.OperationId,
            command.ActorId,
            command.OccurredAtUtc);
        return await _repository.TryAddAsync(link, cancellationToken).ConfigureAwait(false)
            ? RuntimeOperationResult.Accepted("Material genealogy linked.")
            : Duplicate("Material genealogy link", command.LinkId.ToString());
    }

    private async ValueTask<RuntimeOperationResult> TransitionUnboundSlotAsync(
        SlotAddress address,
        string actorId,
        DateTimeOffset occurredAtUtc,
        Func<SlotOccupancy, RuntimeOperationResult> transition,
        SlotOccupancyStatus? expectedPreviousStatus,
        SlotOccupancyStatus targetStatus,
        CancellationToken cancellationToken)
    {
        ValidateEnvelope(actorId, occurredAtUtc);
        var slot = await _repository.GetSlotAsync(address, cancellationToken)
            .ConfigureAwait(false);
        if (slot is null)
        {
            return NotFound("Slot", address.ToString());
        }

        var previousStatus = slot.Aggregate.Status;
        if (slot.Aggregate.Material is not null
            || expectedPreviousStatus is not null && previousStatus != expectedPreviousStatus)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.SlotAvailabilityTransitionRejected",
                $"Slot {address} cannot change availability from {previousStatus} while bound to material.");
        }

        var result = transition(slot.Aggregate);
        if (!result.Succeeded)
        {
            return result;
        }

        if (slot.Aggregate.Status != targetStatus)
        {
            throw new InvalidOperationException(
                $"Slot {address} transition did not reach {targetStatus}.");
        }

        await _repository.CommitAsync(
            new ProductionMaterialCommit(
                slots: [new SlotOccupancyUpdate(slot.Aggregate, slot.Revision)],
                timeline:
                [
                    ProductionMaterialTimelineEntry.SlotOccupancy(
                        Guid.NewGuid(),
                        address,
                        null,
                        null,
                        null,
                        null,
                        previousStatus,
                        targetStatus,
                        actorId,
                        occurredAtUtc)
                ]),
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<RuntimeOperationResult> ArriveProductionUnitAsync(
        ArriveMaterialCommand command,
        CancellationToken cancellationToken)
    {
        var entry = await _repository
            .GetProductionUnitAsync(command.Material.RequireProductionUnitId(), cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return NotFound("Production Unit", command.Material.Value);
        }

        var result = entry.Aggregate.Arrive(command.StationLocation, command.OccurredAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        await _repository.CommitAsync(
            new ProductionMaterialCommit(
                productionUnits: [new ProductionUnitUpdate(entry.Aggregate, entry.Revision)],
                timeline:
                [
                    ProductionMaterialTimelineEntry.Location(
                        command.EvidenceId,
                        command.Material,
                        entry.Aggregate.ActiveProductionRunId,
                        null,
                        command.StationLocation,
                        command.ActorId,
                        command.OccurredAtUtc)
                ]),
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<RuntimeOperationResult> ArriveCarrierAsync(
        ArriveMaterialCommand command,
        CancellationToken cancellationToken)
    {
        var entry = await _repository.GetCarrierAsync(
                command.Material.RequireCarrierId(),
                cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return NotFound("Carrier", command.Material.Value);
        }

        var result = entry.Aggregate.Arrive(command.StationLocation, command.OccurredAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        await _repository.CommitAsync(
            new ProductionMaterialCommit(
                carriers: [new CarrierUpdate(entry.Aggregate, entry.Revision)],
                timeline:
                [
                    ProductionMaterialTimelineEntry.Location(
                        command.EvidenceId,
                        command.Material,
                        null,
                        null,
                        command.StationLocation,
                        command.ActorId,
                        command.OccurredAtUtc)
                ]),
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<RuntimeOperationResult> TransferWithSlotAsync(
        MaterialReference material,
        MaterialLocation expectedLocation,
        MaterialLocation destination,
        DateTimeOffset occurredAtUtc,
        ProductionMaterialPersistenceEntry<SlotOccupancy> slot,
        RuntimeOperationResult slotResult,
        CarrierUpdate? destinationCarrierFence,
        SlotOccupancyStatus previousSlotStatus,
        string actorId,
        CancellationToken cancellationToken)
    {
        return material.Kind switch
        {
            MaterialKind.ProductionUnit => await TransferProductionUnitAsync(
                    material.RequireProductionUnitId(),
                    expectedLocation,
                    destination,
                    occurredAtUtc,
                    new SlotOccupancyUpdate(slot.Aggregate, slot.Revision),
                    destinationCarrierFence,
                    previousSlotStatus,
                    actorId,
                    cancellationToken)
                .ConfigureAwait(false),
            MaterialKind.Carrier => await TransferCarrierAsync(
                    material.RequireCarrierId(),
                    expectedLocation,
                    destination,
                    occurredAtUtc,
                    new SlotOccupancyUpdate(slot.Aggregate, slot.Revision),
                    null,
                    previousSlotStatus,
                    actorId,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => slotResult
        };
    }

    private async ValueTask<RuntimeOperationResult> TransferProductionUnitAsync(
        ProductionUnitId productionUnitId,
        MaterialLocation expectedLocation,
        MaterialLocation destination,
        DateTimeOffset occurredAtUtc,
        SlotOccupancyUpdate? slotUpdate,
        CarrierUpdate? destinationCarrierFence,
        SlotOccupancyStatus? previousSlotStatus,
        string actorId,
        CancellationToken cancellationToken)
    {
        var unit = await _repository.GetProductionUnitAsync(productionUnitId, cancellationToken)
            .ConfigureAwait(false);
        if (unit is null)
        {
            return NotFound("Production Unit", productionUnitId.ToString());
        }

        var result = unit.Aggregate.Transfer(expectedLocation, destination, occurredAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        await _repository.CommitAsync(
            new ProductionMaterialCommit(
                productionUnits: [new ProductionUnitUpdate(unit.Aggregate, unit.Revision)],
                carriers: destinationCarrierFence is null ? null : [destinationCarrierFence],
                slots: slotUpdate is null ? null : [slotUpdate],
                timeline: CreateTransferTimeline(
                    MaterialReference.ForProductionUnit(unit.Aggregate.Id),
                    unit.Aggregate.ActiveProductionRunId,
                    expectedLocation,
                    destination,
                    occurredAtUtc,
                    slotUpdate,
                    previousSlotStatus,
                    actorId)),
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<RuntimeOperationResult> TransferCarrierAsync(
        CarrierId carrierId,
        MaterialLocation expectedLocation,
        MaterialLocation destination,
        DateTimeOffset occurredAtUtc,
        SlotOccupancyUpdate? slotUpdate,
        CarrierUpdate? destinationCarrierFence,
        SlotOccupancyStatus? previousSlotStatus,
        string actorId,
        CancellationToken cancellationToken)
    {
        var carrier = await _repository.GetCarrierAsync(carrierId, cancellationToken)
            .ConfigureAwait(false);
        if (carrier is null)
        {
            return NotFound("Carrier", carrierId.Value);
        }

        var result = carrier.Aggregate.Transfer(expectedLocation, destination, occurredAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        await _repository.CommitAsync(
            new ProductionMaterialCommit(
                carriers: destinationCarrierFence is null
                    ? [new CarrierUpdate(carrier.Aggregate, carrier.Revision)]
                    : throw new InvalidOperationException("A Carrier cannot target a Carrier position."),
                slots: slotUpdate is null ? null : [slotUpdate],
                timeline: CreateTransferTimeline(
                    MaterialReference.ForCarrier(carrier.Aggregate.Id),
                    null,
                    expectedLocation,
                    destination,
                    occurredAtUtc,
                    slotUpdate,
                    previousSlotStatus,
                    actorId)),
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<RuntimeOperationResult> CommitDispositionAsync(
        ProductionMaterialPersistenceEntry<ProductionUnit> entry,
        ProductDisposition previousDisposition,
        RuntimeOperationResult result,
        string actorId,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        if (!result.Succeeded)
        {
            return result;
        }

        await _repository.CommitAsync(
            new ProductionMaterialCommit(
                productionUnits: [new ProductionUnitUpdate(entry.Aggregate, entry.Revision)],
                timeline:
                [
                    ProductionMaterialTimelineEntry.Disposition(
                        Guid.NewGuid(),
                        entry.Aggregate.Id,
                        entry.Aggregate.ActiveProductionRunId,
                        previousDisposition,
                        entry.Aggregate.Disposition,
                        entry.Aggregate.DispositionReason,
                        actorId,
                        occurredAtUtc)
                ]),
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask CommitSlotWithMaterialFenceAsync(
        ProductionMaterialPersistenceEntry<SlotOccupancy> slot,
        MaterialState material,
        MaterialReference materialReference,
        SlotOccupancyStatus previousStatus,
        string actorId,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        await _repository.CommitAsync(
            new ProductionMaterialCommit(
                productionUnits: material.ProductionUnitFence is null
                    ? null
                    : [material.ProductionUnitFence],
                carriers: material.CarrierFence is null
                    ? null
                    : [material.CarrierFence],
                slots: [new SlotOccupancyUpdate(slot.Aggregate, slot.Revision)],
                timeline:
                [
                    ProductionMaterialTimelineEntry.SlotOccupancy(
                        Guid.NewGuid(),
                        slot.Aggregate.Address,
                        materialReference,
                        MaterialProductionRunId(material),
                        null,
                        null,
                        previousStatus,
                        slot.Aggregate.Status,
                        actorId,
                        occurredAtUtc)
                ]),
            cancellationToken).ConfigureAwait(false);
    }

    private static List<ProductionMaterialTimelineEntry> CreateTransferTimeline(
        MaterialReference material,
        ProductionRunId? productionRunId,
        MaterialLocation source,
        MaterialLocation destination,
        DateTimeOffset occurredAtUtc,
        SlotOccupancyUpdate? slotUpdate,
        SlotOccupancyStatus? previousSlotStatus,
        string actorId)
    {
        var timeline = new List<ProductionMaterialTimelineEntry>
        {
            ProductionMaterialTimelineEntry.Location(
                Guid.NewGuid(),
                material,
                productionRunId,
                source,
                destination,
                actorId,
                occurredAtUtc)
        };
        if (slotUpdate is not null)
        {
            timeline.Add(ProductionMaterialTimelineEntry.SlotOccupancy(
                Guid.NewGuid(),
                slotUpdate.Aggregate.Address,
                material,
                productionRunId,
                null,
                null,
                previousSlotStatus ?? throw new InvalidOperationException(
                    "A Slot transfer requires its previous occupancy status."),
                slotUpdate.Aggregate.Status,
                actorId,
                occurredAtUtc));
        }

        return timeline;
    }

    private static ProductionRunId? MaterialProductionRunId(MaterialState material) =>
        material.ProductionUnitFence?.Aggregate.ActiveProductionRunId;

    private async ValueTask<MaterialState> GetMaterialStateAsync(
        MaterialReference material,
        CancellationToken cancellationToken)
    {
        switch (material.Kind)
        {
            case MaterialKind.ProductionUnit:
                {
                    var entry = await _repository
                        .GetProductionUnitAsync(material.RequireProductionUnitId(), cancellationToken)
                        .ConfigureAwait(false);
                    return entry is null
                        ? MaterialState.Missing
                        : new MaterialState(
                            true,
                            entry.Aggregate.Location,
                            entry.Aggregate.Disposition is ProductDisposition.InProcess
                                or ProductDisposition.Nonconforming,
                            new ProductionUnitUpdate(entry.Aggregate, entry.Revision),
                            null);
                }
            case MaterialKind.Carrier:
                {
                    var entry = await _repository.GetCarrierAsync(
                            material.RequireCarrierId(),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return entry is null
                        ? MaterialState.Missing
                        : new MaterialState(
                            true,
                            entry.Aggregate.Location,
                            true,
                            null,
                            new CarrierUpdate(entry.Aggregate, entry.Revision));
                }
            default:
                throw new InvalidOperationException($"Unsupported material kind {material.Kind}.");
        }
    }

    private async ValueTask<DestinationValidation> ValidateDestinationAsync(
        MaterialReference material,
        MaterialLocation destination,
        CancellationToken cancellationToken)
    {
        if (destination.Kind != MaterialLocationKind.CarrierPosition)
        {
            return DestinationValidation.Accepted;
        }

        if (material.Kind != MaterialKind.ProductionUnit)
        {
            return DestinationValidation.Rejected(RuntimeOperationResult.Rejected(
                "Runtime.CarrierNestingRejected",
                "Only a Production Unit can occupy a Carrier position."));
        }

        var carrierId = destination.CarrierId
            ?? throw new InvalidOperationException("Carrier position has no Carrier identity.");
        var carrier = await _repository.GetCarrierAsync(carrierId, cancellationToken)
            .ConfigureAwait(false);
        if (carrier is null)
        {
            return DestinationValidation.Rejected(NotFound("Carrier", carrierId.Value));
        }

        var movingUnitId = material.RequireProductionUnitId();
        var units = await _repository.ListProductionUnitsAsync(cancellationToken)
            .ConfigureAwait(false);
        var occupants = units
            .Where(entry => entry.Aggregate.Id != movingUnitId)
            .Where(entry => entry.Aggregate.Location is
            {
                Kind: MaterialLocationKind.CarrierPosition,
                CarrierId: { } occupantCarrierId
            } && occupantCarrierId == carrierId)
            .ToArray();
        if (occupants.Any(entry => string.Equals(
            entry.Aggregate.Location!.CarrierPositionId,
            destination.CarrierPositionId,
            StringComparison.Ordinal)))
        {
            return DestinationValidation.Rejected(RuntimeOperationResult.Rejected(
                "Runtime.CarrierPositionOccupied",
                $"Carrier {carrierId} position {destination.CarrierPositionId} is occupied."));
        }

        if (occupants.Length >= carrier.Aggregate.Capacity)
        {
            return DestinationValidation.Rejected(RuntimeOperationResult.Rejected(
                "Runtime.CarrierCapacityExceeded",
                $"Carrier {carrierId} has reached capacity {carrier.Aggregate.Capacity}."));
        }

        return new DestinationValidation(
            null,
            new CarrierUpdate(carrier.Aggregate, carrier.Revision));
    }

    private async ValueTask<bool> IsRunningInSlotAsync(
        MaterialLocation? location,
        CancellationToken cancellationToken)
    {
        if (location?.Kind == MaterialLocationKind.CarrierPosition
            && location.CarrierId is { } carrierId)
        {
            var carrier = await _repository.GetCarrierAsync(carrierId, cancellationToken)
                .ConfigureAwait(false);
            location = carrier?.Aggregate.Location;
        }

        if (location?.SlotAddress is not { } slotAddress)
        {
            return false;
        }

        var slot = await _repository.GetSlotAsync(slotAddress, cancellationToken)
            .ConfigureAwait(false);
        return slot?.Aggregate.Status == SlotOccupancyStatus.Running;
    }

    private async ValueTask<RuntimeOperationResult> RequireMaterialMovementAllowedAsync(
        MaterialReference material,
        CancellationToken cancellationToken,
        bool allowActiveRunWithoutRunningOperation = true)
    {
        var units = material.Kind switch
        {
            MaterialKind.ProductionUnit =>
                await LoadSingleUnitAsync(material.RequireProductionUnitId(), cancellationToken)
                    .ConfigureAwait(false),
            MaterialKind.Carrier => (await _repository.ListProductionUnitsAsync(cancellationToken)
                    .ConfigureAwait(false))
                .Select(static entry => entry.Aggregate)
                .Where(unit => unit.Location is
                {
                    Kind: MaterialLocationKind.CarrierPosition,
                    CarrierId: { } carrierId
                } && carrierId == material.RequireCarrierId())
                .ToArray(),
            _ => throw new InvalidOperationException($"Unsupported material kind {material.Kind}.")
        };

        foreach (var unit in units)
        {
            if (unit.ActiveProductionRunId is not { } runId)
            {
                continue;
            }

            if (!allowActiveRunWithoutRunningOperation)
            {
                return RuntimeOperationResult.Rejected(
                    "Runtime.ProductionMaterialRunOwnsSlotLifecycle",
                    $"Production Run {runId} owns Slot completion for Production Unit {unit.Id}.");
            }

            var run = await _productionRuns.GetByIdAsync(runId, cancellationToken)
                .ConfigureAwait(false);
            if (run is null
                || run.Run.IsTerminal
                || run.Run.ProductionUnitId != unit.Id)
            {
                return RuntimeOperationResult.Rejected(
                    "Runtime.ProductionMaterialRunEvidenceMismatch",
                    $"Production Unit {unit.Id} active run evidence is inconsistent; recovery is required.");
            }

            if (run.Run.ControlState != ProductionRunControlState.Active
                || run.Run.Operations.Any(operation =>
                    operation.ExecutionStatus == ExecutionStatus.Running))
            {
                return RuntimeOperationResult.Rejected(
                    "Runtime.ProductionMaterialOperationRunning",
                    $"Production Unit {unit.Id} cannot move while Production Run {runId} is "
                    + $"{run.Run.ControlState} or has a Running Operation.");
            }
        }

        return RuntimeOperationResult.Accepted(
            "Material has no Running Operation and may be handed off.");
    }

    private async ValueTask<ProductionUnit[]> LoadSingleUnitAsync(
        ProductionUnitId productionUnitId,
        CancellationToken cancellationToken)
    {
        var unit = await _repository.GetProductionUnitAsync(productionUnitId, cancellationToken)
            .ConfigureAwait(false);
        return unit is null ? [] : [unit.Aggregate];
    }

    private static bool WouldCreateGenealogyCycle(
        ProductionUnitId parent,
        ProductionUnitId child,
        IReadOnlyCollection<MaterialGenealogyLink> links)
    {
        var childrenByParent = links
            .GroupBy(link => link.ParentUnitId)
            .ToDictionary(group => group.Key, group => group.Select(link => link.ChildUnitId).ToArray());
        var pending = new Stack<ProductionUnitId>();
        var visited = new HashSet<ProductionUnitId>();
        pending.Push(child);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            if (current == parent)
            {
                return true;
            }

            if (childrenByParent.TryGetValue(current, out var children))
            {
                foreach (var candidate in children)
                {
                    pending.Push(candidate);
                }
            }
        }

        return false;
    }

    private static void ValidateEnvelope(string actorId, DateTimeOffset occurredAtUtc)
    {
        if (string.IsNullOrWhiteSpace(actorId)
            || char.IsWhiteSpace(actorId[0])
            || char.IsWhiteSpace(actorId[^1]))
        {
            throw new ArgumentException("Actor id must be non-empty canonical text.", nameof(actorId));
        }

        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Command occurrence time must use the UTC offset.", nameof(occurredAtUtc));
        }
    }

    private static RuntimeOperationResult NotFound(string resourceKind, string resourceId)
    {
        return RuntimeOperationResult.Rejected(
            "Runtime.ProductionMaterialNotFound",
            $"{resourceKind} {resourceId} does not exist.");
    }

    private static RuntimeOperationResult Duplicate(string resourceKind, string resourceId)
    {
        return RuntimeOperationResult.Rejected(
            "Runtime.ProductionMaterialAlreadyExists",
            $"{resourceKind} {resourceId} already exists.");
    }

    private sealed record MaterialState(
        bool Found,
        MaterialLocation? Location,
        bool CanEnterProduction,
        ProductionUnitUpdate? ProductionUnitFence,
        CarrierUpdate? CarrierFence)
    {
        public static MaterialState Missing { get; } = new(false, null, false, null, null);
    }

    private sealed record DestinationValidation(
        RuntimeOperationResult? Rejection,
        CarrierUpdate? CarrierFence)
    {
        public static DestinationValidation Accepted { get; } = new(null, null);

        public static DestinationValidation Rejected(RuntimeOperationResult result)
        {
            ArgumentNullException.ThrowIfNull(result);
            if (result.Succeeded)
            {
                throw new ArgumentException("A destination rejection cannot be successful.", nameof(result));
            }

            return new DestinationValidation(result, null);
        }
    }
}
