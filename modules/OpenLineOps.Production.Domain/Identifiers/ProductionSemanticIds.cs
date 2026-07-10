namespace OpenLineOps.Production.Domain.Identifiers;

public sealed record WorkstationId
{
    public WorkstationId(string value) =>
        Value = ProductionIdGuard.PortablePathSegment(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record ProcessStageId
{
    public ProcessStageId(string value) =>
        Value = ProductionIdGuard.PortablePathSegment(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record DutModelId
{
    public DutModelId(string value) =>
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
