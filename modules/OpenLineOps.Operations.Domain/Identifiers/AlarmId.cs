namespace OpenLineOps.Operations.Domain.Identifiers;

public sealed record AlarmId
{
    public AlarmId(string value)
    {
        Value = OperationsIdGuard.NotBlank(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }
}
