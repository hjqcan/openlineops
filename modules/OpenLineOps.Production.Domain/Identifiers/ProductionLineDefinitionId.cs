namespace OpenLineOps.Production.Domain.Identifiers;

public sealed record ProductionLineDefinitionId
{
    public ProductionLineDefinitionId(string value)
    {
        Value = ProductionIdGuard.PortablePathSegment(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }
}
