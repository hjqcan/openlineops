namespace OpenLineOps.Production.Domain.Identifiers;

public sealed record OperationDefinitionId
{
    public OperationDefinitionId(string value) =>
        Value = ProductionIdGuard.PortablePathSegment(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record RouteTransitionId
{
    public RouteTransitionId(string value) =>
        Value = ProductionIdGuard.PortablePathSegment(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record ProductModelId
{
    public ProductModelId(string value) =>
        Value = ProductionIdGuard.PortablePathSegment(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record ExternalTestProgramAdapterId
{
    public ExternalTestProgramAdapterId(string value) =>
        Value = ProductionIdGuard.PortablePathSegment(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}
