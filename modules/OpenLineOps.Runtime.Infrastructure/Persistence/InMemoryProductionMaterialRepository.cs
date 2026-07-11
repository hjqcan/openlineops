using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class InMemoryProductionMaterialRepository : IProductionMaterialRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<ProductionUnitId, Stored<ProductionUnitSnapshot>> _units = [];
    private readonly Dictionary<ProductionLotId, Stored<ProductionLotSnapshot>> _lots = [];
    private readonly Dictionary<CarrierId, Stored<CarrierSnapshot>> _carriers = [];
    private readonly Dictionary<SlotAddress, Stored<SlotOccupancySnapshot>> _slots = [];
    private readonly Dictionary<MaterialGenealogyLinkId, MaterialGenealogyLink> _genealogy = [];
    private readonly Dictionary<Guid, ProductionMaterialTimelineEntry> _timeline = [];

    internal object CoordinationGate => _gate;

    internal bool TryReserveProductionRun(
        ProductionRun run,
        ProductionRunAdmission admission)
    {
        if (!_units.TryGetValue(run.ProductionUnitId, out var stored)
            || stored.Revision != admission.ExpectedRevision
            || stored.Snapshot != admission.ProductionUnit)
        {
            return false;
        }

        var unit = ProductionUnit.Restore(stored.Snapshot);
        var result = unit.ReserveProductionRun(run.Id, run.CreatedAtUtc);
        if (!result.Succeeded)
        {
            return false;
        }

        _units[unit.Id] = new Stored<ProductionUnitSnapshot>(
            unit.ToSnapshot(),
            checked(stored.Revision + 1));
        return true;
    }

    internal void SynchronizeProductionRun(ProductionRun run, long runRevision)
    {
        if (!_units.TryGetValue(run.ProductionUnitId, out var stored))
        {
            throw new InvalidDataException(
                $"Production Unit {run.ProductionUnitId} disappeared during Production Run {run.Id}.");
        }

        var unit = ProductionUnit.Restore(stored.Snapshot);
        var previousDisposition = unit.Disposition;
        var result = unit.SynchronizeProductionRun(
            run.Id,
            runRevision,
            run.Disposition,
            run.IsTerminal,
            run.FailureReason,
            run.LastTransitionAtUtc);
        if (!result.Succeeded)
        {
            throw new InvalidDataException(result.Message);
        }

        var slotUpdates = ResolveCompletedSlotUpdates(run, unit);

        _units[unit.Id] = new Stored<ProductionUnitSnapshot>(
            unit.ToSnapshot(),
            checked(stored.Revision + 1));
        foreach (var (address, slot, revision, completedOperation) in slotUpdates)
        {
            _slots[address] = new Stored<SlotOccupancySnapshot>(
                slot.ToSnapshot(),
                checked(revision + 1));
            AppendTimeline(ProductionMaterialTimelineEntry.SlotOccupancy(
                Guid.NewGuid(),
                address,
                slot.Material ?? throw new InvalidDataException(
                    $"Completed Slot {address} lost its material binding."),
                run.Id,
                completedOperation.OperationRunId,
                completedOperation.FencingToken,
                SlotOccupancyStatus.Running,
                SlotOccupancyStatus.Occupied,
                run.ActorId,
                slot.LastTransitionAtUtc));
        }

        if (previousDisposition != unit.Disposition)
        {
            AppendTimeline(ProductionMaterialTimelineEntry.Disposition(
                Guid.NewGuid(),
                unit.Id,
                run.Id,
                previousDisposition,
                unit.Disposition,
                unit.DispositionReason,
                run.ActorId,
                unit.LastDispositionTransitionAtUtc));
        }
    }

    private List<(
        SlotAddress Address,
        SlotOccupancy Slot,
        long Revision,
        CompletedSlotOperation CompletedOperation)>
        ResolveCompletedSlotUpdates(ProductionRun run, ProductionUnit unit)
    {
        var updates = new List<(
            SlotAddress,
            SlotOccupancy,
            long,
            CompletedSlotOperation)>();
        var expectedMaterial = unit.Location is
        {
            Kind: MaterialLocationKind.CarrierPosition,
            CarrierId: { } carrierId
        }
            ? MaterialReference.ForCarrier(carrierId)
            : MaterialReference.ForProductionUnit(unit.Id);
        foreach (var completedSlot in ProductionRunSlotLifecycle.ResolveCompletedSlots(run))
        {
            if (_timeline.Values.Any(evidence =>
                    ProductionRunSlotLifecycle.IsCompletionEvidence(
                        evidence,
                        run.Id,
                        completedSlot)))
            {
                continue;
            }

            var address = completedSlot.Address;
            if (!_slots.TryGetValue(address, out var storedSlot))
            {
                throw new InvalidDataException($"Resolved Slot {address} disappeared during run synchronization.");
            }

            var slot = SlotOccupancy.Restore(storedSlot.Snapshot);
            if (slot.Material != expectedMaterial)
            {
                throw new InvalidDataException(
                    $"Resolved Slot {address} is bound to {slot.Material}, not {expectedMaterial}.");
            }

            if (slot.Status != SlotOccupancyStatus.Running)
            {
                throw new InvalidDataException(
                    $"Resolved Slot {address} has no exact completion evidence for Operation Run "
                    + $"{completedSlot.OperationRunId} at fencing token {completedSlot.FencingToken}; "
                    + $"its status must be Running, not {slot.Status}.");
            }

            var completed = slot.Complete(expectedMaterial, completedSlot.CompletedAtUtc);
            if (!completed.Succeeded)
            {
                throw new InvalidDataException(completed.Message);
            }

            updates.Add((address, slot, storedSlot.Revision, completedSlot));
        }

        return updates;
    }

    public ValueTask<bool> TryAddAsync(
        ProductionUnit productionUnit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(productionUnit);
        ProductionMaterialRegistrationGuard.RequireInitial(productionUnit);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var duplicateIdentity = _units.Values.Any(stored =>
                string.Equals(
                    stored.Snapshot.ProductModelId,
                    productionUnit.ProductModelId,
                    StringComparison.Ordinal)
                && string.Equals(
                    stored.Snapshot.IdentityKey,
                    productionUnit.IdentityKey,
                    StringComparison.Ordinal)
                && string.Equals(
                    stored.Snapshot.IdentityValue,
                    productionUnit.IdentityValue,
                    StringComparison.Ordinal));
            return ValueTask.FromResult(!duplicateIdentity && _units.TryAdd(
                productionUnit.Id,
                new Stored<ProductionUnitSnapshot>(productionUnit.ToSnapshot(), 0)));
        }
    }

    public ValueTask<bool> TryAddAsync(
        ProductionLot productionLot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(productionLot);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_lots.TryAdd(
                productionLot.Id,
                new Stored<ProductionLotSnapshot>(productionLot.ToSnapshot(), 0)));
        }
    }

    public ValueTask<bool> TryAddAsync(
        Carrier carrier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        ProductionMaterialRegistrationGuard.RequireInitial(carrier);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_carriers.TryAdd(
                carrier.Id,
                new Stored<CarrierSnapshot>(carrier.ToSnapshot(), 0)));
        }
    }

    public ValueTask<bool> TryAddAsync(
        SlotOccupancy slot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ProductionMaterialRegistrationGuard.RequireInitial(slot);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_slots.TryAdd(
                slot.Address,
                new Stored<SlotOccupancySnapshot>(slot.ToSnapshot(), 0)));
        }
    }

    public ValueTask<bool> TryAddAsync(
        MaterialGenealogyLink link,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_timeline.ContainsKey(link.Id.Value))
            {
                throw new InvalidOperationException(
                    $"Production Material evidence {link.Id.Value:D} already exists.");
            }

            if (!_genealogy.TryAdd(link.Id, link))
            {
                return ValueTask.FromResult(false);
            }

            AppendTimeline(ProductionMaterialTimelineEntry.FromGenealogy(link));
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<ProductionMaterialPersistenceEntry<ProductionUnit>?> GetProductionUnitAsync(
        ProductionUnitId productionUnitId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_units.TryGetValue(productionUnitId, out var stored)
                ? new ProductionMaterialPersistenceEntry<ProductionUnit>(
                    ProductionUnit.Restore(stored.Snapshot),
                    stored.Revision)
                : null);
        }
    }

    public ValueTask<ProductionMaterialPersistenceEntry<ProductionLot>?> GetProductionLotAsync(
        ProductionLotId productionLotId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(productionLotId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_lots.TryGetValue(productionLotId, out var stored)
                ? new ProductionMaterialPersistenceEntry<ProductionLot>(
                    ProductionLot.Restore(stored.Snapshot),
                    stored.Revision)
                : null);
        }
    }

    public ValueTask<ProductionMaterialPersistenceEntry<Carrier>?> GetCarrierAsync(
        CarrierId carrierId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(carrierId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_carriers.TryGetValue(carrierId, out var stored)
                ? new ProductionMaterialPersistenceEntry<Carrier>(
                    Carrier.Restore(stored.Snapshot),
                    stored.Revision)
                : null);
        }
    }

    public ValueTask<ProductionMaterialPersistenceEntry<SlotOccupancy>?> GetSlotAsync(
        SlotAddress slot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slot);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_slots.TryGetValue(slot, out var stored)
                ? new ProductionMaterialPersistenceEntry<SlotOccupancy>(
                    SlotOccupancy.Restore(stored.Snapshot),
                    stored.Revision)
                : null);
        }
    }

    public ValueTask<IReadOnlyCollection<ProductionMaterialPersistenceEntry<ProductionUnit>>>
        ListProductionUnitsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var units = _units.Values
                .Select(stored => new ProductionMaterialPersistenceEntry<ProductionUnit>(
                    ProductionUnit.Restore(stored.Snapshot),
                    stored.Revision))
                .OrderBy(entry => entry.Aggregate.RegisteredAtUtc)
                .ThenBy(entry => entry.Aggregate.Id.Value)
                .ToArray();
            return ValueTask.FromResult<
                IReadOnlyCollection<ProductionMaterialPersistenceEntry<ProductionUnit>>>(units);
        }
    }

    public ValueTask<IReadOnlyCollection<ProductionMaterialPersistenceEntry<Carrier>>>
        ListCarriersAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var carriers = _carriers.Values
                .Select(stored => new ProductionMaterialPersistenceEntry<Carrier>(
                    Carrier.Restore(stored.Snapshot),
                    stored.Revision))
                .OrderBy(entry => entry.Aggregate.RegisteredAtUtc)
                .ThenBy(entry => entry.Aggregate.Id.Value, StringComparer.Ordinal)
                .ToArray();
            return ValueTask.FromResult<
                IReadOnlyCollection<ProductionMaterialPersistenceEntry<Carrier>>>(carriers);
        }
    }

    public ValueTask<IReadOnlyCollection<ProductionMaterialPersistenceEntry<SlotOccupancy>>>
        ListSlotsAsync(
            string? lineId = null,
            string? stationSystemId = null,
            CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateFilter(lineId, nameof(lineId));
        ValidateFilter(stationSystemId, nameof(stationSystemId));
        lock (_gate)
        {
            var slots = _slots.Values
                .Where(stored => lineId is null || string.Equals(
                    stored.Snapshot.Address.LineId,
                    lineId,
                    StringComparison.Ordinal))
                .Where(stored => stationSystemId is null || string.Equals(
                    stored.Snapshot.Address.StationSystemId,
                    stationSystemId,
                    StringComparison.Ordinal))
                .Select(stored => new ProductionMaterialPersistenceEntry<SlotOccupancy>(
                    SlotOccupancy.Restore(stored.Snapshot),
                    stored.Revision))
                .OrderBy(entry => entry.Aggregate.Address.LineId, StringComparer.Ordinal)
                .ThenBy(entry => entry.Aggregate.Address.StationSystemId, StringComparer.Ordinal)
                .ThenBy(entry => entry.Aggregate.Address.SlotId, StringComparer.Ordinal)
                .ToArray();
            return ValueTask.FromResult<
                IReadOnlyCollection<ProductionMaterialPersistenceEntry<SlotOccupancy>>>(slots);
        }
    }

    public ValueTask<IReadOnlyCollection<MaterialGenealogyLink>> ListGenealogyLinksAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var links = _genealogy.Values
                .OrderBy(link => link.LinkedAtUtc)
                .ThenBy(link => link.Id.Value)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyCollection<MaterialGenealogyLink>>(links);
        }
    }

    public ValueTask<IReadOnlyCollection<ProductionMaterialTimelineEntry>> ListTimelineAsync(
        ProductionMaterialTimelineQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyCollection<ProductionMaterialTimelineEntry> entries = _timeline.Values
                .Where(query.Matches)
                .OrderBy(entry => entry.OccurredAtUtc)
                .ThenBy(entry => entry.EvidenceId)
                .ToArray();
            return ValueTask.FromResult(entries);
        }
    }

    public ValueTask CommitAsync(
        ProductionMaterialCommit commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ValidateCommit(commit);
            foreach (var update in commit.ProductionUnits)
            {
                _units[update.Aggregate.Id] = new Stored<ProductionUnitSnapshot>(
                    update.Aggregate.ToSnapshot(),
                    checked(update.ExpectedRevision + 1));
            }

            foreach (var update in commit.Carriers)
            {
                _carriers[update.Aggregate.Id] = new Stored<CarrierSnapshot>(
                    update.Aggregate.ToSnapshot(),
                    checked(update.ExpectedRevision + 1));
            }

            foreach (var update in commit.Slots)
            {
                _slots[update.Aggregate.Address] = new Stored<SlotOccupancySnapshot>(
                    update.Aggregate.ToSnapshot(),
                    checked(update.ExpectedRevision + 1));
            }

            foreach (var evidence in commit.Timeline)
            {
                AppendTimeline(evidence);
            }
        }

        return ValueTask.CompletedTask;
    }

    private void AppendTimeline(ProductionMaterialTimelineEntry evidence)
    {
        if (!_timeline.TryAdd(evidence.EvidenceId, evidence))
        {
            throw new InvalidOperationException(
                $"Production Material evidence {evidence.EvidenceId:D} already exists.");
        }
    }

    private void ValidateCommit(ProductionMaterialCommit commit)
    {
        foreach (var evidence in commit.Timeline)
        {
            if (_timeline.ContainsKey(evidence.EvidenceId))
            {
                throw new InvalidOperationException(
                    $"Production Material evidence {evidence.EvidenceId:D} already exists.");
            }
        }

        foreach (var update in commit.ProductionUnits)
        {
            RequireRevision(
                _units,
                update.Aggregate.Id,
                update.ExpectedRevision,
                "Production Unit",
                update.Aggregate.Id.ToString());
        }

        foreach (var update in commit.Carriers)
        {
            RequireRevision(
                _carriers,
                update.Aggregate.Id,
                update.ExpectedRevision,
                "Carrier",
                update.Aggregate.Id.Value);
        }

        foreach (var update in commit.Slots)
        {
            RequireRevision(
                _slots,
                update.Aggregate.Address,
                update.ExpectedRevision,
                "Slot",
                update.Aggregate.Address.ToString());
        }
    }

    private static void RequireRevision<TKey, TSnapshot>(
        IReadOnlyDictionary<TKey, Stored<TSnapshot>> store,
        TKey key,
        long expectedRevision,
        string resourceKind,
        string resourceId)
        where TKey : notnull
    {
        if (!store.TryGetValue(key, out var stored))
        {
            throw new InvalidOperationException(
                $"{resourceKind} {resourceId} must be added before it can be updated.");
        }

        if (stored.Revision != expectedRevision)
        {
            throw new ProductionMaterialConcurrencyException(
                resourceKind,
                resourceId,
                expectedRevision);
        }
    }

    private static void ValidateFilter(string? value, string parameterName)
    {
        if (value is not null
            && (string.IsNullOrWhiteSpace(value)
                || char.IsWhiteSpace(value[0])
                || char.IsWhiteSpace(value[^1])))
        {
            throw new ArgumentException("Filter must be canonical text when supplied.", parameterName);
        }
    }

    private sealed record Stored<TSnapshot>(TSnapshot Snapshot, long Revision);
}
