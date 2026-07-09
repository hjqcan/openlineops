namespace OpenLineOps.Processes.Domain.Identifiers;

public sealed record ProcessDefinitionId
{
    public ProcessDefinitionId(string value)
    {
        Value = ProcessIdGuard.NotBlank(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }
}
