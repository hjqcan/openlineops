namespace OpenLineOps.Processes.Domain.Identifiers;

public sealed record ProcessCapabilityId
{
    public ProcessCapabilityId(string value)
    {
        Value = ProcessIdGuard.NotBlank(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }
}
