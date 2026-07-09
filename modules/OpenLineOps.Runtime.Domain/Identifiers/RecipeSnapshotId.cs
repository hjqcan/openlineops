namespace OpenLineOps.Runtime.Domain.Identifiers;

public sealed record RecipeSnapshotId
{
    public RecipeSnapshotId(string value)
    {
        Value = RuntimeIdGuard.NotBlank(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }
}
