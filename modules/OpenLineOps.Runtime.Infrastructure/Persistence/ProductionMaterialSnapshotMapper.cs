using OpenLineOps.Domain.Abstractions.Serialization;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.ProductionUnits;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

internal static class ProductionMaterialSnapshotMapper
{
    private const string ProductionUnitResourceKind = "OpenLineOps.ProductionUnit";
    private const string ProductionLotResourceKind = "OpenLineOps.ProductionLot";
    private const string CarrierResourceKind = "OpenLineOps.Carrier";
    private const string SlotOccupancyResourceKind = "OpenLineOps.SlotOccupancy";
    private const string GenealogyResourceKind = "OpenLineOps.MaterialGenealogyLink";

    public static PersistedProductionUnit ToSnapshot(ProductionUnit aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        var snapshot = aggregate.ToSnapshot();
        return new PersistedProductionUnit(
            ProductionUnitResourceKind,
            snapshot.Id.Value,
            snapshot.ProductModelId,
            snapshot.IdentityKey,
            snapshot.IdentityValue,
            snapshot.LotId?.Value,
            snapshot.RegisteredBy,
            snapshot.RegisteredAtUtc,
            snapshot.LastTransitionAtUtc,
            snapshot.Disposition.ToString(),
            snapshot.DispositionBeforeHold?.ToString(),
            snapshot.DispositionReason,
            ToSnapshot(snapshot.Location));
    }

    public static ProductionUnit ToAggregate(PersistedProductionUnit snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RequireResource(snapshot.ResourceKind, ProductionUnitResourceKind);
        if (snapshot.ProductionUnitId == Guid.Empty)
        {
            throw new InvalidDataException("Persisted Production Unit id is empty.");
        }

        return ProductionUnit.Restore(new ProductionUnitSnapshot(
            new ProductionUnitId(snapshot.ProductionUnitId),
            Required(snapshot.ProductModelId, "product model id"),
            Required(snapshot.IdentityKey, "identity key"),
            Required(snapshot.IdentityValue, "identity value"),
            snapshot.LotId is null
                ? null
                : new ProductionLotId(Required(snapshot.LotId, "lot id")),
            Required(snapshot.RegisteredBy, "registered by"),
            snapshot.RegisteredAtUtc,
            snapshot.LastTransitionAtUtc,
            ParseEnum<ProductionUnitDisposition>(snapshot.Disposition, "disposition"),
            snapshot.DispositionBeforeHold is null
                ? null
                : ParseEnum<ProductionUnitDisposition>(
                    snapshot.DispositionBeforeHold,
                    "disposition before hold"),
            Optional(snapshot.DispositionReason, "disposition reason"),
            ToAggregate(snapshot.Location)));
    }

    public static PersistedProductionLot ToSnapshot(ProductionLot aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        var snapshot = aggregate.ToSnapshot();
        return new PersistedProductionLot(
            ProductionLotResourceKind,
            snapshot.Id.Value,
            snapshot.ProductModelId,
            snapshot.DeclaredQuantity,
            snapshot.RegisteredBy,
            snapshot.RegisteredAtUtc);
    }

    public static ProductionLot ToAggregate(PersistedProductionLot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RequireResource(snapshot.ResourceKind, ProductionLotResourceKind);
        return ProductionLot.Restore(new ProductionLotSnapshot(
            new ProductionLotId(Required(snapshot.LotId, "lot id")),
            Required(snapshot.ProductModelId, "product model id"),
            snapshot.DeclaredQuantity,
            Required(snapshot.RegisteredBy, "registered by"),
            snapshot.RegisteredAtUtc));
    }

    public static PersistedCarrier ToSnapshot(Carrier aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        var snapshot = aggregate.ToSnapshot();
        return new PersistedCarrier(
            CarrierResourceKind,
            snapshot.Id.Value,
            snapshot.CarrierTypeId,
            snapshot.Capacity,
            snapshot.RegisteredBy,
            snapshot.RegisteredAtUtc,
            snapshot.LastTransitionAtUtc,
            ToSnapshot(snapshot.Location));
    }

    public static Carrier ToAggregate(PersistedCarrier snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RequireResource(snapshot.ResourceKind, CarrierResourceKind);
        return Carrier.Restore(new CarrierSnapshot(
            new CarrierId(Required(snapshot.CarrierId, "carrier id")),
            Required(snapshot.CarrierTypeId, "carrier type id"),
            snapshot.Capacity,
            Required(snapshot.RegisteredBy, "registered by"),
            snapshot.RegisteredAtUtc,
            snapshot.LastTransitionAtUtc,
            ToAggregate(snapshot.Location)));
    }

    public static PersistedSlotOccupancy ToSnapshot(SlotOccupancy aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        var snapshot = aggregate.ToSnapshot();
        return new PersistedSlotOccupancy(
            SlotOccupancyResourceKind,
            snapshot.Address.LineId,
            snapshot.Address.StationSystemId,
            snapshot.Address.SlotId,
            snapshot.Status.ToString(),
            ToSnapshot(snapshot.Material),
            snapshot.RegisteredAtUtc,
            snapshot.LastTransitionAtUtc);
    }

    public static SlotOccupancy ToAggregate(PersistedSlotOccupancy snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RequireResource(snapshot.ResourceKind, SlotOccupancyResourceKind);
        return SlotOccupancy.Restore(new SlotOccupancySnapshot(
            new SlotAddress(
                Required(snapshot.LineId, "line id"),
                Required(snapshot.StationSystemId, "Station System id"),
                Required(snapshot.SlotId, "Slot id")),
            ParseEnum<SlotOccupancyStatus>(snapshot.Status, "Slot status"),
            ToAggregate(snapshot.Material),
            snapshot.RegisteredAtUtc,
            snapshot.LastTransitionAtUtc));
    }

    public static PersistedMaterialGenealogyLink ToSnapshot(MaterialGenealogyLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return new PersistedMaterialGenealogyLink(
            GenealogyResourceKind,
            link.Id.Value,
            link.ParentUnitId.Value,
            link.ChildUnitId.Value,
            link.Relationship,
            link.OperationId,
            link.LinkedBy,
            link.LinkedAtUtc);
    }

    public static MaterialGenealogyLink ToAggregate(PersistedMaterialGenealogyLink snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RequireResource(snapshot.ResourceKind, GenealogyResourceKind);
        if (snapshot.LinkId == Guid.Empty
            || snapshot.ParentUnitId == Guid.Empty
            || snapshot.ChildUnitId == Guid.Empty)
        {
            throw new InvalidDataException(
                "Persisted Material genealogy link contains an empty identity.");
        }

        return new MaterialGenealogyLink(
            new MaterialGenealogyLinkId(snapshot.LinkId),
            new ProductionUnitId(snapshot.ParentUnitId),
            new ProductionUnitId(snapshot.ChildUnitId),
            Required(snapshot.Relationship, "relationship"),
            Required(snapshot.OperationId, "operation id"),
            Required(snapshot.LinkedBy, "linked by"),
            snapshot.LinkedAtUtc);
    }

    private static PersistedMaterialLocation? ToSnapshot(MaterialLocation? location)
    {
        return location is null
            ? null
            : new PersistedMaterialLocation(
                location.Kind.ToString(),
                location.LineId,
                location.StationSystemId,
                location.SlotId,
                location.CarrierId?.Value,
                location.CarrierPositionId);
    }

    private static MaterialLocation? ToAggregate(PersistedMaterialLocation? location)
    {
        if (location is null)
        {
            return null;
        }

        var kind = ParseEnum<MaterialLocationKind>(location.Kind, "material location kind");
        return kind switch
        {
            MaterialLocationKind.StationQueue => MaterialLocation.AtStation(
                Required(location.LineId, "location line id"),
                Required(location.StationSystemId, "location Station System id")),
            MaterialLocationKind.Slot => MaterialLocation.InSlot(new SlotAddress(
                Required(location.LineId, "location line id"),
                Required(location.StationSystemId, "location Station System id"),
                Required(location.SlotId, "location Slot id"))),
            MaterialLocationKind.CarrierPosition => MaterialLocation.OnCarrier(
                new CarrierId(Required(location.CarrierId, "location Carrier id")),
                Required(location.CarrierPositionId, "location Carrier position id")),
            _ => throw new InvalidDataException($"Unsupported material location kind {kind}.")
        };
    }

    private static PersistedMaterialReference? ToSnapshot(MaterialReference? material)
    {
        return material is null
            ? null
            : new PersistedMaterialReference(material.Kind.ToString(), material.Value);
    }

    private static MaterialReference? ToAggregate(PersistedMaterialReference? material)
    {
        return material is null
            ? null
            : new MaterialReference(
                ParseEnum<MaterialKind>(material.Kind, "material kind"),
                Required(material.Value, "material identity"));
    }

    private static void RequireResource(string? actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Persisted Production Material resource kind '{actual}' is invalid; "
                + $"expected '{expected}'.");
        }
    }

    private static string Required(string? value, string fieldName)
    {
        return string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            ? throw new InvalidDataException(
                $"Persisted Production Material does not declare canonical {fieldName}.")
            : value;
    }

    private static string? Optional(string? value, string fieldName)
    {
        return value is null ? null : Required(value, fieldName);
    }

    private static TEnum ParseEnum<TEnum>(string? value, string fieldName)
        where TEnum : struct, Enum
    {
        if (value is not null && CanonicalEnumToken.TryParse<TEnum>(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidDataException(
            $"Persisted Production Material {fieldName} value '{value}' is invalid. "
            + $"Expected an exact, case-sensitive {typeof(TEnum).Name} token: "
            + $"{CanonicalEnumToken.ExpectedTokens<TEnum>()}.");
    }
}

internal sealed record PersistedProductionUnit(
    string? ResourceKind,
    Guid ProductionUnitId,
    string? ProductModelId,
    string? IdentityKey,
    string? IdentityValue,
    string? LotId,
    string? RegisteredBy,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset LastTransitionAtUtc,
    string? Disposition,
    string? DispositionBeforeHold,
    string? DispositionReason,
    PersistedMaterialLocation? Location);

internal sealed record PersistedProductionLot(
    string? ResourceKind,
    string? LotId,
    string? ProductModelId,
    int? DeclaredQuantity,
    string? RegisteredBy,
    DateTimeOffset RegisteredAtUtc);

internal sealed record PersistedCarrier(
    string? ResourceKind,
    string? CarrierId,
    string? CarrierTypeId,
    int Capacity,
    string? RegisteredBy,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset LastTransitionAtUtc,
    PersistedMaterialLocation? Location);

internal sealed record PersistedSlotOccupancy(
    string? ResourceKind,
    string? LineId,
    string? StationSystemId,
    string? SlotId,
    string? Status,
    PersistedMaterialReference? Material,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset LastTransitionAtUtc);

internal sealed record PersistedMaterialGenealogyLink(
    string? ResourceKind,
    Guid LinkId,
    Guid ParentUnitId,
    Guid ChildUnitId,
    string? Relationship,
    string? OperationId,
    string? LinkedBy,
    DateTimeOffset LinkedAtUtc);

internal sealed record PersistedMaterialLocation(
    string? Kind,
    string? LineId,
    string? StationSystemId,
    string? SlotId,
    string? CarrierId,
    string? CarrierPositionId);

internal sealed record PersistedMaterialReference(string? Kind, string? Value);
