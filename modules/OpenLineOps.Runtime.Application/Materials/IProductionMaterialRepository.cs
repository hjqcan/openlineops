using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.ProductionUnits;

namespace OpenLineOps.Runtime.Application.Materials;

public interface IProductionMaterialRepository
{
    ValueTask<bool> TryAddAsync(
        ProductionUnit productionUnit,
        CancellationToken cancellationToken = default);

    ValueTask<bool> TryAddAsync(
        ProductionLot productionLot,
        CancellationToken cancellationToken = default);

    ValueTask<bool> TryAddAsync(
        Carrier carrier,
        CancellationToken cancellationToken = default);

    ValueTask<bool> TryAddAsync(
        SlotOccupancy slot,
        CancellationToken cancellationToken = default);

    ValueTask<bool> TryAddAsync(
        MaterialGenealogyLink link,
        CancellationToken cancellationToken = default);

    ValueTask<ProductionMaterialPersistenceEntry<ProductionUnit>?> GetProductionUnitAsync(
        ProductionUnitId productionUnitId,
        CancellationToken cancellationToken = default);

    ValueTask<ProductionMaterialPersistenceEntry<ProductionLot>?> GetProductionLotAsync(
        ProductionLotId productionLotId,
        CancellationToken cancellationToken = default);

    ValueTask<ProductionMaterialPersistenceEntry<Carrier>?> GetCarrierAsync(
        CarrierId carrierId,
        CancellationToken cancellationToken = default);

    ValueTask<ProductionMaterialPersistenceEntry<SlotOccupancy>?> GetSlotAsync(
        SlotAddress slot,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<ProductionMaterialPersistenceEntry<ProductionUnit>>>
        ListProductionUnitsAsync(CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<ProductionMaterialPersistenceEntry<Carrier>>>
        ListCarriersAsync(CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<ProductionMaterialPersistenceEntry<SlotOccupancy>>>
        ListSlotsAsync(
            string? lineId = null,
            string? stationSystemId = null,
            CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<MaterialGenealogyLink>> ListGenealogyLinksAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<ProductionMaterialTimelineEntry>> ListTimelineAsync(
        ProductionMaterialTimelineQuery query,
        CancellationToken cancellationToken = default);

    ValueTask CommitAsync(
        ProductionMaterialCommit commit,
        CancellationToken cancellationToken = default);
}

public sealed record ProductionMaterialPersistenceEntry<TAggregate>
    where TAggregate : class
{
    public ProductionMaterialPersistenceEntry(TAggregate aggregate, long revision)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        Aggregate = aggregate;
        Revision = revision;
    }

    public TAggregate Aggregate { get; }

    public long Revision { get; }
}

public sealed record ProductionUnitUpdate
{
    public ProductionUnitUpdate(ProductionUnit aggregate, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        Aggregate = aggregate;
        ExpectedRevision = expectedRevision;
    }

    public ProductionUnit Aggregate { get; }

    public long ExpectedRevision { get; }
}

public sealed record CarrierUpdate
{
    public CarrierUpdate(Carrier aggregate, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        Aggregate = aggregate;
        ExpectedRevision = expectedRevision;
    }

    public Carrier Aggregate { get; }

    public long ExpectedRevision { get; }
}

public sealed record SlotOccupancyUpdate
{
    public SlotOccupancyUpdate(SlotOccupancy aggregate, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        Aggregate = aggregate;
        ExpectedRevision = expectedRevision;
    }

    public SlotOccupancy Aggregate { get; }

    public long ExpectedRevision { get; }
}

public sealed record ProductionMaterialCommit
{
    public ProductionMaterialCommit(
        IEnumerable<ProductionUnitUpdate>? productionUnits = null,
        IEnumerable<CarrierUpdate>? carriers = null,
        IEnumerable<SlotOccupancyUpdate>? slots = null,
        IEnumerable<ProductionMaterialTimelineEntry>? timeline = null)
    {
        ProductionUnits = productionUnits?.ToArray() ?? [];
        Carriers = carriers?.ToArray() ?? [];
        Slots = slots?.ToArray() ?? [];
        Timeline = timeline?.ToArray() ?? [];
        if (ProductionUnits.Count + Carriers.Count + Slots.Count == 0)
        {
            throw new ArgumentException("A Production Material commit cannot be empty.");
        }

        if (Timeline.Count == 0)
        {
            throw new ArgumentException(
                "A Production Material state commit requires append-only timeline evidence.");
        }

        RequireUnique(
            ProductionUnits.Select(update => update.Aggregate.Id),
            "Production Unit");
        RequireUnique(Carriers.Select(update => update.Aggregate.Id), "Carrier");
        RequireUnique(Slots.Select(update => update.Aggregate.Address), "Slot");
        RequireUnique(Timeline.Select(entry => entry.EvidenceId), "Production Material evidence");
    }

    public IReadOnlyList<ProductionUnitUpdate> ProductionUnits { get; }

    public IReadOnlyList<CarrierUpdate> Carriers { get; }

    public IReadOnlyList<SlotOccupancyUpdate> Slots { get; }

    public IReadOnlyList<ProductionMaterialTimelineEntry> Timeline { get; }

    private static void RequireUnique<T>(IEnumerable<T> values, string resourceKind)
        where T : notnull
    {
        var materialized = values.ToArray();
        if (materialized.Distinct().Count() != materialized.Length)
        {
            throw new ArgumentException(
                $"A Production Material commit cannot update the same {resourceKind} twice.");
        }
    }
}

public sealed record ProductionMaterialTimelineQuery
{
    public ProductionMaterialTimelineQuery(
        ProductionUnitId? productionUnitId = null,
        ProductionRunId? productionRunId = null,
        CarrierId? carrierId = null,
        DateTimeOffset? throughUtc = null)
    {
        if (productionUnitId is null && productionRunId is null && carrierId is null)
        {
            throw new ArgumentException(
                "A Production Material timeline query requires Unit, Run, or Carrier identity.");
        }

        if (throughUtc is { } timestamp)
        {
            if (timestamp == default || timestamp.Offset != TimeSpan.Zero)
            {
                throw new ArgumentException(
                    "Production Material timeline boundary must use UTC offset zero.",
                    nameof(throughUtc));
            }
        }

        ProductionUnitId = productionUnitId;
        ProductionRunId = productionRunId;
        CarrierId = carrierId;
        ThroughUtc = throughUtc;
    }

    public ProductionUnitId? ProductionUnitId { get; }

    public ProductionRunId? ProductionRunId { get; }

    public CarrierId? CarrierId { get; }

    public DateTimeOffset? ThroughUtc { get; }

    public bool Matches(ProductionMaterialTimelineEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return (ThroughUtc is null || entry.OccurredAtUtc <= ThroughUtc)
            && (ProductionUnitId is not null && (
                    entry.ProductionUnitId == ProductionUnitId
                    || entry.Genealogy?.ParentUnitId == ProductionUnitId
                    || entry.Genealogy?.ChildUnitId == ProductionUnitId)
                || ProductionRunId is not null && entry.ProductionRunId == ProductionRunId
                || CarrierId is not null && entry.CarrierId == CarrierId);
    }
}

public sealed class ProductionMaterialConcurrencyException : InvalidOperationException
{
    public ProductionMaterialConcurrencyException(
        string resourceKind,
        string resourceId,
        long expectedRevision)
        : base(
            $"{resourceKind} {resourceId} was not stored at expected revision "
            + $"{expectedRevision}; the caller must reload before applying another transition.")
    {
        ResourceKind = resourceKind;
        ResourceId = resourceId;
        ExpectedRevision = expectedRevision;
    }

    public string ResourceKind { get; }

    public string ResourceId { get; }

    public long ExpectedRevision { get; }
}
