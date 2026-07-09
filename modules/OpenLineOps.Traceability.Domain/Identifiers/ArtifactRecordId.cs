namespace OpenLineOps.Traceability.Domain.Identifiers;

public readonly record struct ArtifactRecordId
{
    public ArtifactRecordId(Guid value)
    {
        Value = TraceabilityIdGuard.NotEmpty(value, nameof(value));
    }

    public Guid Value { get; }

    public static ArtifactRecordId New()
    {
        return new ArtifactRecordId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}
