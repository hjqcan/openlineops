namespace OpenLineOps.Projects.Domain.Identifiers;

public sealed record ProjectApplicationId
{
    public ProjectApplicationId(string value)
    {
        Value = ProjectIdGuard.NotBlank(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }
}
