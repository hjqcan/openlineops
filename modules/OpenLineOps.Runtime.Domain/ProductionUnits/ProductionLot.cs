using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Runtime.Domain.Materials;

namespace OpenLineOps.Runtime.Domain.ProductionUnits;

public sealed record ProductionLotId
{
    public ProductionLotId(string value)
    {
        Value = ProductionMaterialGuard.Canonical(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }
}

public sealed class ProductionLot : AggregateRoot<ProductionLotId>
{
    private ProductionLot(
        ProductionLotId id,
        string productModelId,
        int? declaredQuantity,
        string registeredBy,
        DateTimeOffset registeredAtUtc)
        : base(id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (declaredQuantity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(declaredQuantity),
                "Declared lot quantity must be positive when supplied.");
        }

        ProductModelId = ProductionMaterialGuard.Canonical(productModelId, nameof(productModelId));
        DeclaredQuantity = declaredQuantity;
        RegisteredBy = ProductionMaterialGuard.Canonical(registeredBy, nameof(registeredBy));
        RegisteredAtUtc = ProductionMaterialGuard.Utc(registeredAtUtc, nameof(registeredAtUtc));
    }

    public string ProductModelId { get; }

    public int? DeclaredQuantity { get; }

    public string RegisteredBy { get; }

    public DateTimeOffset RegisteredAtUtc { get; }

    public static ProductionLot Register(
        ProductionLotId id,
        string productModelId,
        int? declaredQuantity,
        string registeredBy,
        DateTimeOffset registeredAtUtc)
    {
        return new ProductionLot(
            id,
            productModelId,
            declaredQuantity,
            registeredBy,
            registeredAtUtc);
    }

    public static ProductionLot Restore(ProductionLotSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var lot = new ProductionLot(
            snapshot.Id,
            snapshot.ProductModelId,
            snapshot.DeclaredQuantity,
            snapshot.RegisteredBy,
            snapshot.RegisteredAtUtc);
        lot.ClearDomainEvents();
        return lot;
    }

    public ProductionLotSnapshot ToSnapshot()
    {
        return new ProductionLotSnapshot(
            Id,
            ProductModelId,
            DeclaredQuantity,
            RegisteredBy,
            RegisteredAtUtc);
    }
}

public sealed record ProductionLotSnapshot(
    ProductionLotId Id,
    string ProductModelId,
    int? DeclaredQuantity,
    string RegisteredBy,
    DateTimeOffset RegisteredAtUtc);
