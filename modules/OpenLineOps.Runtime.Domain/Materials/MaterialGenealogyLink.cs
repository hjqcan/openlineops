using OpenLineOps.Runtime.Domain.ProductionUnits;

namespace OpenLineOps.Runtime.Domain.Materials;

public readonly record struct MaterialGenealogyLinkId
{
    public MaterialGenealogyLinkId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Material genealogy link id cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static MaterialGenealogyLinkId New()
    {
        return new MaterialGenealogyLinkId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public sealed record MaterialGenealogyLink
{
    public MaterialGenealogyLink(
        MaterialGenealogyLinkId id,
        ProductionUnitId parentUnitId,
        ProductionUnitId childUnitId,
        string relationship,
        string operationId,
        string linkedBy,
        DateTimeOffset linkedAtUtc)
    {
        if (parentUnitId == childUnitId)
        {
            throw new ArgumentException(
                "A Production Unit cannot be its own genealogy parent.",
                nameof(childUnitId));
        }

        Id = id;
        ParentUnitId = parentUnitId;
        ChildUnitId = childUnitId;
        Relationship = ProductionMaterialGuard.Canonical(relationship, nameof(relationship));
        OperationId = ProductionMaterialGuard.Canonical(operationId, nameof(operationId));
        LinkedBy = ProductionMaterialGuard.Canonical(linkedBy, nameof(linkedBy));
        LinkedAtUtc = ProductionMaterialGuard.Utc(linkedAtUtc, nameof(linkedAtUtc));
    }

    public MaterialGenealogyLinkId Id { get; }

    public ProductionUnitId ParentUnitId { get; }

    public ProductionUnitId ChildUnitId { get; }

    public string Relationship { get; }

    public string OperationId { get; }

    public string LinkedBy { get; }

    public DateTimeOffset LinkedAtUtc { get; }
}
