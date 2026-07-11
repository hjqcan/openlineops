using OpenLineOps.Runtime.Domain.ProductionUnits;

namespace OpenLineOps.Runtime.Domain.Materials;

public enum MaterialKind
{
    ProductionUnit,
    Carrier
}

public sealed record MaterialReference
{
    public MaterialReference(MaterialKind kind, string value)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Material kind is invalid.");
        }

        Kind = kind;
        Value = ProductionMaterialGuard.Canonical(value, nameof(value));
        if (kind == MaterialKind.ProductionUnit
            && (!Guid.TryParseExact(Value, "D", out var parsed) || parsed == Guid.Empty))
        {
            throw new ArgumentException(
                "Production Unit material identity must be a canonical non-empty GUID.",
                nameof(value));
        }
    }

    public MaterialKind Kind { get; }

    public string Value { get; }

    public static MaterialReference ForProductionUnit(ProductionUnitId productionUnitId)
    {
        return new MaterialReference(MaterialKind.ProductionUnit, productionUnitId.ToString());
    }

    public static MaterialReference ForCarrier(CarrierId carrierId)
    {
        ArgumentNullException.ThrowIfNull(carrierId);
        return new MaterialReference(MaterialKind.Carrier, carrierId.Value);
    }

    public ProductionUnitId RequireProductionUnitId()
    {
        if (Kind != MaterialKind.ProductionUnit)
        {
            throw new InvalidOperationException($"Material {this} is not a Production Unit.");
        }

        return new ProductionUnitId(Guid.ParseExact(Value, "D"));
    }

    public CarrierId RequireCarrierId()
    {
        if (Kind != MaterialKind.Carrier)
        {
            throw new InvalidOperationException($"Material {this} is not a Carrier.");
        }

        return new CarrierId(Value);
    }

    public override string ToString()
    {
        return $"{Kind}:{Value}";
    }
}
