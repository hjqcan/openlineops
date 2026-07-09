namespace OpenLineOps.Traceability.Domain.Identifiers;

public readonly record struct AuditEntryId
{
    public AuditEntryId(Guid value)
    {
        Value = TraceabilityIdGuard.NotEmpty(value, nameof(value));
    }

    public Guid Value { get; }

    public static AuditEntryId New()
    {
        return new AuditEntryId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}
